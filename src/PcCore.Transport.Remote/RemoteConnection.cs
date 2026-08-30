using System.Buffers.Text;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Identity;
using PcCore.Capabilities.Input;
using PcCore.Capabilities.Screen;
using PcCore.Protocol;

namespace PcCore.Transport.Remote;

/// <summary>Wire names used only by the remote handshake.</summary>
public static class RemoteProtocol
{
    public const string Subprotocol = "nexus.v1";

    /// <summary>Server -> client, immediately on connect. Payload: challenge_id, challenge, expires_at_utc.</summary>
    public const string ChallengeEvent = "auth.challenge";

    /// <summary>Client -> server. Params: challenge_id, device_id, proof.</summary>
    public const string AuthenticateAction = "auth.authenticate";

    /// <summary>
    /// Client -> server. Params: challenge_id, session_id, proof, display_name.
    /// <para>
    /// The remote pairing-enrollment path, and the only other thing accepted before authentication.
    /// It exists solely on this transport — which binds to nothing but a Tailscale address — and is
    /// authorised by the one-time pairing secret the owner generated locally, never by a device
    /// identity. It does not reach <c>CapabilityDispatcher</c> and does not make the connection a
    /// <c>DEVICE</c> session: the phone receives its credential and reconnects like any other time.
    /// </para>
    /// </summary>
    public const string EnrollAction = "pairing.enroll";

    /// <summary>Server -> client, after a successful handshake.</summary>
    public const string AuthenticatedEvent = "auth.authenticated";

    /// <summary>
    /// Events a remote device may receive.
    /// <para>
    /// An allowlist rather than a filter expression: the phone needs security state changes and
    /// nothing else today, and anything added later should have to be named here deliberately
    /// rather than reach the network because it happened to be published.
    /// </para>
    /// </summary>
    public static bool IsDeliverableToDevice(string eventName) =>
        eventName.StartsWith("security.", StringComparison.Ordinal);
}

/// <summary>
/// One remote WebSocket connection: challenge, verify, then dispatch.
/// <para>
/// The connection owns the trust decision. Everything before a verified proof is refused, and the
/// <see cref="CallerDescriptor"/> used for every later request is built once, here, from the
/// <see cref="AuthenticatedDevice"/> the authenticator returned — never from anything in a request
/// body.
/// </para>
/// </summary>
internal sealed class RemoteConnection : IAsyncDisposable, IFrameSink
{
    private const int ReceiveBufferSize = 8 * 1024;
    private const int MaxConcurrentRequests = 4;

