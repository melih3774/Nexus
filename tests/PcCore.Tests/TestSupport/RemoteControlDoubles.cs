using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Indicator;
using PcCore.Capabilities.Identity;
using PcCore.Capabilities.Input;
using PcCore.Capabilities.Media;
using PcCore.Capabilities.Screen;
using PcCore.Capabilities.Security.Detection;
using PcCore.Capabilities.Storage;
using PcCore.Protocol;

namespace PcCore.Tests.TestSupport;

/// <summary>
/// A desktop stream with no desktop.
/// <para>
/// Everything above the Direct3D call — pacing, encoding, backpressure, reopening after a loss,
/// cleanup — is ordinary logic, and this is what lets it be tested as such. The real
/// <c>DxgiDesktopStream</c> is exercised separately against the actual display.
/// </para>
/// </summary>
public sealed class FakeDesktopStream : IDesktopStream
{
    private readonly ConcurrentQueue<CaptureResult> _scripted = new();

    public FakeDesktopStream(params DisplayInfo[] displays) =>
        Displays = displays.Length > 0
            ? displays
            : [new DisplayInfo(0, @"\\.\DISPLAY1", 0, 0, 1920, 1080, true)];

    public IReadOnlyList<DisplayInfo> Displays { get; set; }

    /// <summary>Every source handed out, so a test can assert they were all disposed.</summary>
    public List<FakeCaptureSource> Opened { get; } = [];

    public int OpenFailures { get; set; }

    /// <summary>Returned in order; once empty, the source produces generated frames forever.</summary>
    public void Script(params CaptureResult[] results)
    {
        foreach (var result in results)
        {
            _scripted.Enqueue(result);
        }
    }

    public IReadOnlyList<DisplayInfo> GetDisplays() => Displays;

    public IDesktopCaptureSource Open(int displayIndex)
    {
        if (OpenFailures > 0)
        {
            OpenFailures--;
            throw new CapabilityException(ErrorCodes.Unsupported, "capture unavailable");
        }

        var display = Displays.FirstOrDefault(d => d.Index == displayIndex)
            ?? throw new CapabilityException(ErrorCodes.InvalidParameters, "no such display");

        var source = new FakeCaptureSource(display, _scripted);
        Opened.Add(source);

        return source;
    }
}

public sealed class FakeCaptureSource : IDesktopCaptureSource
{
    private readonly ConcurrentQueue<CaptureResult> _scripted;
    private int _frames;

    internal FakeCaptureSource(DisplayInfo display, ConcurrentQueue<CaptureResult> scripted)
    {
        Display = display;
        _scripted = scripted;
        PixelWidth = display.Width;
        PixelHeight = display.Height;
    }

    public DisplayInfo Display { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public bool Disposed { get; private set; }

    public int FramesProduced => _frames;

    public CaptureResult TryGetFrame(TimeSpan timeout)
    {
        if (_scripted.TryDequeue(out var scripted))
        {
            if (scripted.Status == CaptureStatus.Frame)
            {
                Interlocked.Increment(ref _frames);
            }

            return scripted;
        }

        Interlocked.Increment(ref _frames);

        // A tiny frame with a recognisable first pixel, so a test can tell frames apart.
        var pixels = new byte[PixelWidth * PixelHeight * 4];
        pixels[0] = (byte)(_frames & 0xff);

        return CaptureResult.Captured(
            new RawFrame(pixels, PixelWidth, PixelHeight, PixelWidth * 4, DateTimeOffset.UtcNow, 10, 20, true));
    }

    public void Dispose() => Disposed = true;
}

/// <summary>An encoder that produces a deterministic payload, so frame bytes can be asserted exactly.</summary>
public sealed class FakeFrameEncoder : IFrameEncoder
{
    public FrameEncoding Encoding => FrameEncoding.Jpeg;

    public int PayloadBytes { get; set; } = 64;

    public int Encoded { get; private set; }

