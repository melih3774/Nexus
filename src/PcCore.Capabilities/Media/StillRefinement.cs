namespace PcCore.Capabilities.Media;

/// <summary>
/// Decides when a picture that has stopped changing is worth encoding again.
/// <para>
/// <b>Why re-encoding the same picture does anything.</b> The frame that ends a burst of change is
/// the one the rate controller has the least budget for, and it is then the frame the owner looks at
/// for as long as nothing else moves. Measured on Quick Sync at 720p and 2.5 Mbit/s, the first still
/// frame after a screenful of new text decoded at 21 dB and was unreadable. Submitting the same
/// picture again lets each predicted frame correct a little more of the last one: 30 dB after a
/// second, 43 dB after three, and 45 dB — where the encoder stops spending — a little after that.
/// </para>
/// <para>
/// <b>Only a starved frame starts this.</b> A caret blinking or a character being typed changes a
/// few macroblocks, and with the whole frame budget to spend on them they are already sharp. Treating
/// those as a reason to refine would keep a 30 fps encode running under an idle text editor. They
/// neither start a refinement nor end one; they only push the next repeat back until the desktop has
/// been quiet again.
/// </para>
/// <para>
/// <b>A keyframe undoes the work.</b> An IDR is coded from nothing, at the same budget, and took the
/// picture from 45 dB back to 30. So the number of repeats stays inside one GOP, and a keyframe that
/// lands during a refinement starts it again — a bounded number of times, because an encoder that
/// ignores the configured GOP must not be able to keep this running forever.
/// </para>
/// </summary>
public sealed class StillRefinement
{
    /// <summary>How long the desktop must be quiet first. Long enough to span the frames of an animation.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(100);

    /// <summary>Keyframes one refinement will start over for.</summary>
    private const int MaxRestarts = 2;

    private readonly double _seconds;

    private int _remaining;
    private int _restarts;
    private TimeSpan _lastChange;

    /// <param name="seconds">How long a refinement runs at the plan's frame rate. Zero turns it off.</param>
    public StillRefinement(double seconds) => _seconds = seconds;

    /// <summary>True while repeats are still owed, so the capture loop should not wait long for news.</summary>
    public bool IsPending => _remaining > 0;

    /// <summary>A new picture was encoded. <paramref name="largestPacket"/> is 0 when none came out.</summary>
    public void Changed(MediaPlan plan, int largestPacket, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _lastChange = now;

        if (IsStarved(plan, largestPacket))
        {
            _remaining = Repeats(plan);
            _restarts = 0;
        }
    }

    /// <summary>True when the held picture should be encoded again now.</summary>
    public bool IsDue(TimeSpan now) => _remaining > 0 && now - _lastChange >= Settle;

    /// <summary>The held picture was encoded again.</summary>
    public void Repeated(MediaPlan plan, int largestPacket, bool keyframe)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (keyframe && IsStarved(plan, largestPacket) && _restarts < MaxRestarts)
        {
            _restarts++;
            _remaining = Repeats(plan);
            return;
        }

        if (_remaining > 0)
        {
            _remaining--;
        }
    }

    /// <summary>There is no longer a picture to repeat.</summary>
    public void Stop()
    {
        _remaining = 0;
        _restarts = 0;
    }

    /// <summary>
    /// Half a frame's share of the bitrate. The encoder spent at least that on this picture, so it
    /// was working to the budget rather than comfortably inside it.
    /// </summary>
    private static bool IsStarved(MediaPlan plan, int largestPacket) =>
        largestPacket * 2L >= plan.BitrateKbps * 1000L / 8 / Math.Max(1, plan.Fps);

    /// <summary>Stops short of the end of a GOP, so that a refinement does not finish on a keyframe.</summary>
    private int Repeats(MediaPlan plan)
    {
        var fps = Math.Max(1, plan.Fps);
        var wanted = (int)Math.Round(_seconds * fps);

        return Math.Clamp(wanted, 0, Math.Max(0, (fps * plan.GopSeconds) - 2));
    }
}
