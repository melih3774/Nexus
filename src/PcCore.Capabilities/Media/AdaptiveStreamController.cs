namespace PcCore.Capabilities.Media;

/// <summary>
/// What a stream is actually managing, as measured rather than intended.
/// </summary>
/// <param name="Backlog">Encoded frames written but not yet on the socket. The PC's own queue depth.</param>
/// <param name="SkippedForBacklog">Captures abandoned because the backlog was already too deep.</param>
/// <param name="DeliveredFps">Frames a second that actually left this machine.</param>
/// <param name="PlannedFps">What the plan asked for. The gap between the two is the real symptom.</param>
/// <param name="ClientLatencyMs">
/// Capture to render, as the phone measured it against a synchronised clock. Negative when the
/// client has not reported yet.
/// </param>
/// <param name="ClientDecodeMs">How long the phone's decoder is taking per frame, or negative.</param>
public readonly record struct StreamHealth(
    int Backlog,
    long SkippedForBacklog,
    double DeliveredFps,
    double PlannedFps,
    double ClientLatencyMs,
    double ClientDecodeMs);

/// <summary>
/// Decides, from measurements, how hard to push a stream.
/// <para>
/// <b>What it is allowed to use.</b> Only things this machine or the phone actually measured: how
/// many encoded frames are waiting for the socket, how many captures had to be abandoned because
/// they were already waiting, how many frames a second really left, and what the phone reports for
/// end-to-end latency. There is no bandwidth estimator and no probing, because a personal tunnel
/// between two machines the owner controls does not need one — if the link cannot carry the stream,
/// the backlog says so within a second and says it without costing anything to find out.
/// </para>
/// <para>
/// <b>Why it is slow to improve and quick to give up quality.</b> Latency is the thing that makes a
/// remote desktop unusable, and a backlog turns into latency immediately. So one bad second steps
/// down; it takes a run of good ones to step back up. The asymmetry is deliberate — the alternative
/// oscillates, and a picture that changes resolution twice a second is worse than one that settled
/// for slightly less.
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

    private static readonly TimeSpan StepDownInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StepUpInterval = TimeSpan.FromSeconds(12);

    private readonly TimeProvider _time;

    private DateTimeOffset _lastChange;
    private DateTimeOffset _healthySince;
    private long _lastSkipped;

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

    /// <summary>Puts the controller back to its best guess. Used when a stream starts.</summary>
    public void Reset()
    {
        Level = 0;
        Reason = "";
        _lastChange = _time.GetUtcNow();
        _healthySince = _lastChange;
        _lastSkipped = 0;
    }

    /// <summary>Feeds in one second's measurements. Returns true when the level changed.</summary>
    public bool Observe(StreamHealth health)
    {
        var now = _time.GetUtcNow();

        var skipped = health.SkippedForBacklog - _lastSkipped;
        _lastSkipped = health.SkippedForBacklog;

        var strained = Strain(health, skipped);

        if (strained is not null)
        {
            _healthySince = now;

            if (Level >= MediaPlanner.MaxLevel || now - _lastChange < StepDownInterval)
            {
                return false;
            }

            Level++;
            Reason = strained;
            _lastChange = now;
            return true;
        }

        if (Level == 0)
        {
            return false;
        }

        // Improving needs a sustained run of good seconds, not one. A link that has just recovered
        // from congestion looks identical to one that was never congested, and moving up on the
        // first quiet second is how a stream ends up cycling.
        if (now - _healthySince < StepUpInterval || now - _lastChange < StepUpInterval)
        {
            return false;
        }

        Level--;
        Reason = "the link kept up for twelve seconds";
        _lastChange = now;
        _healthySince = now;
        return true;
    }

    /// <summary>The reason this stream is struggling, or null when it is not.</summary>
    private static string? Strain(StreamHealth health, long skippedThisSecond)
    {
        if (health.Backlog > BacklogLimit)
        {
            return $"{health.Backlog} frames were waiting to be sent";
        }

        if (skippedThisSecond > health.PlannedFps / 3)
        {
            return $"{skippedThisSecond} captures a second were abandoned to keep the queue short";
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

        // Delivering well under what was planned, with frames to send, means something between the
        // encoder and the socket cannot keep up even though nothing has queued yet.
        if (health.PlannedFps > 0 && health.DeliveredFps > 0 && health.DeliveredFps < health.PlannedFps * 0.55)
        {
            return $"only {health.DeliveredFps:F0} of {health.PlannedFps:F0} frames a second were delivered";
        }

        return null;
    }
}
