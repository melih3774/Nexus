using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcCore.Windows.Interop;

/// <summary>
/// The cursor, for drawing it into a captured frame.
/// <para>
/// Desktop Duplication hands back the screen without the pointer, so the stream has to composite it
/// in. That is all this is for. Input <em>injection</em> used to live here too and now lives in
/// <c>PcCore.NativeInput</c> — one copy, shared by PC Core and the elevated Nexus Input Broker, so
/// the two can never drift into behaving differently.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class InputInterop
{
    private const string User32 = "user32.dll";
    private const string Gdi32 = "gdi32.dll";

    public const int CursorShowing = 0x00000001;
    public const int DiNormal = 0x0003;

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
