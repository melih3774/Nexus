using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Storage;
using PcCore.Protocol;

namespace PcCore.Capabilities.Security;

/// <summary>
/// The owner's current security state, plus when it began and what preceded it.
/// This is exactly what <c>security.get_state</c> returns — no internal fields leak through it.
/// </summary>
/// <param name="State">The current state.</param>
/// <param name="PreviousState">The state before this one, or <see langword="null"/> since startup.</param>
/// <param name="SinceUtc">When the current state began — preserved across restarts.</param>
public sealed record SecurityStateSnapshot(
    SecurityState State,
    SecurityState? PreviousState,
    DateTimeOffset SinceUtc);

/// <summary>On-disk shape of the security state.</summary>
public sealed record PersistedSecurityState
{
    public int SchemaVersion { get; init; } = 1;
    public required SecurityState State { get; init; }
    public SecurityState? PreviousState { get; init; }
    public required DateTimeOffset SinceUtc { get; init; }

    /// <summary>
    /// Increments on every committed change. Not used for concurrency control — the lock does
    /// that — but it makes a state file self-describing when diagnosing an unexpected restart.
    /// </summary>
    public required long Revision { get; init; }
}

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
    public const string FileName = "security-state.json";

    /// <summary>
    /// Guards the state triple (state, previous, since) and its persistence. Held only for the
    /// duration of a commit; no subscriber code ever runs underneath it.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// Serialises publication so events reach subscribers in commit order, without ever being
    /// held at the same time as <see cref="_gate"/>. See <see cref="PublishPending"/>.
    /// </summary>
    private readonly Lock _publishGate = new();

    /// <summary>Committed-but-unpublished events, enqueued under <see cref="_gate"/> in commit order.</summary>
    private readonly ConcurrentQueue<SecurityStateChangedPayload> _pending = new();

    private readonly IEventPublisher _events;
    private readonly AtomicJsonFileStore<PersistedSecurityState> _store;
    private readonly ILogger<SecurityStateService> _logger;

    private SecurityState _state;
    private SecurityState? _previousState;
    private DateTimeOffset _sinceUtc;
    private long _revision;

    public SecurityStateService(
        IEventPublisher events,
        StateDirectory directory,
        ILogger<SecurityStateService> logger)
    {
        _events = events;
        _logger = logger;
        _store = new AtomicJsonFileStore<PersistedSecurityState>(directory, FileName, logger);

        var restored = Restore();
        _state = restored.State;
        _previousState = restored.PreviousState;
        _sinceUtc = restored.SinceUtc;
        _revision = restored.Revision;
    }

    /// <summary>
    /// Loads the state left by the previous run.
    /// <para>
    /// <b>Restart carries every state forward, including <c>SECURITY_SESSION</c>.</b> If
    /// unauthorised activity opened a session, rebooting the machine must not be a way to clear
    /// it — that would make a reboot the cheapest possible attack against the whole feature. The
    /// session ends when the owner ends it.
    /// </para>
    /// <para>
    /// <b>Corruption falls back to <c>ARMED</c>, never <c>OWNER_PRESENT</c>.</b> A damaged file
    /// means the previous state is unknown, and the one thing that must not be inferred from
    /// "unknown" is that the owner is present. <c>ARMED</c> asserts only that presence is
    /// unconfirmed; it does not fabricate an incident that may never have happened. The owner
    /// clears it with <c>security.disarm</c>. First run — no file at all — is a different case and
    /// starts at <c>OWNER_PRESENT</c>.
    /// </para>
    /// </summary>
    private PersistedSecurityState Restore()
    {
        var loaded = _store.Load();

        switch (loaded.Status)
        {
            case StateLoadStatus.Loaded when Enum.IsDefined(loaded.Value!.State):
                var restored = loaded.Value!;
                _logger.LogInformation(
                    "Restored security state {State} (since {Since:u}, revision {Revision}).",
                    restored.State,
                    restored.SinceUtc,
                    restored.Revision);
                return restored;

            case StateLoadStatus.Loaded:
                _logger.LogError(
                    "Persisted security state contained an unrecognised value. Falling back to {State}.",
                    SecurityState.Armed);
                return Fresh(SecurityState.Armed);

            case StateLoadStatus.Corrupt:
                _logger.LogError(
                    "Security state could not be restored. Falling back to {State} rather than assuming the "
                    + "owner is present. Use security.disarm to clear it.",
                    SecurityState.Armed);
                return Persist(Fresh(SecurityState.Armed));

            default:
                _logger.LogInformation("No persisted security state; starting at {State}.", SecurityState.OwnerPresent);
                return Persist(Fresh(SecurityState.OwnerPresent));
        }
    }

    private static PersistedSecurityState Fresh(SecurityState state) => new()
    {
        State = state,
        PreviousState = null,
        SinceUtc = DateTimeOffset.UtcNow,
        Revision = 0,
    };

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

        SecurityTransitionResult result;

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
            _revision++;

            Persist(new PersistedSecurityState
            {
                State = _state,
                PreviousState = _previousState,
                SinceUtc = _sinceUtc,
                Revision = _revision,
            });

            _logger.LogInformation("Security state {From} -> {To} ({Reason}).", previous, target, reason);

            // Enqueued under the lock, so the queue is in commit order. Published below, outside it.
            _pending.Enqueue(new SecurityStateChangedPayload(previous, target));

            result = SecurityTransitionResult.Applied(SnapshotLocked());
        }

        PublishPending();
        return result;
    }

    /// <summary>
    /// Publishes committed events in commit order, with <see cref="_gate"/> released.
    /// <para>
    /// Two properties matter here and they pull in opposite directions. Subscribers must not run
    /// while the mutation lock is held — arbitrary handler code inside a state lock is how a
    /// deadlock or a long stall gets introduced. But event order must still match the order
    /// transitions committed, or a client replaying the stream ends up believing the wrong current
    /// state.
    /// </para>
    /// <para>
    /// A queue filled under <see cref="_gate"/> and drained under a separate
    /// <see cref="_publishGate"/> gives both: enqueue order is commit order, and a single drainer
    /// at a time preserves it. Whichever thread reaches the drain first may publish another
    /// thread's event, which is fine — what matters is the sequence, not who emits it.
    /// </para>
    /// <para>
    /// Because <see cref="_gate"/> is free while subscribers run, a handler may safely call back
    /// into <see cref="RequestTransition"/>: it commits, enqueues, and re-enters the drain, still
    /// in FIFO order.
    /// </para>
    /// </summary>
    private void PublishPending()
    {
        lock (_publishGate)
        {
            while (_pending.TryDequeue(out var payload))
            {
                _events.Publish(SecurityEvents.StateChanged, payload);
            }
        }
    }

    private PersistedSecurityState Persist(PersistedSecurityState state)
    {
        try
        {
            _store.Save(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory transition stands. Refusing to arm because the disk is unavailable
            // would be a worse failure than losing the state on the next restart — but this must
            // be loud, because it silently weakens the restart guarantee.
            _logger.LogError(
                ex,
                "Security state {State} could not be persisted. It is in effect now but will not survive a restart.",
                state.State);
        }

        return state;
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
