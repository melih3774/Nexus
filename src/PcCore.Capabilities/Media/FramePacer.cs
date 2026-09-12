using System.Diagnostics;

namespace PcCore.Capabilities.Media;

/// <summary>
/// Paces a capture loop to a frame rate on a schedule rather than by sleeping for what is left.
/// <para>
/// <b>The bug this replaces.</b> The loops slept for "the interval minus the time the frame took".
/// That is right on paper and wrong on Windows, where a timer wait is rounded up to the system tick
/// of about 15.6 ms: asking to sleep 12 ms sleeps 15.6, asking for 17 sleeps 31. Measured on this
/// machine, a pipeline that captured and encoded a 1080p frame in 22 ms — comfortably inside a
/// 33 ms budget — delivered 20.8 frames a second instead of 30, and conversion and encoding were
/// both already fast enough. The time was all in oversleeping.
/// </para>
/// <para>
/// <b>What it does instead.</b> Every frame has a deadline one interval after the last one's. A wait
/// that overshoots makes the next wait shorter, so the average rate is the one asked for even though
/// no single wait is precise. When the loop falls a whole interval behind — a slow frame, a busy
/// machine — the schedule restarts from now rather than trying to catch up, because a burst of frames
/// to make up the count is exactly the backlog the rest of the pipeline works to avoid.
/// </para>
/// </summary>
public sealed class FramePacer
{
    private long _next;

    /// <summary>Waits until this frame's slot. Returns immediately when the loop is already late.</summary>
    public async ValueTask WaitAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        var now = Stopwatch.GetTimestamp();
        var step = (long)(interval.TotalSeconds * Stopwatch.Frequency);

        if (_next == 0 || now - _next > step)
        {
            _next = now + step;
            return;
        }

        _next += step;

        var wait = _next - now;

        if (wait <= 0)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Forgets the schedule, for a loop that has been paused.</summary>
    public void Reset() => _next = 0;
}
