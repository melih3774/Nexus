using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Screen;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using Xunit;

namespace PcCore.Tests.Screen;

/// <summary>
/// The streaming pipeline, without a desktop.
/// <para>
/// What is being tested here is everything that can go wrong that is not Direct3D: whether stale
/// frames are dropped rather than queued, whether a lost capture reopens, whether a stream stops
/// when nobody is watching, and whether a failure anywhere in it can take the host down. The
/// Direct3D half is verified against the real display by the host self-test.
/// </para>
/// </summary>
public sealed class DesktopStreamServiceTests
{
    private static DesktopStreamService Build(
        FakeDesktopStream capture,
        out FakeFrameEncoder encoder,
        StreamOptions? options = null)
    {
        encoder = new FakeFrameEncoder();

        return new DesktopStreamService(
            capture,
            encoder,
            options ?? new StreamOptions { MaxFps = 60, FrameTimeoutMs = 20, DiagnosticsIntervalSeconds = 0 },
            TestLog.For<DesktopStreamService>());
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        for (var i = 0; i < timeoutMs / 20 && !condition(); i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return condition();
    }

    [Fact]
    public async Task AStreamProducesWellFormedFrames()
    {
        var capture = new FakeDesktopStream();
        await using var service = Build(capture, out _);

        var session = service.Start("conn-1", displayIndex: null);

        var message = await session.Frames.ReadAsync(TestContext.Current.CancellationToken);

        Assert.True(StreamFrameHeader.TryParse(message, out var header));
        Assert.Equal(session.StreamId, header.StreamId);
        Assert.Equal(FrameEncoding.Jpeg, header.Encoding);
        Assert.True(header.Flags.HasFlag(FrameFlags.Complete));
        Assert.Equal(1920, header.DesktopWidth);
        Assert.Equal(message.Length - StreamFrameHeader.Size, header.PayloadLength);

        // The cursor came from the capture, and the frame says it is already drawn in.
        Assert.Equal(10, header.CursorX);
        Assert.Equal(20, header.CursorY);
        Assert.True(header.Flags.HasFlag(FrameFlags.CursorBakedIn));

        await service.StopAsync("conn-1");
    }

    [Fact]
    public async Task SequenceNumbersIncreaseWithinAStream()
    {
        var capture = new FakeDesktopStream();
        await using var service = Build(capture, out _);

        var session = service.Start("conn-1", null);
        var token = TestContext.Current.CancellationToken;

        var previous = 0UL;

        for (var i = 0; i < 3; i++)
        {
            var message = await session.Frames.ReadAsync(token);
            Assert.True(StreamFrameHeader.TryParse(message, out var header));
            Assert.True(header.Sequence > previous, $"sequence went {previous} -> {header.Sequence}");
            previous = header.Sequence;
        }

        await service.StopAsync("conn-1");
    }

    [Fact]
    public async Task ASlowReaderGetsTheNewestFrameAndTheRestAreDropped()
    {
        // The backpressure policy, observed. A reader that stops reading must not cause frames to
        // pile up: the channel holds one, and the count of dropped frames is what grows instead.
        var capture = new FakeDesktopStream();
        await using var service = Build(capture, out _);

        var session = service.Start("conn-1", null);

        // Let the loop run well ahead of any reader.
        await Task.Delay(400, TestContext.Current.CancellationToken);

        var stats = session.Statistics();
        Assert.True(stats.FramesEncoded > 3, $"only {stats.FramesEncoded} frames were encoded");
        Assert.True(stats.FramesDropped > 0, "nothing was dropped despite nobody reading");

        // Exactly one frame is waiting, and it is a recent one rather than the first.
        Assert.True(session.Frames.TryRead(out var message));
        Assert.False(session.Frames.TryRead(out _));

        Assert.True(StreamFrameHeader.TryParse(message!, out var header));
        Assert.True(header.Sequence > 1, "the frame kept was the oldest rather than the newest");

        await service.StopAsync("conn-1");
    }

    [Fact]
    public async Task ARecoverableCaptureLossReopensWithoutEndingTheStream()
    {
        // A resolution change or a display recreation invalidates the duplication. The stream is
        // expected to survive it, because the alternative is a black phone screen every time the
        // owner plugs in a monitor.
        var capture = new FakeDesktopStream();
        capture.Script(
            CaptureResult.TimedOut,
            CaptureResult.Lost("the display mode changed"));

        await using var service = Build(capture, out _);
        var session = service.Start("conn-1", null);

        Assert.True(await WaitAsync(() => capture.Opened.Count >= 2), "the capture was not reopened");

        // The first source was disposed rather than leaked, and frames flow again afterwards.
        Assert.True(capture.Opened[0].Disposed);

        var message = await session.Frames.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(StreamFrameHeader.TryParse(message, out _));
        Assert.True(session.Statistics().CaptureLosses >= 1);

        await service.StopAsync("conn-1");
    }

    [Fact]
    public async Task AnUnavailableDesktopIsRetriedRatherThanFatal()
    {
        // The lock screen and UAC prompts land here. They end, and the stream should still be alive
        // when they do.
        var capture = new FakeDesktopStream();
        capture.Script(CaptureResult.Unavailable("a secure desktop is in the foreground"));

        await using var service = Build(capture, out _);
        var session = service.Start("conn-1", null);

        Assert.True(await WaitAsync(() => session.Statistics().CaptureLosses >= 1));
        Assert.True(await WaitAsync(() => capture.Opened.Count >= 2), "capture was not retried");

        await service.StopAsync("conn-1");
    }

    [Fact]
    public async Task AFailureToOpenIsRetriedInsteadOfKillingTheLoop()
    {
        var capture = new FakeDesktopStream { OpenFailures = 2 };
        await using var service = Build(capture, out _);

        var session = service.Start("conn-1", null);

        var message = await session.Frames.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(StreamFrameHeader.TryParse(message, out _));

        await service.StopAsync("conn-1");
    }

    [Fact]
    public async Task StoppingReleasesTheCaptureAndReportsWhatHappened()
    {
        var capture = new FakeDesktopStream();
        await using var service = Build(capture, out _);

        service.Start("conn-1", null);
        await Task.Delay(150, TestContext.Current.CancellationToken);

        var stats = await service.StopAsync("conn-1");

        Assert.NotNull(stats);
        Assert.True(stats!.FramesEncoded > 0);
        Assert.True(stats.Elapsed > TimeSpan.Zero);

        // Nothing is left holding a duplication open for a viewer that has gone.
        Assert.Equal(0, service.ActiveStreams);
        Assert.All(capture.Opened, source => Assert.True(source.Disposed));
    }

    [Fact]
    public async Task StoppingSomethingThatIsNotRunningIsNotAnError()
    {
        var capture = new FakeDesktopStream();
        await using var service = Build(capture, out _);

        Assert.Null(await service.StopAsync("nobody"));
    }

    [Fact]
    public async Task StartingAgainReplacesTheOwnersPreviousStream()
    {
        // A phone that reconnected and asked again must not be refused because of a stream it can
        // no longer reach.
        var capture = new FakeDesktopStream();
        await using var service = Build(capture, out _);

        var first = service.Start("conn-1", null);
        var second = service.Start("conn-1", null);

        Assert.NotEqual(first.StreamId, second.StreamId);
        Assert.True(await WaitAsync(() => service.ActiveStreams == 1));

        await service.StopAsync("conn-1");
    }

    [Fact]
    public async Task ASecondViewerIsRefusedWhenOnlyOneStreamIsAllowed()
    {
        var capture = new FakeDesktopStream();
        await using var service = Build(
            capture,
            out _,
            new StreamOptions { MaxConcurrentStreams = 1, MaxFps = 60, DiagnosticsIntervalSeconds = 0 });

        service.Start("conn-1", null);

        var refused = Assert.Throws<CapabilityException>(() => service.Start("conn-2", null));
        Assert.Equal(ErrorCodes.Unsupported, refused.Code);

        await service.StopAsync("conn-1");
    }

    [Fact]
    public async Task AnUnknownDisplayIsRefused()
    {
        var capture = new FakeDesktopStream();
        await using var service = Build(capture, out _);

        var refused = Assert.Throws<CapabilityException>(() => service.Start("conn-1", 7));
        Assert.Equal(ErrorCodes.InvalidParameters, refused.Code);
    }

    [Fact]
    public async Task ThePrimaryDisplayIsChosenWhenNoneIsNamed()
    {
        var capture = new FakeDesktopStream(
            new DisplayInfo(0, "left", -1920, 0, 1920, 1080, false),
            new DisplayInfo(1, "primary", 0, 0, 2560, 1440, true));

        await using var service = Build(capture, out _);

        var session = service.Start("conn-1", null);

        Assert.Equal(1, session.Display.Index);
        Assert.True(session.Display.IsPrimary);

        await service.StopAsync("conn-1");
    }

    [Fact]
    public async Task DisposingTheServiceStopsEverything()
    {
        var capture = new FakeDesktopStream();
        var service = Build(capture, out _);

        service.Start("conn-1", null);
        await Task.Delay(80, TestContext.Current.CancellationToken);

        await service.DisposeAsync();

        Assert.Equal(0, service.ActiveStreams);
        Assert.All(capture.Opened, source => Assert.True(source.Disposed));
    }
}
