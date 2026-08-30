using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcCore.Windows.Interop;

/// <summary>
/// The window and notification-area surface the remote-control indicator needs.
/// <para>
/// Every style used here is chosen so the indicator cannot get in the way: it is click-through, it
/// never takes focus, it stays out of the taskbar and Alt+Tab, and it draws with per-pixel alpha so
/// it is a small translucent badge rather than a grey rectangle sitting on top of things.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class IndicatorInterop
{
    private const string User32 = "user32.dll";
    private const string Gdi32 = "gdi32.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string Shell32 = "shell32.dll";

    // --- Window styles ---------------------------------------------------------------------------

    /// <summary>Per-pixel alpha, so the badge can have rounded corners and a soft edge.</summary>
    internal const int WsExLayered = 0x00080000;

    /// <summary>Clicks pass straight through to whatever is underneath.</summary>
    internal const int WsExTransparent = 0x00000020;

    /// <summary>Keeps it out of the taskbar and the Alt+Tab list.</summary>
    internal const int WsExToolWindow = 0x00000080;

    /// <summary>Never becomes the foreground window, so it cannot steal focus from what you are doing.</summary>
    internal const int WsExNoActivate = 0x08000000;

    internal const int WsExTopMost = 0x00000008;

    internal const uint WsPopup = 0x80000000;

    internal const int SwHide = 0;
    internal const int SwShowNoActivate = 4;

    internal const int UlwAlpha = 0x00000002;
    internal const byte AcSrcOver = 0x00;
    internal const byte AcSrcAlpha = 0x01;

    internal const uint SpiGetWorkArea = 0x0030;

    internal const uint WmClose = 0x0010;
    internal const uint WmDestroy = 0x0002;

    /// <summary>Our own message: "the indicator state changed, redraw".</summary>
    internal const uint WmAppUpdate = 0x8000 + 1;

    /// <summary>Our own message: "shut down the loop".</summary>
    internal const uint WmAppQuit = 0x8000 + 2;

    // --- Notification area -------------------------------------------------------------------------

    internal const uint NimAdd = 0x00000000;
    internal const uint NimModify = 0x00000001;
    internal const uint NimDelete = 0x00000002;

    internal const uint NifMessage = 0x00000001;
    internal const uint NifIcon = 0x00000002;
    internal const uint NifTip = 0x00000004;

    [LibraryImport(Kernel32, EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(string? name);

    // DllImport rather than LibraryImport for these two: their structures carry inline string
    // buffers, which the source-generated marshaller deliberately does not handle. The classic
    // marshaller does, and this is exactly the case it still exists for.
    [DllImport(User32, EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [LibraryImport(User32, EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [LibraryImport(User32, EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport(User32, EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint window);

    [LibraryImport(User32, EntryPoint = "GetMessageW")]
    internal static partial int GetMessage(out NativeMessage message, nint window, uint filterMin, uint filterMax);

    [LibraryImport(User32, EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(ref NativeMessage message);

    [LibraryImport(User32, EntryPoint = "DispatchMessageW")]
    internal static partial nint DispatchMessage(ref NativeMessage message);

    [LibraryImport(User32, EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport(User32, EntryPoint = "PostQuitMessage")]
    internal static partial void PostQuitMessage(int exitCode);

    [LibraryImport(User32, EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint window, int command);

    [LibraryImport(User32, EntryPoint = "UpdateLayeredWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateLayeredWindow(
        nint window,
        nint destinationDc,
        ref Point destinationPoint,
        ref NativeSize size,
        nint sourceDc,
        ref Point sourcePoint,
        uint colorKey,
        ref BlendFunction blend,
        int flags);

    [LibraryImport(User32, EntryPoint = "GetDC")]
    internal static partial nint GetDC(nint window);

    [LibraryImport(User32, EntryPoint = "ReleaseDC")]
    internal static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport(User32, EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfo(uint action, uint param, ref NativeRect rect, uint update);

    [LibraryImport(Gdi32, EntryPoint = "CreateCompatibleDC")]
    internal static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport(Gdi32, EntryPoint = "DeleteDC")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(nint dc);

    [LibraryImport(Gdi32, EntryPoint = "SelectObject")]
    internal static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport(Gdi32, EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint obj);

    [DllImport(Shell32, EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
}

internal delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WindowClassEx
{
    public uint Size;
    public uint Style;
    public nint WindowProc;
    public int ClassExtra;
    public int WindowExtra;
    public nint Instance;
    public nint Icon;
    public nint Cursor;
    public nint Background;
    [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
    [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
    public nint SmallIcon;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMessage
{
    public nint Window;
    public uint Message;
    public nint WParam;
    public nint LParam;
    public uint Time;
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Point
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSize
{
    public int Width;
    public int Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BlendFunction
{
    public byte BlendOp;
    public byte BlendFlags;
    public byte SourceConstantAlpha;
    public byte AlphaFormat;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NotifyIconData
{
    public uint Size;
    public nint Window;
    public uint Id;
    public uint Flags;
    public uint CallbackMessage;
    public nint Icon;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string Tip;

    public uint State;
    public uint StateMask;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Info;

    public uint Version;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string InfoTitle;

    public uint InfoFlags;
}
