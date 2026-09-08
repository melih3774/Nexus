using System.Diagnostics;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Screen;
using PcCore.Capabilities.System;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using PcCore.Windows;
using Xunit;

namespace PcCore.Tests.Capabilities;

public sealed class SystemCapabilityTests
{
    private static readonly CapabilityContext Context = new("test", CallerDescriptor.Internal);

    private static async Task<T> Invoke<T>(ICapabilityAction action, object parameters) =>
        (T)(await action.InvokeAsync(parameters, Context, CancellationToken.None))!;

    [Fact]
    public async Task GetInfo_ReturnsTheProvidersValue()
    {
        var action = new GetSystemInfoAction(new FakeSystemInfoProvider());

        var info = await Invoke<SystemInfo>(action, new NoParameters());

        Assert.Equal("TEST-MACHINE", info.MachineName);
        Assert.Equal("system.get_info", action.Name);
    }

    [Fact]
    public async Task ListProcesses_WrapsTheListWithACount()
    {
        var action = new ListProcessesAction(new FakeProcessLister());

        var result = await Invoke<ProcessList>(action, new NoParameters());

        Assert.Equal(2, result.Count);
        Assert.Equal(result.Count, result.Processes.Count);
        // An inaccessible process is still listed; only its memory figure is omitted.
        Assert.Null(result.Processes.Single(p => p.Name == "protected").WorkingSetBytes);
    }

    // --- Real Windows providers ---------------------------------------------------------------

