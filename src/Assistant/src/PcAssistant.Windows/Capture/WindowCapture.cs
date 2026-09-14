using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace PcAssistant.Windows.Capture;

/// <summary>How a window image was obtained. Reported so the reader knows what it is looking at.</summary>
public enum CaptureMethod
{
    /// <summary>Windows Graphics Capture: the window's own composed surface, independent of what covers it.</summary>
    GraphicsCapture = 0,
    /// <summary>WM_PRINT into an offscreen bitmap. Independent of occlusion; blank for some GPU-drawn content.</summary>
    PrintWindow = 1,
    /// <summary>Visible desktop pixels in the window's region. Whatever covers the window is in the image.</summary>
    ScreenRegion = 2,
}

/// <summary>
/// One window image plus everything needed to reason about it and act on it.
/// <see cref="DesktopBounds"/> is the desktop rectangle the image covers, so an image
/// coordinate maps back to the desktop through it and nothing else.
/// </summary>
public sealed record WindowCaptureResult(
    byte[] Png,
    string CaptureId,
    long Hwnd,
    string Title,
    uint ProcessId,
    PixelRect DesktopBounds,
    int ImageWidth,
    int ImageHeight,
    CaptureMethod Method,
    bool Occluded,
    bool Scaled,
    uint Dpi,
    bool IsForeground,
    string Note);

/// <summary>
/// A window could not be observed, and no image would be honest.
///
/// Kept separate from every other failure because the answer is never "retry the same
/// capture": a minimized window has to be restored, a capture-protected one never yields
/// pixels at all, and a closed one is gone.
/// </summary>
public sealed class WindowNotObservableException(string message) : InvalidOperationException(message);

/// <summary>
/// Independent per-window capture.
///
/// Three mechanisms, tried in the order of how much they can be trusted:
///
/// 1. Windows Graphics Capture — the window's own DWM surface. Works while the window is
///    behind other windows, off to the side, or not focused. This is the one that makes
///    "look at that window without disturbing the owner's desktop" true.
/// 2. PrintWindow with PW_RENDERFULLCONTENT — asks the window to render itself into a
///    bitmap. Also unaffected by occlusion, but some hardware-composited content draws
///    nothing, which is why it is second and why a blank result is rejected rather than
///    returned.
/// 3. The visible desktop region — the old behaviour, and the only one that includes
///    whatever is covering the window. Never chosen silently: the result says so.
///
/// Every path reports the desktop rectangle the image actually covers. Nothing here moves,
/// raises, restores or focuses a window.
/// </summary>
public sealed class WindowCapture(DesktopWindows windows, CaptureRegistry registry)
{
    /// <summary>Longest image edge, before the caller asks for something smaller.</summary>
    public const int DefaultMaxEdge = 1600;
    private const int HardMaxEdge = 2400;
    private const long PngBudget = 4_500_000;

    private readonly DesktopWindows windows = windows;
    private readonly CaptureRegistry registry = registry;

    /// <summary>
    /// Whether the compositor path is usable. Declared as a platform guard so the rest of this
    /// file can call the WinRT engine without every call site repeating the version check.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows10.0.19041.0")]
    private static bool GraphicsCaptureAvailable =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && GraphicsCaptureEngine.IsSupported;

    /// <summary>Whether independent capture is available at all, and what limits apply.</summary>
    public static string Availability => GraphicsCaptureAvailable
        ? "Windows Graphics Capture available: a specific window can be observed while other windows cover it."
        : "Windows Graphics Capture unavailable on this Windows build; window capture falls back to PrintWindow and to visible desktop pixels.";

