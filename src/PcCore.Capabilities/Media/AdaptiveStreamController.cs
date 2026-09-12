namespace PcCore.Capabilities.Media;

/// <summary>
/// What a stream is actually managing, as measured rather than intended.
/// </summary>
/// <param name="Backlog">Encoded frames written but not yet on the socket. The PC's own queue depth.</param>
/// <param name="SkippedForBacklog">Captures abandoned because the backlog was already too deep. A running total.</param>
/// <param name="PlannedFps">What the plan asked for.</param>
/// <param name="ClientLatencyMs">
/// Capture to render, as the phone measured it against a synchronised clock, over frames it rendered
/// since its last report. Negative when there is no fresh report: a figure the phone sent earlier
/// describes a stream that may no longer exist.
/// </param>
/// <param name="ClientDecodeMs">How long the phone's decoder is taking per frame, or negative.</param>
public readonly record struct StreamHealth(
    int Backlog,
    long SkippedForBacklog,
    double PlannedFps,
    double ClientLatencyMs,
    double ClientDecodeMs);

/// <summary>
/// Decides, from measurements, how hard to push a stream.
/// <para>
/// <b>What it is allowed to use.</b> Only things this machine or the phone actually measured: how
/// many encoded frames are waiting for the socket, how many captures had to be abandoned because
/// they were already waiting, and what the phone reports for end-to-end latency and decode time.
/// There is no bandwidth estimator and no probing, because a personal tunnel between two machines
/// the owner controls does not need one — if the link cannot carry the stream, the backlog says so
/// within a second and says it without costing anything to find out.
/// </para>
/// <para>
/// <b>What it deliberately does not use.</b> The delivered frame rate. A desktop only produces a
/// frame when something on it changes, so a quiet screen delivers two frames a second over a link
/// that could carry sixty — and treating that as congestion walked an idle stream down to its lowest
/// step, where it then took a minute to climb back once the owner started moving things.
/// </para>
/// <para>
/// <b>It does not react to its own changes.</b> A plan change is followed by a keyframe and, when the
/// size changes, a new encoder: for a second or two the queue, the phone's latency and its decoder all
/// look strained, and the phone's reports still describe the plan before. The first version stepped
/// down on exactly that, twice, on a report that was already stale. So every change is followed by a
/// settling period in which nothing counts, and a step down needs strain in consecutive observations
/// rather than in one.
/// </para>
/// <para>
/// <b>Why it is slow to improve and quick to give up quality.</b> Latency is the thing that makes a
/// remote desktop unusable, and a backlog turns into latency immediately. So strain steps down within
/// a few seconds, and it takes a run of good ones to step back up — longer each time a step up turns
/// out to have been premature, so that a link on the edge of two plans settles on the lower one
/// instead of alternating between them.
/// </para>
/// <para>
/// <b>It only ever moves the level.</b> What a level means — which resolution, frame rate and
/// bitrate — belongs to <see cref="MediaPlanner"/>, including the rule that a resolution the owner
/// chose explicitly is never overridden. This class does not know what a pixel is.
/// </para>
/// </summary>
public sealed class AdaptiveStreamController
{
    /// <summary>Encoded frames waiting for the socket before the link counts as behind.</summary>
    public const int BacklogLimit = 2;

    /// <summary>Capture to render, above which the stream is unpleasant to use whatever it looks like.</summary>
    public const double LatencyLimitMs = 450;

    /// <summary>Consecutive strained observations needed before a step down.</summary>
    public const int StrainedObservationsToStep = 2;

    /// <summary>After any change, how long before a measurement counts again.</summary>
    public static readonly TimeSpan SettleInterval = TimeSpan.FromSeconds(3);

    /// <summary>The healthy run needed to step up the first time.</summary>
    public static readonly TimeSpan StepUpInterval = TimeSpan.FromSeconds(12);

    /// <summary>The longest a stream waits to try again after repeated premature step ups.</summary>
    public static readonly TimeSpan MaxStepUpInterval = TimeSpan.FromSeconds(96);

    /// <summary>A step down this soon after a step up means the step up was premature.</summary>
    private static readonly TimeSpan PrematureWindow = TimeSpan.FromSeconds(20);

    private readonly TimeProvider _time;