    public EncodedFrame Encode(RawFrame frame, int maxWidth, int maxHeight, int quality)
    {
        Encoded++;

        var payload = new byte[PayloadBytes];
        payload[0] = frame.Pixels.Length > 0 ? frame.Pixels[0] : (byte)0;

        var width = Math.Min(frame.Width, maxWidth <= 0 ? frame.Width : maxWidth);
        var height = Math.Min(frame.Height, maxHeight <= 0 ? frame.Height : maxHeight);

        return new EncodedFrame(payload, width, height);
    }
}

/// <summary>Records what would have been injected, and can pretend Windows refused.</summary>
public sealed class RecordingInputController : IInputController
{
    public List<string> Events { get; } = [];

    public (int Left, int Top, int Width, int Height) VirtualDesktop { get; set; } = (0, 0, 1920, 1080);

    /// <summary>When set, every call reports the UIPI refusal instead of succeeding.</summary>
    public bool BlockedByIntegrity { get; set; }

    public bool Fail { get; set; }

    private InputResult Record(string what)
    {
        Events.Add(what);

        if (BlockedByIntegrity)
        {
            return InputResult.Blocked("an elevated window has focus");
        }

        return Fail ? InputResult.Failed("windows said no") : InputResult.Ok;
    }

    public InputResult MovePointerAbsolute(int x, int y) => Record($"move-absolute {x},{y}");

    public InputResult MovePointerRelative(int dx, int dy) => Record($"move-relative {dx},{dy}");

    public InputResult Button(PointerButton button, bool down) => Record($"button {button} {(down ? "down" : "up")}");

    public InputResult Scroll(int horizontalNotches, int verticalNotches) =>
        Record($"scroll {horizontalNotches},{verticalNotches}");

    public InputResult Key(NexusKey key, bool down) => Record($"key {key} {(down ? "down" : "up")}");

    public InputResult Text(string text) => Record($"text {text}");

    /// <summary>When false, touch reports UNSUPPORTED so the pointer fallback can be exercised.</summary>
    public bool TouchAvailable { get; set; } = true;

    public InputResult Touch(int contactId, TouchAction action, int x, int y)
    {
        if (!TouchAvailable)
        {
            Events.Add($"touch-unsupported {action}");
            return InputResult.Unsupported("no touch on this fake");
        }

        return Record($"touch {contactId} {action} {x},{y}");
    }

    public NativeInputStatus Status { get; set; } = new()
    {
        Path = InputPath.Local,
        State = NativeInputState.NotInstalled,
        ReachesElevatedWindows = false,
        TouchAvailable = true,
        PhysicalDigitizer = false,
        Desktop = "DEFAULT",
    };

    /// <summary>What the platform is pretending to hold, for release-path tests.</summary>
    public int PlatformHeld { get; set; }

    public int ReleaseAllHeld()
    {
        Events.Add("release-all-held");

        var held = PlatformHeld;
        PlatformHeld = 0;

        return held;
    }
}

/// <summary>A display catalog with no capture behind it, for testing input coordinate mapping.</summary>
public sealed class FakeDisplayCatalog(params DisplayInfo[] displays) : IDisplayCatalog
{
    public IReadOnlyList<DisplayInfo> Displays { get; set; } =
        displays.Length > 0 ? displays : [new DisplayInfo(0, @"\\.\DISPLAY1", 0, 0, 1920, 1080, true)];

