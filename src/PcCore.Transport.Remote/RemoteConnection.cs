using System.Buffers.Text;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Identity;
using PcCore.Capabilities.Input;
using PcCore.Capabilities.Media;
using PcCore.Capabilities.Screen;
using PcCore.Capabilities.Update;
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
    /// An allowlist rather than a filter expression: anything added later should have to be named
    /// here deliberately rather than reach the network because it happened to be published.
    /// </para>
    /// <para>
    /// Security state is deliverable by name, to a phone whose owner has not authenticated too, because
    /// a locked Nexus must still be able to say the PC is armed. PC Assistant's events are not: they
    /// carry the owner's conversations, so they go through <see cref="IEventAudience"/>, which asks
    /// whether this connection subscribed and holds the owner's session at the moment of sending.
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

    /// <summary>The largest message accepted before the peer has authenticated. The handshake is a few hundred bytes.</summary>
    internal const int MaxHandshakeMessageBytes = 64 * 1024;

    /// <summary>
    /// The largest message accepted from an authenticated device.
    /// <para>
    /// Larger than the handshake's limit for one reason: an upload chunk is a megabyte of file, which is
    /// about 1.4 MB of base64. Only a peer that has already proved it holds the Primary Phone's secret
    /// gets it, at most <see cref="MaxConcurrentRequests"/> at a time, so what it bounds is a few tens
    /// of megabytes from a caller that could already do far more with a screen and a keyboard.
    /// </para>
    /// </summary>
    internal const int MaxAuthenticatedMessageBytes = 2 * 1024 * 1024;

    private readonly WebSocket _socket;
    private readonly ICapabilityDispatcher _dispatcher;
    private readonly IDeviceAuthenticator _authenticator;
    private readonly IRemoteEnrollmentService _enrollment;
    private readonly IPrimaryPhoneRegistry _registry;
    private readonly IPcIdentityProvider _identity;
    private readonly IReadOnlyList<string> _allowedRoots;
    private readonly RemoteSessionRegistry _sessions;
    private readonly RemoteStreamCoordinator _streams;

    /// <summary>One pump per live sensor, so the camera and the microphone are independent.</summary>
    private readonly Dictionary<LiveSensorKind, CancellationTokenSource> _sensorPumps = [];

    private readonly Lock _sensorGate = new();
    private readonly IInputService _input;
    private readonly IOwnerSessionService _ownerSessions;
    private readonly RemoteTransportOptions _options;
    private readonly ILogger _logger;
    private readonly string _connectionId;

    /// <summary>Decides events the name allowlist cannot, such as PC Assistant's. Null delivers nothing extra.</summary>
    private readonly IEventAudience? _audience;

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
        IOwnerSessionService ownerSessions,
        RemoteTransportOptions options,
        ILogger logger,
        IEventAudience? audience = null)
    {
        _socket = socket;
        _audience = audience;
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
        _ownerSessions = ownerSessions;
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
        catch (OperationCanceledException)
        {
            // The phone hung up, or PC Core is stopping, before the handshake finished.
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

        var message = await ReceiveMessageAsync(MaxHandshakeMessageBytes, cancellationToken).ConfigureAwait(false);
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
            // Debug, not warning: the authenticator has already reported this at warning level with
            // the reason and with repeats collapsed. A second warning per attempt would restore
            // exactly the flood that one is there to prevent, and adds only the connection id.
            _logger.LogDebug(
                "Remote authentication failed on {ConnectionId} for device {DeviceId}.",
                _connectionId,
                parameters.DeviceId);

            // The one thing the authenticator cannot see, because it is not part of verification:
            // what the caller thinks it is signing for. Reported only when it disagrees, and only
            // as the caller's own claim — this is an unauthenticated peer's assertion, not a fact.
            if (!string.IsNullOrEmpty(parameters.PcInstanceId)
                && !string.Equals(parameters.PcInstanceId, _identity.PcInstanceId, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "That caller says it is signing for installation {ClaimedInstance}, but this is "
                    + "installation {ActualInstance}. Its stored pairing is for a different PC Core, "
                    + "so no proof it makes can verify here.",
                    parameters.PcInstanceId,
                    _identity.PcInstanceId);
            }

            await SendAsync(FailureResponse(request.Id, ex.Code), cancellationToken).ConfigureAwait(false);
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "authentication failed", CancellationToken.None)
                .ConfigureAwait(false);
            return false;
        }

        // Too old for this PC Core? Decided once, here, from what the app reported. A phone that
        // reports nothing predates the update path and is treated as the last build before it.
        var requiresUpdate = ClientCompatibility.RequiresUpdate(parameters.ClientVersionCode);

        // The trusted identity is minted here, from the verification result, and nowhere else.
        _caller = device.ToCaller(RemoteTransport.TransportName, _connectionId) with
        {
            ClientVersionCode = parameters.ClientVersionCode,
            RequiresUpdate = requiresUpdate,
        };

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

                // What the phone must do next. Authenticating as the Primary Phone is not the same
                // as the owner being present, and the reply says so rather than leaving the client
                // to infer it: until session.authorize or session.resume succeeds, this connection
                // may read two status fields and nothing else.
                OwnerAuthorizationRequired = true,
                OwnerKeyRegistered = _registry.Current?.HasOwnerKey ?? false,

                // Said outright rather than left for the phone to discover one refusal at a time:
                // this app is too old, and update.check / update.apk.get are what it may still do.
                UpdateRequired = requiresUpdate,
                MinimumClientVersionCode = ClientCompatibility.MinimumAndroidVersionCode,
            })),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Remote device authenticated: {Caller} (session {SessionId}, {Sessions} active; app {AppVersion}).",
            _caller,
            _session.SessionId,
            _sessions.Count,
            parameters.ClientVersionCode is { } code ? $"{parameters.ClientVersionName ?? "?"} ({code})" : "did not report a version");

        if (requiresUpdate)
        {
            _logger.LogWarning(
                "That phone's app is older than this PC Core serves (minimum version code {Minimum}); "
                + "it may only check for and download the update.",
                ClientCompatibility.MinimumAndroidVersionCode);
        }

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
                var message = await ReceiveMessageAsync(MaxAuthenticatedMessageBytes, cancellationToken).ConfigureAwait(false);
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
        // The name allowlist first, unchanged. Anything else needs the audience to say this connection,
        // as it stands at this moment, may have it.
        if (!RemoteProtocol.IsDeliverableToDevice(envelope.EventName)
            && (_audience is null || _caller is null || !_audience.MayReceive(envelope, _caller)))
        {
            return;
        }

        _ = SendAsync(ProtocolCodec.Encode(envelope), CancellationToken.None);
    }

    private async Task<string?> ReceiveMessageAsync(int maxBytes, CancellationToken cancellationToken)
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

            if (message.Length + result.Count > maxBytes)
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
    /// One task per stream, reading a short channel. Each frame that reaches the socket is reported
    /// back to the session, and that report is the whole feedback loop: the capture side counts what
    /// is still outstanding and stops capturing when too much is, which is how latency is kept down
    /// now that frames cannot simply be discarded. Without the report the queue looks permanently
    /// full and the stream throttles itself to nothing.
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

                    session.ReportSent();
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

    /// <summary>
    /// Starts pumping a live camera or microphone stream to this peer.
    /// <para>
    /// A second pump alongside the desktop one rather than replacing it, so the owner can watch the
    /// screen and listen to the room at once. That is safe because every send goes through the same
    /// gate: <c>SendFrameAsync</c> serialises on <c>_sendGate</c> and yields to control traffic
    /// first, so two producers interleave whole messages instead of corrupting one.
    /// </para>
    /// <para>
    /// Keyed by sensor, so starting the microphone does not cancel the camera.
    /// </para>
    /// </summary>
    public void AttachSensor(LiveSensorSession session)
    {
        DetachSensor(session.Kind);

        var pump = new CancellationTokenSource();

        lock (_sensorGate)
        {
            _sensorPumps[session.Kind] = pump;
        }

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

                    session.ReportSent();
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped, or the connection ended.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex, "Live {Sensor} delivery ended on connection {ConnectionId}.", session.Kind, _connectionId);
            }
        });
    }

    public void DetachSensor(LiveSensorKind kind)
    {
        CancellationTokenSource? pump;

        lock (_sensorGate)
        {
            if (!_sensorPumps.Remove(kind, out pump))
            {
                return;
            }
        }

        pump.Cancel();
        pump.Dispose();
    }

    private void DetachAllSensors()
    {
        CancellationTokenSource[] pumps;

        lock (_sensorGate)
        {
            pumps = [.. _sensorPumps.Values];
            _sensorPumps.Clear();
        }

        foreach (var pump in pumps)
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

        // Control traffic goes first, because the control traffic is the pointer: an input event
        // queued behind a video frame is a cursor that lags the finger. The gate already hands out
        // turns in arrival order, so a control message that is waiting is ahead of this frame; the
        // one gap is a message that has been counted but not yet reached the gate, and a single
        // yield closes it.
        //
        // Not a timed wait. This used to poll with one-millisecond delays, and on Windows a
        // one-millisecond delay is a timer tick — about fifteen. While the pointer was moving there
        // was nearly always a reply pending, so every frame waited a tick or more: moving the mouse
        // was what made the screen stutter, and the resulting queue was read as a slow link.
        if (Volatile.Read(ref _controlPending) > 0)
        {
            await Task.Yield();
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

        // Live sensors stop with the connection that asked for them, unconditionally and on every
        // path out. A camera that outlived the session watching it would be the exact failure this
        // feature must not have.
        DetachAllSensors();
        _streams.StopLiveSensorsFor(_connectionId);

        _streams.Unregister(_connectionId);

        // The connection is forgotten; the owner's Full Remote authorisation is not. A phone link
        // drops constantly and must not cost a fingerprint each time - the ticket the phone holds is
        // what re-establishes authority, and it expires on its own.
        _ownerSessions.Forget(_connectionId);

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
        _audience?.ConnectionClosed(_connectionId);

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

        /// <summary>
        /// Which installation the caller believes it is signing for. <b>Diagnostic only.</b>
        /// <para>
        /// Never used to verify anything: the signed message is always built from this PC's own
        /// identity, because taking the caller's word for it would let anyone choose the message
        /// they are checked against. It exists because a phone whose stored installation id has
        /// drifted produces a proof that fails for a reason indistinguishable from a wrong secret,
        /// and the two have completely different causes. Absent from older clients, which is fine —
        /// it changes nothing about whether they authenticate.
        /// </para>
        /// </summary>
        public string? PcInstanceId { get; init; }

        /// <summary>The app's Android versionCode. Absent from builds before the update path existed.</summary>
        public int? ClientVersionCode { get; init; }

        /// <summary>The app's versionName, for the log.</summary>
        public string? ClientVersionName { get; init; }
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
