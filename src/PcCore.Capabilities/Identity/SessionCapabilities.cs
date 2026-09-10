using Microsoft.Extensions.Logging;

using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Identity;

/// <summary>
/// The owner-authentication handshake, as capabilities.
/// <para>
/// Deliberately actions rather than transport-level messages, unlike device authentication. Device
/// authentication has to happen before the dispatcher exists for that connection, so it lives in the
/// transport. This does not: the connection is already authenticated, already policed, and already
/// logged. Making it ordinary capabilities means the same dispatcher, the same policy evaluation and
/// the same error envelope apply — and that the rule "these six are the only things a locked phone
/// may call" is a line in <see cref="DefaultCapabilityPolicy"/> rather than a special case inside a
/// socket loop.
/// </para>
/// </summary>
public sealed record OwnerSessionStatusResult
{
    public required bool Authorized { get; init; }

    public required bool OwnerKeyRegistered { get; init; }

    public DateTimeOffset? GrantedAtUtc { get; init; }

    public DateTimeOffset? ExpiresAtUtc { get; init; }

    public required string Reason { get; init; }
}

public sealed class GetSessionStatusAction : CapabilityAction<NoParameters, OwnerSessionStatusResult>
{
    private readonly IOwnerSessionService _sessions;

    public GetSessionStatusAction(IOwnerSessionService sessions) => _sessions = sessions;

    public override string Name => "session.status";

    public override string Description => "Whether this connection is an owner-authenticated Full Remote Session.";

    protected override ValueTask<OwnerSessionStatusResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var state = _sessions.Describe(context.Caller.ConnectionId, context.Caller.DeviceId);

        return ValueTask.FromResult(new OwnerSessionStatusResult
        {
            Authorized = state.Authorized,
            OwnerKeyRegistered = state.OwnerKeyRegistered,
            GrantedAtUtc = state.GrantedAtUtc,
            ExpiresAtUtc = state.ExpiresAtUtc,
            Reason = state.Reason,
        });
    }
}

public sealed record EnrollOwnerKeyParameters
{
    /// <summary>Base64 of an X.509 SubjectPublicKeyInfo for a P-256 key.</summary>
    public string? PublicKey { get; init; }
}

public sealed record EnrollOwnerKeyResult
{
    public required bool Registered { get; init; }

    public required string Reason { get; init; }
}

/// <summary>
/// Registers the phone's owner-authentication public key, once.
/// <para>
/// Refuses to replace a different key that is already registered, and that refusal is the point. A
/// phone in the wrong hands is an authenticated phone; if it could swap in a key of its own, the
/// biometric gate would be worth nothing. When the phone genuinely needs a new key — a reinstall, or
/// Android invalidating the old one because a new fingerprint was enrolled — the owner clears the
/// old one at the PC, which is a place a thief with the phone is not.
/// </para>
/// </summary>
public sealed class EnrollOwnerKeyAction : CapabilityAction<EnrollOwnerKeyParameters, EnrollOwnerKeyResult>
{
    private readonly IOwnerSessionService _sessions;

    public EnrollOwnerKeyAction(IOwnerSessionService sessions) => _sessions = sessions;

    public override string Name => "session.enroll_key";

    public override string Description => "Register this phone's owner-authentication public key.";

    protected override ValueTask<EnrollOwnerKeyResult> ExecuteAsync(
        EnrollOwnerKeyParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var deviceId = RequireDevice(context);

        if (string.IsNullOrWhiteSpace(parameters.PublicKey))
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, "A public key is required.");
        }

        var registered = _sessions.RegisterOwnerKey(deviceId, parameters.PublicKey.Trim());

        return ValueTask.FromResult(new EnrollOwnerKeyResult
        {
            Registered = registered,
            Reason = registered
                ? "this phone's owner-authentication key is registered"
                : "a different owner-authentication key is already registered; clear it at the PC first",
        });
    }

    internal static string RequireDevice(CapabilityContext context) =>
        context.Caller.DeviceId
        ?? throw new CapabilityException(
            ErrorCodes.Unsupported,
            "Owner authorisation applies to a paired phone; a local caller already has full control of this PC.");
}

public sealed record OwnerChallengeResult
{
    public required string ChallengeId { get; init; }

    public required string Challenge { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }
}

public sealed class GetSessionChallengeAction : CapabilityAction<NoParameters, OwnerChallengeResult>
{
    private readonly IOwnerSessionService _sessions;

    public GetSessionChallengeAction(IOwnerSessionService sessions) => _sessions = sessions;

    public override string Name => "session.challenge";

    public override string Description => "A nonce for the phone to sign with its owner-authentication key.";

    protected override ValueTask<OwnerChallengeResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var challenge = _sessions.IssueChallenge(
            context.Caller.ConnectionId, EnrollOwnerKeyAction.RequireDevice(context));

        return ValueTask.FromResult(new OwnerChallengeResult
        {
            ChallengeId = challenge.ChallengeId,
            Challenge = challenge.Challenge,
            ExpiresAtUtc = challenge.ExpiresAtUtc,
        });
    }
}

public sealed record AuthorizeSessionParameters
{
    public string? ChallengeId { get; init; }

    /// <summary>Base64 DER ECDSA signature over the challenge message.</summary>
    public string? Signature { get; init; }
}

public sealed record AuthorizeSessionResult
{
    public required bool Authorized { get; init; }

