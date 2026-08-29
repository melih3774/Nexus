using PcCore.Protocol;

namespace PcCore.Capabilities.Security;

/// <summary>
/// The complete set of permitted state changes, written out as data.
/// <para>
/// A table rather than a state-machine library: there are four transitions across three states,
/// and the entire security model of the subsystem is legible in one screenful. A framework would
/// add a dependency and hide the thing most worth reviewing.
/// </para>
/// </summary>
public static class SecurityStateTransitions
{
    /// <summary>
    /// Every allowed change. Anything absent is refused with
    /// <see cref="ErrorCodes.InvalidStateTransition"/>.
    /// </summary>
    public static readonly IReadOnlyList<(SecurityState From, SecurityState To)> Allowed =
    [
        // The owner declares they are leaving.
        (SecurityState.OwnerPresent, SecurityState.Armed),

        // The owner comes back and stands down before anything happened.
        (SecurityState.Armed, SecurityState.OwnerPresent),

        // Future activity detection escalates. Internal-only: no capability exposes this.
        (SecurityState.Armed, SecurityState.SecuritySession),

        // The owner returns and ends the session.
        (SecurityState.SecuritySession, SecurityState.OwnerPresent),

        // NOT ALLOWED: SECURITY_SESSION -> ARMED. See the note below.
    ];

    /// <summary>
    /// <b>Why <c>SECURITY_SESSION -> ARMED</c> is deliberately absent.</b>
    /// <para>
    /// It is superficially attractive: the owner is still away, so after a session "finishes" the
    /// machine could quietly resume watching. Two reasons not to:
    /// </para>
    /// <para>
    /// 1. <i>Nothing can legitimately trigger it in v0.2.</i> The only honest trigger is a
    /// session-lifecycle rule — a quiet period, a timeout, an evidence-complete signal — and none
    /// of those exist. Adding the edge now would create a transition with no caller and no
    /// definition of when it fires, which is exactly the speculative machinery this project avoids.
    /// </para>
    /// <para>
    /// 2. <i>It would let an incident close itself.</i> Returning to ARMED without the owner
    /// involved means the system decides on its own that the event is over. In v0.2 the end of a
    /// security session is an owner-visible act (<c>security.disarm</c>), which keeps the state
    /// trail honest: a session ends because someone came back, not because a timer elapsed.
    /// </para>
    /// <para>
    /// When session lifecycle rules do arrive, enabling it is one row in <see cref="Allowed"/>
    /// plus its tests.
    /// </para>
    /// </summary>
    public static bool IsAllowed(SecurityState from, SecurityState to)
    {
        foreach (var (allowedFrom, allowedTo) in Allowed)
        {
            if (allowedFrom == from && allowedTo == to)
            {
                return true;
            }
        }

        return false;
    }
}
