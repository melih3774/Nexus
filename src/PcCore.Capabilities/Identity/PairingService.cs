using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Identity;

public sealed class PairingOptions
{
    public const string SectionName = "PcCore:Pairing";

    /// <summary>How long a pairing session stays usable. Five minutes is enough to walk to a phone.</summary>
    public int SessionTimeoutSeconds { get; set; } = 300;

    /// <summary>How long an authentication challenge stays usable.</summary>
    public int ChallengeTimeoutSeconds { get; set; } = 60;

    public string DefaultDisplayName { get; set; } = "Primary Phone";
}

/// <summary>What the owner sees after starting pairing. Contains no permanent credential.</summary>
/// <param name="Payload">
/// The <c>NEXUS2.…</c> code to show as a QR and offer for copying. This is what a phone scans.
/// </param>
/// <param name="PayloadV1">
/// The old <c>NEXUS1.…</c> code, kept for the loopback development simulator, which pairs over the
/// local device surface and needs no endpoint. Not shown to a phone.
/// </param>
/// <param name="RemoteEndpoint">
/// The address the code points at, or <see langword="null"/> when the remote transport has no
/// Tailscale address bound. Surfaced so the owner sees <em>why</em> a scan will fail, instead of
/// discovering it on the phone.
/// </param>
public sealed record PairingSessionInfo(
    string SessionId,
    DateTimeOffset ExpiresAtUtc,
    string PcInstanceId,
    string Payload,
    string PayloadV1,
    string? RemoteEndpoint);

/// <summary>Returned to the phone exactly once, on successful pairing.</summary>
public sealed record PairingCompletion(
    string PcInstanceId,
    string DeviceId,
    string SharedSecret,
    DateTimeOffset PairedAtUtc);

/// <summary>
/// Owner-initiated pairing of the one Primary Phone.
/// <para>
/// Completion is <b>not</b> a capability. A phone completing pairing is an untrusted party that
/// has not authenticated yet, so it must not arrive through the same dispatcher that serves the
/// owner's trusted local calls. The transport exposes <see cref="Complete"/> on a separate
/// device-facing surface.
/// </para>
/// </summary>
public interface IPairingService
{
    /// <summary>The session currently on offer, or null. At most one exists at a time.</summary>
    PairingSessionInfo? Active { get; }

    /// <summary>
    /// Starts a session. Throws <c>already_paired</c> if a phone is paired and
    /// <paramref name="replaceExisting"/> is false — replacing the owner's phone is never implicit.
    /// </summary>
    PairingSessionInfo Begin(bool replaceExisting);

    /// <summary>Cancels the active session. Returns false if there was none.</summary>
    bool Cancel();

    /// <summary>
    /// Completes pairing by presenting the pairing secret itself. Single-use: the session is
    /// consumed whatever the outcome, so a wrong guess cannot be retried against the same code.
    /// <para>
    /// Used by the loopback development surface, where the secret never leaves this machine.
    /// </para>
    /// </summary>
    PairingCompletion Complete(string sessionId, string pairingSecret, string? displayName);

    /// <summary>
    /// Completes pairing by presenting an HMAC over <paramref name="message"/> keyed by the pairing
    /// secret, so the secret itself is never transmitted.
    /// <para>
    /// This is what a phone uses over the remote transport. Consumption and the constant-time
    /// comparison are identical to <see cref="Complete"/>; only the proof differs. The secret stays
    /// inside this service — no caller can read it back, in either form.
    /// </para>
    /// </summary>
    PairingCompletion CompleteWithProof(string sessionId, byte[] message, byte[] proof, string? displayName);
}

public sealed class PairingService : IPairingService
{
    private const int SessionIdBytes = 16;

    /// <summary>256 bits. High entropy so no rate limiting is load-bearing.</summary>
    private const int PairingSecretBytes = 32;

    private readonly IPcIdentityProvider _identity;
    private readonly IPrimaryPhoneRegistry _registry;
    private readonly IDeviceEndpointProvider _endpoint;
    private readonly PairingOptions _options;
    private readonly ILogger<PairingService> _logger;
    private readonly Lock _gate = new();

    private Session? _session;

    public PairingService(
        IPcIdentityProvider identity,
        IPrimaryPhoneRegistry registry,
        IDeviceEndpointProvider endpoint,
        PairingOptions options,
        ILogger<PairingService> logger)
    {
        _identity = identity;
        _registry = registry;
        _endpoint = endpoint;
        _options = options;
        _logger = logger;
    }

    public PairingSessionInfo? Active
    {
        get
        {
            lock (_gate)
            {
                return _session is not null && !IsExpired(_session) ? _session.Info : null;
            }
        }
    }

