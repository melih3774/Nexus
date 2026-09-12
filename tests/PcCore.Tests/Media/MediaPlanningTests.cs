using PcCore.Capabilities.Media;
using PcCore.Tests.TestSupport;

using Xunit;

namespace PcCore.Tests.Media;

/// <summary>
/// What a profile and a resolution resolve to, and when the adaptive controller may move them.
/// <para>
/// These are the rules the owner sees the consequences of without ever seeing the rules: that a
/// resolution they picked stays picked, that "Smooth" really is more frames and "Quality" really is
/// more bits, that nothing is ever encoded larger than it was captured, and that adaptation is quick
/// to back off and slow to come back.
/// </para>
/// </summary>
public sealed class MediaPlanningTests
{
    private static MediaQuality Q(
        StreamingProfile profile,
        StreamingResolution resolution = StreamingResolution.Auto) =>
        new() { Profile = profile, Resolution = resolution };

    [Theory]
    [InlineData(StreamingResolution.P360, 640, 360)]
    [InlineData(StreamingResolution.P540, 960, 540)]
    [InlineData(StreamingResolution.P720, 1280, 720)]
    [InlineData(StreamingResolution.P1080, 1920, 1080)]
    [InlineData(StreamingResolution.Native, 1920, 1080)]
    public void AnExplicitResolutionIsWhatGetsEncoded(StreamingResolution resolution, int width, int height)
    {
        var plan = MediaPlanner.Resolve(Q(StreamingProfile.Balanced, resolution), MediaTarget.Screen, 1920, 1080);

        Assert.Equal((width, height), (plan.Width, plan.Height));
    }

    [Fact]
    public void AutoAdaptationNeverOverridesAResolutionTheOwnerChose()
    {
        var quality = Q(StreamingProfile.Auto, StreamingResolution.P1080);

        var top = MediaPlanner.Resolve(quality, MediaTarget.Screen, 1920, 1080, level: 0);

        for (var level = 1; level <= MediaPlanner.MaxLevel; level++)
        {
            var stepped = MediaPlanner.Resolve(quality, MediaTarget.Screen, 1920, 1080, level);

            Assert.Equal(1080, stepped.Height);
            Assert.True(stepped.BitrateKbps < top.BitrateKbps, $"level {level} did not spend less");
        }
    }

    [Fact]
    public void AutoAdaptationLowersTheResolutionWhenTheOwnerLeftItOnAuto()
    {
        var quality = Q(StreamingProfile.Auto);

        var heights = Enumerable.Range(0, MediaPlanner.MaxLevel + 1)
            .Select(level => MediaPlanner.Resolve(quality, MediaTarget.Screen, 1920, 1080, level).Height)
            .ToArray();

        Assert.Equal(720, heights[0]);

        for (var i = 1; i < heights.Length; i++)
        {
            Assert.True(heights[i] <= heights[i - 1], $"step {i} went up: {string.Join(",", heights)}");
        }

        Assert.True(heights[^1] < heights[0]);
    }

    [Fact]
    public void AManualProfileIgnoresTheAdaptiveLevel()
    {
        var quality = Q(StreamingProfile.Balanced);

        Assert.Equal(
            MediaPlanner.Resolve(quality, MediaTarget.Screen, 1920, 1080, level: 0),
            MediaPlanner.Resolve(quality, MediaTarget.Screen, 1920, 1080, level: 4));
    }

    [Fact]
    public void SmoothIsMoreFramesAndQualityIsMoreBitsThanBalanced()
    {
        var smooth = MediaPlanner.Resolve(Q(StreamingProfile.Smooth, StreamingResolution.P720), MediaTarget.Screen, 1920, 1080);
        var balanced = MediaPlanner.Resolve(Q(StreamingProfile.Balanced, StreamingResolution.P720), MediaTarget.Screen, 1920, 1080);
        var quality = MediaPlanner.Resolve(Q(StreamingProfile.Quality, StreamingResolution.P720), MediaTarget.Screen, 1920, 1080);

        Assert.True(smooth.Fps > balanced.Fps);
        Assert.True(quality.Fps < balanced.Fps);

        // Bits per frame is the thing Quality buys and Smooth gives up.
        Assert.True(quality.BitrateKbps / (double)quality.Fps > balanced.BitrateKbps / (double)balanced.Fps);
        Assert.True(smooth.BitrateKbps / (double)smooth.Fps < balanced.BitrateKbps / (double)balanced.Fps);
    }

