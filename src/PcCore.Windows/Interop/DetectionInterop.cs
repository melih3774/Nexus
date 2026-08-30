using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcCore.Windows.Interop;

/// <summary>
/// The Windows surface local-activity detection needs.
/// <para>
/// <b>On the keyboard hook.</b> A low-level keyboard hook is the same mechanism a keylogger uses,
/// and the difference is entirely in what the callback reads. This code never reads the virtual-key
/// code or the scan code — not into a variable, not into a struct, not to discard. The two fields
/// that <em>are</em> read live at fixed offsets and are read individually, so there is no
/// deserialised structure holding a keystroke even momentarily. See
/// <see cref="ReadKeyboardFlags"/> and <see cref="ReadKeyboardExtraInfo"/>.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class DetectionInterop
{
    private const string User32 = "user32.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string Wtsapi32 = "wtsapi32.dll";

    internal const int WhKeyboardLowLevel = 13;
    internal const int WhMouseLowLevel = 14;

    /// <summary>Injected by any process, at any integrity level.</summary>
    internal const int LlkhfInjected = 0x10;

    internal const int LlmhfInjected = 0x01;

    internal const int WmMouseMove = 0x0200;
    internal const int WmLeftButtonDown = 0x0201;
    internal const int WmRightButtonDown = 0x0204;
    internal const int WmMiddleButtonDown = 0x0207;
    internal const int WmMouseWheel = 0x020A;

    internal const int WmWtsSessionChange = 0x02B1;
    internal const int WtsSessionLogon = 0x5;
    internal const int WtsSessionUnlock = 0x8;

    internal const int NotifyForThisSession = 0;

    /// <summary>A window that exists only to receive messages; it is never shown and has no pixels.</summary>
    internal static readonly nint MessageOnlyWindowParent = -3;

    internal const uint EventSystemForeground = 0x0003;
    internal const uint WinEventOutOfContext = 0x0000;
    internal const uint WinEventSkipOwnProcess = 0x0002;

    // --- Hooks ------------------------------------------------------------------------------------

    [LibraryImport(User32, EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    internal static partial nint SetWindowsHookEx(int idHook, nint callback, nint module, uint threadId);

    [LibraryImport(User32, EntryPoint = "UnhookWindowsHookEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport(User32, EntryPoint = "CallNextHookEx")]
    internal static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [LibraryImport(Kernel32, EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(string? name);

    // --- Accessibility event hook, for foreground changes -------------------------------------------

    [LibraryImport(User32, EntryPoint = "SetWinEventHook")]
    internal static partial nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint module,
        nint callback,
        uint processId,
        uint threadId,
        uint flags);

    [LibraryImport(User32, EntryPoint = "UnhookWinEvent")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(nint hook);

    [LibraryImport(User32, EntryPoint = "GetWindowThreadProcessId")]
    internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    // --- Session notifications ----------------------------------------------------------------------

    [LibraryImport(Wtsapi32, EntryPoint = "WTSRegisterSessionNotification", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSRegisterSessionNotification(nint window, uint flags);

    [LibraryImport(Wtsapi32, EntryPoint = "WTSUnRegisterSessionNotification")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSUnRegisterSessionNotification(nint window);

    // --- Reading hook payloads ------------------------------------------------------------------------

    /// <summary>
    /// The <c>flags</c> field of <c>KBDLLHOOKSTRUCT</c>, at offset 8.
    /// <para>
    /// Read on its own, by offset. The virtual-key code at offset 0 and the scan code at offset 4 are
    /// never touched, which is the structural reason this cannot become a keylogger: the identity of
    /// the key never enters managed memory at all.
    /// </para>
    /// </summary>
    internal static int ReadKeyboardFlags(nint lParam) => Marshal.ReadInt32(lParam, 8);

    /// <summary>The <c>dwExtraInfo</c> field of <c>KBDLLHOOKSTRUCT</c>, at offset 16 on x64.</summary>
    internal static nint ReadKeyboardExtraInfo(nint lParam) => Marshal.ReadIntPtr(lParam, 16);

    /// <summary>
    /// The <c>flags</c> field of <c>MSLLHOOKSTRUCT</c>, at offset 12.
    /// <para>
    /// The pointer position at offset 0 is deliberately not read either. Where the owner clicked is
    /// no more the security subsystem's business than what they typed.
    /// </para>
    /// </summary>
    internal static int ReadMouseFlags(nint lParam) => Marshal.ReadInt32(lParam, 12);

    /// <summary>The <c>dwExtraInfo</c> field of <c>MSLLHOOKSTRUCT</c>, at offset 24 on x64.</summary>
    internal static nint ReadMouseExtraInfo(nint lParam) => Marshal.ReadIntPtr(lParam, 24);
}

internal delegate nint HookProcedure(int code, nint wParam, nint lParam);

internal delegate void WinEventProcedure(
    nint hook,
    uint eventId,
    nint window,
    int objectId,
    int childId,
    uint threadId,
    uint time);