    [Fact]
    public void WindowsSystemInfo_ReportsUsableValues()
    {
        var info = new WindowsSystemInfoProvider().GetInfo();

        Assert.False(string.IsNullOrWhiteSpace(info.MachineName));
        Assert.False(string.IsNullOrWhiteSpace(info.OsDescription));
        Assert.False(string.IsNullOrWhiteSpace(info.RuntimeVersion));
        Assert.Equal("0.7.1", info.PcCoreVersion);
        Assert.True(info.ProcessorCount > 0);
        Assert.True(info.UptimeSeconds > 0);
        Assert.True(info.TimestampUtc > DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void WindowsSystemInfo_ExcludesSensitiveFields()
    {
        // A guard against someone later "helpfully" adding the user name to this payload.
        var info = new WindowsSystemInfoProvider().GetInfo();
        var properties = info.GetType().GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain("UserName", properties);
        Assert.DoesNotContain("UserDomainName", properties);
        Assert.DoesNotContain("IpAddress", properties);
    }

    [Fact]
    public void WindowsProcessLister_IncludesTheCurrentProcess()
    {
        using var current = Process.GetCurrentProcess();

        var processes = new WindowsProcessLister(TestLog.For<WindowsProcessLister>()).ListProcesses();

        Assert.NotEmpty(processes);
        Assert.Contains(processes, p => p.Pid == current.Id);

        // Protected system processes must be skipped rather than fail the whole enumeration.
        Assert.All(processes, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }
}

public sealed class ScreenCapabilityTests
{
    private static readonly CapabilityContext Context = new("test", CallerDescriptor.Internal);
    private static readonly ScreenOptions Enabled = new() { Enabled = true, MaxWidthLimit = 7680 };

    private static async Task<CaptureScreenResult> Capture(
        IScreenCapture capture,
        CaptureScreenParameters parameters,
        ScreenOptions? options = null)
    {
        var action = new CaptureScreenAction(capture, options ?? Enabled);
        return (CaptureScreenResult)(await ((ICapabilityAction)action)
            .InvokeAsync(parameters, Context, CancellationToken.None))!;
    }

    [Fact]
    public async Task Capture_ReturnsBase64Png()
    {
        var fake = new FakeScreenCapture();

        var result = await Capture(fake, new CaptureScreenParameters());

        Assert.Equal("image/png", result.ContentType);
        Assert.Equal("base64", result.Encoding);
        Assert.Equal(FakeScreenCapture.OnePixelPng, Convert.FromBase64String(result.Data));
        Assert.Equal(1, result.Width);
        Assert.Equal(1920, result.SourceWidth);
        Assert.Equal(1080, result.SourceHeight);
        Assert.Equal("virtual", result.Display);
    }

    [Theory]
    [InlineData(null, DisplaySelectionKind.Virtual, 0)]
    [InlineData("", DisplaySelectionKind.Virtual, 0)]
    [InlineData("virtual", DisplaySelectionKind.Virtual, 0)]
    [InlineData("VIRTUAL", DisplaySelectionKind.Virtual, 0)]
    [InlineData("primary", DisplaySelectionKind.Primary, 0)]
    [InlineData("Primary", DisplaySelectionKind.Primary, 0)]
    [InlineData("0", DisplaySelectionKind.Index, 0)]
    [InlineData("2", DisplaySelectionKind.Index, 2)]
    public void ParseDisplay_AcceptsValidSelections(string? input, DisplaySelectionKind kind, int index)
    {
        var selection = CaptureScreenAction.ParseDisplay(input);

        Assert.Equal(kind, selection.Kind);
        Assert.Equal(index, selection.Index);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public void ParseDisplay_RejectsInvalidSelections(string input)
    {
        var exception = Assert.Throws<CapabilityException>(() => CaptureScreenAction.ParseDisplay(input));

        Assert.Equal(ErrorCodes.InvalidParameters, exception.Code);
    }

    [Fact]
    public async Task Capture_PassesMaxWidthThrough()
    {
        var fake = new FakeScreenCapture();

        await Capture(fake, new CaptureScreenParameters { MaxWidth = 640 });

        Assert.Equal(640, fake.LastMaxWidth);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(99999)]
    public async Task Capture_RejectsAnOutOfRangeMaxWidth(int maxWidth)
    {
        var exception = await Assert.ThrowsAsync<CapabilityException>(
            () => Capture(new FakeScreenCapture(), new CaptureScreenParameters { MaxWidth = maxWidth }));

        Assert.Equal(ErrorCodes.InvalidParameters, exception.Code);
    }

    [Fact]
    public async Task Capture_ReturnsUnsupportedWhenDisabled()
    {
        var options = new ScreenOptions { Enabled = false };

        var exception = await Assert.ThrowsAsync<CapabilityException>(
            () => Capture(new FakeScreenCapture(), new CaptureScreenParameters(), options));

        Assert.Equal(ErrorCodes.Unsupported, exception.Code);
    }

    [Fact]
    public async Task Capture_SurfacesACaptureFailureAsAProtocolError()
    {
        var failing = new FakeScreenCapture
        {
            OnCapture = (_, _) => throw new CapabilityException(ErrorCodes.Unsupported, "no desktop"),
        };

        var exception = await Assert.ThrowsAsync<CapabilityException>(
            () => Capture(failing, new CaptureScreenParameters()));

        Assert.Equal(ErrorCodes.Unsupported, exception.Code);
    }

    // --- Real GDI capture ---------------------------------------------------------------------

    [Fact]
    public void GdiCapture_ProducesARealPngOfTheDesktop()
    {
        var geometry = new SystemDisplayGeometry();
        Assert.SkipWhen(
            geometry.VirtualBounds.Width <= 0 || geometry.VirtualBounds.Height <= 0,
            "no interactive desktop in this session");

        var capture = new GdiScreenCapture(geometry, TestLog.For<GdiScreenCapture>());

        var frame = capture.Capture(DisplaySelection.Virtual, maxWidth: 320);

        Assert.True(frame.Width <= 320);
        Assert.Equal(geometry.VirtualBounds.Width, frame.SourceWidth);
        Assert.NotEmpty(frame.Png);

        // PNG magic number, so we know it is really an image and not an empty buffer.
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, frame.Png.Take(4).ToArray());
    }

    [Fact]
    public void GdiCapture_RejectsAnOutOfRangeDisplayIndex()
    {
        var geometry = new SystemDisplayGeometry();
        Assert.SkipWhen(geometry.Displays.Count == 0, "no displays in this session");

        var capture = new GdiScreenCapture(geometry, TestLog.For<GdiScreenCapture>());

        var exception = Assert.Throws<CapabilityException>(
            () => capture.Capture(new DisplaySelection(DisplaySelectionKind.Index, 99), null));

        Assert.Equal(ErrorCodes.InvalidParameters, exception.Code);
    }

    [Fact]
    public void DisplayGeometry_ReportsAConsistentLayout()
    {
        var geometry = new SystemDisplayGeometry();
        Assert.SkipWhen(geometry.Displays.Count == 0, "no displays in this session");

        // Every physical display must fall inside the virtual desktop rectangle.
        foreach (var display in geometry.Displays)
        {
            Assert.True(geometry.VirtualBounds.Contains(display), $"{display} escapes {geometry.VirtualBounds}");
        }
    }
}
