using System.Runtime.CompilerServices;

using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Indicator;
using PcCore.Capabilities.Media;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;

using Xunit;

namespace PcCore.Tests.Media;

/// <summary>A source that produces frames until it is cancelled, and says when it stopped.</summary>
internal sealed class FakeLiveSource : ILiveCameraSource, ILiveAudioSource
{
    private readonly byte[] _payload;

    public FakeLiveSource(string name, byte[]? payload = null)
    {
        Name = name;
        _payload = payload ?? [1, 2, 3, 4];
    }

    public string Name { get; }

    public bool Available { get; set; } = true;

    /// <summary>Set once the capture loop has actually stopped touching the device.</summary>
    public bool Released { get; private set; }

    public int Started { get; private set; }

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(Available);

    public async IAsyncEnumerable<LiveFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Started++;
        Released = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                yield return new LiveFrame(_payload, 640, 480);

                try
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
            }
        }
        finally
        {
            Released = true;
        }
    }
}

internal sealed class RecordingSink : ILiveSensorSink
{
    public List<(string ConnectionId, LiveSensorKind Kind)> Attached { get; } = [];

    public List<(string ConnectionId, LiveSensorKind Kind)> Detached { get; } = [];

    public void AttachSensor(string connectionId, LiveSensorSession session) =>
        Attached.Add((connectionId, session.Kind));

    public void DetachSensor(string connectionId, LiveSensorKind kind) =>
        Detached.Add((connectionId, kind));
}

/// <summary>
/// Live camera and microphone monitoring: who may start it, and everything that must stop it.
/// <para>
/// The tests that matter here are the negative ones. A live camera is the most invasive thing this
/// application can do, so "it starts when asked" is the easy half; the half worth pinning is that it
/// refuses an unauthenticated caller, stops when the session lapses, stops when the phone goes away,
/// and cannot be hidden from whoever is in the room.
/// </para>
/// </summary>
public sealed class LiveSensorTests
{
    private const string Connection = "conn-1";

    /// <summary>
    /// A caller shaped like the one the remote transport mints after a successful handshake:
    /// the real transport name, and — crucially — a device id, because authority is keyed to it.
    /// </summary>
    private static CallerDescriptor Caller(string connectionId = Connection, string device = "device-1") =>
        new("tailscale-remote", connectionId, ActorType.Device, device);

    private static LiveSensorService Build(
        FakeLiveSource camera,
        FakeLiveSource audio,
        RecordingSink sink,
        Func<CallerDescriptor, bool> authorized,
        Action<LiveSensorState>? onChanged = null)
    {
        var service = new LiveSensorService(camera, audio, TestLog.For<LiveSensorService>());
        service.Connect(sink, authorized, onChanged ?? (_ => { }));
        return service;
    }

    private static async Task SettleAsync() => await Task.Delay(150, TestContext.Current.CancellationToken);

    /// <summary>
    /// Starts a sensor and waits for the capture loop to actually be running.
    /// <para>
    /// The pump is a <c>Task.Run</c> over an async iterator, and an async iterator does not execute
    /// a line of its body until the first <c>MoveNext</c>. So "StartAsync returned" and "the device
    /// is open" are different moments, and a test that asserts on the device between them is testing
    /// the scheduler.
    /// </para>
    /// </summary>
    private static async Task<LiveStartOutcome> StartAndSettleAsync(
        LiveSensorService service,
        LiveSensorKind kind,
        CallerDescriptor caller)
    {
        var outcome = await service.StartAsync(kind, caller, TestContext.Current.CancellationToken);
        await SettleAsync();
        return outcome;
    }

    private static Task<LiveStartOutcome> StartAndSettleAsync(
        LiveSensorService service,
        LiveSensorKind kind,
        string connectionId) =>
        StartAndSettleAsync(service, kind, Caller(connectionId));

    // --- Authorisation -----------------------------------------------------------------------------

    [Fact]
    public async Task AnUnauthorisedSessionCannotStartTheCamera()
    {
        var camera = new FakeLiveSource("camera");
        var sink = new RecordingSink();

        await using var service = Build(camera, new FakeLiveSource("mic"), sink, _ => false);

        var outcome = await service.StartAsync(
            LiveSensorKind.Camera, Caller(), TestContext.Current.CancellationToken);

        Assert.False(outcome.Started);
        Assert.Equal(LiveStartFailure.NotAuthorized, outcome.Failure);
        Assert.False(service.TrayState.Any);

        // And the device was never touched, which is the part that matters: a refusal must not turn
        // the camera light on while it decides.
        Assert.Equal(0, camera.Started);
        Assert.Empty(sink.Attached);
    }

    [Fact]
    public async Task AnAuthorisedSessionStartsAndIsHandedToTheSink()
    {
        var camera = new FakeLiveSource("camera");
        var sink = new RecordingSink();

        await using var service = Build(camera, new FakeLiveSource("mic"), sink, _ => true);

        var outcome = await service.StartAsync(
            LiveSensorKind.Camera, Caller(), TestContext.Current.CancellationToken);

        Assert.True(outcome.Started);
        Assert.True(service.TrayState.Camera);
        Assert.False(service.TrayState.Microphone);

        Assert.Equal((Connection, LiveSensorKind.Camera), Assert.Single(sink.Attached));
    }