    /// <summary>
    /// The resume ticket. The phone keeps it in memory and never writes it down: losing it on
    /// process death is exactly the behaviour that makes a cold launch ask for a fingerprint.
    /// </summary>
    public required string Ticket { get; init; }

    public required DateTimeOffset GrantedAtUtc { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public required int IdleMinutes { get; init; }
}

public sealed class AuthorizeSessionAction : CapabilityAction<AuthorizeSessionParameters, AuthorizeSessionResult>
{
    private readonly IOwnerSessionService _sessions;

    public AuthorizeSessionAction(IOwnerSessionService sessions) => _sessions = sessions;

    public override string Name => "session.authorize";

    public override string Description => "Prove the owner authenticated on this phone, opening a Full Remote Session.";

    protected override ValueTask<AuthorizeSessionResult> ExecuteAsync(
        AuthorizeSessionParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var grant = _sessions.Authorize(
            context.Caller.ConnectionId,
            EnrollOwnerKeyAction.RequireDevice(context),
            parameters.ChallengeId ?? string.Empty,
            parameters.Signature ?? string.Empty);

        return ValueTask.FromResult(Describe(grant));
    }

    internal static AuthorizeSessionResult Describe(OwnerSessionGrant grant) => new()
    {
        Authorized = true,
        Ticket = grant.Ticket,
        GrantedAtUtc = grant.GrantedAtUtc,
        ExpiresAtUtc = grant.ExpiresAtUtc,
        IdleMinutes = grant.IdleMinutes,
    };
}

public sealed record ResumeSessionParameters
{
    public string? Ticket { get; init; }
}

/// <summary>
/// Re-establishes Full Remote authority on a reconnect, without another prompt.
/// <para>
/// A phone link drops for reasons that have nothing to do with the owner: a lift, a cell handover, a
/// screen going off for a moment. Making each of those cost a fingerprint would not be more secure,
/// it would be less — the reliable way to make someone stop reading a prompt is to show it to them
/// forty times a day.
/// </para>
/// </summary>
public sealed class ResumeSessionAction : CapabilityAction<ResumeSessionParameters, AuthorizeSessionResult>
{
    private readonly IOwnerSessionService _sessions;

    public ResumeSessionAction(IOwnerSessionService sessions) => _sessions = sessions;

    public override string Name => "session.resume";

    public override string Description => "Re-establish a Full Remote Session from a ticket issued earlier.";

    protected override ValueTask<AuthorizeSessionResult> ExecuteAsync(
        ResumeSessionParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var grant = _sessions.Resume(
            context.Caller.ConnectionId,
            EnrollOwnerKeyAction.RequireDevice(context),
            parameters.Ticket ?? string.Empty);

        return ValueTask.FromResult(AuthorizeSessionAction.Describe(grant));
    }
}

public sealed record LockSessionResult
{
    public required bool Locked { get; init; }
}

/// <summary>
/// The phone telling the PC it has locked itself. Screen off, backgrounded too long, or asked.
/// <para>
/// Not the only thing that ends a session — the idle window does it anyway, and the PC does not rely
/// on the phone remembering to say so. It exists so that locking the phone takes effect
/// <em>immediately</em> rather than in up to fifteen minutes.
/// </para>
/// </summary>
public sealed class LockSessionAction : CapabilityAction<NoParameters, LockSessionResult>
{
    private readonly IOwnerSessionService _sessions;

    public LockSessionAction(IOwnerSessionService sessions) => _sessions = sessions;

    public override string Name => "session.lock";

    public override string Description => "Withdraw this phone's Full Remote authority immediately.";

    protected override ValueTask<LockSessionResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        _sessions.Lock(EnrollOwnerKeyAction.RequireDevice(context), "the phone locked itself");
        return ValueTask.FromResult(new LockSessionResult { Locked = true });
    }
}

public sealed record ResetOwnerKeyResult
{
    public required bool Cleared { get; init; }
}

/// <summary>
/// Forgets the registered owner key, at the PC.
/// <para>
/// Local-only, and absent from the phone's allowlist, because it is the escape hatch <em>from</em>
/// the phone: it is what the owner uses when Android has invalidated the key — a new fingerprint
/// enrolled, the app reinstalled, the phone replaced — and the only thing standing between a thief
/// and registering a key of their own is that they cannot reach this.
/// </para>
/// </summary>
public sealed class ResetOwnerKeyAction : CapabilityAction<NoParameters, ResetOwnerKeyResult>
{
    private readonly IPrimaryPhoneRegistry _phones;
    private readonly IOwnerSessionService _sessions;
    private readonly ILogger<ResetOwnerKeyAction> _logger;

    public ResetOwnerKeyAction(
        IPrimaryPhoneRegistry phones,
        IOwnerSessionService sessions,
        ILogger<ResetOwnerKeyAction> logger)
    {
        _phones = phones;
        _sessions = sessions;
        _logger = logger;
    }

    public override string Name => "session.reset_owner_key";

    public override string Description => "Forget the phone's owner-authentication key so it can register a new one.";

    protected override ValueTask<ResetOwnerKeyResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var phone = _phones.Current;
        var cleared = _phones.ClearOwnerKey();

        if (cleared && phone is not null)
        {
            // Whatever authority that key had bought is withdrawn in the same breath.
            _sessions.Lock(phone.DeviceId, "the owner-authentication key was reset at the PC");
            _logger.LogWarning("Owner-authentication key reset at the PC for phone {DeviceId}.", phone.DeviceId);
        }

        return ValueTask.FromResult(new ResetOwnerKeyResult { Cleared = cleared });
    }
}
