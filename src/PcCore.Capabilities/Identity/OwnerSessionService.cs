using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Identity;

public sealed class OwnerSessionOptions
{
    public const string SectionName = "PcCore:OwnerSession";

    /// <summary>
    /// How long an authorised session survives without being used.
    /// <para>
    /// Fifteen minutes, and it is a sliding window rather than a countdown: any request the owner
    /// makes refreshes it. What it actually measures is "the phone stopped talking to the PC", which
    /// is what backgrounding Nexus, walking away, or locking the phone all look like from here. The
    /// phone enforces the same interval for itself; neither side trusts the other to do it.
    /// </para>
    /// </summary>
    public int IdleMinutes { get; set; } = 15;

    /// <summary>
    /// A ceiling no amount of activity extends. A session left running all day is still a session
    /// nobody re-proved they were holding.
    /// </summary>
    public int MaximumHours { get; set; } = 12;

    /// <summary>How long a signing challenge stays valid. Long enough for a slow fingerprint, no longer.</summary>
    public int ChallengeSeconds { get; set; } = 90;
}

/// <summary>What a challenge round asks the phone to sign.</summary>
public sealed record OwnerSessionChallenge(string ChallengeId, string Challenge, DateTimeOffset ExpiresAtUtc);

/// <summary>The result of proving owner presence.</summary>
public sealed record OwnerSessionGrant(
    string Ticket,
    DateTimeOffset GrantedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int IdleMinutes);

/// <summary>What <c>session.status</c> reports.</summary>
public sealed record OwnerSessionState(
    bool Authorized,
    bool OwnerKeyRegistered,
    DateTimeOffset? GrantedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string Reason);

/// <summary>
/// Owner presence, as a property of a live connection.
/// <para>
/// <b>The distinction this exists to make.</b> Every remote connection is already authenticated as
/// the Primary Phone before it reaches a capability, and that has always been a statement about a
/// stored credential — which means it is a statement about a <em>phone</em>, not about a person. A
/// phone can be taken. This adds the second question: is the owner holding it right now?
/// </para>
/// <para>
/// <b>Why a signature rather than a flag.</b> The phone could simply say "the owner authenticated";
/// nothing about that claim is checkable, and a client that can send input protocol messages could
/// send that one too. Instead the phone signs a challenge with a key it generated inside the Android
/// Keystore under <c>setUserAuthenticationRequired</c>. The private half cannot be exported and the
/// platform refuses to use it until a system biometric or device-credential prompt has been
/// satisfied — so a valid signature is evidence the prompt happened, produced by the part of the
/// system that actually saw it. Nexus never sees a fingerprint, and there is nothing here it could
/// see one with.
/// </para>
/// <para>
/// <b>Why a ticket afterwards.</b> A phone on mobile data reconnects constantly, and demanding a
/// fingerprint per socket would make the thing unusable and train the owner to tap past prompts. The
/// grant therefore issues a random ticket the phone keeps <em>in memory only</em>. Reconnecting
/// redeems it; process death, phone reboot, the app locking itself, or fifteen idle minutes lose it,
/// and the prompt comes back. The ticket is state on both sides and a secret on neither disk.
/// </para>
/// </summary>
public interface IOwnerSessionService : IRemoteAuthorityProvider
{
    /// <summary>A nonce for the phone to sign. Bound to this connection and this device.</summary>
    OwnerSessionChallenge IssueChallenge(string connectionId, string deviceId);

    /// <summary>Verifies the signature and, if it holds, makes this connection a Full Remote Session.</summary>
    OwnerSessionGrant Authorize(string connectionId, string deviceId, string challengeId, string signature);

    /// <summary>Re-establishes authority on a new connection from a ticket the phone already holds.</summary>
    OwnerSessionGrant Resume(string connectionId, string deviceId, string ticket);

    /// <summary>
    /// Drops the authorisation and the ticket. Called when the phone locks itself — screen off,
    /// backgrounded too long, or the owner asked.
    /// </summary>
    void Lock(string deviceId, string reason);

    /// <summary>Forgets a connection. Called when the socket ends; never revokes the ticket.</summary>
    void Forget(string connectionId);

    OwnerSessionState Describe(string connectionId, string? deviceId);

