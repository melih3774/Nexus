namespace PcCore.NativeInput.Hid;

/// <summary>One display's rectangle in virtual-desktop pixels. The origin can be negative.</summary>
public readonly record struct DisplayRectangle(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Turns a position the phone sent into the logical coordinates one digitizer report carries.
/// <para>
/// <b>Why this is not two multiplications inline at the call site.</b> Every coordinate bug in a
/// remote-control tool lives in this conversion, and they are all the same bug wearing different
/// clothes: a display whose origin is negative because it sits left of the primary one, a DPI scale
/// that makes logical pixels differ from physical ones, a resolution change between the frame the
/// phone drew and the tap it sent back, or an axis that was normalised against the wrong rectangle.
/// Putting the arithmetic in one tested place is the only way any of that is checkable.
/// </para>
/// <para>
/// <b>The phone sends a fraction, not a pixel.</b> That is deliberate and it is what makes a
/// resolution change harmless: 0.5 is the middle of the display whatever the display is doing, and a
/// stale frame costs sub-pixel accuracy rather than sending the pointer to the wrong place entirely.
/// The conversion to pixels happens here, against the display rectangle as it is <em>now</em>.
/// </para>
/// <para>
/// <b>A HID touch screen is bound to one display, and that is a real limitation.</b> Unlike absolute
/// mouse positioning, which spans the whole virtual desktop, Windows associates a digitizer with a
/// single monitor — by default the primary one, and otherwise through the Tablet PC monitor-mapping
/// the owner sets. So the digitizer's logical range means "across the associated display", not
/// "across the desktop". Nexus maps into the target display's own rectangle and reports which
/// display it assumed; when the streamed display is not the one Windows has associated, touch lands
/// on the wrong screen and the honest answer is to say so rather than to silently offset it.
/// </para>
/// </summary>
public static class DigitizerSurface
{
    /// <summary>The top of the logical range, matching the descriptor's LOGICAL_MAXIMUM.</summary>
    public const int LogicalMaximum = HidReportDescriptors.DigitizerLogicalMaximum;

    /// <summary>
    /// Maps a normalised position within one display onto the digitizer's logical range.
    /// <para>
    /// <paramref name="fractionX"/> and <paramref name="fractionY"/> are clamped to 0..1 rather than
    /// rejected: a finger tracked slightly past the edge of the streamed image is an ordinary thing
    /// for a touch UI to produce, and the sensible response is the edge of the screen.
    /// </para>
    /// </summary>
    public static (ushort X, ushort Y) FromDisplayFraction(double fractionX, double fractionY)
    {
        return (Scale(fractionX), Scale(fractionY));

        static ushort Scale(double fraction)
        {
            if (double.IsNaN(fraction))
            {
                return 0;
            }

            var clamped = Math.Clamp(fraction, 0.0, 1.0);

            // Round rather than truncate: truncation makes the far edge unreachable, because only an
            // exact 1.0 would ever reach the maximum.
            return (ushort)Math.Round(clamped * LogicalMaximum, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>
    /// Maps a point in virtual-desktop pixels onto the digitizer's logical range, relative to the
    /// display that contains it.
    /// <para>
    /// Used when a caller already holds pixels — a test, or a path that resolved a fraction against
    /// a display earlier. Returns false for an empty rectangle rather than dividing by zero, which
    /// is what a display that is being reconfigured briefly looks like.
    /// </para>
    /// </summary>
    public static bool TryFromDesktopPoint(
        DisplayRectangle display,
        int desktopX,
        int desktopY,
        out ushort x,
        out ushort y)
    {
        if (display.IsEmpty)
        {
            x = 0;
            y = 0;
            return false;
        }

        // The subtraction is what handles a negative origin: a display at left = -1920 turns
        // desktop x = -1920 into a local 0, and nothing downstream has to know the desktop is
        // signed.
        var localX = (double)(desktopX - display.Left) / display.Width;
        var localY = (double)(desktopY - display.Top) / display.Height;

        (x, y) = FromDisplayFraction(localX, localY);
        return true;
    }

    /// <summary>
    /// The scan-time value for a report, in the 100-microsecond units the descriptor declares.
    /// <para>
    /// Wraps at 16 bits, which is expected and which Windows handles: the field is a rolling clock
    /// used for velocity between consecutive reports, not an absolute timestamp. Taking it from a
    /// caller-supplied elapsed time rather than reading a clock in here keeps the conversion
    /// testable and keeps this file free of ambient state.
    /// </para>
    /// </summary>
    public static ushort ScanTime(TimeSpan elapsed)
    {
        var units = (long)(elapsed.Ticks / (TimeSpan.TicksPerMillisecond / 10));
        return (ushort)(units & 0xFFFF);
    }
}