    [Fact]
    public void CustomIsExactlyWhatWasTyped()
    {
        var quality = new MediaQuality
        {
            Profile = StreamingProfile.Custom,
            CustomHeight = 540,
            CustomFps = 45,
            CustomBitrateKbps = 3300,
        };

        var plan = MediaPlanner.Resolve(quality, MediaTarget.Screen, 1920, 1080, level: 3);

        Assert.Equal((960, 540, 45, 3300), (plan.Width, plan.Height, plan.Fps, plan.BitrateKbps));
    }

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(2560, 1600)]
    [InlineData(1280, 1024)]
    [InlineData(641, 481)]
    public void NothingIsEncodedLargerThanCapturedOrWithOddDimensions(int sourceWidth, int sourceHeight)
    {
        foreach (var resolution in Enum.GetValues<StreamingResolution>())
        {
            foreach (var profile in Enum.GetValues<StreamingProfile>())
            {
                var plan = MediaPlanner.Resolve(Q(profile, resolution), MediaTarget.Screen, sourceWidth, sourceHeight);

                Assert.True(plan.Width <= sourceWidth && plan.Height <= sourceHeight, $"{profile}/{resolution} enlarged: {plan}");
                Assert.Equal(0, plan.Width % 2);
                Assert.Equal(0, plan.Height % 2);
            }
        }
    }

    [Fact]
    public void TheCameraCostsLessThanTheDesktopAtTheSameSize()
    {
        var screen = MediaPlanner.Resolve(Q(StreamingProfile.Balanced, StreamingResolution.P720), MediaTarget.Screen, 1280, 720);
        var camera = MediaPlanner.Resolve(Q(StreamingProfile.Balanced, StreamingResolution.P720), MediaTarget.Camera, 1280, 720);

        Assert.True(camera.BitrateKbps < screen.BitrateKbps);
    }

    [Fact]
    public void ValuesAClientCouldSendAreClampedBeforeTheyReachAnEncoder()
    {
        var hostile = new MediaQuality
        {
            Profile = StreamingProfile.Custom,
            CustomHeight = 100_000,
            CustomFps = -5,
            CustomBitrateKbps = int.MaxValue,
        }.Sanitised();

        Assert.InRange(hostile.CustomHeight, 144, 2160);
        Assert.InRange(hostile.CustomFps, 1, 60);
        Assert.InRange(hostile.CustomBitrateKbps, 100, 60_000);
    }

    [Fact]
    public void SettingsSurviveARestart()
    {
        var first = TestMediaQuality.Store(out var directory);

        first.Update(MediaTarget.Camera, Q(StreamingProfile.Smooth, StreamingResolution.P360));
        first.Update(MediaTarget.Screen, Q(StreamingProfile.Quality, StreamingResolution.P1080));

        var second = new MediaQualityStore(
            new PcCore.Capabilities.Storage.StateDirectory(directory), TestLog.For<MediaQualityStore>());

        Assert.Equal(StreamingProfile.Smooth, second.Current.Camera.Profile);
        Assert.Equal(StreamingResolution.P360, second.Current.Camera.Resolution);
        Assert.Equal(StreamingProfile.Quality, second.Current.Screen.Profile);
        Assert.Equal(StreamingResolution.P1080, second.Current.Screen.Resolution);
    }

    // --- The adaptive controller -------------------------------------------------------------------

    private static StreamHealth Healthy => new(0, 0, 30, 80, 4);

    /// <summary>A controller whose start-up settling period is already over.</summary>
    private static AdaptiveStreamController Settled(AdvanceableTimeProvider clock)
    {
        var controller = new AdaptiveStreamController(clock);
        clock.Advance(AdaptiveStreamController.SettleInterval + TimeSpan.FromSeconds(1));
        return controller;
    }

    /// <summary>Feeds one observation a second, returning whether any of them changed the level.</summary>
    private static bool ObserveFor(AdvanceableTimeProvider clock, AdaptiveStreamController controller, StreamHealth health, int seconds)
    {
        var changed = false;

        for (var i = 0; i < seconds; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            changed |= controller.Observe(health);
        }

        return changed;
    }

    [Fact]
    public void ABacklogThatPersistsStepsDown()
    {
        var clock = new AdvanceableTimeProvider();
        var controller = Settled(clock);
        var strained = Healthy with { Backlog = AdaptiveStreamController.BacklogLimit + 1 };

        Assert.False(controller.Observe(strained));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(controller.Observe(strained));
        Assert.Equal(1, controller.Level);
        Assert.Contains("waiting", controller.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OneStrainedSecondBetweenHealthyOnesIsNotEnough()
    {
        // A keyframe on a busy link, or a Wi-Fi hiccup, is one bad second. Stepping down on it is how
        // a stream that was fine ends up at a lower resolution for the next twelve seconds.
        var clock = new AdvanceableTimeProvider();
        var controller = Settled(clock);
        var strained = Healthy with { Backlog = 9 };

        for (var i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.False(controller.Observe(i % 2 == 0 ? strained : Healthy));
        }

        Assert.Equal(0, controller.Level);
    }

    [Fact]
    public void HighPhoneLatencyStepsDown()
    {
        var clock = new AdvanceableTimeProvider();
        var controller = Settled(clock);
        var slow = Healthy with { ClientLatencyMs = AdaptiveStreamController.LatencyLimitMs + 50 };

        controller.Observe(slow);
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(controller.Observe(slow));
        Assert.Contains("capture to screen", controller.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NoFreshLatencyReportIsNotStrain()
    {
        var clock = new AdvanceableTimeProvider();
        var controller = Settled(clock);

        Assert.False(ObserveFor(clock, controller, Healthy with { ClientLatencyMs = -1, ClientDecodeMs = -1 }, 30));
        Assert.Equal(0, controller.Level);
    }

    [Fact]
    public void AQuietDesktopIsNotCongestion()
    {
        // A desktop only produces a frame when something changes. An idle screen delivers almost
        // nothing over a link that could carry everything, and that must never read as a slow link.
        var clock = new AdvanceableTimeProvider();
        var controller = Settled(clock);

        Assert.False(ObserveFor(clock, controller, new StreamHealth(0, 0, 30, -1, -1), 60));
        Assert.Equal(0, controller.Level);
    }

    [Fact]
    public void StrainRightAfterAChangeIsTheChangeItselfAndIsIgnored()
    {
        // The regression from the phone: a step down rebuilt the encoder, the phone's decoder
        // restarted, its latency spiked, and that spike — reported twice — caused two more steps.
        var clock = new AdvanceableTimeProvider();
        var controller = Settled(clock);
        var strained = Healthy with { ClientLatencyMs = 2593 };

        controller.Observe(strained);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(controller.Observe(strained));
        Assert.Equal(1, controller.Level);

        // Everything inside the settling period is ignored, however bad it looks.
        var settle = (int)AdaptiveStreamController.SettleInterval.TotalSeconds;

        for (var i = 1; i < settle; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.False(controller.Observe(strained), $"stepped again {i} s after the last change");
        }

        Assert.Equal(1, controller.Level);
    }

    [Fact]
    public void ANoteOfDisruptionRestartsTheSettlingPeriod()
    {
        var clock = new AdvanceableTimeProvider();
        var controller = Settled(clock);
        var strained = Healthy with { Backlog = 9 };

        controller.NoteDisruption();

        Assert.False(ObserveFor(clock, controller, strained, (int)AdaptiveStreamController.SettleInterval.TotalSeconds - 1));
        Assert.Equal(0, controller.Level);
    }

    [Fact]
    public void SkipsDuringSettlingAreNotCountedAfterIt()
    {
        // The skip count is a running total. Taking the difference only when out of the settling
        // period would charge every skip from those seconds to the first second after them.
        var clock = new AdvanceableTimeProvider();
        var controller = new AdaptiveStreamController(clock);

        long total = 0;

        for (var i = 0; i < (int)AdaptiveStreamController.SettleInterval.TotalSeconds - 1; i++)
        {
            total += 150;
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.False(controller.Observe(Healthy with { SkippedForBacklog = total }));
        }

        // The first second after settling has no new skips, so it is healthy; one strained second
        // after it is therefore the first of a run, not the second.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(controller.Observe(Healthy with { SkippedForBacklog = total }));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(controller.Observe(Healthy with { SkippedForBacklog = total, Backlog = 9 }));
        Assert.Equal(0, controller.Level);
    }

    [Fact]
    public void ItStepsBackUpOnlyAfterTwelveHealthySeconds()
    {
        var clock = new AdvanceableTimeProvider();
        var controller = Settled(clock);

        ObserveFor(clock, controller, Healthy with { Backlog = 9 }, 2);
        Assert.Equal(1, controller.Level);

        for (var second = 1; second < 12; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.False(controller.Observe(Healthy), $"stepped up after only {second} s");
        }

        clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.True(controller.Observe(Healthy));
        Assert.Equal(0, controller.Level);
    }

    [Fact]
    public void AStepUpThatDoesNotHoldMakesTheNextOneWaitLonger()
    {
        // The stream from the phone: up to 720p after twelve seconds, down again two seconds later,
        // and round again. Each premature step up doubles the healthy run the next one needs.
        var clock = new AdvanceableTimeProvider();
        var controller = Settled(clock);
        var strained = Healthy with { Backlog = 9 };

        ObserveFor(clock, controller, strained, 2);
        Assert.Equal(1, controller.Level);

        Assert.True(ObserveFor(clock, controller, Healthy, 13));
        Assert.Equal(0, controller.Level);

        // Settle, then strain again straight away.
        ObserveFor(clock, controller, Healthy, (int)AdaptiveStreamController.SettleInterval.TotalSeconds);
        ObserveFor(clock, controller, strained, 2);
        Assert.Equal(1, controller.Level);
        Assert.Equal(AdaptiveStreamController.StepUpInterval * 2, controller.CurrentStepUpInterval);

        // Twelve healthy seconds are no longer enough.
        Assert.False(ObserveFor(clock, controller, Healthy, 13));
        Assert.Equal(1, controller.Level);

        Assert.True(ObserveFor(clock, controller, Healthy, 12));
        Assert.Equal(0, controller.Level);
    }

    [Fact]
    public void TheStepUpWaitIsCapped()
    {
        var clock = new AdvanceableTimeProvider();
        var controller = Settled(clock);
        var strained = Healthy with { Backlog = 9 };

        ObserveFor(clock, controller, strained, 2);

        // Up as soon as allowed, a moment to settle, then straight back down: a premature step up
        // every round.
        for (var round = 0; round < 8; round++)
        {
            Assert.True(ObserveFor(clock, controller, Healthy, (int)controller.CurrentStepUpInterval.TotalSeconds + 3));
            ObserveFor(clock, controller, strained, 2);
            Assert.Equal(1, controller.Level);
        }

        Assert.Equal(AdaptiveStreamController.MaxStepUpInterval, controller.CurrentStepUpInterval);
    }

    [Fact]
    public void AWebcamThatIsSimplySlowIsNotCongestion()
    {
        var clock = new AdvanceableTimeProvider();
        var controller = Settled(clock);

        Assert.False(ObserveFor(clock, controller, new StreamHealth(0, 0, 30, -1, -1), 20));
        Assert.Equal(0, controller.Level);
    }

    // --- Annex-B ---------------------------------------------------------------------------------------

    [Fact]
    public void ParameterSetsAreFoundWhateverStartCodeCarriesThem()
    {
        byte[] stream =
        [
            0, 0, 0, 1, 0x67, 1, 2, 3,
            0, 0, 1, 0x68, 4, 5,
            0, 0, 0, 1, 0x65, 9, 9, 9,
        ];

        Assert.Equal([7, 8, 5], AnnexB.Units(stream).Select(u => u.Type));
        Assert.True(AnnexB.CarriesParameterSets(stream));
        Assert.Equal(stream[..14], AnnexB.ExtractParameterSets(stream));
    }

    [Fact]
    public void APredictedFrameCarriesNoParameterSets()
    {
        byte[] slice = [0, 0, 0, 1, 0x41, 1, 2, 3];

        Assert.False(AnnexB.CarriesParameterSets(slice));
        Assert.Null(AnnexB.ExtractParameterSets(slice));
    }

    [Fact]
    public void MalformedBytesYieldNothingRatherThanThrowing()
    {
        Assert.Empty(AnnexB.Units([]));
        Assert.Empty(AnnexB.Units([1, 2, 3, 4, 5]));
        Assert.Empty(AnnexB.Units([0, 0, 1]));
    }
}
