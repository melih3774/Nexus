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
public sealed record PairingSessionInfo(
    string SessionId,
    DateTimeOffset ExpiresAtUtc,
    string PcInstanceId,
    string Payload);

/// <summary>Returned to the phone exactly once, on successful pairing.</summary>
public sealed record PairingCompletion(
    string PcInstanceId,
    string DeviceId,
    string SharedSecret,
    DateTimeOffset PairedAtUtc);

/// <summary>
/// The contents of the QR code / copyable pairing code.
/// <para>
/// Wire form: <c>NEXUS1.&lt;base64url(compact JSON)&gt;</c>. The prefix identifies the format and
/// its version so a scanner can reject anything else outright; base64url keeps it QR- and
/// clipboard-safe with no escaping surprises.
/// </para>
/// <para>
/// It carries only what pairing needs: the format version, which installation is offering to pair,
/// which session, and the one-time material proving the scanner was physically shown this code.
/// No hostname, user name, address, or hardware detail appears in it.
/// </para>
/// </summary>
public static class PairingPayload
{
    public const string Prefix = "NEXUS1.";

    public static string Encode(int version, string pcInstanceId, string sessionId, string pairingSecret)
    {
        // Short keys: the payload is scanned, not read.
        var json = $$"""{"v":{{version}},"pc":"{{pcInstanceId}}","s":"{{sessionId}}","k":"{{pairingSecret}}"}""";
        return Prefix + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Decodes a payload. Used by tests and by the development phone simulator.</summary>
    public static bool TryDecode(
        string? payload,
        out int version,
        out string pcInstanceId,
        out string sessionId,
        out string pairingSecret)
    {
        version = 0;
        pcInstanceId = string.Empty;
        sessionId = string.Empty;
        pairingSecret = string.Empty;

        if (string.IsNullOrWhiteSpace(payload) || !payload.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(payload.AsSpan(Prefix.Length)));
            using var document = global::System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;

            version = root.GetProperty("v").GetInt32();
            pcInstanceId = root.GetProperty("pc").GetString() ?? string.Empty;
            sessionId = root.GetProperty("s").GetString() ?? string.Empty;
            pairingSecret = root.GetProperty("k").GetString() ?? string.Empty;

            return pcInstanceId.Length > 0 && sessionId.Length > 0 && pairingSecret.Length > 0;
        }
        catch (Exception ex) when (ex is FormatException or global::System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }
}

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
    /// Completes pairing. Single-use: the session is consumed whatever the outcome, so a wrong
    /// guess cannot be retried against the same code.
    /// </summary>
    PairingCompletion Complete(string sessionId, string pairingSecret, string? displayName);
}

public sealed class PairingService : IPairingService
{
    private const int SessionIdBytes = 16;

    /// <summary>256 bits. High entropy so no rate limiting is load-bearing.</summary>
    private const int PairingSecretBytes = 32;

    private const int PayloadVersion = 1;

    private readonly IPcIdentityProvider _identity;
    private readonly IPrimaryPhoneRegistry _registry;
    private readonly PairingOptions _options;
    private readonly ILogger<PairingService> _logger;
    private readonly Lock _gate = new();

    private Session? _session;

    public PairingService(
        IPcIdentityProvider identity,
        IPrimaryPhoneRegistry registry,
        PairingOptions options,
        ILogger<PairingService> logger)
    {
        _identity = identity;
        _registry = registry;
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

            var info = new PairingSessionInfo(
                sessionId,
                expires,
                _identity.PcInstanceId,
                PairingPayload.Encode(PayloadVersion, _identity.PcInstanceId, sessionId, secret));

            _session = new Session(info, Encoding.UTF8.GetBytes(secret), replaceExisting);

            // The session id is safe to log; the pairing secret and payload are not.
            _logger.LogInformation(
                "Pairing session {SessionId} open until {Expires:u}.", sessionId, expires);

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

    public PairingCompletion Complete(string sessionId, string pairingSecret, string? displayName)
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

            var presented = Encoding.UTF8.GetBytes(pairingSecret ?? string.Empty);
            if (!CryptographicOperations.FixedTimeEquals(session.Secret, presented))
            {
                throw Failed($"incorrect pairing secret for session {sessionId}");
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
