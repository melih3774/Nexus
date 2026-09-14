using System;
using System.Collections.Concurrent;
using System.Linq;

namespace PcAssistant.Windows.Capture;

/// <summary>Where an image coordinate landed on the real desktop, and what had to be rechecked to say so.</summary>
public sealed record ResolvedPoint(int X, int Y, long Hwnd, bool WindowMoved, string Note);

/// <summary>
/// Ties image coordinates to the capture they came from.
///
/// The failure this exists to prevent: read a screenshot, reason about it, and click a pixel
/// several seconds later — by which time the window has moved, resized, or gone, and the
/// click lands on whatever is there now. Absolute desktop coordinates carry no evidence of
/// which observation produced them, so nothing can catch that.
///
/// A capture id does carry it. Resolving one re-reads the window's current geometry and
/// compares it to the geometry that was captured:
///
/// * gone, minimized or a different window   -> refused
/// * resized                                 -> refused; the content has been laid out again
/// * moved                                   -> translated to the new origin, and said so
/// * unchanged                               -> translated
///
/// Ids are short-lived because a stale one is a worse failure than an expired one.
/// </summary>
public sealed class CaptureRegistry
{
    /// <summary>How long a capture stays usable as a coordinate frame.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    /// <summary>Pixels of size drift tolerated before a capture is treated as describing a different layout.</summary>
    private const int SizeTolerance = 2;
    private const int Capacity = 32;

    private sealed record Entry(long Hwnd, PixelRect Bounds, int ImageWidth, int ImageHeight,
        CaptureMethod Method, uint ProcessId, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> now;

    public CaptureRegistry() : this(() => DateTimeOffset.UtcNow) { }

    /// <summary>Test seam for expiry; production always uses the wall clock.</summary>
    public CaptureRegistry(Func<DateTimeOffset> clock) => now = clock;

    public string Register(long hwnd, PixelRect bounds, int imageWidth, int imageHeight, CaptureMethod method)
    {
        if (imageWidth < 1 || imageHeight < 1) throw new ArgumentOutOfRangeException(nameof(imageWidth));
        Prune();
        Native.GetWindowThreadProcessId((nint)hwnd, out var processId);
        var id = "cap-" + Guid.NewGuid().ToString("N")[..10];
        entries[id] = new Entry(hwnd, bounds, imageWidth, imageHeight, method, processId, now() + Lifetime);
        return id;
    }

    public bool TryDescribe(string captureId, out long hwnd, out PixelRect bounds, out int imageWidth, out int imageHeight)
    {
        if (captureId is not null && entries.TryGetValue(captureId, out var entry) && entry.ExpiresAt > now())
        {
            (hwnd, bounds, imageWidth, imageHeight) = (entry.Hwnd, entry.Bounds, entry.ImageWidth, entry.ImageHeight);
            return true;
        }
        (hwnd, bounds, imageWidth, imageHeight) = (0, new PixelRect(0, 0, 1, 1), 0, 0);
        return false;
    }

    /// <summary>
    /// Maps a point in a captured image back to the desktop, or explains why that cannot be
    /// done honestly. Never guesses: an unverifiable frame of reference is refused.
    /// </summary>
    public ResolvedPoint Resolve(string captureId, int imageX, int imageY)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        if (!entries.TryGetValue(captureId, out var entry))
            throw new StaleCaptureException(
                "That capture id is unknown. Capture the window again and use the id from that result.");

        if (entry.ExpiresAt <= now())
        {
            entries.TryRemove(captureId, out _);
            throw new StaleCaptureException(
                $"That capture is older than {Lifetime.TotalMinutes:0} minutes, so it is no longer a safe frame of " +
                "reference for clicking. Capture the window again and work from the new image.");
        }

        if (imageX < 0 || imageY < 0 || imageX >= entry.ImageWidth || imageY >= entry.ImageHeight)
            throw new ArgumentOutOfRangeException(nameof(imageX),
                $"The point must be inside the captured image, which is {entry.ImageWidth}x{entry.ImageHeight} pixels.");

        using var dpi = new Native.DpiScope();
        var handle = (nint)entry.Hwnd;
        if (!Native.IsWindow(handle))
            throw new StaleCaptureException($"Window {entry.Hwnd} has closed since it was captured. Observe again.");

        // A recycled handle belonging to a different process would otherwise pass every
        // geometry check and take the click somewhere else entirely.
        Native.GetWindowThreadProcessId(handle, out var processId);
        if (processId != entry.ProcessId)
            throw new StaleCaptureException(
                $"Window handle {entry.Hwnd} now belongs to a different process than the one that was captured. " +
                "List windows again and re-observe the target.");

        if (Native.IsIconic(handle) || !Native.IsWindowVisible(handle))
            throw new StaleCaptureException(
                $"Window {entry.Hwnd} is minimized or hidden now, so a point from that image means nothing. " +
                "Restore it and observe again.");

        var current = CurrentBounds(handle, entry.Method);
        if (Math.Abs(current.Width - entry.Bounds.Width) > SizeTolerance ||
            Math.Abs(current.Height - entry.Bounds.Height) > SizeTolerance)
            throw new StaleCaptureException(
                $"Window {entry.Hwnd} was {entry.Bounds.Width}x{entry.Bounds.Height} when it was captured and is " +
                $"{current.Width}x{current.Height} now. Its contents have been laid out again, so nothing in that image " +
                "can be located on it. Capture it again and work from the new image.");

        var moved = current.X != entry.Bounds.X || current.Y != entry.Bounds.Y;

        // Scaling is per-axis on purpose: a captured image is scaled to fit a longest-edge
        // budget, and rounding leaves the two axes with slightly different ratios.
        var x = current.X + (int)Math.Round((imageX + 0.5) * entry.Bounds.Width / entry.ImageWidth - 0.5, MidpointRounding.AwayFromZero);
        var y = current.Y + (int)Math.Round((imageY + 0.5) * entry.Bounds.Height / entry.ImageHeight - 0.5, MidpointRounding.AwayFromZero);

        var note = moved
            ? $"The window moved from ({entry.Bounds.X},{entry.Bounds.Y}) to ({current.X},{current.Y}) since it was " +
              "captured; the point was translated to follow it. Its size is unchanged, so the image is still valid."
            : "Resolved against the window's verified current position.";

        return new ResolvedPoint(x, y, entry.Hwnd, moved, note);
    }