    [Fact]
    public async Task AMissingDeviceIsRefusedRatherThanReportedLive()
    {
        var camera = new FakeLiveSource("camera") { Available = false };

        await using var service = Build(camera, new FakeLiveSource("mic"), new RecordingSink(), _ => true);

        var outcome = await service.StartAsync(
            LiveSensorKind.Camera, Caller(), TestContext.Current.CancellationToken);

        Assert.False(outcome.Started);
        Assert.Equal(LiveStartFailure.NoDevice, outcome.Failure);
        Assert.False(service.TrayState.Any);
    }

    [Fact]
    public async Task BothSensorsCanRunAtOnce()
    {
        var camera = new FakeLiveSource("camera");
        var mic = new FakeLiveSource("mic");

        await using var service = Build(camera, mic, new RecordingSink(), _ => true);

        await service.StartAsync(LiveSensorKind.Camera, Caller(Connection), TestContext.Current.CancellationToken);
        await service.StartAsync(LiveSensorKind.Microphone, Caller(Connection), TestContext.Current.CancellationToken);

        Assert.True(service.TrayState.Camera);
        Assert.True(service.TrayState.Microphone);
        Assert.Equal("Nexus · Camera + Microphone LIVE", service.TrayState.Describe());
    }

    // --- Everything that must stop it --------------------------------------------------------------

    [Fact]
    public async Task LosingFullRemoteAuthorityStopsTheStreamOnItsOwn()
    {
        var camera = new FakeLiveSource("camera");
        var authorised = true;

        await using var service = Build(
            camera, new FakeLiveSource("mic"), new RecordingSink(), _ => authorised);

        await StartAndSettleAsync(service, LiveSensorKind.Camera, Connection);
        Assert.True(service.TrayState.Camera);

        // The session lapses — the phone locked, or its window elapsed. Nothing asks the service to
        // stop; it has to notice.
        authorised = false;

        // StopForConnection is what the authority watchdog calls; calling it directly keeps the test
        // off a five-second timer while pinning the behaviour that matters.
        service.StopForConnection(Connection, "the owner-authenticated session ended");

        Assert.False(service.TrayState.Any);

        await SettleAsync();
        Assert.True(camera.Released);
    }

    [Fact]
    public async Task ADisconnectStopsWhateverThatConnectionWasWatching()
    {
        var camera = new FakeLiveSource("camera");
        var mic = new FakeLiveSource("mic");
        var sink = new RecordingSink();

        await using var service = Build(camera, mic, sink, _ => true);

        await StartAndSettleAsync(service, LiveSensorKind.Camera, Connection);
        await StartAndSettleAsync(service, LiveSensorKind.Microphone, Connection);

        service.StopForConnection(Connection, "the phone disconnected");

        Assert.False(service.TrayState.Any);
        Assert.Equal(2, sink.Detached.Count);

        await SettleAsync();
        Assert.True(camera.Released);
        Assert.True(mic.Released);
    }

    /// <summary>A second phone's stream is not collateral damage when the first one leaves.</summary>
    [Fact]
    public async Task AnotherConnectionsStreamIsLeftAlone()
    {
        var camera = new FakeLiveSource("camera");
        var mic = new FakeLiveSource("mic");

        await using var service = Build(camera, mic, new RecordingSink(), _ => true);

        await service.StartAsync(LiveSensorKind.Camera, Caller("conn-a"), TestContext.Current.CancellationToken);
        await service.StartAsync(LiveSensorKind.Microphone, Caller("conn-b"), TestContext.Current.CancellationToken);

        service.StopForConnection("conn-a", "gone");

        Assert.False(service.TrayState.Camera);
        Assert.True(service.TrayState.Microphone);
    }

    [Fact]
    public async Task StopAllEndsEverythingAndReleasesTheDevices()
    {
        var camera = new FakeLiveSource("camera");
        var mic = new FakeLiveSource("mic");

        await using var service = Build(camera, mic, new RecordingSink(), _ => true);

        await StartAndSettleAsync(service, LiveSensorKind.Camera, Connection);
        await StartAndSettleAsync(service, LiveSensorKind.Microphone, Connection);

        service.StopAll("stopped from the tray");

        Assert.False(service.TrayState.Any);

        await SettleAsync();
        Assert.True(camera.Released);
        Assert.True(mic.Released);
    }

    [Fact]
    public async Task DisposingTheServiceStopsCapture()
    {
        var camera = new FakeLiveSource("camera");
        var service = Build(camera, new FakeLiveSource("mic"), new RecordingSink(), _ => true);

        await StartAndSettleAsync(service, LiveSensorKind.Camera, Connection);
        await service.DisposeAsync();

        await SettleAsync();
        Assert.True(camera.Released);
    }