    /// <summary>Records the phone's public key on first use. Idempotent for the same key.</summary>
    bool RegisterOwnerKey(string deviceId, string publicKeySpki);

    /// <summary>
    /// True while any connection holds Full Remote authority.
    /// <para>
    /// Asked by the parts of PC Core that are not serving a request and so have no caller to ask
    /// about — the capture loop above all, which has to decide whether it may photograph the Secure
    /// Desktop while nobody is calling anything. There is one owner and one phone, so "somebody is
    /// authorised" and "the owner is authorised" are the same fact here.
    /// </para>
    /// </summary>
    bool AnyFullRemote { get; }
}

public sealed class OwnerSessionService : IOwnerSessionService
{
    private const string ChallengeContext = "nexus.owner-session.v1";
    private const int ChallengeBytes = 32;
    private const int TicketBytes = 32;

    private readonly IPrimaryPhoneRegistry _phones;
    private readonly IPcIdentityProvider _identity;
    private readonly OwnerSessionOptions _options;
    private readonly ILogger<OwnerSessionService> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private readonly Dictionary<string, PendingChallenge> _challenges = new(StringComparer.Ordinal);

    /// <summary>Connections that have proved owner presence, and the device each one proved it for.</summary>
    private readonly Dictionary<string, string> _grants = new(StringComparer.Ordinal);

    private Ticket? _ticket;

    public OwnerSessionService(
        IPrimaryPhoneRegistry phones,
        IPcIdentityProvider identity,
        OwnerSessionOptions options,
        ILogger<OwnerSessionService> logger,
        TimeProvider? time = null)
    {
        _phones = phones;
        _identity = identity;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public RemoteAuthority AuthorityOf(CallerDescriptor caller)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (caller.ActorType != ActorType.Device || caller.DeviceId is null)
        {
            return RemoteAuthority.None;
        }

        lock (_gate)
        {
            if (!_grants.TryGetValue(caller.ConnectionId, out var deviceId)
                || !string.Equals(deviceId, caller.DeviceId, StringComparison.Ordinal))
            {
                return RemoteAuthority.None;
            }

            var now = _time.GetUtcNow();

            if (_ticket is null || !_ticket.IsAlive(now, _options) || _ticket.DeviceId != deviceId)
            {
                // The window closed, or the phone locked itself. Drop the grant so the next call
                // takes the same path rather than re-deciding it.
                _grants.Remove(caller.ConnectionId);
                return RemoteAuthority.None;
            }

            // Any authorised request is activity. That is what makes the window slide, and it is why
            // "the phone went quiet" and "the session expired" are the same event.
            _ticket.LastUsedUtc = now;
            return RemoteAuthority.FullRemote;
        }
    }

    public bool AnyFullRemote
    {
        get
        {
            lock (_gate)
            {
                return _grants.Count > 0 && _ticket is not null && _ticket.IsAlive(_time.GetUtcNow(), _options);
            }
        }
    }

