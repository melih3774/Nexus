using System.Runtime.InteropServices;
using System.Text;

namespace PcCore.SecureCapture;

/// <summary>One captured desktop image, in BGRA, plus where the pointer was.</summary>
internal sealed record CapturedDesktop(
    byte[] Pixels,
    int Width,
    int Height,
    int Stride,
    int CursorX,
    int CursorY,
    bool CursorVisible,
    string Desktop);

/// <summary>
/// Captures whichever desktop is currently receiving input.
/// <para>
/// <b>Why GDI and not Desktop Duplication.</b> Desktop Duplication is the right answer for the
/// ordinary desktop and PC Core keeps using it there: it hands over frames the compositor already
/// produced, only when they change, which is what makes a fifteen-frame-per-second stream
/// affordable. None of that matters here. What this captures is a consent prompt — a still image,
/// on screen for a few seconds, that the owner needs to read and click. A whole-screen
/// <c>BitBlt</c> at four frames a second is unremarkable next to that, and it is a great deal less
/// code to run as SYSTEM.
/// </para>
/// <para>
/// <b>Why the desktop is re-opened every frame.</b> The whole point of this component is that the
/// input desktop is changing underneath it. <c>SetThreadDesktop</c> binds the thread, and a thread
/// left bound to Winlogon after UAC closes would keep capturing an empty desktop. Opening the input
/// desktop each time is a handle open and a close, and it makes the frame self-evidently a picture
/// of whatever is live right now.
/// </para>
/// </summary>
internal sealed class DesktopCapture : IDisposable
{
    private nint _desktop;
    private byte[] _pixels = [];
    private int _width;
    private int _height;

    /// <summary>
    /// Captures the input desktop.
    /// </summary>
    /// <returns>The frame, or null with <paramref name="diagnostic"/> saying why not.</returns>
    public CapturedDesktop? TryCapture(out string diagnostic)
    {
        diagnostic = string.Empty;

        var desktop = Interop.OpenInputDesktop(0, false, Interop.DesktopAll);

        if (desktop == nint.Zero)
        {
            // Falls back to read-only access: capturing needs to read the desktop, not write to it,
            // and asking for less is both more likely to succeed and the right thing to ask for.
            desktop = Interop.OpenInputDesktop(0, false, Interop.DesktopReadObjects | Interop.DesktopEnumerate);
        }

        if (desktop == nint.Zero)
        {
            diagnostic = $"the input desktop could not be opened (error {Marshal.GetLastWin32Error()})";
            return null;
        }

        try
        {
            if (!Interop.SetThreadDesktop(desktop))
            {
                diagnostic = $"the capture thread could not switch to the input desktop (error {Marshal.GetLastWin32Error()})";
                return null;
            }

            // The previous desktop handle is only safe to close once the thread is no longer on it.
            if (_desktop != nint.Zero && _desktop != desktop)
            {
                Interop.CloseDesktop(_desktop);
            }

            _desktop = desktop;
            desktop = nint.Zero;

            return Capture(out diagnostic);
        }
        finally
        {
            if (desktop != nint.Zero)
            {
                Interop.CloseDesktop(desktop);
            }
        }
    }

