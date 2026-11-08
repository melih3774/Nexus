using PcCore.Capabilities.Media;

using Xunit;

namespace PcCore.Tests.Media;

/// <summary>
/// When a still picture is encoded again: only after a frame the encoder had to skimp on, only once
/// the desktop has gone quiet, and never for longer than the keyframe interval allows.
/// </summary>
public sealed class StillRefinementTests
{
    /// <summary>10,416 bytes a frame, so half of it is the line between starved and comfortable.</summary>
    private static readonly MediaPlan Plan = new(1280, 720, 30, 2500, 10, "test");

    private const int Starved = 20_000;
    private const int Comfortable = 800;

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static int RunToEnd(StillRefinement refinement, TimeSpan now)
    {
        var repeats = 0;

        while (refinement.IsDue(now))
        {
            refinement.Repeated(Plan, Comfortable, keyframe: false);
            repeats++;
        }

        return repeats;
    }

    [Fact]
    public void AStarvedFrameIsRefinedForTheConfiguredTimeOnceTheDesktopIsQuiet()
    {
        var refinement = new StillRefinement(4);

        refinement.Changed(Plan, Starved, Ms(1000));

        Assert.True(refinement.IsPending);
        Assert.False(refinement.IsDue(Ms(1050)));
        Assert.True(refinement.IsDue(Ms(1100)));
        Assert.Equal(120, RunToEnd(refinement, Ms(1100)));
        Assert.False(refinement.IsPending);
    }

    [Fact]
    public void AFrameInsideItsBudgetDoesNotStartARefinement()
    {
        var refinement = new StillRefinement(4);

        refinement.Changed(Plan, Comfortable, Ms(1000));

        Assert.False(refinement.IsPending);
        Assert.False(refinement.IsDue(Ms(5000)));
    }

    [Fact]
    public void ABlinkingCaretDelaysARunningRefinementWithoutEndingOrRestartingIt()
    {
        var refinement = new StillRefinement(4);

        refinement.Changed(Plan, Starved, Ms(0));

        refinement.Repeated(Plan, Comfortable, keyframe: false);
        refinement.Repeated(Plan, Comfortable, keyframe: false);

        refinement.Changed(Plan, Comfortable, Ms(500));

        Assert.False(refinement.IsDue(Ms(550)));
        Assert.Equal(118, RunToEnd(refinement, Ms(600)));
    }

    [Fact]
    public void ANewStarvedFrameStartsAgain()
    {
        var refinement = new StillRefinement(4);

        refinement.Changed(Plan, Starved, Ms(0));
        refinement.Repeated(Plan, Comfortable, keyframe: false);
        refinement.Changed(Plan, Starved, Ms(500));

        Assert.Equal(120, RunToEnd(refinement, Ms(600)));
    }

    [Fact]
    public void AKeyframeDuringARefinementStartsItOverBecauseItThrewTheWorkAway()
    {
        var refinement = new StillRefinement(4);

        refinement.Changed(Plan, Starved, Ms(0));

        for (var i = 0; i < 50; i++)
        {
            refinement.Repeated(Plan, Comfortable, keyframe: false);
        }

        refinement.Repeated(Plan, Starved, keyframe: true);

        Assert.Equal(120, RunToEnd(refinement, Ms(100)));
    }

    [Fact]
    public void AnEncoderThatKeepsProducingKeyframesCannotKeepARefinementRunningForever()
    {
        var refinement = new StillRefinement(4);
        var repeats = 0;

        refinement.Changed(Plan, Starved, Ms(0));

        while (refinement.IsDue(Ms(100)) && repeats < 10_000)
        {
            refinement.Repeated(Plan, Starved, keyframe: true);
            repeats++;
        }

        Assert.True(repeats < 200, $"{repeats} repeats");
    }

    [Fact]
    public void ARefinementStopsShortOfTheNextScheduledKeyframe()
    {
        var shortGop = Plan with { GopSeconds = 2 };
        var refinement = new StillRefinement(4);

        refinement.Changed(shortGop, Starved, Ms(0));

        var repeats = 0;

        while (refinement.IsDue(Ms(100)))
        {
            refinement.Repeated(shortGop, Comfortable, keyframe: false);
            repeats++;
        }

        Assert.Equal(58, repeats);
    }

    [Fact]
    public void ZeroSecondsTurnsItOff()
    {
        var refinement = new StillRefinement(0);

        refinement.Changed(Plan, Starved, Ms(0));

        Assert.False(refinement.IsPending);
    }

    [Fact]
    public void StoppingAbandonsWhatWasOwed()
    {
        var refinement = new StillRefinement(4);

        refinement.Changed(Plan, Starved, Ms(0));
        refinement.Stop();

        Assert.False(refinement.IsPending);
        Assert.False(refinement.IsDue(Ms(5000)));
    }
}
