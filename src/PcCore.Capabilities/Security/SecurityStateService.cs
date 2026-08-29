using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Security;

/// <summary>
/// The owner's current security state, plus when it began and what preceded it.
/// This is exactly what <c>security.get_state</c> returns — no internal fields leak through it.
/// </summary>
/// <param name="State">The current state.</param>
/// <param name="PreviousState">The state before this one, or <see langword="null"/> since startup.</param>
/// <param name="SinceUtc">When the current state began. At startup, when PC Core started.</param>
public sealed record SecurityStateSnapshot(
    SecurityState State,
    SecurityState? PreviousState,
    DateTimeOffset SinceUtc);

public enum SecurityTransitionOutcome
{
    /// <summary>The state changed and <c>security.state_changed</c> was published.</summary>
    Changed,

    /// <summary>Already in the requested state. Nothing changed and no event was published.</summary>
    Unchanged,

    /// <summary>The transition is not permitted from the current state.</summary>
    Rejected,
}

/// <summary>
/// Outcome of a transition request. Mirrors <see cref="PathAuthorizationResult"/>: callers that
/// want a protocol error call <see cref="EnsureAccepted"/>, callers that want to branch read
/// <see cref="Outcome"/>.
/// </summary>
public readonly record struct SecurityTransitionResult
{
    private SecurityTransitionResult(
        SecurityTransitionOutcome outcome,
        SecurityStateSnapshot snapshot,
        SecurityState? requested)
    {
        Outcome = outcome;
        Snapshot = snapshot;
        Requested = requested;
    }

    public SecurityTransitionOutcome Outcome { get; }

    /// <summary>State after the request. On a rejection this is the unchanged current state.</summary>
    public SecurityStateSnapshot Snapshot { get; }

    /// <summary>What was asked for. Only set on a rejection.</summary>
    public SecurityState? Requested { get; }

    public bool Changed => Outcome == SecurityTransitionOutcome.Changed;

    public bool Accepted => Outcome is SecurityTransitionOutcome.Changed or SecurityTransitionOutcome.Unchanged;

    internal static SecurityTransitionResult Applied(SecurityStateSnapshot snapshot) =>
        new(SecurityTransitionOutcome.Changed, snapshot, null);

    internal static SecurityTransitionResult NoOp(SecurityStateSnapshot snapshot) =>
        new(SecurityTransitionOutcome.Unchanged, snapshot, null);

    internal static SecurityTransitionResult Refused(SecurityStateSnapshot snapshot, SecurityState requested) =>
        new(SecurityTransitionOutcome.Rejected, snapshot, requested);

    /// <summary>
    /// Returns the resulting snapshot, or throws the matching protocol error.
    /// <para>
    /// A rejected transition is refused outright and never rewritten into some other valid state:
    /// silently landing somewhere the caller did not ask for is how a security state stops meaning
    /// anything.
    /// </para>
    /// </summary>
    public SecurityStateSnapshot EnsureAccepted()
    {
        if (Accepted)
        {
            return Snapshot;
        }

        throw new CapabilityException(
            ErrorCodes.InvalidStateTransition,
            $"Cannot move from {Snapshot.State} to {Requested}.",
            $"current: {Snapshot.State}");
    }
}

/// <summary>
/// The authoritative owner-presence state for the whole process.
/// <para>
/// Capabilities must never keep their own copy: there is one state, and everything that needs it
/// reads it from here.
/// </para>
/// </summary>
public interface ISecurityStateService
{
    /// <summary>The current state, its start time, and the state it replaced.</summary>
    SecurityStateSnapshot Current { get; }

    /// <summary>
    /// Whether a transition would be permitted right now. Advisory only — the answer can be stale
    /// the instant it returns, so <see cref="RequestTransition"/> re-checks under the lock.
    /// </summary>
    bool CanTransitionTo(SecurityState target);

    /// <summary>
    /// Requests a transition. Publishes <c>security.state_changed</c> if and only if the state
    /// actually changed.
    /// </summary>
    /// <param name="target">Desired state.</param>
    /// <param name="reason">Short internal audit string, for example <c>security.arm</c>. Logged, never returned.</param>
    SecurityTransitionResult RequestTransition(SecurityState target, string reason);
}

public sealed class SecurityStateService : ISecurityStateService
{
    /// <summary>
    /// One lock guards the whole triple (state, previous, since) and the event publish.
    /// <para>
    /// Publishing <em>inside</em> the lock is deliberate. It is the simplest way to guarantee that
    /// the order of <c>security.state_changed</c> events can never disagree with the order the
    /// state actually moved: with the publish outside, two racing transitions could be applied
    /// A-then-B but announced B-then-A, and a client replaying the events would end up believing
    /// the wrong current state.
    /// </para>
    /// <para>
    /// This is safe because the only subscriber — the WebSocket forwarder — hands the frame to a
    /// background send and returns immediately, and <c>EventBus</c> swallows subscriber faults. The
    /// standing constraint it creates: a <c>security.state_changed</c> handler must not synchronously
    /// request another transition.
    /// </para>
    /// </summary>
    private readonly Lock _gate = new();

    private readonly IEventPublisher _events;
    private readonly ILogger<SecurityStateService> _logger;

    private SecurityState _state;
    private SecurityState? _previousState;
    private DateTimeOffset _sinceUtc;

    public SecurityStateService(IEventPublisher events, ILogger<SecurityStateService> logger)
    {
        _events = events;
        _logger = logger;

        // Always OWNER_PRESENT at startup. State is in memory only: v0.2 does not persist it, so a
        // restart cannot leave the machine believing it is still armed when nobody armed it.
        _state = SecurityState.OwnerPresent;
        _previousState = null;
        _sinceUtc = DateTimeOffset.UtcNow;
    }

    public SecurityStateSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return SnapshotLocked();
            }
        }
    }

    public bool CanTransitionTo(SecurityState target)
    {
        lock (_gate)
        {
            return _state == target || SecurityStateTransitions.IsAllowed(_state, target);
        }
    }

    public SecurityTransitionResult RequestTransition(SecurityState target, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_gate)
        {
            if (_state == target)
            {
                // Idempotent: asking for the state you are already in succeeds and changes nothing.
                // No event, because nothing changed — a duplicate would make listeners believe the
                // owner acted twice.
                _logger.LogDebug("Security state already {State}; no change ({Reason}).", _state, reason);
                return SecurityTransitionResult.NoOp(SnapshotLocked());
            }

            if (!SecurityStateTransitions.IsAllowed(_state, target))
            {
                _logger.LogWarning(
                    "Refused security state transition {From} -> {To} ({Reason}).", _state, target, reason);
                return SecurityTransitionResult.Refused(SnapshotLocked(), target);
            }

            var previous = _state;
            _state = target;
            _previousState = previous;
            _sinceUtc = DateTimeOffset.UtcNow;

            var snapshot = SnapshotLocked();

            _logger.LogInformation(
                "Security state {From} -> {To} ({Reason}).", previous, target, reason);

            _events.Publish(
                SecurityEvents.StateChanged,
                new SecurityStateChangedPayload(previous, target));

            return SecurityTransitionResult.Applied(snapshot);
        }
    }

    private SecurityStateSnapshot SnapshotLocked() => new(_state, _previousState, _sinceUtc);
}

/// <summary>
/// Payload of <c>security.state_changed</c>. The occurrence time is the envelope's
/// <c>timestamp_utc</c>; repeating it here would just create two timestamps that can disagree.
/// </summary>
public sealed record SecurityStateChangedPayload(
    SecurityState PreviousState,
    SecurityState CurrentState);
