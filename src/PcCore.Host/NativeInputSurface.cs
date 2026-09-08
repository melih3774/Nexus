using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PcCore.Host;

/// <summary>
/// A throwaway on-screen window with a text box in it, used only by the native-input self-test.
/// <para>
/// <b>Why a real window.</b> Every cheaper way of checking that input worked checks the wrong thing.
/// A return value from <c>SendInput</c> says a call succeeded, not that anything received it; the
/// cursor position says the pointer moved, not that a click landed anywhere; and neither says
/// whether a Turkish character survived the trip. A real window with a real edit control can be
/// typed into and then read back, which is the only way to prove the keyboard path end to end. The
/// same window logs which input messages it received, which is how the touch path is proved to
/// arrive as touch <em>and</em> as the mouse messages Windows promotes it into for legacy apps.
/// </para>
/// <para>
/// It exists for a few seconds, in front of the owner, and is destroyed afterwards.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeInputSurface : IDisposable
{
    private const string ClassName = "NexusNativeInputSelfTest";

    private const uint WmDestroy = 0x0002;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmRightButtonDown = 0x0204;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmPointerUpdate = 0x0245;
    private const uint WmPointerDown = 0x0246;
    private const uint WmPointerUp = 0x0247;
    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;
    private const uint WmSetText = 0x000C;

    private readonly Lock _gate = new();
    private readonly List<string> _messages = [];
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WindowProc _proc;

    private Thread? _thread;
    private nint _window;
    private nint _edit;

    public NativeInputSurface() => _proc = OnMessage;

    public nint Window => _window;

    /// <summary>Every input message the window saw, in order, with its extra-info tag.</summary>
    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _messages.Clear();
        }
    }

    /// <summary>Shows the window and waits for it to exist. False if Windows refused to create it.</summary>
    public bool Open(string title)
    {
        _thread = new Thread(() => Run(title))
        {
            IsBackground = true,
            Name = "Nexus native input self-test surface",
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        return _ready.Wait(TimeSpan.FromSeconds(5)) && _window != nint.Zero;
    }

    /// <summary>The middle of the window's client area, in screen pixels — somewhere safe to click.</summary>
    public (int X, int Y) ClientCentre()
    {
        GetClientRect(_window, out var rectangle);

        var point = new Point
        {
            X = (rectangle.Right - rectangle.Left) / 2,
            Y = (rectangle.Bottom - rectangle.Top) / 2,
        };

        ClientToScreen(_window, ref point);
        return (point.X, point.Y);
    }

    /// <summary>
    /// A point in the empty upper half of the window, in screen pixels.
    /// <para>
    /// Clear of the text box on purpose. A child control has its own window procedure, so anything
    /// landing on it is reported to that control and never reaches the log this test reads — which
    /// looks exactly like the input not arriving at all.
    /// </para>
    /// </summary>
    public (int X, int Y) TargetSpot()
    {
        GetClientRect(_window, out var rectangle);

        var point = new Point
        {
            X = (rectangle.Right - rectangle.Left) / 2,
            Y = (rectangle.Bottom - rectangle.Top) / 4,
        };

        ClientToScreen(_window, ref point);
        return (point.X, point.Y);
    }

    /// <summary>The middle of the text box, in screen pixels.</summary>
    public (int X, int Y) EditCentre()
    {
        GetWindowRect(_edit, out var rectangle);
        return ((rectangle.Left + rectangle.Right) / 2, (rectangle.Top + rectangle.Bottom) / 2);
    }

    public void FocusEdit()
    {
        SetForegroundWindow(_window);
        SetFocus(_edit);
    }

    public void ClearEdit() => SendMessageW(_edit, WmSetText, nint.Zero, string.Empty);

    /// <summary>Whatever is in the text box now. The proof that typing arrived intact.</summary>
    public string ReadEdit()
    {
        var length = (int)SendMessageW(_edit, WmGetTextLength, nint.Zero, nint.Zero);

        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        SendMessageTextW(_edit, WmGetText, buffer.Capacity, buffer);

        return buffer.ToString();
    }

    private void Run(string title)
    {
        try
        {
            var instance = GetModuleHandleW(null);

            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                WindowProc = Marshal.GetFunctionPointerForDelegate(_proc),
                Instance = instance,
                ClassName = ClassName,

                // COLOR_WINDOW + 1, so the window is opaque and visibly present for the owner.
                Background = 6,
            };

            RegisterClassExW(ref windowClass);

            _window = CreateWindowExW(
                WsExTopMost,
                ClassName,
                title,
                WsOverlappedWindow,
                120,
                120,
                640,
                320,
                nint.Zero,
                nint.Zero,
                instance,
                nint.Zero);

            if (_window == nint.Zero)
            {
                return;
            }

            _edit = CreateWindowExW(
                WsExClientEdge,
                "EDIT",
                string.Empty,
                WsChild | WsVisible | EsAutoHScroll,
                24,
                140,
                560,
                40,
                _window,
                nint.Zero,
                instance,
                nint.Zero);

            ShowWindow(_window, ShowNormal);
            UpdateWindow(_window);
            SetForegroundWindow(_window);
        }
        finally
        {
            _ready.Set();
        }

        while (GetMessageW(out var message, nint.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    private nint OnMessage(nint window, uint message, nint wParam, nint lParam)
    {
        var name = message switch
        {
            WmMouseMove => "WM_MOUSEMOVE",
            WmLeftButtonDown => "WM_LBUTTONDOWN",
            WmLeftButtonUp => "WM_LBUTTONUP",
            WmRightButtonDown => "WM_RBUTTONDOWN",
            WmMouseWheel => "WM_MOUSEWHEEL",
            WmPointerDown => "WM_POINTERDOWN",
            WmPointerUpdate => "WM_POINTERUPDATE",
            WmPointerUp => "WM_POINTERUP",
            _ => null,
        };

        if (name is not null)
        {
            var extra = GetMessageExtraInfo().ToInt64();

            var tag = PcCore.NativeInput.NexusInputTag.IsNexus(extra)
                ? " nexus-tag"
                : PcCore.NativeInput.NexusInputTag.IsPenOrTouch(extra)
                    ? " pen-or-touch"
                    : string.Empty;

            lock (_gate)
            {
                _messages.Add($"{name}{tag}");
            }
        }

        if (message == WmDestroy)
        {
            PostQuitMessage(0);
            return nint.Zero;
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_window != nint.Zero)
        {
            PostMessageW(_window, WmDestroy, nint.Zero, nint.Zero);
            _window = nint.Zero;
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint EsAutoHScroll = 0x0080;
    private const uint WsExTopMost = 0x00000008;
    private const uint WsExClientEdge = 0x00000200;
    private const int ShowNormal = 5;

    private delegate nint WindowProc(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Value;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle,
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
        nint parameter);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Message message, nint window, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Message message);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, string? text);

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern nint SendMessageTextW(nint window, uint message, int wParam, StringBuilder text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out Rect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref Point point);

    [DllImport("user32.dll")]
    private static extern nint GetMessageExtraInfo();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
}
