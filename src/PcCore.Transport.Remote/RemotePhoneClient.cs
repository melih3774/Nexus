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
    private readonly List<byte[]> _frames = [];

    /// <summary>Latest-frames-only, for the same reason the server keeps one: nothing needs the old ones.</summary>
    private const int MaxBufferedFrames = 4;

    private int _requestCounter;

    /// <param name="deviceId">The paired device id.</param>
    /// <param name="sharedSecretBase64Url">The phone's copy of the shared secret.</param>
    public RemotePhoneClient(string deviceId, string sharedSecretBase64Url)
    {
        _deviceId = deviceId;
        _sharedSecret = Base64Url.DecodeFromChars(sharedSecretBase64Url.AsSpan());
        _socket.Options.AddSubProtocol(RemoteProtocol.Subprotocol);
    }

    /// <summary>What the PC returns once enrollment succeeds. The secret is permanent; guard it.</summary>
    public sealed record Enrollment(
        string PcInstanceId,
        string DeviceId,
        string SharedSecret,
        DateTimeOffset PairedAtUtc);

    /// <summary>Set after a successful handshake.</summary>
    public string? PcInstanceId { get; private set; }

    public bool IsAuthenticated { get; private set; }

    /// <summary>
    /// The app build this client claims to be, sent at authentication the way the Android app sends
    /// its versionCode. Null sends nothing, which is what a phone from before the update path does.
    /// </summary>
    public int? ClientVersionCode { get; init; }

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

        var challengeMessage = (await ReceiveAsync(cancellationToken).ConfigureAwait(false)).Text
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

                // Diagnostic, and sent for the same reason the Android client sends it: the server
                // can then say "you are signing for a different installation" instead of leaving
                // that indistinguishable from a wrong secret.
                pc_instance_id = pcInstanceId,
                client_version_code = ClientVersionCode,
            },
            cancellationToken).ConfigureAwait(false);

        IsAuthenticated = response.Success;
        return response;
    }

    /// <summary>
    /// Reproduces the server's signed message and HMACs it. Mirrors
    /// <c>DeviceAuthenticator.BuildSignedMessage</c> â€” the Android client must produce these exact
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

    /// <summary>
    /// Completes remote pairing enrollment on a fresh connection and returns the permanent
    /// credential.
    /// <para>
    /// Static, and it opens its own socket, because at this point there is no device id and no
    /// permanent secret â€” the very things an instance of this class is constructed from. That is
    /// the bootstrap this path exists to resolve.
    /// </para>
    /// <para>
    /// The pairing secret is not transmitted: it keys an HMAC over the PC's own single-use
    /// challenge, bound to this installation and this pairing session.
    /// </para>
    /// </summary>
    public static async Task<Enrollment> EnrollAsync(
        Uri uri,
        string pcInstanceId,
        string sessionId,
        string pairingSecret,
        string? displayName,
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(RemoteProtocol.Subprotocol);

        await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);

        var challengeMessage = (await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false)).Text
            ?? throw new InvalidOperationException("connection closed before a challenge arrived");

        using var challengeDocument = JsonDocument.Parse(challengeMessage);
        var challengeRoot = challengeDocument.RootElement;

        if (challengeRoot.GetProperty("event").GetString() != RemoteProtocol.ChallengeEvent)
        {
            throw new InvalidOperationException($"expected {RemoteProtocol.ChallengeEvent}");
        }

        var payload = challengeRoot.GetProperty("payload");
        var challengeId = payload.GetProperty("challenge_id").GetString()!;
        var challenge = payload.GetProperty("challenge").GetString()!;

        var envelope = JsonSerializer.Serialize(
            new
            {
                protocol_version = ProtocolConstants.CurrentVersion,
                id = "enroll-1",
                type = MessageTypes.Request,
                action = RemoteProtocol.EnrollAction,
                @params = new
                {
                    challenge_id = challengeId,
                    session_id = sessionId,
                    proof = SignEnrollment(pcInstanceId, sessionId, pairingSecret, challenge),
                    display_name = displayName,
                },
            },
            ProtocolCodec.Options);

        await socket.SendAsync(
            Encoding.UTF8.GetBytes(envelope),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);

        var reply = (await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false)).Text
            ?? throw new InvalidOperationException("connection closed before an enrollment reply arrived");

        using var replyDocument = JsonDocument.Parse(reply);
        var replyRoot = replyDocument.RootElement;

        if (!replyRoot.GetProperty("success").GetBoolean())
        {
            var code = replyRoot.GetProperty("error").GetProperty("code").GetString();
            throw new InvalidOperationException($"enrollment refused: {code}");
        }

        var result = replyRoot.GetProperty("result");

        try
        {
            if (socket.State is WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "enrolled", cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The PC closes from its side as soon as it has answered; either order is fine.
        }

        return new Enrollment(
            result.GetProperty("pc_instance_id").GetString()!,
            result.GetProperty("device_id").GetString()!,
            result.GetProperty("shared_secret").GetString()!,
            result.GetProperty("paired_at_utc").GetDateTimeOffset());
    }

    /// <summary>
    /// Mirrors <c>RemoteEnrollmentService.BuildEnrollmentMessage</c>. The key is the UTF-8 bytes of
    /// the pairing secret exactly as it appears in the pairing code â€” not its decoded form â€” because
    /// that is what the PC stored when it created the session.
    /// </summary>
    public static string SignEnrollment(
        string pcInstanceId,
        string sessionId,
        string pairingSecret,
        string challengeBase64Url)
    {
        var prefix = Encoding.UTF8.GetBytes($"nexus-pair-v1\0{pcInstanceId}\0{sessionId}\0");
        var challenge = Base64Url.DecodeFromChars(challengeBase64Url.AsSpan());

        var message = new byte[prefix.Length + challenge.Length];
        prefix.CopyTo(message, 0);
        challenge.CopyTo(message.AsSpan(prefix.Length));

        return Base64Url.EncodeToString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(pairingSecret), message));
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
            var received = await ReceiveAsync(cancellationToken).ConfigureAwait(false);

            if (received.Closed)
            {
                throw new InvalidOperationException($"connection closed while awaiting response to {action}");
            }

            if (received.Frame is not null)
            {
                RecordFrame(received.Frame);
                continue;
            }

            var message = received.Text!;

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
                var received = await ReceiveAsync(deadline.Token).ConfigureAwait(false);
                if (received.Closed)
                {
                    return null;
                }

                if (received.Frame is not null)
                {
                    RecordFrame(received.Frame);
                    continue;
                }

                using var document = JsonDocument.Parse(received.Text!);
                if (document.RootElement.GetProperty("type").GetString() != MessageTypes.Event)
                {
                    continue;
                }

                var envelope = Record(received.Text!);
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

    /// <summary>Binary desktop frames received on this connection, newest last.</summary>
    public IReadOnlyList<byte[]> ReceivedFrames
    {
        get
        {
            lock (_gate)
            {
                return [.. _frames];
            }
        }
    }

    /// <summary>
    /// Waits for a live desktop frame, or returns null on timeout.
    /// <para>
    /// Keeps only the most recent few: a test client that buffered every frame of a long stream
    /// would be measuring its own memory growth rather than the server's.
    /// </para>
    /// </summary>
    public async Task<byte[]?> WaitForFrameAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var existing = ReceivedFrames;
        if (existing.Count > 0)
        {
            return existing[^1];
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            while (!deadline.Token.IsCancellationRequested)
            {
                var received = await ReceiveAsync(deadline.Token).ConfigureAwait(false);

                if (received.Closed)
                {
                    return null;
                }

                if (received.Frame is not null)
                {
                    RecordFrame(received.Frame);
                    return received.Frame;
                }

                if (received.Text is not null)
                {
                    using var document = JsonDocument.Parse(received.Text);

                    if (document.RootElement.GetProperty("type").GetString() == MessageTypes.Event)
                    {
                        Record(received.Text);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        return null;
    }

    private void RecordFrame(byte[] frame)
    {
        lock (_gate)
        {
            _frames.Add(frame);

            if (_frames.Count > MaxBufferedFrames)
            {
                _frames.RemoveAt(0);
            }
        }
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

    /// <summary>One received message: either protocol text, or a binary desktop frame.</summary>
    private readonly record struct Received(string? Text, byte[]? Frame)
    {
        public bool Closed => Text is null && Frame is null;
    }

    private Task<Received> ReceiveAsync(CancellationToken cancellationToken) =>
        ReceiveAsync(_socket, cancellationToken);

    /// <summary>
    /// Reads one WebSocket message of either kind.
    /// <para>
    /// The two message types share a connection, so a reader that assumed text would treat the
    /// first frame as a malformed envelope. The Android client has exactly this shape.
    /// </para>
    /// </summary>
    private static async Task<Received> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        var binary = false;

        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return default;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return default;
            }

            if (message.Length == 0)
            {
                binary = result.MessageType == WebSocketMessageType.Binary;
            }

            message.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                return binary
                    ? new Received(null, message.ToArray())
                    : new Received(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length), null);
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
