using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcCore.NativeInput;

/// <summary>
/// The Windows input surface Nexus uses, and nothing else.
/// <para>
/// Two families live here. <c>SendInput</c> is the ordinary way an application injects mouse and
/// keyboard events. <c>InjectTouchInput</c> is the ordinary way an application injects <em>touch</em>
/// — a supported, documented Win32 API since Windows 8 that produces real <c>WM_POINTER</c> touch
/// messages and the legacy mouse messages Windows promotes them into. Neither is a hook, a driver,
/// or a trick; both are the paths Microsoft publishes for this exact purpose.
/// </para>
/// <para>
/// <b>What is deliberately absent.</b> Nothing here switches desktops, opens another process's
/// window station, or reaches for the Secure Desktop. <see cref="InputDesktop"/> exists only to
/// notice when the Secure Desktop is up so that Nexus can refuse to inject at all.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class NativeInputInterop
{
    private const string User32 = "user32.dll";

    // --- SendInput ---------------------------------------------------------------------------------

    [LibraryImport(User32, SetLastError = true)]
    internal static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport(User32)]
    internal static partial int GetSystemMetrics(int index);

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

    internal const uint KeyEventExtendedKey = 0x0001;
    internal const uint KeyEventKeyUp = 0x0002;

    /// <summary>The scan code is a Unicode code unit rather than a keyboard scan code.</summary>
    internal const uint KeyEventUnicode = 0x0004;

    internal const int WheelDelta = 120;

    /// <summary>The absolute coordinate space SendInput uses, regardless of pixel resolution.</summary>
    internal const int AbsoluteRange = 65535;

    internal const int SmXVirtualScreen = 76;
    internal const int SmYVirtualScreen = 77;
    internal const int SmCxVirtualScreen = 78;
    internal const int SmCyVirtualScreen = 79;

    /// <summary>Non-zero when this machine has a real pen or touch digitizer attached.</summary>
    internal const int SmDigitizer = 94;

    // --- Touch injection ---------------------------------------------------------------------------

    /// <summary>
    /// Must be called once per process before any <see cref="InjectTouchInput"/> call.
    /// </summary>
    /// <param name="maxCount">Maximum simultaneous contacts, 1 to 256.</param>
    /// <param name="mode">Visual feedback mode; Nexus uses <see cref="TouchFeedbackIndirect"/>.</param>
    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitializeTouchInjection(uint maxCount, uint mode);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InjectTouchInput(uint count, [In] PointerTouchInfo[] contacts);

    internal const uint PointerTypeTouch = 0x00000002;

    internal const uint PointerFlagNone = 0x00000000;
    internal const uint PointerFlagInRange = 0x00000002;
    internal const uint PointerFlagInContact = 0x00000004;
    internal const uint PointerFlagDown = 0x00010000;
    internal const uint PointerFlagUpdate = 0x00020000;
    internal const uint PointerFlagUp = 0x00040000;
    internal const uint PointerFlagCanceled = 0x00008000;

    internal const uint TouchMaskContactArea = 0x00000001;
    internal const uint TouchMaskPressure = 0x00000002;

    /// <summary>
    /// Draws the same touch visualisation Windows uses for an external digitizer.
    /// <para>
    /// Chosen over <c>TOUCH_FEEDBACK_NONE</c> on purpose: the owner is watching this desktop through
    /// a video stream, and without the ripple there is nothing on screen that says where the touch
    /// landed. Touch injection does not move the mouse cursor, so the feedback is the only cue.
    /// </para>
    /// </summary>
    internal const uint TouchFeedbackIndirect = 0x00000002;

    internal const uint TouchFeedbackNone = 0x00000003;

    // --- Desktops, for the Secure Desktop boundary --------------------------------------------------

    [LibraryImport(User32, SetLastError = true)]
    internal static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseDesktop(nint desktop);

    [LibraryImport(User32, EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetUserObjectInformation(
        nint handle,
        int index,
        [Out] byte[] buffer,
        uint length,
        out uint lengthNeeded);

    internal const uint DesktopReadObjects = 0x0001;
    internal const int UoiName = 2;

    // --- Per-monitor DPI awareness -------------------------------------------------------------------

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDpiAwarenessContext(nint context);

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.</summary>
    internal static readonly nint PerMonitorAwareV2 = -4;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
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

[StructLayout(LayoutKind.Sequential)]
internal struct PointerInfo
{
    public uint PointerType;
    public uint PointerId;
    public uint FrameId;
    public uint PointerFlags;
    public nint SourceDevice;
    public nint HwndTarget;
    public NativePoint PtPixelLocation;
    public NativePoint PtHimetricLocation;
    public NativePoint PtPixelLocationRaw;
    public NativePoint PtHimetricLocationRaw;
    public uint DwTime;
    public uint HistoryCount;
    public int InputData;
    public uint DwKeyStates;
    public ulong PerformanceCount;
    public int ButtonChangeType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PointerTouchInfo
{
    public PointerInfo PointerInfo;
    public uint TouchFlags;
    public uint TouchMask;
    public NativeRect Contact;
    public NativeRect ContactRaw;
    public uint Orientation;
    public uint Pressure;
}
