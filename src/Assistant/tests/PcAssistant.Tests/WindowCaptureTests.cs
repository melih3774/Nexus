using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PcAssistant.Windows;
using PcAssistant.Windows.Capture;
using Xunit;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Drawing.Color;

namespace PcAssistant.Tests;

/// <summary>
/// The claim under test is the one that makes independent observation worth having: a window
/// that is completely covered by another window is still returned as its own content.
///
/// Proving that needs real pixels, so the fixtures are two solid-colour windows placed on top
/// of each other. If the capture ever silently degraded to reading the screen, the covered
/// window's image would come back the covering window's colour and these assertions would say so.
/// </summary>
[Collection(DesktopCollection.Name)]
public sealed class WindowCaptureTests
{
    private const int Width = 420;
    private const int Height = 320;

    [Fact]
    public async Task CoveredWindowIsCapturedAsItsOwnContentNotAsWhatIsOnTopOfIt()
    {
        using var target = await ColourWindow.CreateAsync("PC Assistant capture target", Colors.Red, 140, 140, topmost: false);
        using var cover = await ColourWindow.CreateAsync("PC Assistant capture cover", Colors.Blue, 100, 100, topmost: true,
            width: Width + 200, height: Height + 200);

        var registry = new CaptureRegistry();
        var capture = new WindowCapture(new DesktopWindows(), registry);
        var shot = capture.CaptureWindow(target.Hwnd);

        // The screen where the target sits is blue; the target itself is red.
        Assert.True(shot.Occluded, "The cover window should be reported as overlapping the target.");
        Assert.Equal(CaptureMethod.GraphicsCapture, shot.Method);
        Assert.True(IsMostly(shot.Png, Color.Red),
            $"A covered window must be captured as its own content. Method was {shot.Method}, note: {shot.Note}");

        // The screen-pixel path is the control: it should see the cover, which is exactly why
        // it is not what capture_window uses.
        var onScreen = new DesktopWindows().Capture(shot.DesktopBounds);
        Assert.True(IsMostly(onScreen.Png, Color.Blue),
            "The visible desktop under the target should be the covering window; otherwise this test proves nothing.");

        Assert.Equal(shot.DesktopBounds.Width, shot.ImageWidth);
        Assert.Equal(shot.DesktopBounds.Height, shot.ImageHeight);
        Assert.False(shot.Scaled);
        Assert.StartsWith("cap-", shot.CaptureId);
    }

    [Fact]
    public async Task CapturePointResolvesToTheDesktopAndFollowsTheWindowWhenItMoves()
    {
        using var target = await ColourWindow.CreateAsync("PC Assistant capture geometry", Colors.Green, 200, 200);
        var registry = new CaptureRegistry();
        var capture = new WindowCapture(new DesktopWindows(), registry);
        var shot = capture.CaptureWindow(target.Hwnd);

        var centre = registry.Resolve(shot.CaptureId, shot.ImageWidth / 2, shot.ImageHeight / 2);
        Assert.False(centre.WindowMoved);
        Assert.Equal(target.Hwnd, centre.Hwnd);
        Assert.InRange(centre.X, shot.DesktopBounds.X, shot.DesktopBounds.X + shot.DesktopBounds.Width);
        Assert.InRange(centre.Y, shot.DesktopBounds.Y, shot.DesktopBounds.Y + shot.DesktopBounds.Height);

        // Corners map to the edges of the rectangle the image actually covers.
        var origin = registry.Resolve(shot.CaptureId, 0, 0);
        Assert.Equal(shot.DesktopBounds.X, origin.X);
        Assert.Equal(shot.DesktopBounds.Y, origin.Y);

        // Moving the window does not re-lay-out its contents, so the image stays valid and the
        // point simply follows it. This is the case that would otherwise click the old location.
        // The move is asserted against the window's measured physical displacement rather than
        // the logical units it was asked for: the desktop this runs on is not at 100% scaling.
        var before = shot.DesktopBounds;
        await target.MoveByAsync(120, 90);
        var after = new DesktopWindows().ListWindows(400).Single(w => w.Hwnd == target.Hwnd).Bounds;
        Assert.True(after.X != before.X || after.Y != before.Y, "The fixture window should have moved.");

        var afterMove = registry.Resolve(shot.CaptureId, shot.ImageWidth / 2, shot.ImageHeight / 2);
        Assert.True(afterMove.WindowMoved);
        Assert.Contains("translated", afterMove.Note);
        Assert.Equal(centre.X + (after.X - before.X), afterMove.X);
        Assert.Equal(centre.Y + (after.Y - before.Y), afterMove.Y);
    }