    public WindowCaptureResult CaptureWindow(long hwnd, int maxEdge = DefaultMaxEdge,
        bool includeCursor = false, bool allowScreenFallback = true, CancellationToken cancellationToken = default)
    {
        if (maxEdge is < 200 or > HardMaxEdge)
            throw new ArgumentOutOfRangeException(nameof(maxEdge), $"Use a longest-edge budget between 200 and {HardMaxEdge} pixels.");

        using var dpi = new Native.DpiScope();
        var handle = Native.Window(hwnd);
        var info = DesktopWindows.Describe(handle);

        // Refused before anything is captured: an approval card must never be photographed
        // and reasoned about as if it were an ordinary application window.
        ProtectedWindows.EnsureNotProtectedWindow(hwnd);

        if (info.IsMinimized)
            throw new WindowNotObservableException(
                $"Window {hwnd} ('{Shorten(info.Title)}') is minimized, so Windows holds no current surface for it. " +
                "Restore it with window_action restore first, or tell the owner why you need it visible.");

        if (IsCloaked(handle))
            throw new WindowNotObservableException(
                $"Window {hwnd} ('{Shorten(info.Title)}') is cloaked by Windows — it belongs to another virtual desktop, " +
                "or is a suspended packaged application. It has no observable content until the owner switches to it.");

        if (IsCaptureProtected(handle))
            throw new WindowNotObservableException(
                $"Window {hwnd} ('{Shorten(info.Title)}') sets a display affinity that excludes it from capture. " +
                "Applications do this deliberately for protected content; there is no image to return and no way around it. " +
                "Use ui_snapshot to read its structure, or ask the owner what it shows.");

        var occluded = IsOccluded(handle, info.Bounds);
        var failures = new List<string>();

        if (GraphicsCaptureAvailable)
        {
            var frame = FrameBounds(handle, info.Bounds);
            try
            {
                var (png, width, height) = GraphicsCaptureEngine.Capture(handle, maxEdge, includeCursor, cancellationToken);
                return Result(info, frame, png, width, height, CaptureMethod.GraphicsCapture, occluded,
                    "The window's own content, read from the compositor. Anything overlapping it on screen is not in this image.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failures.Add("Windows Graphics Capture: " + ex.Message);
                // A lost device fails every later capture identically until it is rebuilt.
                GraphicsCaptureEngine.ResetDevice();
            }
        }
        else failures.Add("Windows Graphics Capture is not supported on this Windows build.");

        try
        {
            var (png, width, height, bounds) = PrintWindowCapture(handle, info.Bounds, maxEdge);
            return Result(info, bounds, png, width, height, CaptureMethod.PrintWindow, occluded,
                "Rendered by the window itself. Overlapping windows are not in this image, but hardware-composited " +
                "regions (video, some 3D content) can appear blank.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { failures.Add("PrintWindow: " + ex.Message); }

        if (!allowScreenFallback)
            throw new WindowNotObservableException(
                $"Window {hwnd} ('{Shorten(info.Title)}') could not be captured independently. " +
                string.Join(" ", failures));

        var region = VisibleRegion(info.Bounds)
            ?? throw new WindowNotObservableException(
                $"Window {hwnd} ('{Shorten(info.Title)}') is entirely outside the visible desktop, and independent capture failed. " +
                string.Join(" ", failures));

        var shot = windows.Capture(region);
        var (screenPng, screenWidth, screenHeight) = Fit(shot.Png, maxEdge);
        return Result(info, region, screenPng, screenWidth, screenHeight, CaptureMethod.ScreenRegion, occluded,
            "Independent capture was unavailable, so this is the visible desktop where the window sits" +
            (occluded ? " — and another window is currently over it, so part of this image is not this window." : ".") +
            " Reason: " + string.Join(" ", failures));
    }

    /// <summary>The window the owner is actually looking at, or an honest refusal.</summary>
    public WindowCaptureResult CaptureForeground(int maxEdge = DefaultMaxEdge, bool includeCursor = false,
        CancellationToken cancellationToken = default)
    {
        using var dpi = new Native.DpiScope();
        var foreground = Native.GetForegroundWindow();
        if (foreground == 0)
            throw new WindowNotObservableException(
                "There is no foreground window right now. Windows reports this while the secure desktop is up — " +
                "a UAC prompt, the lock screen or the login screen. PC Assistant cannot observe or operate any of those; " +
                "if one is expected, tell the owner it needs answering on the local PC.");

        return CaptureWindow((long)foreground, maxEdge, includeCursor, allowScreenFallback: true, cancellationToken);
    }

    private WindowCaptureResult Result(WindowInfo info, PixelRect bounds, byte[] png, int width, int height,
        CaptureMethod method, bool occluded, string note)
    {
        if (png.LongLength > PngBudget)
            throw new InvalidOperationException(
                "The captured image exceeds the size budget even after scaling. Capture with a smaller maxEdge.");

        var scaled = width != bounds.Width || height != bounds.Height;
        var id = registry.Register(info.Hwnd, bounds, width, height, method);
        var detail = note;
        if (scaled) detail += $" Scaled to {width}x{height} from {bounds.Width}x{bounds.Height}.";
        if (occluded && method != CaptureMethod.ScreenRegion)
            detail += " Another window is currently over this one on screen; the owner cannot see what is in this image.";

        return new WindowCaptureResult(png, id, info.Hwnd, info.Title, info.ProcessId, bounds, width, height,
            method, occluded, scaled, info.Dpi, info.IsForeground, detail);
    }

    // ------------------------------------------------------------- geometry

    /// <summary>
    /// The rectangle a Graphics Capture frame covers. That is the DWM frame, which excludes the
    /// invisible resize border <c>GetWindowRect</c> includes — measured as a 7 px horizontal
    /// difference on this machine. Getting this wrong offsets every coordinate derived from
    /// the image, so the window rect is only a fallback.
    /// </summary>
    private static PixelRect FrameBounds(nint handle, PixelRect windowRect)
    {
        if (Native.DwmGetWindowAttribute(handle, Native.ExtendedFrameBounds, out Native.Rect frame, 16) == 0)
        {
            var width = frame.Right - frame.Left;
            var height = frame.Bottom - frame.Top;
            if (width > 0 && height > 0) return new PixelRect(frame.Left, frame.Top, width, height);
        }
        return windowRect;
    }

    private static bool IsCloaked(nint handle) =>
        Native.DwmGetWindowAttribute(handle, Native.Cloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>WDA_MONITOR / WDA_EXCLUDEFROMCAPTURE: the application opted out of being recorded.</summary>
    private static bool IsCaptureProtected(nint handle) =>
        Native.GetWindowDisplayAffinity(handle, out var affinity) && affinity != 0;

    /// <summary>
    /// Whether another window currently covers any of this one. Reported alongside every
    /// capture so the reader knows whether the owner can actually see what it just looked at.
    /// EnumWindows walks front to back, so everything before the target is above it.
    /// </summary>
    private static bool IsOccluded(nint handle, PixelRect bounds)
    {
        var target = bounds.Rectangle;
        if (target.Width < 1 || target.Height < 1) return false;

        var occluded = false;
        var reached = false;
        Native.EnumWindows((candidate, _) =>
        {
            // Enumeration stops at the target, so only windows genuinely above it are considered.
            // It must not stop earlier than that, even once an overlap is found: without reaching
            // the target there is no evidence the overlapping window was in front of it.
            if (candidate == handle) { reached = true; return false; }
            if (occluded) return true;
            if (!Native.IsWindowVisible(candidate) || Native.IsIconic(candidate) || IsCloaked(candidate)) return true;
            try
            {
                if (!Native.GetWindowRect(candidate, out var rect)) return true;
                var other = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                if (other.Width < 1 || other.Height < 1) return true;
                var overlap = Rectangle.Intersect(other, target);
                // A shared edge is not occlusion; covering a hundredth of the window is.
                if ((long)overlap.Width * overlap.Height > (long)target.Width * target.Height / 100) occluded = true;
            }
            catch (System.ComponentModel.Win32Exception) { /* Window went away mid-enumeration. */ }
            return true;
        }, 0);

        return reached && occluded;
    }

    private PixelRect? VisibleRegion(PixelRect bounds)
    {
        var monitors = windows.Monitors();
        if (monitors.Count == 0) return null;
        var left = monitors.Min(m => m.Bounds.X);
        var top = monitors.Min(m => m.Bounds.Y);
        var right = monitors.Max(m => m.Bounds.X + m.Bounds.Width);
        var bottom = monitors.Max(m => m.Bounds.Y + m.Bounds.Height);
        var x = Math.Max(left, bounds.X);
        var y = Math.Max(top, bounds.Y);
        var width = Math.Min(right, bounds.X + bounds.Width) - x;
        var height = Math.Min(bottom, bounds.Y + bounds.Height) - y;
        return width < 1 || height < 1 ? null : new PixelRect(x, y, width, height);
    }

    // ---------------------------------------------------------- PrintWindow

    /// <summary>
    /// PrintWindow renders into the window rect, not the DWM frame, so it reports that rect as
    /// its geometry. A window that ignores WM_PRINT hands back a uniform bitmap; returning
    /// that as an image of the application would be worse than failing, so it is rejected.
    /// </summary>
    private static (byte[] Png, int Width, int Height, PixelRect Bounds) PrintWindowCapture(
        nint handle, PixelRect bounds, int maxEdge)
    {
        bounds.Validate();
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try
            {
                if (!Native.PrintWindow(handle, dc, Native.RenderFullContent))
                    throw new InvalidOperationException("The window did not render itself when asked.");
            }
            finally { graphics.ReleaseHdc(dc); }
        }

        if (IsUniform(bitmap))
            throw new InvalidOperationException("The window rendered a blank bitmap, which is not a usable image of it.");

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var (png, width, height) = Fit(stream.ToArray(), maxEdge);
        return (png, width, height, bounds);
    }

    /// <summary>Samples a grid rather than every pixel: this only has to catch an all-one-colour bitmap.</summary>
    private static bool IsUniform(Bitmap bitmap)
    {
        var first = bitmap.GetPixel(0, 0);
        for (var y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 24))
        for (var x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 24))
            if (bitmap.GetPixel(x, y) != first) return false;
        return true;
    }

    /// <summary>Scales an encoded PNG down to the edge budget, leaving it alone when it already fits.</summary>
    private static (byte[] Png, int Width, int Height) Fit(byte[] png, int maxEdge)
    {
        using var source = new MemoryStream(png, writable: false);
        using var image = new Bitmap(source);
        var longest = Math.Max(image.Width, image.Height);
        if (longest <= maxEdge) return (png, image.Width, image.Height);

        var scale = (double)maxEdge / longest;
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        using var scaled = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(image, 0, 0, width, height);
        }
        using var output = new MemoryStream();
        scaled.Save(output, ImageFormat.Png);
        return (output.ToArray(), width, height);
    }

    private static string Shorten(string value) =>
        string.IsNullOrWhiteSpace(value) ? "untitled" : value.Length <= 60 ? value : value[..59] + "…";
}