    public IReadOnlyList<DisplayInfo> GetDisplays() => Displays;
}

/// <summary>Registers everything v0.6 adds, so transport fixtures do not each restate it.</summary>
public static class RemoteControlTestServices
{
    public static IServiceCollection AddRemoteControlDoubles(this IServiceCollection services)
    {
        // Registered as an instance: the params constructor is not something DI can resolve.
        services.AddSingleton(new FakeDesktopStream());
        services.AddSingleton<IDesktopStream>(p => p.GetRequiredService<FakeDesktopStream>());
        services.AddSingleton<IDisplayCatalog>(p => p.GetRequiredService<FakeDesktopStream>());
        services.AddSingleton<IFrameEncoder, FakeFrameEncoder>();

        // No video encoder in the transport fixtures: they are about who may stream and what happens
        // to a stream when a connection ends, and a still frame is a frame for those purposes. The
        // codec's own behaviour is tested where it belongs, against a fake encoder that models it.
        services.AddSingleton<IVideoEncoderFactory>(new FakeVideoEncoderFactory(available: false));
        services.AddSingleton<IMediaSourceSizes>(new FakeMediaSourceSizes());
        services.AddSingleton(TestMediaQuality.Store());
        services.AddSingleton(new StreamOptions { FallbackFps = 30, DiagnosticsIntervalSeconds = 0 });
        services.AddSingleton<DesktopStreamService>();

        services.AddSingleton<RecordingInputController>();
        services.AddSingleton<IInputController>(p => p.GetRequiredService<RecordingInputController>());
        services.AddSingleton(new InputOptions());
        services.AddSingleton<INexusInputAttribution, NullNexusInputAttribution>();
        services.AddSingleton<IInputService, InputService>();

        // The real owner-session service, not a double. Every remote test that authenticates a
        // device now also has to be able to authorise one, and a fake would be testing the fake.
        services.AddSingleton(new OwnerSessionOptions());
        services.AddSingleton<OwnerSessionService>();
        services.AddSingleton<IOwnerSessionService>(p => p.GetRequiredService<OwnerSessionService>());
        services.AddSingleton<IRemoteAuthorityProvider>(p => p.GetRequiredService<OwnerSessionService>());

        services.AddSingleton<ICapabilityAction, GetSessionStatusAction>();
        services.AddSingleton<ICapabilityAction, EnrollOwnerKeyAction>();
        services.AddSingleton<ICapabilityAction, GetSessionChallengeAction>();
        services.AddSingleton<ICapabilityAction, AuthorizeSessionAction>();
        services.AddSingleton<ICapabilityAction, ResumeSessionAction>();
        services.AddSingleton<ICapabilityAction, LockSessionAction>();

        services.AddSingleton<NullRemoteIndicator>();
        services.AddSingleton<IRemoteIndicator>(p => p.GetRequiredService<NullRemoteIndicator>());
        services.AddSingleton<RemoteIndicatorService>();

        return services;
    }
}

/// <summary>
/// An input service that never has a session, for tests about the indicator alone.
/// <para>
/// The indicator subscribes to input activity, so it needs one even when the test is only about
/// which badge a mode shows.
/// </para>
/// </summary>
public sealed class NoInput : IInputService
{
    public static readonly NoInput Instance = new();

    public event Action<InputActivityChanged>? ActivityChanged;

    public IReadOnlyList<InputSession> Active => [];

    public InputSession Begin(string ownerId) => new(ownerId, DateTimeOffset.UtcNow);

    public bool End(string ownerId) => false;

    public bool IsActive(string ownerId) => false;

    public InputResult MoveAbsolute(string ownerId, int displayIndex, double x, double y) => InputResult.Ok;

    public InputResult MoveRelative(string ownerId, double dx, double dy) => InputResult.Ok;

    public InputResult Button(string ownerId, PointerButton button, ButtonAction action) => InputResult.Ok;

    public InputResult Scroll(string ownerId, double horizontal, double vertical) => InputResult.Ok;

    public InputResult Key(string ownerId, NexusKey key, KeyAction action) => InputResult.Ok;

    public InputResult Text(string ownerId, string text) => InputResult.Ok;

    public InputResult Touch(
        string ownerId,
        int displayIndex,
        int contactId,
        TouchAction action,
        double x,
        double y) => InputResult.Ok;

    public NativeInputStatus Status => new()
    {
        Path = InputPath.Local,
        State = NativeInputState.NotInstalled,
        ReachesElevatedWindows = false,
        TouchAvailable = false,
        PhysicalDigitizer = false,
        Desktop = "DEFAULT",
    };

    public int ReleaseAll(string ownerId) => 0;

    /// <summary>Lets a test drive the indicator through the event rather than the setter.</summary>
    public void Announce(int sessions) => ActivityChanged?.Invoke(new InputActivityChanged(sessions));
}
