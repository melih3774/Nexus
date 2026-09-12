using PcCore.Capabilities.Media;
using PcCore.Capabilities.Screen;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;

using Xunit;

namespace PcCore.Tests.Screen;

/// <summary>
/// The desktop stream's video path, against an encoder that behaves like H.264 without a GPU.
/// <para>
/// What matters here is everything a phone depends on and cannot check for itself: that the first
/// thing it receives is enough to configure a decoder, that it can get a keyframe whenever it loses
/// its place, that a settings change or a display change arrives as a fresh configuration rather than
/// as pictures of the wrong size, that a broken encoder degrades to still frames, and that the Auto
/// profile backs off on what the phone reports — without ever touching a resolution the owner chose.
/// </para>
/// </summary>
public sealed class DesktopVideoStreamTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public Rig(bool videoAvailable = true, Action<MediaQualityStore>? configure = null, bool acceptsBitrateChanges = true)
        {
            Capture = new FakeDesktopStream();
            Encoders = new FakeVideoEncoderFactory(videoAvailable, acceptsBitrateChanges: acceptsBitrateChanges);
            Quality = TestMediaQuality.Store();
            configure?.Invoke(Quality);

            Service = new DesktopStreamService(
                Capture,
                new FakeFrameEncoder(),
                Encoders,
                Quality,
                new StreamOptions { FrameTimeoutMs = 20, DiagnosticsIntervalSeconds = 0, MaxBacklog = 2 },
                TestLog.For<DesktopStreamService>());
        }

        public FakeDesktopStream Capture { get; }
        public FakeVideoEncoderFactory Encoders { get; }
        public MediaQualityStore Quality { get; }
        public DesktopStreamService Service { get; }

        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }

    private sealed record Received(StreamFrameHeader Header, byte[] Payload);

    /// <summary>
    /// Reads frames the way the transport does — reporting each one sent — until the condition holds.
    /// </summary>
    private static async Task<List<Received>> ReadUntilAsync(
        DesktopStreamSession session,
        Func<List<Received>, bool> done,
        int timeoutMs = 5000)
    {
        var received = new List<Received>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(timeoutMs);

        try
        {
            while (!done(received))
            {
                var message = await session.Frames.ReadAsync(timeout.Token);
                session.ReportSent();

                Assert.True(StreamFrameHeader.TryParse(message, out var header));
                received.Add(new Received(header, message[StreamFrameHeader.Size..]));
            }
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            // The condition never held; the caller's assertion says what was missing.
        }

        return received;
    }

    [Fact]
    public async Task TheFirstFramesAreAConfigurationThenAKeyframeThatCarriesItsOwnParameterSets()
    {
        await using var rig = new Rig();
        var session = rig.Service.Start("conn-1", null);

        var frames = await ReadUntilAsync(session, f => f.Count >= 3);

        Assert.True(frames.Count >= 3);
        Assert.All(frames, f => Assert.Equal(FrameEncoding.H264, f.Header.Encoding));

        Assert.True(frames[0].Header.Flags.HasFlag(FrameFlags.CodecConfig), "the first frame did not configure the decoder");
        Assert.True(frames[1].Header.Flags.HasFlag(FrameFlags.Keyframe), "the first picture was not a keyframe");
        Assert.True(AnnexB.CarriesParameterSets(frames[1].Payload), "the keyframe cannot be decoded alone");
        Assert.False(frames[2].Header.Flags.HasFlag(FrameFlags.Keyframe));

        // Sequence numbers are contiguous, which is what lets the phone notice a gap at all.
        for (var i = 1; i < frames.Count; i++)
        {
            Assert.Equal(frames[i - 1].Header.Sequence + 1, frames[i].Header.Sequence);
        }

        await rig.Service.StopAsync("conn-1");
    }

    [Fact]
    public async Task APhoneThatLostItsPlaceGetsAKeyframeWhenItAsks()
    {
        await using var rig = new Rig();
        var session = rig.Service.Start("conn-1", null);

        await ReadUntilAsync(session, f => f.Count >= 5);

        session.RequestKeyframe();

        var after = await ReadUntilAsync(session, f => f.Any(x => x.Header.Flags.HasFlag(FrameFlags.Keyframe)), 2000);

        Assert.Contains(after, f => f.Header.Flags.HasFlag(FrameFlags.Keyframe));
        Assert.True(after.Count <= 3, $"the keyframe took {after.Count} frames to arrive");

        await rig.Service.StopAsync("conn-1");
    }

    [Fact]
    public async Task ChangingTheSettingsMidStreamArrivesAsANewConfigurationAtTheNewSize()
    {
        await using var rig = new Rig(configure: q =>
            q.Update(MediaTarget.Screen, new MediaQuality { Profile = StreamingProfile.Balanced, Resolution = StreamingResolution.P720 }));

        var session = rig.Service.Start("conn-1", null);

        var before = await ReadUntilAsync(session, f => f.Count >= 3);
        Assert.Equal(1280, before[^1].Header.Width);

        rig.Quality.Update(
            MediaTarget.Screen,
            new MediaQuality { Profile = StreamingProfile.Balanced, Resolution = StreamingResolution.P360 });

        var after = await ReadUntilAsync(session, f => f.Count(x => x.Header.Width == 640) >= 3);
        var firstSmall = after.FindIndex(f => f.Header.Width == 640);

        Assert.True(firstSmall >= 0, "the stream never changed size");
        Assert.True(after[firstSmall].Header.Flags.HasFlag(FrameFlags.CodecConfig), "the new size did not start with a configuration");
        Assert.True(after[firstSmall + 1].Header.Flags.HasFlag(FrameFlags.Keyframe), "the new size did not start with a keyframe");
        Assert.Equal(1, rig.Encoders.Created[0].Disposals);

        await rig.Service.StopAsync("conn-1");
    }

    [Fact]
    public async Task ALostCaptureRestartsTheEncoderAndTheStreamResumesAtAKeyframe()
    {
        await using var rig = new Rig();

        var session = rig.Service.Start("conn-1", null);

        var frames = await ReadUntilAsync(session, f => f.Count >= 3);

        // Mid-stream: a mode change invalidates the duplication, which can mean a new size.
        rig.Capture.Script(CaptureResult.Lost("display mode changed"));

        frames.AddRange(await ReadUntilAsync(session, f => f.Any(x => x.Header.Flags.HasFlag(FrameFlags.Keyframe))));
        var configs = frames.Select((f, i) => (f, i)).Where(x => x.f.Header.Flags.HasFlag(FrameFlags.CodecConfig)).ToList();

        Assert.True(configs.Count >= 2, "the stream did not reconfigure after the capture was lost");
        Assert.True(rig.Encoders.Created.Count >= 2);
        Assert.True(frames[configs[1].i + 1].Header.Flags.HasFlag(FrameFlags.Keyframe));

        await rig.Service.StopAsync("conn-1");
    }

    [Fact]
    public async Task WithNoVideoEncoderTheStreamIsStillFrames()
    {
        await using var rig = new Rig(videoAvailable: false);
        var session = rig.Service.Start("conn-1", null);

        var frames = await ReadUntilAsync(session, f => f.Count >= 2);

        Assert.All(frames, f => Assert.Equal(FrameEncoding.Jpeg, f.Header.Encoding));
        Assert.Equal("JPEG still frames", session.Encoder);

        await rig.Service.StopAsync("conn-1");
    }

    [Fact]
    public async Task AnAutoStreamBacksOffWhenThePhoneReportsItIsBehind()
    {
        await using var rig = new Rig();
        var session = rig.Service.Start("conn-1", null);

        await ReadUntilAsync(session, f => f.Count >= 3);
        Assert.Equal(1280, session.Plan.Width);
        var initialBitrate = session.Plan.BitrateKbps;

        var frames = await ReadUntilAsync(
            session,
            f =>
            {
                session.ReportClient(new ClientPlayback(LatencyMs: 900, DecodeMs: 5, RenderedFps: 12, Dropped: 0));
                return session.Plan.BitrateKbps < initialBitrate;
            },
            timeoutMs: 12000);

        Assert.True(session.Plan.BitrateKbps < initialBitrate, "the strained stream did not back off");
        Assert.Contains("capture to screen", session.Statistics().AdaptiveReason, StringComparison.Ordinal);

        // The first step is the cheap one: the running encoder spends less. No new encoder, and so no
        // gap and no keyframe for the phone to wait on.
        Assert.Single(rig.Encoders.Created);
        Assert.Equal([session.Plan.BitrateKbps], rig.Encoders.Created[0].BitrateChanges);
        Assert.All(frames, f => Assert.Equal(1280, f.Header.Width));

        await rig.Service.StopAsync("conn-1");
    }

    [Fact]
    public async Task ABitrateChangeIsAppliedToTheRunningEncoderWithoutAKeyframe()
    {
        await using var rig = new Rig(configure: q => q.Update(
            MediaTarget.Screen,
            new MediaQuality { Profile = StreamingProfile.Custom, CustomHeight = 720, CustomFps = 30, CustomBitrateKbps = 2500 }));

        var session = rig.Service.Start("conn-1", null);

        await ReadUntilAsync(session, f => f.Count >= 4);

        rig.Quality.Update(
            MediaTarget.Screen,
            new MediaQuality { Profile = StreamingProfile.Custom, CustomHeight = 720, CustomFps = 30, CustomBitrateKbps = 1200 });

        var after = await ReadUntilAsync(session, f => session.Plan.BitrateKbps == 1200 && f.Count >= 10);

        Assert.Equal(1200, session.Plan.BitrateKbps);
        Assert.Single(rig.Encoders.Created);
        Assert.Equal([1200], rig.Encoders.Created[0].BitrateChanges);
        Assert.Equal(0, rig.Encoders.Created[0].Disposals);
        Assert.DoesNotContain(after, f => f.Header.Flags.HasFlag(FrameFlags.CodecConfig));
        Assert.DoesNotContain(after, f => f.Header.Flags.HasFlag(FrameFlags.Keyframe));

        await rig.Service.StopAsync("conn-1");
    }

    [Fact]
    public async Task AnEncoderThatCannotChangeItsBitrateIsReplacedInstead()
    {
        await using var rig = new Rig(
            acceptsBitrateChanges: false,
            configure: q => q.Update(
                MediaTarget.Screen,
                new MediaQuality { Profile = StreamingProfile.Custom, CustomHeight = 720, CustomFps = 30, CustomBitrateKbps = 2500 }));

        var session = rig.Service.Start("conn-1", null);

        await ReadUntilAsync(session, f => f.Count >= 4);

        rig.Quality.Update(
            MediaTarget.Screen,
            new MediaQuality { Profile = StreamingProfile.Custom, CustomHeight = 720, CustomFps = 30, CustomBitrateKbps = 1200 });

        var after = await ReadUntilAsync(
            session,
            f => session.Plan.BitrateKbps == 1200 && f.Any(x => x.Header.Flags.HasFlag(FrameFlags.Keyframe)));

        Assert.Equal(1200, session.Plan.BitrateKbps);
        Assert.Equal(2, rig.Encoders.Created.Count);
        Assert.Equal(1200, rig.Encoders.Created[1].Settings.BitrateKbps);
        Assert.Contains(after, f => f.Header.Flags.HasFlag(FrameFlags.Keyframe));

        await rig.Service.StopAsync("conn-1");
    }

    [Fact]
    public async Task ANewSizeIsBuiltWhileThePictureKeepsMoving()
    {
        // A hardware encoder takes a noticeable time to build. The stream used to stop capturing for
        // all of it, which the phone saw as a frozen picture followed by a large keyframe - and the
        // adaptive controller then saw as a slow link.
        await using var rig = new Rig(configure: q =>
            q.Update(MediaTarget.Screen, new MediaQuality { Profile = StreamingProfile.Balanced, Resolution = StreamingResolution.P720 }));

        var session = rig.Service.Start("conn-1", null);

        await ReadUntilAsync(session, f => f.Count >= 3);

        using var gate = new ManualResetEventSlim(false);
        rig.Encoders.CreationGate = gate;

        var changedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        rig.Quality.Update(
            MediaTarget.Screen,
            new MediaQuality { Profile = StreamingProfile.Balanced, Resolution = StreamingResolution.P360 });

        // While the new encoder cannot finish, pictures captured after the change must keep arriving
        // at the old size. Counted by capture time rather than position, because frames queued before
        // the change arrive either way. A loop that built the encoder in line would be stuck behind
        // the gate and deliver none.
        var during = await ReadUntilAsync(
            session,
            f => f.Count(x => x.Header.TimestampUnixMs > changedAt + 100) >= 5);

        Assert.True(
            during.Count(f => f.Header.TimestampUnixMs > changedAt + 100) >= 5,
            "the picture stopped while the new encoder was being built");
        Assert.All(during, f => Assert.Equal(1280, f.Header.Width));

        gate.Set();

        var after = await ReadUntilAsync(session, f => f.Count(x => x.Header.Width == 640) >= 3);
        var firstSmall = after.FindIndex(f => f.Header.Width == 640);

        Assert.True(firstSmall >= 0, "the stream never changed size");
        Assert.All(after.Take(firstSmall), f => Assert.Equal(1280, f.Header.Width));

        Assert.True(after[firstSmall].Header.Flags.HasFlag(FrameFlags.CodecConfig));
        Assert.True(after[firstSmall + 1].Header.Flags.HasFlag(FrameFlags.Keyframe));
        Assert.Equal(1, rig.Encoders.Created[0].Disposals);

        await rig.Service.StopAsync("conn-1");
    }

    [Fact]
    public async Task AReportIsTakenOnceAndNotReadAgain()
    {
        await using var rig = new Rig();
        var session = rig.Service.Start("conn-1", null);

        Assert.Null(session.TakeFreshClient());

        session.ReportClient(new ClientPlayback(LatencyMs: 2593, DecodeMs: 4, RenderedFps: 0, Dropped: 12));

        Assert.Equal(2593, session.TakeFreshClient()?.LatencyMs);
        Assert.Null(session.TakeFreshClient());

        // The last report is still what the log and the owner's statistics show.
        Assert.Equal(2593, session.Statistics().Client.LatencyMs);

        await rig.Service.StopAsync("conn-1");
    }

    [Fact]
    public async Task AResolutionTheOwnerChoseSurvivesTheSameStrain()
    {
        await using var rig = new Rig(configure: q =>
            q.Update(MediaTarget.Screen, new MediaQuality { Profile = StreamingProfile.Auto, Resolution = StreamingResolution.P720 }));

        var session = rig.Service.Start("conn-1", null);
        var initialBitrate = session.Plan.BitrateKbps;

        var frames = await ReadUntilAsync(
            session,
            f =>
            {
                session.ReportClient(new ClientPlayback(LatencyMs: 900, DecodeMs: 5, RenderedFps: 12, Dropped: 0));
                return session.Plan.BitrateKbps < initialBitrate && f.Count > 10;
            },
            timeoutMs: 12000);

        Assert.True(session.Plan.BitrateKbps < initialBitrate, "the strained stream did not spend less");
        Assert.All(frames, f => Assert.Equal(1280, f.Header.Width));

        await rig.Service.StopAsync("conn-1");
    }
}