    [Fact]
    public async Task ResizingTheWindowInvalidatesEveryCoordinateFromTheOldImage()
    {
        using var target = await ColourWindow.CreateAsync("PC Assistant capture resize", Colors.DarkOrange, 260, 240);
        var registry = new CaptureRegistry();
        var shot = new WindowCapture(new DesktopWindows(), registry).CaptureWindow(target.Hwnd);
        registry.Resolve(shot.CaptureId, 10, 10);

        await target.ResizeByAsync(70, 50);

        var error = Assert.Throws<StaleCaptureException>(() => registry.Resolve(shot.CaptureId, 10, 10));
        Assert.Contains("laid out again", error.Message);
        Assert.Contains("Capture it again", error.Message);
    }

    [Fact]
    public async Task ClosedMinimizedAndExpiredCapturesAreRefusedRatherThanGuessedAt()
    {
        var clock = DateTimeOffset.UtcNow;
        var registry = new CaptureRegistry(() => clock);
        var capture = new WindowCapture(new DesktopWindows(), registry);

        using (var target = await ColourWindow.CreateAsync("PC Assistant capture lifetime", Colors.Purple, 320, 260))
        {
            var shot = capture.CaptureWindow(target.Hwnd);
            registry.Resolve(shot.CaptureId, 5, 5);

            clock += CaptureRegistry.Lifetime + TimeSpan.FromSeconds(1);
            Assert.Contains("no longer a safe frame of reference",
                Assert.Throws<StaleCaptureException>(() => registry.Resolve(shot.CaptureId, 5, 5)).Message);
            clock -= CaptureRegistry.Lifetime + TimeSpan.FromSeconds(1);

            var live = capture.CaptureWindow(target.Hwnd);
            await target.MinimizeAsync();

            Assert.Contains("minimized",
                Assert.Throws<WindowNotObservableException>(() => capture.CaptureWindow(target.Hwnd)).Message);
            Assert.Contains("minimized or hidden",
                Assert.Throws<StaleCaptureException>(() => registry.Resolve(live.CaptureId, 5, 5)).Message);
        }

        Assert.Throws<ArgumentException>(() => capture.CaptureWindow(0));
    }

