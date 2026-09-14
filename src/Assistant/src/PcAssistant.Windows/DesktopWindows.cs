using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PcAssistant.Windows;

public sealed record PixelRect(int X, int Y, int Width, int Height)
{
    internal Rectangle Rectangle => new(X, Y, Width, Height);
    public void Validate()
    {
        if (Width < 1 || Height < 1 || Width > 32768 || Height > 32768 ||
            (long)Width * Height > 64_000_000 || Math.Abs((long)X) > 100_000 || Math.Abs((long)Y) > 100_000)
            throw new ArgumentOutOfRangeException(nameof(Width), "Use positive pixel dimensions, at most 64 million pixels, within desktop coordinate limits.");
    }
}
public sealed record WindowInfo(long Hwnd, uint ProcessId, string Title, PixelRect Bounds, bool IsForeground, bool IsMinimized, bool IsMaximized, uint Dpi);
public sealed record MonitorInfo(string DeviceName, PixelRect Bounds, PixelRect WorkingArea, bool Primary);
public sealed record ScreenshotResult(byte[] Png, PixelRect Bounds, string MimeType = "image/png");
public enum WindowAction { Focus, Minimize, Maximize, Restore, MoveResize, Close }

public sealed class DesktopWindows
{
    public IReadOnlyList<WindowInfo> ListWindows(int limit = 100)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        using var dpi = new Native.DpiScope();
        var result = new List<WindowInfo>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (Native.IsWindowVisible(hwnd))
            {
                try { var info = Describe(hwnd); if (!string.IsNullOrWhiteSpace(info.Title)) result.Add(info); }
                catch (System.ComponentModel.Win32Exception) { /* Window disappeared during enumeration. */ }
            }
            return result.Count < limit;
        }, 0);
        return result;
    }

    public WindowInfo? ForegroundWindow()
    {
        using var dpi = new Native.DpiScope();
        var hwnd = Native.GetForegroundWindow();
        return hwnd == 0 ? null : Describe(hwnd);
    }

    /// <summary>
    /// Every state change reports what the desktop actually ended up doing. Focus in particular acquires
    /// the foreground through <see cref="ForegroundService"/> and returns verified state — never
    /// "activation requested", which left callers with nothing to act on.
    /// </summary>
    public async Task<string> ChangeWindowAsync(long hwnd, WindowAction action, PixelRect? bounds = null, CancellationToken cancellationToken = default)
    {
        var handle = Native.Window(hwnd);
        using var dpi = new Native.DpiScope();
        switch (action)
        {
            case WindowAction.Focus:
                var foreground = await ForegroundService.EnsureAsync(hwnd, cancellationToken).ConfigureAwait(false);
                if (!foreground.Verified) throw new ForegroundUnavailableException(foreground.Message);
                return foreground.Message + " Ordinary input does not need this call: input and ui_action take the foreground themselves.";
            case WindowAction.Minimize:
                Native.Check(Native.ShowWindowAsync(handle, 6), "Could not queue minimize.");
                return await SettleAsync(handle, hwnd, "minimized", h => Native.IsIconic(h), cancellationToken).ConfigureAwait(false);
            case WindowAction.Maximize:
                Native.Check(Native.ShowWindowAsync(handle, 3), "Could not queue maximize.");
                return await SettleAsync(handle, hwnd, "maximized", h => Native.IsZoomed(h), cancellationToken).ConfigureAwait(false);
            case WindowAction.Restore:
                Native.Check(Native.ShowWindowAsync(handle, 9), "Could not queue restore.");
                return await SettleAsync(handle, hwnd, "restored", h => !Native.IsIconic(h), cancellationToken).ConfigureAwait(false);
            case WindowAction.MoveResize:
                if (bounds is null) throw new ArgumentException("MoveResize requires bounds.", nameof(bounds));
                bounds.Validate();
                if (!Monitors().Any(m => m.Bounds.Rectangle.IntersectsWith(bounds.Rectangle))) throw new ArgumentException("Window bounds must intersect a connected monitor.", nameof(bounds));
                Native.Check(Native.SetWindowPos(handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x14), "Window move/resize failed.");
                return "Window move/resize completed.";
            case WindowAction.Close:
                ProtectedWindows.EnsureNotProtectedWindow(hwnd);
                Native.Check(Native.PostMessage(handle, 0x0010, 0, 0), "Could not queue WM_CLOSE.");
                return "Close requested with WM_CLOSE. The application may show an unsaved-work prompt or decline.";
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private static async Task<string> SettleAsync(nint handle, long hwnd, string state, Func<nint, bool> reached, CancellationToken cancellationToken)
    {
        foreach (var wait in new[] { 0, 30, 60, 120, 240 })
        {
            if (wait > 0) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            if (!Native.IsWindow(handle)) return $"Window {hwnd} closed while being {state}.";
            if (reached(handle)) return $"Window {hwnd} is verified {state}.";
        }
        return $"Window {hwnd} did not report {state} within the wait; list windows to see its actual state.";
    }

    public IReadOnlyList<MonitorInfo> Monitors()
    {
        using var dpi = new Native.DpiScope();
        return Screen.AllScreens.Select(s => new MonitorInfo(s.DeviceName, From(s.Bounds), From(s.WorkingArea), s.Primary)).ToArray();
    }

    public ScreenshotResult Capture(PixelRect? region = null)
    {
        using var dpi = new Native.DpiScope();
        var desktop = SystemInformation.VirtualScreen;
        region ??= From(desktop);
        region.Validate();
        if (!desktop.Contains(region.Rectangle)) throw new ArgumentException("Capture bounds must be inside the virtual desktop.", nameof(region));
        using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(region.X, region.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        if (stream.Length > 4_500_000) throw new InvalidOperationException("Screenshot exceeds the 4.5 MB image limit. Capture one monitor or a smaller pixel region.");
        return new ScreenshotResult(stream.ToArray(), region);
    }

    internal static WindowInfo Describe(nint hwnd)
    {
        var title = new StringBuilder(1024);
        Native.GetWindowText(hwnd, title, title.Capacity);
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        Native.Check(Native.GetWindowRect(hwnd, out var rect), "Window geometry is unavailable.");
        return new WindowInfo((long)hwnd, pid, title.ToString(), new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top),
            Native.GetForegroundWindow() == hwnd, Native.IsIconic(hwnd), Native.IsZoomed(hwnd), Native.GetDpiForWindow(hwnd));
    }
    private static PixelRect From(Rectangle value) => new(value.X, value.Y, value.Width, value.Height);
}
