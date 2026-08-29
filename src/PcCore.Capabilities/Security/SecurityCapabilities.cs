using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Security;

/// <summary>Result of <c>security.get_state</c>.</summary>
public sealed record SecurityStateResult
{
    public required SecurityState State { get; init; }

    /// <summary>The state this one replaced, or <see langword="null"/> if nothing has changed since startup.</summary>
    public SecurityState? PreviousState { get; init; }

    /// <summary>When the current state began.</summary>
    public required DateTimeOffset SinceUtc { get; init; }

    public static SecurityStateResult From(SecurityStateSnapshot snapshot) => new()
    {
        State = snapshot.State,
        PreviousState = snapshot.PreviousState,
        SinceUtc = snapshot.SinceUtc,
    };
}

/// <summary>
/// Result of <c>security.arm</c> and <c>security.disarm</c>.
/// <para>
/// <see cref="Changed"/> is what distinguishes "I armed it" from "it was already armed". Both are
/// successes; a caller that needs to tell them apart reads this rather than diffing states itself.
/// </para>
/// </summary>
public sealed record SecurityCommandResult
{
    public required bool Changed { get; init; }
    public required SecurityState State { get; init; }
    public SecurityState? PreviousState { get; init; }
    public required DateTimeOffset SinceUtc { get; init; }

    public static SecurityCommandResult From(SecurityTransitionResult result) => new()
    {
        Changed = result.Changed,
        State = result.Snapshot.State,
        PreviousState = result.Snapshot.PreviousState,
        SinceUtc = result.Snapshot.SinceUtc,
    };
}

public sealed class GetSecurityStateAction : CapabilityAction<NoParameters, SecurityStateResult>
{
    private readonly ISecurityStateService _state;

    public GetSecurityStateAction(ISecurityStateService state) => _state = state;

    public override string Name => "security.get_state";

    public override string Description => "The owner's current security state, when it began, and what it replaced.";

    protected override ValueTask<SecurityStateResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(SecurityStateResult.From(_state.Current));
}

/// <summary>
/// <c>security.arm</c> — the owner declares they are away.
/// <para>
/// Idempotency: already <c>ARMED</c> succeeds with <c>changed: false</c> and publishes no event.
/// Re-arming is a harmless restatement of intent, and a client retrying after a dropped response
/// must not be punished for it.
/// </para>
/// <para>
/// From <c>SECURITY_SESSION</c> this is <b>refused</b>, not silently accepted. That edge is absent
/// from the transition table on purpose (see <see cref="SecurityStateTransitions"/>), and quietly
/// treating "arm" as "stand the incident down" would be the system inventing an owner decision.
/// </para>
/// </summary>
public sealed class ArmSecurityAction : CapabilityAction<NoParameters, SecurityCommandResult>
{
    private readonly ISecurityStateService _state;

    public ArmSecurityAction(ISecurityStateService state) => _state = state;

    public override string Name => "security.arm";

    public override string Description => "Mark the owner as away (OWNER_PRESENT -> ARMED).";

    protected override ValueTask<SecurityCommandResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var result = _state.RequestTransition(SecurityState.Armed, Name);
        result.EnsureAccepted();

        return ValueTask.FromResult(SecurityCommandResult.From(result));
    }
}

/// <summary>
/// <c>security.disarm</c> — the owner is back.
/// <para>
/// Works from <c>ARMED</c> and from <c>SECURITY_SESSION</c>; both lead to <c>OWNER_PRESENT</c>.
/// Idempotency: already <c>OWNER_PRESENT</c> succeeds with <c>changed: false</c> and no event.
/// </para>
/// <para>
/// Disarm is the one action that must never fail for a legitimate owner — being unable to stand
/// your own machine down is a far worse failure than disarming twice.
/// </para>
/// </summary>
public sealed class DisarmSecurityAction : CapabilityAction<NoParameters, SecurityCommandResult>
{
    private readonly ISecurityStateService _state;

    public DisarmSecurityAction(ISecurityStateService state) => _state = state;

    public override string Name => "security.disarm";

    public override string Description => "Return to OWNER_PRESENT from ARMED or SECURITY_SESSION.";

    protected override ValueTask<SecurityCommandResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var result = _state.RequestTransition(SecurityState.OwnerPresent, Name);
        result.EnsureAccepted();

        return ValueTask.FromResult(SecurityCommandResult.From(result));
    }
}
