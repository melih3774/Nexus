using System.Runtime.CompilerServices;

using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Media;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;

using Xunit;

namespace PcCore.Tests.Media;

/// <summary>
/// A camera that produces H.264-shaped frames: a configuration frame, a keyframe, then predicted
/// frames, each numbered so a test can tell whether what arrived is decodable.
/// </summary>
internal sealed class FakeVideoCamera : ILiveCameraSource, ILiveVideoControl
{
    private int _keyframeWanted = 1;
    private int _keyframeRequests;
    private int _congestion;

    public string Name => "camera";

    public int KeyframeRequests => Volatile.Read(ref _keyframeRequests);

    public int Congestion => Volatile.Read(ref _congestion);

    public void RequestKeyframe()
    {
        Interlocked.Increment(ref _keyframeRequests);
        Interlocked.Exchange(ref _keyframeWanted, 1);
    }

    public void ReportCongestion() => Interlocked.Increment(ref _congestion);

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public async IAsyncEnumerable<LiveFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new LiveFrame([0, 0, 0, 1, 0x67, 0, 0, 0, 1, 0x68], 1280, 720, FrameEncoding.H264, FrameFlags.CodecConfig);

        var index = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            index++;
            var keyframe = Interlocked.Exchange(ref _keyframeWanted, 0) == 1;

            yield return new LiveFrame(
                BitConverter.GetBytes(index),
                1280,
                720,
                FrameEncoding.H264,
                FrameFlags.Complete | (keyframe ? FrameFlags.Keyframe : FrameFlags.None));

            try
            {
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }
}

internal sealed class CapturingSink : ILiveSensorSink
{
    public LiveSensorSession? Session { get; private set; }

    public void AttachSensor(string connectionId, LiveSensorSession session) => Session = session;

    public void DetachSensor(string connectionId, LiveSensorKind kind)
    {
    }
}

/// <summary>
/// What the live camera does when the link cannot keep up, now that its frames depend on each other.
/// <para>
/// With JPEG stills the answer was to keep the newest frame and discard the rest, and any frame that
/// arrived could be drawn. With H.264 that answer produces a phone showing a smeared room until the
/// next scheduled keyframe. These pin the replacement: stop forwarding predicted frames when the
/// socket is behind, ask for a keyframe, and resume only at one — so that every run the phone
/// receives starts somewhere it can decode.
/// </para>
/// </summary>
public sealed class LiveVideoBackpressureTests
{
    private static readonly CallerDescriptor Caller = new("tailscale-remote", "conn-1", ActorType.Device, "device-1");

    [Fact]
    public async Task AStalledLinkPausesTheCameraAndItResumesOnAFreshKeyframe()
    {
        var camera = new FakeVideoCamera();
        var sink = new CapturingSink();

        await using var service = new LiveSensorService(
            camera, new FakeLiveSource("microphone"), TestLog.For<LiveSensorService>());

        service.Connect(sink, _ => true, _ => { });

        var outcome = await service.StartAsync(LiveSensorKind.Camera, Caller, TestContext.Current.CancellationToken);
        Assert.True(outcome.Started);

        // Nobody reads: the transport is stalled.
        await Task.Delay(300, TestContext.Current.CancellationToken);

        var session = Assert.IsType<LiveSensorSession>(sink.Session);

        Assert.True(
            session.Backlog <= LiveSensorService.MaxVideoBacklog + 2,
            $"{session.Backlog} frames queued for a link that was not sending");

        Assert.True(camera.Congestion > 0, "the camera was never told the link was behind");

        // While the link stays stalled, nothing is asked for: a keyframe produced now would only join
        // the queue, and it is the largest frame there is.
        Assert.Equal(0, camera.KeyframeRequests);

        // The link recovers. Everything queued goes out, and the next thing asked for is a keyframe.
        while (session.Frames.TryRead(out _))
        {
            session.ReportSent();
        }

        LiveSensorFrameHeader? next = null;

        for (var i = 0; i < 100 && next is null; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);

            while (session.Frames.TryRead(out var message))
            {
                session.ReportSent();
                next = LiveSensorFrameHeader.TryRead(message);
                break;
            }
        }

        Assert.True(camera.KeyframeRequests > 0, "the drained link did not ask for a keyframe");
        Assert.NotNull(next);
        Assert.True(next.Value.Flags.HasFlag(FrameFlags.Keyframe), "the first frame after recovery was not a keyframe");

        service.StopAll("test over");
    }

    [Fact]
    public async Task EveryRunDeliveredAfterCongestionStartsAtAKeyframe()
    {
        var camera = new FakeVideoCamera();
        var sink = new CapturingSink();

        await using var service = new LiveSensorService(
            camera, new FakeLiveSource("microphone"), TestLog.For<LiveSensorService>());

        service.Connect(sink, _ => true, _ => { });
        await service.StartAsync(LiveSensorKind.Camera, Caller, TestContext.Current.CancellationToken);

        var delivered = new List<(int Index, bool Keyframe)>();
        var deadline = DateTime.UtcNow.AddMilliseconds(1200);
        var stalls = 0;

        while (DateTime.UtcNow < deadline)
        {
            var session = sink.Session;

            if (session is null)
            {
                await Task.Delay(5, TestContext.Current.CancellationToken);
                continue;
            }

            // Alternate between a stalled link and a draining one, several times.
            if (stalls++ % 2 == 0)
            {
                await Task.Delay(120, TestContext.Current.CancellationToken);
            }

            while (session.Frames.TryRead(out var message))
            {
                session.ReportSent();

                var header = LiveSensorFrameHeader.TryRead(message);
                Assert.NotNull(header);

                if (header.Value.Flags.HasFlag(FrameFlags.CodecConfig))
                {
                    continue;
                }

                delivered.Add((
                    BitConverter.ToInt32(message, LiveSensorFrameHeader.Size),
                    header.Value.Flags.HasFlag(FrameFlags.Keyframe)));
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        service.StopAll("test over");

        Assert.NotEmpty(delivered);
        Assert.True(delivered[0].Keyframe, "the first delivered picture was not a keyframe");

        // The decodability invariant: a predicted frame is only ever delivered directly after the
        // frame it was predicted from. Anything else is a picture built on one the phone never got.
        for (var i = 1; i < delivered.Count; i++)
        {
            if (!delivered[i].Keyframe)
            {
                Assert.True(
                    delivered[i].Index == delivered[i - 1].Index + 1,
                    $"predicted frame {delivered[i].Index} followed {delivered[i - 1].Index}");
            }
        }

        Assert.True(camera.KeyframeRequests > 1, "the stalls never caused a keyframe request");
    }
}
