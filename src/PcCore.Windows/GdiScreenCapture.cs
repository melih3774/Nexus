using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Screen;
using PcCore.Protocol;
using PcCore.Windows.Interop;

namespace PcCore.Windows;

/// <summary>
/// Single-frame desktop capture using GDI (<c>BitBlt</c> via <see cref="Graphics.CopyFromScreen(Point, Point, Size)"/>).
/// <para>
/// <b>Why GDI for v0.1:</b> it needs no elevation, no DirectX device, and no extra dependency, and
/// it works in an ordinary interactive session. It is the right choice for "one request, one
/// screenshot".
/// </para>
/// <para>
/// <b>Known blind spots</b>, documented rather than hidden: windows using DRM/protected content
/// (<c>SetWindowDisplayAffinity</c>) capture as black; hardware-overlay video may not appear;
/// the mouse cursor is not included; and capture is comparatively slow, which is precisely why
/// this sits behind <see cref="IScreenCapture"/> — a streaming milestone replaces it with DXGI
/// Desktop Duplication without any capability, transport, or protocol change.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GdiScreenCapture : IScreenCapture
{
    private readonly IDisplayGeometry _geometry;
    private readonly ILogger<GdiScreenCapture> _logger;

    public GdiScreenCapture(IDisplayGeometry geometry, ILogger<GdiScreenCapture> logger)
    {
        _geometry = geometry;
        _logger = logger;
    }

    public CapturedFrame Capture(DisplaySelection selection, int? maxWidth)
    {
        var bounds = ResolveBounds(selection);

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new CapabilityException(
                ErrorCodes.Unsupported,
                "No interactive desktop is available to capture.");
        }

        try
        {
            using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);

            using (var graphics = Graphics.FromImage(source))
            {
                // The virtual desktop origin can be negative when a secondary monitor sits above
                // or to the left of the primary one. Copying from (0,0) would silently capture
                // the wrong region on those layouts.
                graphics.CopyFromScreen(
                    bounds.X,
                    bounds.Y,
                    0,
                    0,
                    new Size(bounds.Width, bounds.Height),
                    CopyPixelOperation.SourceCopy);
            }

            using var output = Downscale(source, maxWidth);

            using var stream = new MemoryStream();
            output.Save(stream, ImageFormat.Png);

            return new CapturedFrame(
                stream.ToArray(),
                output.Width,
                output.Height,
                bounds.Width,
                bounds.Height);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or ArgumentException)
        {
            // Happens on a locked workstation, a session without a desktop, or a display change
            // mid-capture. It is an environment condition, not a caller mistake.
            _logger.LogWarning(ex, "Screen capture failed for display {Display}.", selection);
            throw new CapabilityException(ErrorCodes.Unsupported, "The desktop could not be captured.");
        }
        catch (OutOfMemoryException)
        {
            throw new CapabilityException(ErrorCodes.Unsupported, "Not enough memory to capture the desktop.");
        }
    }

    private Rectangle ResolveBounds(DisplaySelection selection)
    {
        switch (selection.Kind)
        {
            case DisplaySelectionKind.Virtual:
                return _geometry.VirtualBounds;

            case DisplaySelectionKind.Primary:
                return _geometry.PrimaryBounds;

            case DisplaySelectionKind.Index:
                var displays = _geometry.Displays;
                if (selection.Index >= displays.Count)
                {
                    throw new CapabilityException(
                        ErrorCodes.InvalidParameters,
                        $"Display index {selection.Index} does not exist. {displays.Count} display(s) are connected.");
                }

                return displays[selection.Index];

            default:
                return _geometry.VirtualBounds;
        }
    }

    private static Bitmap Downscale(Bitmap source, int? maxWidth)
    {
        if (maxWidth is null || source.Width <= maxWidth.Value)
        {
            return source;
        }

        var scale = (double)maxWidth.Value / source.Width;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        var scaled = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        using var graphics = Graphics.FromImage(scaled);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, 0, 0, width, height);

        return scaled;
    }
}

/// <summary>Display layout, separated so screen capture can be unit-tested with a fake geometry.</summary>
public interface IDisplayGeometry
{
    Rectangle VirtualBounds { get; }

    Rectangle PrimaryBounds { get; }

    IReadOnlyList<Rectangle> Displays { get; }
}

[SupportedOSPlatform("windows")]
public sealed class SystemDisplayGeometry : IDisplayGeometry
{
    // Enumerated through Win32 rather than System.Windows.Forms.Screen so the host stays a plain
    // console process and does not pull in the Windows Desktop framework reference.

    public Rectangle VirtualBounds
    {
        get
        {
            var x = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
            var y = NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen);
            var width = NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen);
            var height = NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen);
            return new Rectangle(x, y, width, height);
        }
    }

    /// <summary>The primary display is by definition anchored at the virtual-desktop origin (0,0).</summary>
    public Rectangle PrimaryBounds => new(
        0,
        0,
        NativeMethods.GetSystemMetrics(NativeMethods.SmCxScreen),
        NativeMethods.GetSystemMetrics(NativeMethods.SmCyScreen));

    public IReadOnlyList<Rectangle> Displays
    {
        get
        {
            var monitors = new List<Rectangle>();
            var handle = GCHandle.Alloc(monitors);

            try
            {
                unsafe
                {
                    NativeMethods.EnumDisplayMonitors(
                        0,
                        0,
                        &EnumerateMonitor,
                        GCHandle.ToIntPtr(handle));
                }
            }
            finally
            {
                handle.Free();
            }

            if (monitors.Count == 0)
            {
                // No interactive desktop, or enumeration failed. Fall back to the virtual bounds
                // so an index-based request still has something coherent to validate against.
                var virtualBounds = VirtualBounds;
                if (virtualBounds.Width > 0 && virtualBounds.Height > 0)
                {
                    monitors.Add(virtualBounds);
                }
            }

            // Stable order so a display index means the same thing between calls.
            monitors.Sort(static (left, right) =>
            {
                var byX = left.X.CompareTo(right.X);
                return byX != 0 ? byX : left.Y.CompareTo(right.Y);
            });

            return monitors;
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe int EnumerateMonitor(nint monitor, nint hdc, NativeMethods.Rect* bounds, nint data)
    {
        // Must not let a managed exception escape into unmanaged code.
        try
        {
            if (GCHandle.FromIntPtr(data).Target is List<Rectangle> monitors && bounds is not null)
            {
                monitors.Add(Rectangle.FromLTRB(bounds->Left, bounds->Top, bounds->Right, bounds->Bottom));
            }
        }
        catch
        {
            // Stop enumerating rather than propagate.
            return 0;
        }

        return 1;
    }
}

/// <summary>
/// Process-wide DPI awareness.
/// <para>
/// This must be set before any GDI work happens. Without it a process is DPI-virtualised on a
/// scaled display: Windows reports the <em>scaled</em> desktop size, and the screenshot comes back
/// smaller and blurrier than the real desktop — a silently wrong result rather than an error.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class DpiAwareness
{
    public static bool TryEnablePerMonitorV2()
    {
        try
        {
            return NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DpiAwarenessContextPerMonitorAwareV2);
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-1703 Windows. Capture still works; it may be scaled on high-DPI displays.
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }
}
