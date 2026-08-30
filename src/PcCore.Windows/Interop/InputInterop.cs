using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcCore.Windows.Interop;

/// <summary>
/// Cursor and input injection, through the ordinary user-level Windows APIs.
/// <para>
/// <c>SendInput</c> is what a normal application uses and what Windows expects an application to
/// use. It is subject to every rule that protects the desktop — most importantly UIPI, which stops a
/// standard-user process from sending input to an elevated window. PC Core keeps that restriction
/// rather than working around it: no driver, no hook, no elevated helper, no integrity-level tricks.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class InputInterop
{
    private const string User32 = "user32.dll";
    private const string Gdi32 = "gdi32.dll";

    public const int CursorShowing = 0x00000001;
    public const int DiNormal = 0x0003;

    // --- Cursor ----------------------------------------------------------------------------------

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorInfo(ref CursorInfo info);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetIconInfo(nint icon, out IconInfo info);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DrawIconEx(
        nint hdc,
        int x,
        int y,
        nint icon,
        int width,
        int height,
        uint step,
        nint brush,
        int flags);

    [LibraryImport(Gdi32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint handle);

    // --- Input -----------------------------------------------------------------------------------

    /// <summary>
    /// Returns the number of events actually inserted. A short count means Windows blocked them,
    /// which in practice means UIPI: the foreground window is more privileged than this process.
    /// </summary>
    [LibraryImport(User32, SetLastError = true)]
    internal static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    internal const uint InputMouse = 0;
    internal const uint InputKeyboard = 1;

    internal const uint MouseEventMove = 0x0001;
    internal const uint MouseEventLeftDown = 0x0002;
    internal const uint MouseEventLeftUp = 0x0004;
    internal const uint MouseEventRightDown = 0x0008;
    internal const uint MouseEventRightUp = 0x0010;
    internal const uint MouseEventMiddleDown = 0x0020;
    internal const uint MouseEventMiddleUp = 0x0040;
    internal const uint MouseEventWheel = 0x0800;
    internal const uint MouseEventHWheel = 0x1000;
    internal const uint MouseEventAbsolute = 0x8000;

    /// <summary>Required with ABSOLUTE for coordinates spanning more than the primary monitor.</summary>
    internal const uint MouseEventVirtualDesk = 0x4000;

    internal const uint KeyEventKeyUp = 0x0002;

    /// <summary>The scan code is a Unicode code unit rather than a keyboard scan code.</summary>
    internal const uint KeyEventUnicode = 0x0004;

    internal const uint KeyEventExtendedKey = 0x0001;

    internal const int WheelDelta = 120;

    /// <summary>The absolute coordinate space SendInput uses, regardless of pixel resolution.</summary>
    internal const int AbsoluteRange = 65535;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CursorInfo
{
    public int Size;
    public int Flags;
    public nint Cursor;
    public int ScreenX;
    public int ScreenY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IconInfo
{
    public int IsIcon;
    public int HotspotX;
    public int HotspotY;
    public nint MaskBitmap;
    public nint ColorBitmap;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MouseInput
{
    public int Dx;
    public int Dy;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public nint ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KeyboardInput
{
    public ushort VirtualKey;
    public ushort ScanCode;
    public uint Flags;
    public uint Time;
    public nint ExtraInfo;
}

/// <summary>
/// The INPUT union. The keyboard variant is smaller than the mouse one, so the explicit layout
/// overlays both at the same offset and the struct keeps the size Windows expects.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct Input
{
    [FieldOffset(0)]
    public uint Type;

    [FieldOffset(8)]
    public MouseInput Mouse;

    [FieldOffset(8)]
    public KeyboardInput Keyboard;
}