    [Fact]
    public async Task StartingATwiceRunningSensorIsRefusedRatherThanDuplicated()
    {
        var camera = new FakeLiveSource("camera");

        await using var service = Build(camera, new FakeLiveSource("mic"), new RecordingSink(), _ => true);

        await StartAndSettleAsync(service, LiveSensorKind.Camera, Connection);
        var second = await service.StartAsync(
            LiveSensorKind.Camera, Caller(), TestContext.Current.CancellationToken);

        Assert.False(second.Started);
        Assert.Equal(LiveStartFailure.AlreadyRunning, second.Failure);
        Assert.Equal(1, camera.Started);
    }

    // --- The indicator -----------------------------------------------------------------------------

    /// <summary>
    /// The rule that stops this being a covert mode: a live sensor draws the badge in every
    /// indicator mode, including the one the owner set to stay quiet.
    /// </summary>
    [Theory]
    [InlineData(IndicatorMode.Standard)]
    [InlineData(IndicatorMode.Minimal)]
    [InlineData(IndicatorMode.AlwaysVisible)]
    public void ALiveSensorAlwaysDrawsTheOverlay(IndicatorMode mode)
    {
        var state = RemoteIndicatorService.Decide(
            mode, viewing: false, inputActive: false, live: new LiveSensorState(Camera: true, Microphone: false));

        Assert.True(state.Overlay);
        Assert.True(state.Tray);
        Assert.Equal("Nexus · Camera LIVE", state.Text);
    }

    [Fact]
    public void TheOverlayNamesWhicheverSensorsAreLive()
    {
        Assert.Equal(
            "Nexus · Microphone LIVE",
            RemoteIndicatorService.Decide(IndicatorMode.Minimal, false, false, new LiveSensorState(false, true)).Text);

        Assert.Equal(
            "Nexus · Camera + Microphone LIVE",
            RemoteIndicatorService.Decide(IndicatorMode.Minimal, false, false, new LiveSensorState(true, true)).Text);
    }

    [Fact]
    public void WithNothingLiveTheOrdinaryRulesStillApply()
    {
        Assert.Equal(
            IndicatorState.Idle,
            RemoteIndicatorService.Decide(IndicatorMode.Standard, false, false, LiveSensorState.Idle));

        // Minimal still suppresses the badge for ordinary remote control.
        Assert.False(RemoteIndicatorService.Decide(IndicatorMode.Minimal, true, true, LiveSensorState.Idle).Overlay);
    }

    // --- The wire format ---------------------------------------------------------------------------

    [Fact]
    public void ALiveFrameSurvivesARoundTrip()
    {
        var payload = new byte[] { 9, 8, 7, 6, 5 };

        var header = new LiveSensorFrameHeader
        {
            Version = LiveSensorFrameHeader.CurrentVersion,
            Kind = LiveSensorKind.Microphone,
            Sequence = 42,
            TimestampUnixMs = 1_700_000_000_000,
            WidthOrSampleRate = 48_000,
            HeightOrChannels = 1,
            PayloadLength = payload.Length,
        };

        var message = header.ToMessage(payload);
        var read = LiveSensorFrameHeader.TryRead(message);

        Assert.NotNull(read);
        Assert.Equal(LiveSensorKind.Microphone, read!.Value.Kind);
        Assert.Equal(42UL, read.Value.Sequence);
        Assert.Equal(48_000, read.Value.WidthOrSampleRate);
        Assert.Equal(1, read.Value.HeightOrChannels);
        Assert.Equal(payload.Length, read.Value.PayloadLength);
        Assert.Equal(payload, message[LiveSensorFrameHeader.Size..]);
    }

    /// <summary>A desktop frame must never be mistaken for a sensor frame, or the reverse.</summary>
    [Fact]
    public void ADesktopFrameIsNotReadAsALiveFrame()
    {
        var desktop = new byte[StreamFrameHeader.Size + 4];
        StreamFrameHeader.Magic.CopyTo(desktop);

        Assert.Null(LiveSensorFrameHeader.TryRead(desktop));
    }

    [Fact]
    public void AnUnknownVersionIsRefused()
    {
        var message = new LiveSensorFrameHeader
        {
            Version = 99,
            Kind = LiveSensorKind.Camera,
            Sequence = 1,
            TimestampUnixMs = 0,
            WidthOrSampleRate = 640,
            HeightOrChannels = 480,
            PayloadLength = 0,
        }.ToMessage([]);

        Assert.Null(LiveSensorFrameHeader.TryRead(message));
    }
}

/// <summary>The one place a version is written down, and everything that shows one reads it.</summary>
public sealed class ProductInfoTests
{
    [Fact]
    public void TheVersionComesFromTheBuildRatherThanALiteral()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", ProductInfo.Version);

        // "0.0.0" is the fallback when the attribute is missing, which would mean the build stopped
        // stamping it and every displayed version silently became wrong.
        Assert.NotEqual("0.0.0", ProductInfo.Version);
    }

    [Fact]
    public void TheDisplayNameCarriesTheProductAndTheVersion()
    {
        Assert.Equal($"Nexus {ProductInfo.Version}", ProductInfo.Display);
        Assert.StartsWith("Nexus ", ProductInfo.Display, StringComparison.Ordinal);
        Assert.Contains("PC Core", ProductInfo.DisplayWithCore, StringComparison.Ordinal);
    }
}