    /// <summary>Forgets everything captured for a window; used when a task's work on it is finished.</summary>
    public void Invalidate(long hwnd)
    {
        foreach (var pair in entries.Where(e => e.Value.Hwnd == hwnd).ToArray())
            entries.TryRemove(pair.Key, out _);
    }

    public void Clear() => entries.Clear();

    /// <summary>
    /// Re-reads the rectangle the capture method's image covers, in the same terms that method
    /// used — the DWM frame for a compositor capture, the window rect for PrintWindow, and the
    /// window rect clipped to the visible desktop for a screen-region capture.
    ///
    /// The clipping matters: a window hanging off the edge of a monitor is captured as only its
    /// visible part, so comparing that against the full window rect would report a size change
    /// that never happened and refuse every point in a perfectly good image.
    /// </summary>
    private static PixelRect CurrentBounds(nint handle, CaptureMethod method)
    {
        if (method == CaptureMethod.GraphicsCapture &&
            Native.DwmGetWindowAttribute(handle, Native.ExtendedFrameBounds, out Native.Rect frame, 16) == 0)
        {
            var frameWidth = frame.Right - frame.Left;
            var frameHeight = frame.Bottom - frame.Top;
            if (frameWidth > 0 && frameHeight > 0) return new PixelRect(frame.Left, frame.Top, frameWidth, frameHeight);
        }

        Native.Check(Native.GetWindowRect(handle, out var rect), "Window geometry is unavailable.");
        var bounds = new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return method == CaptureMethod.ScreenRegion ? ClipToVirtualDesktop(bounds) : bounds;
    }

    /// <summary>Intersects a window rect with the virtual desktop, the way a screen capture does.</summary>
    private static PixelRect ClipToVirtualDesktop(PixelRect bounds)
    {
        var left = Native.GetSystemMetrics(76);   // SM_XVIRTUALSCREEN
        var top = Native.GetSystemMetrics(77);    // SM_YVIRTUALSCREEN
        var width = Native.GetSystemMetrics(78);  // SM_CXVIRTUALSCREEN
        var height = Native.GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
        if (width <= 0 || height <= 0) return bounds;

        var x = Math.Max(left, bounds.X);
        var y = Math.Max(top, bounds.Y);
        var right = Math.Min(left + width, bounds.X + bounds.Width);
        var bottom = Math.Min(top + height, bounds.Y + bounds.Height);
        return right - x < 1 || bottom - y < 1 ? bounds : new PixelRect(x, y, right - x, bottom - y);
    }

    /// <summary>Bounded on purpose: this is a short-lived coordinate frame, not a screenshot history.</summary>
    private void Prune()
    {
        var current = now();
        foreach (var pair in entries.Where(e => e.Value.ExpiresAt <= current).ToArray())
            entries.TryRemove(pair.Key, out _);

        if (entries.Count < Capacity) return;
        foreach (var pair in entries.OrderBy(e => e.Value.ExpiresAt).Take(entries.Count - Capacity + 1).ToArray())
            entries.TryRemove(pair.Key, out _);
    }
}

/// <summary>
/// A coordinate frame that can no longer be trusted. Distinct from every other input failure
/// because the recovery is always the same and always cheap: observe again.
/// </summary>
public sealed class StaleCaptureException(string message) : InvalidOperationException(message);