    private CapturedDesktop? Capture(out string diagnostic)
    {
        diagnostic = string.Empty;

        var left = Interop.GetSystemMetrics(Interop.SmXVirtualScreen);
        var top = Interop.GetSystemMetrics(Interop.SmYVirtualScreen);
        var width = Interop.GetSystemMetrics(Interop.SmCxVirtualScreen);
        var height = Interop.GetSystemMetrics(Interop.SmCyVirtualScreen);

        if (width <= 0 || height <= 0)
        {
            diagnostic = "the desktop reported no size";
            return null;
        }

        var screen = Interop.GetDC(nint.Zero);

        if (screen == nint.Zero)
        {
            diagnostic = "the desktop device context could not be opened";
            return null;
        }

        var memory = nint.Zero;
        var bitmap = nint.Zero;
        var previous = nint.Zero;

        try
        {
            memory = Interop.CreateCompatibleDC(screen);

            if (memory == nint.Zero)
            {
                diagnostic = "a memory device context could not be created";
                return null;
            }

            var info = new Interop.BitmapInfo
            {
                Header = new Interop.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<Interop.BitmapInfoHeader>(),
                    Width = width,

                    // Negative: a top-down DIB, so row 0 is the top of the screen and the bytes are
                    // already in the order every consumer downstream expects.
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = Interop.BiRgb,
                },
            };

            bitmap = Interop.CreateDIBSection(memory, ref info, Interop.DibRgbColors, out var bits, nint.Zero, 0);

            if (bitmap == nint.Zero || bits == nint.Zero)
            {
                diagnostic = $"a capture surface could not be created (error {Marshal.GetLastWin32Error()})";
                return null;
            }

            previous = Interop.SelectObject(memory, bitmap);

            // CAPTUREBLT includes layered windows. The UAC prompt's dimming is one.
            if (!Interop.BitBlt(memory, 0, 0, width, height, screen, left, top, Interop.SrcCopy | Interop.CaptureBlt))
            {
                diagnostic = $"the desktop could not be copied (error {Marshal.GetLastWin32Error()})";
                return null;
            }

            DrawCursor(memory, left, top, out var cursorX, out var cursorY, out var cursorVisible);

            var stride = width * 4;
            var bytes = stride * (long)height;

            if (bytes <= 0 || bytes > SecureCaptureProtocol.MaxFrameBytes)
            {
                diagnostic = "the desktop is too large to capture";
                return null;
            }

            if (_pixels.Length != bytes || _width != width || _height != height)
            {
                _pixels = new byte[bytes];
                _width = width;
                _height = height;
            }

            Marshal.Copy(bits, _pixels, 0, (int)bytes);

            return new CapturedDesktop(
                _pixels, width, height, stride, cursorX, cursorY, cursorVisible, DesktopName());
        }
        finally
        {
            if (previous != nint.Zero)
            {
                Interop.SelectObject(memory, previous);
            }

            if (bitmap != nint.Zero)
            {
                Interop.DeleteObject(bitmap);
            }

            if (memory != nint.Zero)
            {
                Interop.DeleteDC(memory);
            }

            Interop.ReleaseDC(nint.Zero, screen);
        }
    }

    private static void DrawCursor(nint dc, int left, int top, out int x, out int y, out bool visible)
    {
        x = 0;
        y = 0;
        visible = false;

        var info = new Interop.CursorInfo { Size = Marshal.SizeOf<Interop.CursorInfo>() };

        if (!Interop.GetCursorInfo(ref info)
            || (info.Flags & Interop.CursorShowing) == 0
            || info.Cursor == nint.Zero)
        {
            return;
        }

        x = info.ScreenX - left;
        y = info.ScreenY - top;
        visible = true;

        if (!Interop.GetIconInfo(info.Cursor, out var icon))
        {
            return;
        }

        try
        {
            // Drawn by Windows into the same DC the desktop was just copied into. On this path that
            // is the correct choice rather than the compositing PC Core does for Desktop
            // Duplication: DrawIconEx is being handed a live cursor handle on the desktop that owns
            // it, so monochrome masks and alpha are resolved by GDI itself.
            Interop.DrawIconEx(dc, x - icon.HotspotX, y - icon.HotspotY, info.Cursor, 0, 0, 0, nint.Zero, Interop.DiNormal);
        }
        finally
        {
            if (icon.MaskBitmap != nint.Zero)
            {
                Interop.DeleteObject(icon.MaskBitmap);
            }

            if (icon.ColorBitmap != nint.Zero)
            {
                Interop.DeleteObject(icon.ColorBitmap);
            }
        }
    }

    private static string DesktopName()
    {
        var desktop = Interop.OpenInputDesktop(0, false, Interop.DesktopReadObjects);

        if (desktop == nint.Zero)
        {
            return "unknown";
        }

        try
        {
            var buffer = new byte[256];

            if (!Interop.GetUserObjectInformation(desktop, Interop.UoiName, buffer, (uint)buffer.Length, out var needed))
            {
                return "unknown";
            }

            return Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(needed, (uint)buffer.Length)).TrimEnd('\0');
        }
        finally
        {
            Interop.CloseDesktop(desktop);
        }
    }

    public void Dispose()
    {
        if (_desktop != nint.Zero)
        {
            Interop.CloseDesktop(_desktop);
            _desktop = nint.Zero;
        }

        _pixels = [];
    }
}