    [Fact]
    public void UnknownCaptureIdsAndOutOfRangePointsAreRejected()
    {
        var registry = new CaptureRegistry();
        Assert.Throws<StaleCaptureException>(() => registry.Resolve("cap-does-not-exist", 0, 0));
        Assert.Throws<ArgumentException>(() => registry.Resolve("   ", 0, 0));

        var id = registry.Register(hwnd: 0, new PixelRect(10, 20, 100, 50), 100, 50, CaptureMethod.GraphicsCapture);
        Assert.True(registry.TryDescribe(id, out var hwnd, out var bounds, out var width, out var height));
        Assert.Equal(0, hwnd);
        Assert.Equal(new PixelRect(10, 20, 100, 50), bounds);
        Assert.Equal((100, 50), (width, height));
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.Resolve(id, 100, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.Resolve(id, 0, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.Resolve(id, -1, 0));
    }

    [Fact]
    public async Task PcAssistantsOwnWindowsAreNeverCaptured()
    {
        using var target = await ColourWindow.CreateAsync("PC Assistant capture protection", Colors.Teal, 380, 300);
        var previous = ProtectedWindows.OwnerProcessId;
        ProtectedWindows.OwnerProcessId = Environment.ProcessId;
        try
        {
            var capture = new WindowCapture(new DesktopWindows(), new CaptureRegistry());
            Assert.Contains("belongs to PC Assistant itself",
                Assert.Throws<InvalidOperationException>(() => capture.CaptureWindow(target.Hwnd)).Message);
        }
        finally { ProtectedWindows.OwnerProcessId = previous; }
    }


    [Fact]
    public async Task CaptureAnchoredInputRefusesAnUnusableFrameBeforeTouchingTheDesktop()
    {
        var registry = new CaptureRegistry();
        using var input = new InputService(registry);

        // An unknown or expired id must fail before anything acquires the foreground: the
        // owner's window order is not disturbed to discover that a coordinate frame is stale.
        var before = new DesktopWindows().ForegroundWindow()?.Hwnd;

        await Assert.ThrowsAsync<StaleCaptureException>(
            () => input.ClickCaptureAsync("cap-unknown", 10, 10));
        await Assert.ThrowsAsync<StaleCaptureException>(
            () => input.ScrollCaptureAsync("cap-unknown", 10, 10, ticks: 3));
        await Assert.ThrowsAsync<StaleCaptureException>(
            () => input.DragCaptureAsync("cap-unknown", 1, 1, 2, 2));

        Assert.Equal(before, new DesktopWindows().ForegroundWindow()?.Hwnd);

        // Argument validation also happens before the frame is even looked up.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => input.ClickCaptureAsync("cap-unknown", 0, 0, count: 5));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => input.ScrollCaptureAsync("cap-unknown", 0, 0, ticks: 0));

        // Nothing was injected, so nothing can be left held down.
        InputService.ReleaseAll();
        Assert.Empty(InputService.HeldInputs());
    }

    [Fact]
    public async Task ACancelledCaptureClickIsCancelledRatherThanPartiallyInjected()
    {
        using var target = await ColourWindow.CreateAsync("PC Assistant capture cancel", Colors.SlateGray, 240, 220);
        var registry = new CaptureRegistry();
        var shot = new WindowCapture(new DesktopWindows(), registry).CaptureWindow(target.Hwnd);
        using var input = new InputService(registry);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => input.ClickCaptureAsync(shot.CaptureId, 5, 5, cancellationToken: new CancellationToken(true)));

        InputService.ReleaseAll();
        Assert.Empty(InputService.HeldInputs());
    }
    /// <summary>True when most sampled pixels are close to <paramref name="expected"/>.</summary>
    private static bool IsMostly(byte[] png, Color expected)
    {
        using var stream = new MemoryStream(png, writable: false);
        using var bitmap = new Bitmap(stream);
        var hits = 0;
        var total = 0;
        // Sampled from the middle half, away from title bars, borders and rounded corners.
        for (var y = bitmap.Height / 4; y < bitmap.Height * 3 / 4; y += 7)
        for (var x = bitmap.Width / 4; x < bitmap.Width * 3 / 4; x += 7)
        {
            var pixel = bitmap.GetPixel(x, y);
            total++;
            if (Math.Abs(pixel.R - expected.R) < 60 && Math.Abs(pixel.G - expected.G) < 60 && Math.Abs(pixel.B - expected.B) < 60) hits++;
        }
        return total > 0 && hits * 2 > total;
    }

    /// <summary>A borderless solid-colour window, so a captured image can be checked by colour alone.</summary>
    private sealed class ColourWindow(Window window, long hwnd) : IDisposable
    {
        public long Hwnd { get; } = hwnd;

        public static Task<ColourWindow> CreateAsync(string title, System.Windows.Media.Color colour, int left, int top,
            bool topmost = false, int width = Width, int height = Height)
        {
            var completion = new TaskCompletionSource<ColourWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    var window = new Window
                    {
                        Title = title, Width = width, Height = height, Left = left, Top = top,
                        WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                        ShowActivated = false, ShowInTaskbar = false, Topmost = topmost,
                        Background = new SolidColorBrush(colour),
                        // A drop shadow would blend the two colours along the edges of the sample area.
                        AllowsTransparency = false,
                    };
                    window.Show();
                    completion.SetResult(new ColourWindow(window, new WindowInteropHelper(window).Handle.ToInt64()));
                    Dispatcher.Run();
                }
                catch (Exception error) { completion.TrySetException(error); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return Settle(completion.Task);
        }

        public Task MoveByAsync(int dx, int dy) => Change(w => { w.Left += dx; w.Top += dy; });
        public Task ResizeByAsync(int dw, int dh) => Change(w => { w.Width += dw; w.Height += dh; });
        public Task MinimizeAsync() => Change(w => w.WindowState = WindowState.Minimized);

        private async Task Change(Action<Window> change)
        {
            await window.Dispatcher.InvokeAsync(() => change(window));
            await Task.Delay(300);
        }

        /// <summary>Waits for the compositor to have something to hand back before the first capture.</summary>
        private static async Task<ColourWindow> Settle(Task<ColourWindow> creation)
        {
            var created = await creation;
            await Task.Delay(400);
            return created;
        }

        public void Dispose() => window.Dispatcher.Invoke(() =>
        {
            window.Close();
            window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
        });
    }
}