    public PairingSessionInfo Begin(bool replaceExisting)
    {
        lock (_gate)
        {
            if (_registry.Current is not null && !replaceExisting)
            {
                // A phone is already paired. Replacing it must be a decision the owner states
                // explicitly, so a stray pairing attempt can never silently take over the system.
                throw new CapabilityException(
                    ErrorCodes.AlreadyPaired,
                    "A primary phone is already paired. Unpair it first, or pass replace_existing.");
            }

            var sessionId = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SessionIdBytes));
            var secretBytes = RandomNumberGenerator.GetBytes(PairingSecretBytes);
            var secret = Base64Url.EncodeToString(secretBytes);
            var expires = DateTimeOffset.UtcNow.AddSeconds(_options.SessionTimeoutSeconds);

            // Starting a new session invalidates any previous one: only one code is ever live.
            if (_session is not null)
            {
                _logger.LogInformation("Replacing pairing session {SessionId}.", _session.Info.SessionId);
            }

            // Read once: the code must describe the endpoint as it was when the code was made,
            // and a code that promises an address the transport is not actually serving would be
            // worse than one that promises none.
            var endpoint = _endpoint.Current;

            var info = new PairingSessionInfo(
                sessionId,
                expires,
                _identity.PcInstanceId,
                new PairingPayloadV2
                {
                    PcInstanceId = _identity.PcInstanceId,
                    SessionId = sessionId,
                    PairingSecret = secret,
                    Host = endpoint?.Host,
                    Port = endpoint?.Port ?? 0,
                }.Encode(),
                PairingPayload.Encode(PairingPayload.Version, _identity.PcInstanceId, sessionId, secret),
                endpoint?.ToString());

            _session = new Session(info, Encoding.UTF8.GetBytes(secret), replaceExisting);

            // The session id is safe to log; the pairing secret and payload are not.
            _logger.LogInformation(
                "Pairing session {SessionId} open until {Expires:u} (endpoint {Endpoint}).",
                sessionId,
                expires,
                endpoint?.ToString() ?? "none — no Tailscale address bound");

            return info;
        }
    }

    public bool Cancel()
    {
        lock (_gate)
        {
            if (_session is null)
            {
                return false;
            }

            _logger.LogInformation("Cancelled pairing session {SessionId}.", _session.Info.SessionId);
            _session = null;
            return true;
        }
    }

    public PairingCompletion Complete(string sessionId, string pairingSecret, string? displayName) =>
        CompleteCore(
            sessionId,
            secret => CryptographicOperations.FixedTimeEquals(secret, Encoding.UTF8.GetBytes(pairingSecret ?? string.Empty)),
            displayName);

    public PairingCompletion CompleteWithProof(string sessionId, byte[] message, byte[] proof, string? displayName) =>
        CompleteCore(
            sessionId,
            secret => CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(secret, message ?? []), proof ?? []),
            displayName);

    /// <param name="verify">
    /// Given the session's pairing secret, decides whether the caller proved possession of it. The
    /// secret is handed to this callback and never returned: both proof forms are checked in
    /// constant time, and neither leaves the lock.
    /// </param>
    private PairingCompletion CompleteCore(string sessionId, Func<byte[], bool> verify, string? displayName)
    {
        lock (_gate)
        {
            var session = _session;

            if (session is null)
            {
                throw Failed("no active pairing session");
            }

            if (IsExpired(session))
            {
                _session = null;
                throw Failed($"session {session.Info.SessionId} expired");
            }

            // Consume first. Whether the secret turns out to be right or wrong, this code is spent:
            // that is what makes a pairing session single-use and un-guessable by repetition, and
            // it is also what makes concurrent completions resolve to exactly one winner.
            _session = null;

            if (!string.Equals(session.Info.SessionId, sessionId, StringComparison.Ordinal))
            {
                throw Failed("session id mismatch");
            }

            if (!verify(session.Secret))
            {
                throw Failed($"incorrect pairing proof for session {sessionId}");
            }

            if (_registry.Current is not null && !session.ReplaceExisting)
            {
                // Belt and braces: a phone could have been paired between Begin and Complete.
                throw Failed("a primary phone was paired after this session started");
            }

            var name = string.IsNullOrWhiteSpace(displayName) ? _options.DefaultDisplayName : displayName.Trim();
            var credential = _registry.Pair(name);

            return new PairingCompletion(
                _identity.PcInstanceId,
                credential.Phone.DeviceId,
                credential.SharedSecret,
                credential.Phone.PairedAtUtc);
        }
    }

    private CapabilityException Failed(string internalReason)
    {
        // The caller is told only that pairing failed. Distinguishing "wrong secret" from "expired"
        // from "no session" would let an unpaired party map the state of the pairing machine.
        _logger.LogWarning("Pairing completion refused: {Reason}.", internalReason);
        return new CapabilityException(ErrorCodes.PairingFailed, "Pairing failed.");
    }

    private static bool IsExpired(Session session) => DateTimeOffset.UtcNow >= session.Info.ExpiresAtUtc;

    private sealed record Session(PairingSessionInfo Info, byte[] Secret, bool ReplaceExisting);
}