    private DateTimeOffset _lastChange;
    private DateTimeOffset _healthySince;
    private DateTimeOffset _lastStepUp = DateTimeOffset.MinValue;
    private TimeSpan _stepUpInterval = StepUpInterval;
    private long _lastSkipped;
    private int _strainedRun;

    public AdaptiveStreamController(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _lastChange = _time.GetUtcNow();
        _healthySince = _lastChange;
    }

    /// <summary>Zero is the plan as asked for; each step above it asks for less.</summary>
    public int Level { get; private set; }

    /// <summary>Why the level last moved. Shown to the owner rather than left as a mystery.</summary>
    public string Reason { get; private set; } = "";

    /// <summary>How long a healthy run must currently last before a step up.</summary>
    public TimeSpan CurrentStepUpInterval => _stepUpInterval;

    /// <summary>Puts the controller back to its best guess. Used when a stream starts or the owner changes settings.</summary>
    public void Reset()
    {
        Level = 0;
        Reason = "";
        _lastChange = _time.GetUtcNow();
        _healthySince = _lastChange;
        _lastStepUp = DateTimeOffset.MinValue;
        _stepUpInterval = StepUpInterval;
        _strainedRun = 0;
    }

    /// <summary>
    /// Tells the controller that the stream changed for a reason of its own — a new encoder after a
    /// lost capture, say — so the disruption that follows is not mistaken for the link.
    /// </summary>
    public void NoteDisruption()
    {
        _lastChange = _time.GetUtcNow();
        _strainedRun = 0;
    }

    /// <summary>Feeds in about a second's measurements. Returns true when the level changed.</summary>
    public bool Observe(StreamHealth health)
    {
        var now = _time.GetUtcNow();

        // Always taken, settling or not, so that captures skipped during the settling period are not
        // all counted against the first second after it.
        var skipped = Math.Max(0, health.SkippedForBacklog - _lastSkipped);
        _lastSkipped = health.SkippedForBacklog;

        if (now - _lastChange < SettleInterval)
        {
            _strainedRun = 0;
            return false;
        }

        var strained = Strain(health, skipped);

        if (strained is not null)
        {
            _healthySince = now;
            _strainedRun++;

            if (_strainedRun < StrainedObservationsToStep || Level >= MediaPlanner.MaxLevel)
            {
                return false;
            }

            if (now - _lastStepUp < PrematureWindow)
            {
                // The level above did not hold. Waiting twice as long before trying it again is what
                // stops a stream on the boundary between two plans from alternating between them.
                _stepUpInterval = TimeSpan.FromTicks(Math.Min(MaxStepUpInterval.Ticks, _stepUpInterval.Ticks * 2));
            }

            Level++;
            Reason = strained;
            _lastChange = now;
            _strainedRun = 0;
            return true;
        }

        _strainedRun = 0;

        if (Level == 0)
        {
            return false;
        }

        // Improving needs a sustained run of good seconds, not one. A link that has just recovered
        // from congestion looks identical to one that was never congested, and moving up on the
        // first quiet second is how a stream ends up cycling.
        if (now - _healthySince < _stepUpInterval || now - _lastChange < _stepUpInterval)
        {
            return false;
        }

        Level--;
        Reason = $"the link kept up for {_stepUpInterval.TotalSeconds:F0} seconds";
        _lastChange = now;
        _healthySince = now;
        _lastStepUp = now;
        return true;
    }

    /// <summary>The reason this stream is struggling, or null when it is not.</summary>
    private static string? Strain(StreamHealth health, long skippedThisObservation)
    {
        if (health.Backlog > BacklogLimit)
        {
            return $"{health.Backlog} frames were waiting to be sent";
        }

        if (skippedThisObservation > health.PlannedFps / 3)
        {
            return $"{skippedThisObservation} captures in a second were abandoned to keep the queue short";
        }

        if (health.ClientLatencyMs >= 0 && health.ClientLatencyMs > LatencyLimitMs)
        {
            return $"the phone measured {health.ClientLatencyMs:F0} ms from capture to screen";
        }

        // Only when the client is genuinely the bottleneck: a decoder slower than the frame interval
        // cannot catch up, and sending it more frames only lengthens its own queue.
        if (health.ClientDecodeMs >= 0 && health.PlannedFps > 0 &&
            health.ClientDecodeMs > 1000.0 / health.PlannedFps)
        {
            return $"the phone's decoder needed {health.ClientDecodeMs:F0} ms a frame";
        }

        return null;
    }
}
