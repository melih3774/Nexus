namespace PcCore.NativeInput.Hid;

// Lives here rather than in the capabilities layer because PcCore.NativeInput deliberately has no
// project references - the elevated broker shares it, and its dependency surface is part of its
// threat model. The lifecycle is pure state logic with nothing to import, so it costs nothing to
// keep it beside the backend that drives it.

/// <summary>Where a virtual-HID backend is in its connection life.</summary>
public enum VirtualHidLifecycleState
{
    /// <summary>Nothing is installed, or nothing has been attempted yet.</summary>
    Absent,

    /// <summary>A connection attempt is in progress.</summary>
    Connecting,

    /// <summary>
    /// Connected, protocol and capabilities accepted, identities resolved — and <em>not yet usable</em>.
    /// The neutral handshake has not completed.
    /// </summary>
    Handshaking,

    /// <summary>Everything succeeded, including the handshake. The only state that may carry input.</summary>
    Ready,

    /// <summary>
    /// Reports are failing but the backend has not been declared gone. Input still routes here while
    /// the failure budget lasts, because one refused report is not a dead driver.
    /// </summary>
    Degraded,

    /// <summary>Declared gone. Input has fallen back and a cooldown must elapse before retrying.</summary>
    Lost,
}

/// <summary>
/// The connection life of a virtual-HID backend, as an explicit machine rather than a set of
/// booleans checked in different places.
/// <para>
/// <b>Two problems, one type.</b> The first is flapping: a driver that fails intermittently must not
/// move input between backends on every report, because each move costs a neutralisation and the
/// owner feels it as the pointer stuttering between two behaviours. The second is trust: a backend
/// that has just come back is a <em>different device</em>, and treating it as a continuation of the
/// one that vanished is how a modifier ends up held on hardware nobody is tracking.
/// </para>
/// <para>
/// <b>The failure budget is what stops flapping.</b> A single refused report leaves the backend
/// <see cref="VirtualHidLifecycleState.Degraded"/> and still carrying input; only
/// <see cref="MaxConsecutiveFailures"/> in a row declares it lost. One success resets the count,
/// because a driver that is working is working regardless of what it did a moment ago.
/// </para>
/// <para>
/// <b>The cooldown backs off.</b> Retrying a dead driver every few seconds forever is its own kind
/// of flapping — it produces a connect attempt, a handshake, a failure and a fallback on a loop.
/// Each consecutive loss doubles the wait to a ceiling, and a spell of health resets it.
/// </para>
/// </summary>
public sealed class VirtualHidLifecycle
{
    /// <summary>Consecutive failed reports before the backend is declared lost.</summary>
    public const int MaxConsecutiveFailures = 3;

    /// <summary>The first wait after a loss. Doubles per consecutive loss, up to the ceiling.</summary>
    public static readonly TimeSpan BaseCooldown = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan MaxCooldown = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a backend must stay healthy before its loss streak is forgiven. Without this, a
    /// driver that connects, works for one report and dies would keep the shortest cooldown forever.
    /// </summary>
    public static readonly TimeSpan HealthyResetPeriod = TimeSpan.FromMinutes(2);

    private readonly Lock _gate = new();

    private VirtualHidLifecycleState _state = VirtualHidLifecycleState.Absent;
    private int _generation;
    private int _consecutiveFailures;
    private int _consecutiveLosses;
    private DateTimeOffset _retryNotBefore = DateTimeOffset.MinValue;
    private DateTimeOffset _readySince = DateTimeOffset.MinValue;
    private string? _diagnostic;

    public VirtualHidLifecycleState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// The current instance number. Advances on every connection attempt, so state recorded against
    /// one instance can never be attributed to its successor.
    /// </summary>
    public int Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    /// <summary>Only <see cref="VirtualHidLifecycleState.Ready"/> may carry input.</summary>
    public bool IsEligible => State == VirtualHidLifecycleState.Ready;

    public string? Diagnostic
    {
        get
        {
            lock (_gate)
            {
                return _diagnostic;
            }
        }
    }

