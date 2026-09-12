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

    private static StreamHealth Healthy => new(0, 0, 30, 30, 80, 4);

    [Fact]
    public void ABacklogStepsDownAtOnce()
    {
        var clock = new AdvanceableTimeProvider();
        var controller = new AdaptiveStreamController(clock);

        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.True(controller.Observe(Healthy with { Backlog = AdaptiveStreamController.BacklogLimit + 1 }));
        Assert.Equal(1, controller.Level);
        Assert.Contains("waiting", controller.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void HighPhoneLatencyStepsDown()
    {
        var clock = new AdvanceableTimeProvider();
        var controller = new AdaptiveStreamController(clock);

        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.True(controller.Observe(Healthy with { ClientLatencyMs = AdaptiveStreamController.LatencyLimitMs + 50 }));
        Assert.Contains("capture to screen", controller.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ItDoesNotStepDownMoreThanOnceEveryTwoSeconds()
    {
        var clock = new AdvanceableTimeProvider();
        var controller = new AdaptiveStreamController(clock);
        var strained = Healthy with { Backlog = 9 };

        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.True(controller.Observe(strained));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(controller.Observe(strained));

        clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.True(controller.Observe(strained));
        Assert.Equal(2, controller.Level);
    }

    [Fact]
    public void ItStepsBackUpOnlyAfterTwelveHealthySeconds()
    {
        var clock = new AdvanceableTimeProvider();
        var controller = new AdaptiveStreamController(clock);

        clock.Advance(TimeSpan.FromSeconds(3));
        controller.Observe(Healthy with { Backlog = 9 });
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
    public void AWebcamThatIsSimplySlowIsNotCongestion()
    {
        // The camera passes no delivered rate precisely so that a dim room at fifteen frames does not
        // read as a link that cannot keep up.
        var clock = new AdvanceableTimeProvider();
        var controller = new AdaptiveStreamController(clock);

        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.False(controller.Observe(new StreamHealth(0, 0, 0, 30, -1, -1)));
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
