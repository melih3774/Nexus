using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PcAssistant.Windows;
using Xunit;

namespace PcAssistant.Tests;

[Collection(DesktopCollection.Name)]
public sealed class NativeWindowsTests
{
    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData("https://owner:secret@example.com")]
    [InlineData("relative/path")]
    public void OpenUrlRejectsNonWebTargetsAndCredentials(string url) => Assert.Throws<ArgumentException>(() => SystemDiscovery.ValidateUrl(url));

    [Theory]
    [InlineData("https://example.com/a?q=hello%20world")]
    [InlineData("http://localhost:8080/")]
    public void WebUrlsValidateWithoutLaunching(string url) => SystemDiscovery.ValidateUrl(url);

    [Theory]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("C:\\Users\\owner\\run.ps1")]
    [InlineData("C:\\Users\\owner\\app.lnk")]
    [InlineData("\\\\server\\share\\document.pdf")]
    [InlineData("C:\\document.txt:payload.txt")]
    public void DocumentOpenRejectsExecutableNetworkAndAlternateStreams(string path) => Assert.Throws<ArgumentException>(() => SystemDiscovery.ValidateDocumentPath(path));

    [Fact]
    public void RecentFilesFiltersBeforeTakingNewestLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pc-assistant-native-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var pdf = Path.Combine(directory, "older.pdf");
            File.WriteAllText(pdf, "test fixture");
            File.SetLastWriteTimeUtc(pdf, DateTime.UtcNow.AddDays(-1));
            File.WriteAllText(Path.Combine(directory, "newer.txt"), "test fixture");
            var files = new SystemDiscovery().RecentFiles(1, directory, ".pdf");
            Assert.Equal(pdf, Assert.Single(files).Path);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void StaleAppAndUiReferencesFailWithoutAction()
    {
        using var ui = new UiAutomationService();
        Assert.Throws<InvalidOperationException>(() => ui.DescribeReference("unknown"));
    }

    [Fact]
    public async Task InvalidAndCancelledInputFailsBeforeDesktopMutation()
    {
        using var input = new InputService(new PcAssistant.Windows.Capture.CaptureRegistry());
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = input.ClickAsync(0, 0, 0, count: 3); });
        Assert.Throws<ArgumentException>(() => { _ = input.TypeTextAsync(0, new string('x', 4001)); });
        Assert.Throws<ArgumentException>(() => InputService.ValidateKeys(["Win", "R"]));
        Assert.Throws<ArgumentException>(() => InputService.ValidateKeys(["Alt", "Tab"]));
        Assert.Throws<ArgumentException>(() => InputService.ValidateKeys(["Ctrl", "Ctrl"]));
        Assert.Throws<ArgumentException>(() => InputService.ValidateKeys([new string('A', 33)]));
        Assert.Equal(new ushort[] { 0x11, 0x41 }, InputService.ValidateKeys(["Ctrl", "A"]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => input.ClickAsync(0, 0, 0, cancellationToken: new CancellationToken(true)));
    }

    [Fact]
    public void PhysicalMonitorCaptureReturnsPngWithMatchingPixelGeometry()
    {
        var desktop = new DesktopWindows();
        var monitor = desktop.Monitors().First();
        var region = new PixelRect(monitor.Bounds.X, monitor.Bounds.Y, 16, 12);
        var shot = desktop.Capture(region);
        Assert.Equal(region, shot.Bounds);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, shot.Png.Take(8));
        Assert.Equal(16, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(shot.Png.AsSpan(16, 4)));
        Assert.Equal(12, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(shot.Png.AsSpan(20, 4)));
    }

    [Fact]
    public async Task NativeDiscoveryReportsRealSystemDataWithinLimits()
    {
        using var discovery = new SystemDiscovery();
        var info = discovery.GetSystemInfo();
        Assert.True(info.TotalMemoryBytes > 0);
        Assert.InRange(info.AvailableMemoryBytes, 0UL, info.TotalMemoryBytes);
        Assert.InRange((await discovery.ListProcessesAsync(3)).Count, 1, 3);
    }

    [Fact]
    public async Task UiSnapshotBoundsTraversalProtectsPasswordsAndInvalidatesOldReferences()
    {
        using var fixture = await NativeFixture.CreateAsync();
        using var ui = new UiAutomationService();
        var snapshot = await ui.SnapshotAsync(fixture.Hwnd, 5, 30);
        Assert.Contains(snapshot.Nodes, n => n.Name == "Native fixture button");
        Assert.DoesNotContain(snapshot.Nodes, n => n.Name.Contains("NeverExpose123", StringComparison.Ordinal));
        var password = Assert.Single(snapshot.Nodes, n => n.Password);
        Assert.Empty(password.Actions);
        var first = snapshot.Nodes[0].Reference;
        Assert.Contains(fixture.Hwnd.ToString(), ui.DescribeReference(first));
        var bounded = await ui.SnapshotAsync(fixture.Hwnd, 0, 1);
        Assert.Single(bounded.Nodes);
        Assert.True(bounded.Truncated);
        Assert.Throws<InvalidOperationException>(() => ui.DescribeReference(first));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ui.ActAsync(first, UiAction.Invoke));
    }

    [SkippableFact]
    public async Task ExplicitSmokeUsesOnlyDisposableFixtureForInputAndWindowActions()
    {
        Skip.If(Environment.GetEnvironmentVariable("PCASSISTANT_NATIVE_SMOKE") != "1", "Set PCASSISTANT_NATIVE_SMOKE=1 to allow foreground/input against the disposable fixture.");
        var previous = GetForegroundWindow();
        using var fixture = await NativeFixture.CreateAsync();
        using var ui = new UiAutomationService();
        using var input = new InputService(new PcAssistant.Windows.Capture.CaptureRegistry());
        var desktop = new DesktopWindows();
        try
        {
            // Focus now returns verified state, so the caller needs no delay and no second look.
            Assert.Contains("verified foreground", await desktop.ChangeWindowAsync(fixture.Hwnd, WindowAction.Focus));
            Assert.Equal(fixture.Hwnd, desktop.ForegroundWindow()?.Hwnd);
            var snapshot = await ui.SnapshotAsync(fixture.Hwnd);
            var edit = Assert.Single(snapshot.Nodes, n => n.ControlType == "ControlType.Edit" && !n.Password);
            await ui.ActAsync(edit.Reference, UiAction.SetValue, "Fixture edited");
            Assert.Equal("Fixture edited", fixture.ReadText());
            var button = Assert.Single(snapshot.Nodes, n => n.Name == "Native fixture button" && n.ControlType == "ControlType.Button");
            await ui.ActAsync(button.Reference, UiAction.Invoke);
            await WaitForAsync(() => fixture.ClickCount == 1);
            Assert.Equal(1, fixture.ClickCount);
            await input.ClickAsync(fixture.Hwnd, edit.Bounds!.X + 20, edit.Bounds.Y + edit.Bounds.Height / 2);
            await input.KeysAsync(fixture.Hwnd, ["Ctrl", "A"]);
            await input.TypeTextAsync(fixture.Hwnd, "Native input ✓");
            await Task.Delay(200);
            Assert.Equal("Native input ✓", fixture.ReadText());
            await input.ClickAsync(fixture.Hwnd, button.Bounds!.X + 20, button.Bounds.Y + button.Bounds.Height / 2);
            await Task.Delay(150);
            Assert.Equal(2, fixture.ClickCount);
            var initial = desktop.ListWindows().Single(w => w.Hwnd == fixture.Hwnd).Bounds;
            var moved = initial with { Width = initial.Width + 25, Height = initial.Height + 25 };
            await desktop.ChangeWindowAsync(fixture.Hwnd, WindowAction.MoveResize, moved);
            await Task.Delay(150);
            Assert.Equal(moved, desktop.ListWindows().Single(w => w.Hwnd == fixture.Hwnd).Bounds);
            Assert.Contains("verified minimized", await desktop.ChangeWindowAsync(fixture.Hwnd, WindowAction.Minimize));
            Assert.True(desktop.ListWindows().Single(w => w.Hwnd == fixture.Hwnd).IsMinimized);
            Assert.Contains("verified restored", await desktop.ChangeWindowAsync(fixture.Hwnd, WindowAction.Restore));
            Assert.False(desktop.ListWindows().Single(w => w.Hwnd == fixture.Hwnd).IsMinimized);
        }
        finally { InputService.ReleaseAll(); if (previous != 0) SetForegroundWindow(previous); }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    private sealed class NativeFixture(Window window, long hwnd) : IDisposable
    {
        public long Hwnd { get; } = hwnd;
        public int ClickCount => window.Dispatcher.Invoke(() => (int)(window.Tag ?? 0));
        public string ReadText() => window.Dispatcher.Invoke(() => ((TextBox)((StackPanel)window.Content).Children[1]).Text);
        public static Task<NativeFixture> CreateAsync()
        {
            var completion = new TaskCompletionSource<NativeFixture>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    var panel = new StackPanel();
                    var button = new Button { Content = "Native fixture button" };
                    panel.Children.Add(button);
                    panel.Children.Add(new TextBox { Text = "Ordinary fixture text" });
                    panel.Children.Add(new PasswordBox { Password = "NeverExpose123" });
                    var window = new Window { Title = "PC Assistant disposable native test", Width = 360, Height = 220,
                        ShowActivated = false, ShowInTaskbar = false, Content = panel, WindowStyle = WindowStyle.ToolWindow };
                    button.Click += (_, _) => window.Tag = (int)(window.Tag ?? 0) + 1;
                    window.Show();
                    completion.SetResult(new NativeFixture(window, new WindowInteropHelper(window).Handle.ToInt64()));
                    Dispatcher.Run();
                }
                catch (Exception error) { completion.TrySetException(error); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task;
        }
        public void Dispose() => window.Dispatcher.Invoke(() => { window.Close(); window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); });
    }
}
