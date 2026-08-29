using System.Buffers.Text;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PcCore.Protocol;

namespace PcCore.Transport.Remote;

/// <summary>
/// The client half of the remote protocol.
/// <para>
/// <b>Development and reference only.</b> This is what the future Android application will
/// reimplement in Kotlin; keeping a working reference here means the handshake has exactly one
/// definition that both the server tests and the simulator exercise, rather than a prose
/// description that drifts.
/// </para>
/// <para>
/// It is a plain protocol client: it holds a device id and shared secret, connects, answers the
/// challenge, and sends request envelopes. It has no UI and is not the Android app.
/// </para>
/// </summary>
public sealed class RemotePhoneClient : IAsyncDisposable
{
    private readonly string _deviceId;
    private readonly byte[] _sharedSecret;
    private readonly ClientWebSocket _socket = new();
    private readonly Lock _gate = new();
    private readonly List<EventEnvelope> _events = [];

    private int _requestCounter;

    /// <param name="deviceId">The paired device id.</param>
    /// <param name="sharedSecretBase64Url">The phone's copy of the shared secret.</param>
    public RemotePhoneClient(string deviceId, string sharedSecretBase64Url)
    {
        _deviceId = deviceId;
        _sharedSecret = Base64Url.DecodeFromChars(sharedSecretBase64Url.AsSpan());
        _socket.Options.AddSubProtocol(RemoteProtocol.Subprotocol);
    }

    /// <summary>Set after a successful handshake.</summary>
    public string? PcInstanceId { get; private set; }

    public bool IsAuthenticated { get; private set; }

    /// <summary>Events received on this connection, in arrival order.</summary>
    public IReadOnlyList<EventEnvelope> ReceivedEvents
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        _socket.ConnectAsync(uri, cancellationToken);

    /// <summary>
    /// Completes the challenge/response handshake.
    /// <para>
    /// The permanent secret never goes on the wire: the server sends a random challenge and this
    /// returns an HMAC over a message that also binds the installation and the device id.
    /// </para>
    /// </summary>
    /// <param name="pcInstanceId">
    /// The installation being authenticated to. The phone knows this from pairing; it is part of
    /// the signed message so a proof cannot be replayed against a different PC.
    /// </param>
    public async Task<ResponseEnvelope> AuthenticateAsync(string pcInstanceId, CancellationToken cancellationToken)
    {
        PcInstanceId = pcInstanceId;

        var challengeMessage = await ReceiveAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("connection closed before a challenge arrived");

        using var document = JsonDocument.Parse(challengeMessage);
        var root = document.RootElement;

        if (root.GetProperty("event").GetString() != RemoteProtocol.ChallengeEvent)
        {
            throw new InvalidOperationException($"expected {RemoteProtocol.ChallengeEvent}");
        }

        var payload = root.GetProperty("payload");
        var challengeId = payload.GetProperty("challenge_id").GetString()!;
        var challenge = payload.GetProperty("challenge").GetString()!;

        var response = await SendRequestAsync(
            RemoteProtocol.AuthenticateAction,
            new
            {
                challenge_id = challengeId,
                device_id = _deviceId,
                proof = Sign(pcInstanceId, challenge),
            },
            cancellationToken).ConfigureAwait(false);

        IsAuthenticated = response.Success;
        return response;
    }

    /// <summary>
    /// Reproduces the server's signed message and HMACs it. Mirrors
    /// <c>DeviceAuthenticator.BuildSignedMessage</c> — the Android client must produce these exact
    /// bytes.
    /// </summary>
    public string Sign(string pcInstanceId, string challengeBase64Url)
    {
        var prefix = Encoding.UTF8.GetBytes($"nexus-auth-v1\0{pcInstanceId}\0{_deviceId}\0");
        var challenge = Base64Url.DecodeFromChars(challengeBase64Url.AsSpan());

        var message = new byte[prefix.Length + challenge.Length];
        prefix.CopyTo(message, 0);
        challenge.CopyTo(message.AsSpan(prefix.Length));

        return Base64Url.EncodeToString(HMACSHA256.HashData(_sharedSecret, message));
    }

    /// <summary>Sends a request and waits for the response with the matching id, buffering events.</summary>
    public async Task<ResponseEnvelope> SendRequestAsync(
        string action,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var id = $"sim-{Interlocked.Increment(ref _requestCounter)}";

        var envelope = JsonSerializer.Serialize(
            new
            {
                protocol_version = ProtocolConstants.CurrentVersion,
                id,
                type = MessageTypes.Request,
                action,
                @params = parameters,
            },
            ProtocolCodec.Options);

        await _socket.SendAsync(
            Encoding.UTF8.GetBytes(envelope),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var message = await ReceiveAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"connection closed while awaiting response to {action}");

            using var document = JsonDocument.Parse(message);
            var type = document.RootElement.GetProperty("type").GetString();

            if (type == MessageTypes.Event)
            {
                Record(message);
                continue;
            }

            if (document.RootElement.GetProperty("id").GetString() != id)
            {
                continue;
            }

            return JsonSerializer.Deserialize<ResponseEnvelope>(message, ProtocolCodec.Options)!;
        }
    }

    /// <summary>Waits for an event with the given name, or returns null on timeout.</summary>
    public async Task<EventEnvelope?> WaitForEventAsync(
        string eventName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var existing = ReceivedEvents.FirstOrDefault(e => e.EventName == eventName);
        if (existing is not null)
        {
            return existing;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            while (!deadline.Token.IsCancellationRequested)
            {
                var message = await ReceiveAsync(deadline.Token).ConfigureAwait(false);
                if (message is null)
                {
                    return null;
                }

                using var document = JsonDocument.Parse(message);
                if (document.RootElement.GetProperty("type").GetString() != MessageTypes.Event)
                {
                    continue;
                }

                var envelope = Record(message);
                if (envelope.EventName == eventName)
                {
                    return envelope;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        return null;
    }

    private EventEnvelope Record(string message)
    {
        var envelope = JsonSerializer.Deserialize<EventEnvelope>(message, ProtocolCodec.Options)!;

        lock (_gate)
        {
            _events.Add(envelope);
        }

        return envelope;
    }

    private async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_socket.State is WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Already gone.
        }
    }

    public ValueTask DisposeAsync()
    {
        CryptographicOperations.ZeroMemory(_sharedSecret);
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