    /// <summary>Whether a connection attempt may start now, given the cooldown.</summary>
    public bool MayAttemptConnect(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _state is VirtualHidLifecycleState.Absent or VirtualHidLifecycleState.Lost
                && now >= _retryNotBefore;
        }
    }

    /// <summary>
    /// Begins an attempt and takes a fresh generation. Returns false when a cooldown is still
    /// running or an attempt is already under way.
    /// </summary>
    public bool BeginConnect(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_state is not (VirtualHidLifecycleState.Absent or VirtualHidLifecycleState.Lost)
                || now < _retryNotBefore)
            {
                return false;
            }

            _generation++;
            _state = VirtualHidLifecycleState.Connecting;
            _consecutiveFailures = 0;
            _diagnostic = null;
            return true;
        }
    }

    /// <summary>
    /// Protocol, capabilities and identities all checked out. The backend is connected and is
    /// deliberately <em>not</em> eligible yet — see <see cref="CompleteHandshake"/>.
    /// </summary>
    public void Connected()
    {
        lock (_gate)
        {
            if (_state == VirtualHidLifecycleState.Connecting)
            {
                _state = VirtualHidLifecycleState.Handshaking;
            }
        }
    }

    /// <summary>
    /// The neutral handshake: the last thing between a fresh backend and carrying input.
    /// <para>
    /// <b>Why a new instance must be told to release before it is allowed to press.</b> A device that
    /// has just appeared is not necessarily in a neutral state — a driver restart, a reconnect after
    /// a crash, or an install over a running instance can all leave a HID device reporting something
    /// held that nothing on this side knows about. Sending an explicit neutral report and requiring
    /// it to be accepted is the only way to start from a known position rather than an assumed one.
    /// </para>
    /// <para>
    /// A handshake that fails does not degrade the backend, it loses it. A device that will not
    /// accept a neutral report is not one to send a keystroke to.
    /// </para>
    /// </summary>
    public bool CompleteHandshake(DateTimeOffset now, Func<bool> sendNeutral)
    {
        ArgumentNullException.ThrowIfNull(sendNeutral);

        lock (_gate)
        {
            if (_state != VirtualHidLifecycleState.Handshaking)
            {
                return false;
            }
        }

        // Outside the lock: this crosses a process boundary.
        var accepted = sendNeutral();

        lock (_gate)
        {
            if (_state != VirtualHidLifecycleState.Handshaking)
            {
                return false;
            }

            if (!accepted)
            {
                LoseLocked(now, "the backend did not accept a neutral report during the handshake");
                return false;
            }

            _state = VirtualHidLifecycleState.Ready;
            _readySince = now;
            _consecutiveFailures = 0;
            _diagnostic = null;
            return true;
        }
    }

    /// <summary>A report was delivered. Resets the failure budget and may forgive the loss streak.</summary>
    public void RecordSuccess(DateTimeOffset now)
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;

            if (_state == VirtualHidLifecycleState.Degraded)
            {
                _state = VirtualHidLifecycleState.Ready;
                _diagnostic = null;
            }

            if (_state == VirtualHidLifecycleState.Ready
                && _readySince != DateTimeOffset.MinValue
                && now - _readySince >= HealthyResetPeriod)
            {
                _consecutiveLosses = 0;
            }
        }
    }

    /// <summary>
    /// A report was refused. Degrades first and only declares loss after the budget is spent, which
    /// is what keeps one bad report from moving every subsequent one to another backend.
    /// </summary>
    public VirtualHidLifecycleState RecordFailure(DateTimeOffset now, string? reason = null)
    {
        lock (_gate)
        {
            if (_state is not (VirtualHidLifecycleState.Ready or VirtualHidLifecycleState.Degraded))
            {
                return _state;
            }

            _consecutiveFailures++;

            if (_consecutiveFailures >= MaxConsecutiveFailures)
            {
                LoseLocked(now, reason ?? $"{_consecutiveFailures} consecutive reports were refused");
            }
            else
            {
                _state = VirtualHidLifecycleState.Degraded;
                _diagnostic = reason ?? "a report was refused";
            }

            return _state;
        }
    }

    /// <summary>Declares the backend gone immediately — a vanished device, a failed probe.</summary>
    public void Lose(DateTimeOffset now, string reason)
    {
        lock (_gate)
        {
            LoseLocked(now, reason);
        }
    }

    /// <summary>Resets to Absent without a cooldown. For shutdown and for tests.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _state = VirtualHidLifecycleState.Absent;
            _consecutiveFailures = 0;
            _consecutiveLosses = 0;
            _retryNotBefore = DateTimeOffset.MinValue;
            _readySince = DateTimeOffset.MinValue;
            _diagnostic = null;
        }
    }

    /// <summary>When the next connection attempt may begin. Diagnostics.</summary>
    public DateTimeOffset RetryNotBefore
    {
        get
        {
            lock (_gate)
            {
                return _retryNotBefore;
            }
        }
    }

    private void LoseLocked(DateTimeOffset now, string reason)
    {
        _state = VirtualHidLifecycleState.Lost;
        _diagnostic = reason;
        _consecutiveFailures = 0;
        _readySince = DateTimeOffset.MinValue;

        _consecutiveLosses = Math.Min(_consecutiveLosses + 1, 16);

        // Exponential backoff with a ceiling. Shift on a long to avoid overflowing the multiply on a
        // long streak, which would produce a tiny cooldown at exactly the moment it should be largest.
        var factor = 1L << Math.Min(_consecutiveLosses - 1, 8);
        var wait = TimeSpan.FromTicks(Math.Min(BaseCooldown.Ticks * factor, MaxCooldown.Ticks));

        _retryNotBefore = now + wait;
    }
}