    public OwnerSessionChallenge IssueChallenge(string connectionId, string deviceId)
    {
        var phone = RequirePhone(deviceId);

        if (!phone.HasOwnerKey)
        {
            throw new CapabilityException(
                ErrorCodes.AuthenticationFailed,
                "This phone has not registered an owner-authentication key yet.");
        }

        var now = _time.GetUtcNow();
        var challenge = new PendingChallenge(
            Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)),
            RandomNumberGenerator.GetBytes(ChallengeBytes),
            now.AddSeconds(Math.Clamp(_options.ChallengeSeconds, 15, 600)),
            deviceId);

        lock (_gate)
        {
            PruneChallenges(now);
            _challenges[connectionId] = challenge;
        }

        return new OwnerSessionChallenge(
            challenge.ChallengeId,
            Base64Url.EncodeToString(challenge.Bytes),
            challenge.ExpiresAtUtc);
    }

    public OwnerSessionGrant Authorize(string connectionId, string deviceId, string challengeId, string signature)
    {
        var phone = RequirePhone(deviceId);
        var now = _time.GetUtcNow();

        PendingChallenge pending;

        lock (_gate)
        {
            // Taken, not read: a challenge is single-use whether it verifies or not, so a wrong
            // signature costs the caller a round trip rather than giving them unlimited attempts
            // against one nonce.
            if (!_challenges.Remove(connectionId, out var found))
            {
                throw new CapabilityException(ErrorCodes.AuthenticationFailed, "No challenge is outstanding.");
            }

            pending = found;
        }

        if (!string.Equals(pending.ChallengeId, challengeId, StringComparison.Ordinal)
            || !string.Equals(pending.DeviceId, deviceId, StringComparison.Ordinal)
            || pending.ExpiresAtUtc <= now)
        {
            throw new CapabilityException(ErrorCodes.AuthenticationFailed, "That challenge cannot be used.");
        }

        if (!Verify(phone.OwnerPublicKey!, deviceId, pending.Bytes, signature))
        {
            _logger.LogWarning(
                "Owner authorisation failed for phone {DeviceId} on connection {ConnectionId}.",
                deviceId,
                connectionId);

            throw new CapabilityException(ErrorCodes.AuthenticationFailed, "Owner authentication failed.");
        }

        lock (_gate)
        {
            _ticket = new Ticket(deviceId, RandomNumberGenerator.GetBytes(TicketBytes), now) { LastUsedUtc = now };
            _grants[connectionId] = deviceId;

            _logger.LogInformation(
                "Full Remote Session opened for phone {DeviceId} on connection {ConnectionId}.",
                deviceId,
                connectionId);

            return Grant(_ticket, now);
        }
    }

    public OwnerSessionGrant Resume(string connectionId, string deviceId, string ticket)
    {
        RequirePhone(deviceId);

        var now = _time.GetUtcNow();
        byte[] presented;

        try
        {
            presented = Base64Url.DecodeFromChars(ticket);
        }
        catch (FormatException)
        {
            throw new CapabilityException(ErrorCodes.AuthenticationFailed, "That session ticket cannot be used.");
        }

        lock (_gate)
        {
            if (_ticket is null
                || !_ticket.IsAlive(now, _options)
                || !string.Equals(_ticket.DeviceId, deviceId, StringComparison.Ordinal)
                || !CryptographicOperations.FixedTimeEquals(_ticket.Secret, presented))
            {
                throw new CapabilityException(
                    ErrorCodes.AuthenticationFailed, "That session ticket is no longer valid.");
            }

            _ticket.LastUsedUtc = now;
            _grants[connectionId] = deviceId;

            _logger.LogInformation(
                "Full Remote Session resumed for phone {DeviceId} on connection {ConnectionId}.",
                deviceId,
                connectionId);

            return Grant(_ticket, now);
        }
    }

    public void Lock(string deviceId, string reason)
    {
        lock (_gate)
        {
            if (_ticket is not null && string.Equals(_ticket.DeviceId, deviceId, StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(_ticket.Secret);
                _ticket = null;
            }

            var affected = _grants.Where(g => g.Value == deviceId).Select(g => g.Key).ToArray();

            foreach (var connection in affected)
            {
                _grants.Remove(connection);
            }

            _logger.LogInformation(
                "Full Remote authority withdrawn for phone {DeviceId} ({Reason}); {Count} connection(s) affected.",
                deviceId,
                reason,
                affected.Length);
        }
    }

    public void Forget(string connectionId)
    {
        lock (_gate)
        {
            // The connection goes; the ticket stays. A dropped socket is the ordinary condition on a
            // phone link and must not cost the owner a fingerprint.
            _grants.Remove(connectionId);
            _challenges.Remove(connectionId);
        }
    }

    public OwnerSessionState Describe(string connectionId, string? deviceId)
    {
        var phone = _phones.Current;
        var registered = phone is not null
            && phone.HasOwnerKey
            && (deviceId is null || string.Equals(phone.DeviceId, deviceId, StringComparison.Ordinal));

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var granted = _grants.TryGetValue(connectionId, out var grantedDevice)
                && _ticket is not null
                && _ticket.IsAlive(now, _options)
                && _ticket.DeviceId == grantedDevice;

            if (granted)
            {
                return new OwnerSessionState(
                    true,
                    registered,
                    _ticket!.IssuedAtUtc,
                    Expiry(_ticket, _options),
                    "the owner authenticated on this phone");
            }

            return new OwnerSessionState(
                false,
                registered,
                null,
                null,
                registered
                    ? "the owner has not authenticated on this phone"
                    : "this phone has not registered an owner-authentication key");
        }
    }

    public bool RegisterOwnerKey(string deviceId, string publicKeySpki)
    {
        if (!TryImport(publicKeySpki, out _))
        {
            throw new CapabilityException(
                ErrorCodes.InvalidParameters, "That is not a usable P-256 public key.");
        }

        return _phones.RegisterOwnerKey(deviceId, publicKeySpki);
    }

    /// <summary>
    /// What the phone signs.
    /// <para>
    /// The PC instance id and the device id are in the message, so a signature produced for one PC
    /// cannot be replayed at another and a signature is bound to the identity that authenticated.
    /// </para>
    /// </summary>
    internal static byte[] SigningMessage(string pcInstanceId, string deviceId, ReadOnlySpan<byte> challenge)
    {
        var prefix = Encoding.UTF8.GetBytes($"{ChallengeContext}\n{pcInstanceId}\n{deviceId}\n");
        var message = new byte[prefix.Length + challenge.Length];

        prefix.CopyTo(message, 0);
        challenge.CopyTo(message.AsSpan(prefix.Length));

        return message;
    }

    private bool Verify(string publicKeySpki, string deviceId, byte[] challenge, string signature)
    {
        if (!TryImport(publicKeySpki, out var key))
        {
            return false;
        }

        using (key)
        {
            byte[] bytes;

            try
            {
                bytes = Convert.FromBase64String(signature);
            }
            catch (FormatException)
            {
                return false;
            }

            // DER, because that is what Java's SHA256withECDSA produces and converting on the phone
            // would be one more place for the two sides to disagree.
            return key.VerifyData(
                SigningMessage(_identity.PcInstanceId, deviceId, challenge),
                bytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
    }

    private static bool TryImport(string publicKeySpki, out ECDsa key)
    {
        key = ECDsa.Create();

        try
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);

            if (key.KeySize != 256)
            {
                key.Dispose();
                key = null!;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            key.Dispose();
            key = null!;
            return false;
        }
    }

    private PrimaryPhone RequirePhone(string deviceId)
    {
        var phone = _phones.Current;

        if (phone is null || !string.Equals(phone.DeviceId, deviceId, StringComparison.Ordinal))
        {
            throw new CapabilityException(ErrorCodes.AuthenticationFailed, "Owner authentication failed.");
        }

        return phone;
    }

    private OwnerSessionGrant Grant(Ticket ticket, DateTimeOffset now) => new(
        Base64Url.EncodeToString(ticket.Secret),
        ticket.IssuedAtUtc,
        Expiry(ticket, _options),
        Math.Clamp(_options.IdleMinutes, 1, 720));

    private static DateTimeOffset Expiry(Ticket ticket, OwnerSessionOptions options)
    {
        var idle = ticket.LastUsedUtc.AddMinutes(Math.Clamp(options.IdleMinutes, 1, 720));
        var absolute = ticket.IssuedAtUtc.AddHours(Math.Clamp(options.MaximumHours, 1, 168));

        return idle < absolute ? idle : absolute;
    }

    private void PruneChallenges(DateTimeOffset now)
    {
        foreach (var stale in _challenges.Where(c => c.Value.ExpiresAtUtc <= now).Select(c => c.Key).ToArray())
        {
            _challenges.Remove(stale);
        }
    }

    private sealed record PendingChallenge(
        string ChallengeId,
        byte[] Bytes,
        DateTimeOffset ExpiresAtUtc,
        string DeviceId);

    private sealed class Ticket
    {
        public Ticket(string deviceId, byte[] secret, DateTimeOffset issuedAtUtc)
        {
            DeviceId = deviceId;
            Secret = secret;
            IssuedAtUtc = issuedAtUtc;
            LastUsedUtc = issuedAtUtc;
        }

        public string DeviceId { get; }

        public byte[] Secret { get; }

        public DateTimeOffset IssuedAtUtc { get; }

        public DateTimeOffset LastUsedUtc { get; set; }

        public bool IsAlive(DateTimeOffset now, OwnerSessionOptions options) => now < Expiry(this, options);
    }
}