    private readonly WebSocket _socket;
    private readonly ICapabilityDispatcher _dispatcher;
    private readonly IDeviceAuthenticator _authenticator;
    private readonly IRemoteEnrollmentService _enrollment;
    private readonly IPrimaryPhoneRegistry _registry;
    private readonly IPcIdentityProvider _identity;
    private readonly IReadOnlyList<string> _allowedRoots;
    private readonly RemoteSessionRegistry _sessions;
    private readonly RemoteStreamCoordinator _streams;
    private readonly IInputService _input;
    private readonly RemoteTransportOptions _options;
    private readonly ILogger _logger;
    private readonly string _connectionId;

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentRequests, MaxConcurrentRequests);

    private CallerDescriptor? _caller;
    private RemoteSession? _session;
    private IDisposable? _eventSubscription;

    /// <summary>
    /// How many control messages are queued. Frames wait for this to reach zero.
    /// <para>
    /// Both kinds of message share one send gate, and a 90 KB frame takes long enough on a phone
    /// link that a response queued behind several of them arrives visibly late. Losing a frame costs
    /// one repaint of content that is about to be replaced anyway; delaying a response makes the
    /// whole interface feel stuck, and delaying a security event is worse than that.
    /// </para>
    /// </summary>
    private int _controlPending;

    private CancellationTokenSource? _framePump;

    public RemoteConnection(
        WebSocket socket,
        string connectionId,
        ICapabilityDispatcher dispatcher,
        IDeviceAuthenticator authenticator,
        IRemoteEnrollmentService enrollment,
        IPrimaryPhoneRegistry registry,
        IPcIdentityProvider identity,
        IReadOnlyList<string> allowedRoots,
        RemoteSessionRegistry sessions,
        RemoteStreamCoordinator streams,
        IInputService input,
        RemoteTransportOptions options,
        ILogger logger)
    {
        _socket = socket;
        _connectionId = connectionId;
        _dispatcher = dispatcher;
        _authenticator = authenticator;
        _enrollment = enrollment;
        _registry = registry;
        _identity = identity;
        _allowedRoots = allowedRoots;
        _sessions = sessions;
        _streams = streams;
        _input = input;
        _options = options;
        _logger = logger;
    }

    public async Task RunAsync(IEventStream events, CancellationToken cancellationToken)
    {
        using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        authTimeout.CancelAfter(TimeSpan.FromSeconds(_options.AuthenticationTimeoutSeconds));

        try
        {
            if (!await HandshakeAsync(events, authTimeout.Token).ConfigureAwait(false))
            {
                return;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The unauthenticated grace period elapsed. Holding sockets open for callers that never
            // prove anything is free resource consumption for an attacker.
            _logger.LogWarning("Remote connection {ConnectionId} did not authenticate in time.", _connectionId);
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "authentication timeout", CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        await PumpAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Challenge, verify, and bind the connection to the authenticated device.</summary>
    private async Task<bool> HandshakeAsync(IEventStream events, CancellationToken cancellationToken)
    {
        if (_registry.Current is null)
        {
            // Logged for the operator, but deliberately *not* short-circuited. If an unpaired PC
            // refused before issuing a challenge, its reply shape and timing would differ from a
            // wrong-secret rejection, telling anyone on the tailnet whether a phone is paired.
            // Letting it fail through the normal path keeps every rejection identical: the registry
            // simply has no secret to verify against, and the caller gets the same opaque
            // authentication_failed with a correlatable request id.
            _logger.LogWarning(
                "Remote connection {ConnectionId} cannot succeed: no primary phone is paired.", _connectionId);
        }

        var challenge = _authenticator.IssueChallenge();

        await SendAsync(
            ProtocolCodec.Encode(new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("n"),
                EventName = RemoteProtocol.ChallengeEvent,
                Payload = new
                {
                    ChallengeId = challenge.ChallengeId,
                    Challenge = challenge.Challenge,
                    ExpiresAtUtc = challenge.ExpiresAtUtc,
                },
                TimestampUtc = DateTimeOffset.UtcNow,
            }),
            cancellationToken).ConfigureAwait(false);

        var message = await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
        if (message is null)
        {
            return false;
        }

        var decoded = ProtocolCodec.DecodeRequest(message);
        if (!decoded.IsSuccess)
        {
            await SendAsync(
                ProtocolCodec.Encode(ResponseEnvelope.Fail(decoded.Id ?? string.Empty, null, decoded.Error)),
                cancellationToken).ConfigureAwait(false);
            await CloseAsync(WebSocketCloseStatus.ProtocolError, "malformed handshake", CancellationToken.None)
                .ConfigureAwait(false);
            return false;
        }

        var request = decoded.Request;

        if (string.Equals(request.Action, RemoteProtocol.EnrollAction, StringComparison.Ordinal))
        {
            // Enrollment ends the connection either way. It is a bootstrap, not a session: the
            // phone leaves holding a credential, then connects again and authenticates with it like
            // every subsequent time. Returning false here means the request pump is never entered,
            // so an enrolled-but-unauthenticated socket cannot dispatch anything.
            await EnrollAsync(request, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!string.Equals(request.Action, RemoteProtocol.AuthenticateAction, StringComparison.Ordinal))
        {
            // Nothing but the handshake is reachable before authentication — the dispatcher is
            // never invoked for an unauthenticated connection.
            _logger.LogWarning(
                "Remote connection {ConnectionId} attempted {Action} before authenticating.",
                _connectionId,
                request.Action);

            await SendAsync(FailureResponse(request.Id, ErrorCodes.AuthenticationFailed), cancellationToken)
                .ConfigureAwait(false);
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "not authenticated", CancellationToken.None)
                .ConfigureAwait(false);
            return false;
        }

        if (!ProtocolCodec.TryBindParameters(request.Params, typeof(AuthenticateParameters), out var bound, out _)
            || bound is not AuthenticateParameters parameters)
        {
            await SendAsync(FailureResponse(request.Id, ErrorCodes.AuthenticationFailed), cancellationToken)
                .ConfigureAwait(false);
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "authentication failed", CancellationToken.None)
                .ConfigureAwait(false);
            return false;
        }

        AuthenticatedDevice device;
        try
        {
            // The shared authenticator from v0.3 — this transport implements no competing
            // authentication logic, so replay and expiry rules are the ones already tested there.
            device = _authenticator.Verify(
                parameters.ChallengeId ?? string.Empty,
                parameters.DeviceId ?? string.Empty,
                parameters.Proof ?? string.Empty);
        }
        catch (CapabilityException ex)
        {
            _logger.LogWarning(
                "Remote authentication failed on {ConnectionId} for device {DeviceId}.",
                _connectionId,
                parameters.DeviceId);

            await SendAsync(FailureResponse(request.Id, ex.Code), cancellationToken).ConfigureAwait(false);
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "authentication failed", CancellationToken.None)
                .ConfigureAwait(false);
            return false;
        }

        // The trusted identity is minted here, from the verification result, and nowhere else.
        _caller = device.ToCaller(RemoteTransport.TransportName, _connectionId);

        _session = new RemoteSession(_connectionId, _caller, device.AuthenticatedAtUtc)
        {
            CloseAsync = () => CloseAsync(WebSocketCloseStatus.NormalClosure, "superseded", CancellationToken.None),
        };

        var evicted = _sessions.Add(_session);

        // Subscribe before confirming, so no state change can slip through between the client
        // learning it is authenticated and the stream being live.
        _eventSubscription = events.Subscribe(OnEvent);

        // Only now can this connection carry frames, and only now may it ask for them.
        _streams.Register(_connectionId, this);

        await SendAsync(
            ProtocolCodec.Encode(ResponseEnvelope.Ok(request.Id, request.Action, new
            {
                Authenticated = true,
                ActorType = _caller.ActorType,
                DeviceId = _caller.DeviceId,
                SessionId = _session.SessionId,
                AuthenticatedAtUtc = device.AuthenticatedAtUtc,

                // Confirms which installation answered, so a client that stored a credential for
                // one PC notices immediately if it reached another.
                PcInstanceId = _identity.PcInstanceId,

                // Session bootstrap, not a capability: the phone needs somewhere to start browsing,
                // and the roots are the owner's own configured directories, which every files.list
                // response already contains. Handing them over once at session setup mirrors how
                // the local test page receives them, and avoids adding a capability whose only job
                // would be to enumerate the boundary. It widens nothing — every path sent back is
                // still authorised from scratch.
                AllowedRoots = _allowedRoots,
            })),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Remote device authenticated: {Caller} (session {SessionId}, {Sessions} active).",
            _caller,
            _session.SessionId,
            _sessions.Count);

        foreach (var old in evicted)
        {
            _ = old.CloseAsync();
        }

        return true;
    }

    /// <summary>
    /// Remote pairing enrollment: the one thing an unpaired phone may do here.
    /// <para>
    /// The response carries the permanent shared secret. That is the single moment it is ever
    /// transmitted, it happens inside the Tailscale tunnel, and it is never logged — here or
    /// anywhere else.
    /// </para>
    /// </summary>
    private async Task EnrollAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        if (!ProtocolCodec.TryBindParameters(request.Params, typeof(EnrollParameters), out var bound, out _)
            || bound is not EnrollParameters parameters)
        {
            await RefuseEnrollmentAsync(request, ErrorCodes.InvalidParameters, cancellationToken).ConfigureAwait(false);
            return;
        }

        PairingCompletion completion;
        try
        {
            completion = _enrollment.Enroll(
                parameters.ChallengeId ?? string.Empty,
                parameters.SessionId ?? string.Empty,
                parameters.Proof ?? string.Empty,
                parameters.DisplayName);
        }
        catch (CapabilityException ex)
        {
            await RefuseEnrollmentAsync(request, ex.Code, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendAsync(
            ProtocolCodec.Encode(ResponseEnvelope.Ok(request.Id, request.Action, new
            {
                completion.PcInstanceId,
                completion.DeviceId,
                completion.SharedSecret,
                completion.PairedAtUtc,
            })),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Remote connection {ConnectionId} enrolled device {DeviceId}; closing so it can authenticate.",
            _connectionId,
            completion.DeviceId);

        await CloseAsync(WebSocketCloseStatus.NormalClosure, "enrolled", CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RefuseEnrollmentAsync(RequestEnvelope request, string code, CancellationToken cancellationToken)
    {
        await SendAsync(
            ProtocolCodec.Encode(ResponseEnvelope.Fail(request.Id, request.Action, code, "Pairing failed.")),
            cancellationToken).ConfigureAwait(false);

        await CloseAsync(WebSocketCloseStatus.PolicyViolation, "pairing failed", CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Post-authentication request loop.</summary>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var pending = new List<Task>();

        try
        {
            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                if (message.Length == 0)
                {
                    continue;
                }

                pending.RemoveAll(static t => t.IsCompleted);
                pending.Add(HandleAsync(message, cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "Remote connection {ConnectionId} closed unexpectedly.", _connectionId);
        }
        finally
        {
            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "In-flight remote requests faulted while closing {ConnectionId}.", _connectionId);
            }
        }
    }

    private async Task HandleAsync(string json, CancellationToken cancellationToken)
    {
        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var decoded = ProtocolCodec.DecodeRequest(json);

            if (!decoded.IsSuccess)
            {
                await SendAsync(
                    ProtocolCodec.Encode(ResponseEnvelope.Fail(decoded.Id ?? string.Empty, null, decoded.Error)),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            // Re-authenticating on an established session is meaningless and would burn a
            // challenge. Enrolling on one is worse: a phone that already holds a credential must
            // never be able to mint another, or to re-enroll itself as the primary phone from the
            // tailnet. Neither is a capability, so neither can reach the dispatcher — but refusing
            // them explicitly says so rather than letting them read as an unknown action.
            if (string.Equals(decoded.Request.Action, RemoteProtocol.AuthenticateAction, StringComparison.Ordinal)
                || string.Equals(decoded.Request.Action, RemoteProtocol.EnrollAction, StringComparison.Ordinal))
            {
                await SendAsync(
                    ProtocolCodec.Encode(ResponseEnvelope.Fail(
                        decoded.Request.Id,
                        decoded.Request.Action,
                        ErrorCodes.InvalidRequest,
                        "This session is already authenticated.")),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            // Every request on this connection inherits the identity established at handshake.
            // The request body has no say in it.
            var response = await _dispatcher
                .DispatchAsync(decoded.Request, _caller!, cancellationToken)
                .ConfigureAwait(false);

            await SendAsync(ProtocolCodec.Encode(response), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle a remote message on {ConnectionId}.", _connectionId);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private void OnEvent(EventEnvelope envelope)
    {
        if (!RemoteProtocol.IsDeliverableToDevice(envelope.EventName))
        {
            return;
        }

        _ = SendAsync(ProtocolCodec.Encode(envelope), CancellationToken.None);
    }

    private async Task<string?> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferSize];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None)
                    .ConfigureAwait(false);
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                await CloseAsync(WebSocketCloseStatus.InvalidMessageType, "text frames only", CancellationToken.None)
                    .ConfigureAwait(false);
                return null;
            }

            if (message.Length + result.Count > 64 * 1024)
            {
                await CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", CancellationToken.None)
                    .ConfigureAwait(false);
                return null;
            }

            message.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }

    private async Task SendAsync(string payload, CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open)
        {
            return;
        }

        // Counted before waiting, so a frame deciding whether to send sees that a control message
        // is on its way.
        Interlocked.Increment(ref _controlPending);

        try
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            Interlocked.Decrement(ref _controlPending);
            return;
        }

        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                return;
            }

            await _socket.SendAsync(
                Encoding.UTF8.GetBytes(payload),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug("Send failed on remote connection {ConnectionId}; client has gone away.", _connectionId);
        }
        finally
        {
            Interlocked.Decrement(ref _controlPending);
            _sendGate.Release();
        }
    }

    // --- Live desktop frames ---------------------------------------------------------------------

    /// <summary>
    /// Starts pumping a live stream to this peer.
    /// <para>
    /// One task per stream, reading a channel that holds a single frame. There is no queue to grow
    /// and nothing to flush: if this task is busy sending when a newer frame is captured, the older
    /// one is discarded before it is ever offered here.
    /// </para>
    /// </summary>
    public void Attach(DesktopStreamSession session)
    {
        Detach();

        var pump = new CancellationTokenSource();
        _framePump = pump;

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in session.Frames.ReadAllAsync(pump.Token).ConfigureAwait(false))
                {
                    if (!await SendFrameAsync(frame, pump.Token).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The stream was stopped, or the connection ended.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Frame delivery ended on connection {ConnectionId}.", _connectionId);
            }
        });
    }

    public void Detach()
    {
        var pump = _framePump;
        _framePump = null;

        if (pump is not null)
        {
            pump.Cancel();
            pump.Dispose();
        }
    }

    /// <summary>Sends one binary frame, yielding first to any control message that is waiting.</summary>
    private async Task<bool> SendFrameAsync(byte[] message, CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open)
        {
            return false;
        }

        // Control traffic goes first. A short bounded wait is enough: responses are small and the
        // gate is held briefly, so under normal load this yields once at most. The bound matters
        // more than the delay - a frame must never wait indefinitely and stall the stream.
        for (var i = 0; i < 50 && Volatile.Read(ref _controlPending) > 0; i++)
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }

        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                return false;
            }

            await _socket.SendAsync(
                message,
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken cancellationToken)
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseAsync(status, description, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Peer already gone.
        }
    }

    private static string FailureResponse(string id, string code) =>
        ProtocolCodec.Encode(ResponseEnvelope.Fail(
            id,
            null,
            code,
            code == ErrorCodes.AuthenticationFailed ? "Authentication failed." : "Request refused."));

    public async ValueTask DisposeAsync()
    {
        // The session lives exactly as long as the connection. Nothing survives the socket - and in
        // v0.6 that matters far more than it used to, because what could survive is a held mouse
        // button, a latched Ctrl, a capture loop with no viewer, and an indicator still claiming a
        // session that ended. Every one of those is cleaned up here, on every path out, including
        // the ones nobody chose.
        Detach();
        _streams.Unregister(_connectionId);

        try
        {
            await _streams.StopForConnectionAsync(_connectionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stopping the stream for {ConnectionId} failed during cleanup.", _connectionId);
        }

        try
        {
            // Releases whatever this connection held down, then clears the input session so the
            // indicator stops reporting that control is active.
            _input.End(_connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ending remote input for {ConnectionId} failed during cleanup.", _connectionId);
        }

        _eventSubscription?.Dispose();

        if (_session is not null)
        {
            _sessions.Remove(_session.SessionId);
            _logger.LogInformation(
                "Remote session {SessionId} ended ({Sessions} active).", _session.SessionId, _sessions.Count);
        }

        _sendGate.Dispose();
        _concurrency.Dispose();
    }

    /// <summary>Handshake parameters. Bound through the shared codec like any other typed params.</summary>
    private sealed record AuthenticateParameters
    {
        public string? ChallengeId { get; init; }
        public string? DeviceId { get; init; }
        public string? Proof { get; init; }
    }

    /// <summary>
    /// Enrollment parameters. Note what is absent: there is no device id and no actor type here,
    /// because the caller does not get to name itself. The device id is minted by the PC when the
    /// pairing succeeds.
    /// </summary>
    private sealed record EnrollParameters
    {
        public string? ChallengeId { get; init; }
        public string? SessionId { get; init; }
        public string? Proof { get; init; }
        public string? DisplayName { get; init; }
    }
}
