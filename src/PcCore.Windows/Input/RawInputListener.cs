using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

using Microsoft.Extensions.Logging;

using PcCore.NativeInput.Hid;

namespace PcCore.Windows.Input;

/// <summary>What one Raw Input event was, reduced to what attribution needs and nothing more.</summary>
public enum RawInputKind
{
    Mouse,
    Keyboard,
}

/// <summary>
/// One observed hardware event, carrying its source device and nothing about its content.
/// <para>
/// <b>There is deliberately nowhere here to put a keystroke.</b> No virtual key, no scan code, no
/// character, no coordinates. This mirrors <c>LocalActivity</c>, which was designed the same way for
/// the same reason: a type with nowhere to put a key cannot accidentally become a keylogger, and
/// this is source attribution, not input capture.
/// </para>
/// </summary>
public readonly record struct RawInputObservation(
    RawInputKind Kind,
    nint DeviceHandle,
    string? DevicePath,
    DateTimeOffset AtUtc);

/// <summary>
/// Watches which physical devices produce input, so security attribution can name a source instead
/// of guessing at one.
/// <para>
/// <b>Why Raw Input rather than more low-level hook.</b> A <c>WH_MOUSE_LL</c> or
/// <c>WH_KEYBOARD_LL</c> hook sees events with no idea which device produced them. That was
/// tolerable while Nexus's own input was identifiable by the <c>dwExtraInfo</c> tag it set on every
/// <c>SendInput</c> call. A HID report carries no such field — it travels the real HID stack and
/// arrives indistinguishable from hardware, which is the entire point of the virtual-HID work and
/// simultaneously what breaks the old mechanism. <c>WM_INPUT</c> carries <c>hDevice</c>, and that is
/// the thing that survives.
/// </para>
/// <para>
/// <b>This listener is an oracle, not an event source — and that correction matters.</b> It was
/// first written believing the hook and Raw Input never observe the same event. That is true only
/// for <c>SendInput</c>, which goes through no device and raises no <c>WM_INPUT</c>. It is false for
/// everything else: a physical click raises a hook event <em>and</em> a <c>WM_INPUT</c>, and so will
/// a Nexus HID report, because a virtual HID device is a real device. Feeding this into the security
/// monitor as a second source would therefore have doubled every physical action.
/// </para>
/// <para>
/// So the hook stays the only event stream into <c>SecurityActivityMonitor</c>, preserving every
/// existing coalescing, noise and incident rule, and what this listener feeds is
/// <c>RawInputSourceLedger</c> — a bounded record of "a device Nexus published just produced input
/// of this kind" that the monitor consults. One observation excuses one hook event, within a
/// window, and an empty or unhealthy ledger excuses nothing.
/// </para>
/// <para>
/// <b>A message-only window, on its own thread.</b> Raw Input is delivered as a window message, and
/// <c>RIDEV_INPUTSINK</c> is what makes it arrive while PC Core is in the background — which is the
/// only state that matters, since the whole point is watching a machine nobody is sitting at. A
/// message-only window (<c>HWND_MESSAGE</c>) is the documented way to have a message queue without
/// having a UI. Nothing here polls.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RawInputListener : IDisposable
{
    private const int WmInput = 0x00FF;
    private const int WmInputDeviceChange = 0x00FE;
    private const int WmClose = 0x0010;
    private const int WmDestroy = 0x0002;

    private const int GidcArrival = 1;
    private const int GidcRemoval = 2;

    private const uint RidevInputSink = 0x00000100;
    private const uint RidevDevNotify = 0x00002000;

    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;

    private const uint RimTypeMouse = 0;
    private const uint RimTypeKeyboard = 1;

    private static readonly nint HwndMessage = -3;

    private readonly ILogger<RawInputListener> _logger;
    private readonly NexusHidDeviceRegistry _registry;
    private readonly TimeProvider _time;

    private readonly Lock _gate = new();
    private readonly Dictionary<nint, string> _devicePaths = [];

    private Thread? _thread;
    private nint _window;
    private WndProc? _wndProc;   // held so the delegate is not collected while Windows holds it
    private volatile bool _running;

    public RawInputListener(
        ILogger<RawInputListener> logger,
        NexusHidDeviceRegistry registry,
        TimeProvider? time = null)
    {
        _logger = logger;
        _registry = registry;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised for every observed hardware event, already classified by source.</summary>
    public event Action<RawInputObservation, InputSourceVerdict>? Observed;

    /// <summary>True once the listener has a window and a registration.</summary>
    public bool IsRunning => _running;

    /// <summary>How many devices are currently known. Diagnostics.</summary>
    public int KnownDeviceCount
    {
        get
        {
            lock (_gate)
            {
                return _devicePaths.Count;
            }
        }
    }

    /// <summary>
    /// Enumerates the mice and keyboards Windows currently knows about, without starting anything.
    /// <para>
    /// Used for diagnostics and at start-up to seed the device map, so the first event from a device
    /// does not have to resolve its path on the input path.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(RawInputKind Kind, nint Handle, string? Path)> EnumerateDevices()
    {
        var results = new List<(RawInputKind, nint, string?)>();

        if (GetRawInputDeviceList(IntPtr.Zero, out var count, (uint)Marshal.SizeOf<RawInputDeviceList>()) != 0
            || count == 0)
        {
            return results;
        }

        var size = Marshal.SizeOf<RawInputDeviceList>();
        var buffer = Marshal.AllocHGlobal((int)(size * count));

        try
        {
            var written = GetRawInputDeviceList(buffer, out count, (uint)size);
            if (written == unchecked((uint)-1))
            {
                return results;
            }

            for (var i = 0; i < written; i++)
            {
                var entry = Marshal.PtrToStructure<RawInputDeviceList>(buffer + (i * size));

                var kind = entry.Type switch
                {
                    RimTypeMouse => (RawInputKind?)RawInputKind.Mouse,
                    RimTypeKeyboard => RawInputKind.Keyboard,
                    _ => null,
                };

                if (kind is null)
                {
                    continue;
                }

                results.Add((kind.Value, entry.Device, ResolveDevicePath(entry.Device)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return results;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null)
            {
                return;
            }

            foreach (var (kind, handle, path) in EnumerateDevices())
            {
                if (path is not null)
                {
                    _devicePaths[handle] = path;
                }

                _logger.LogDebug("Raw Input device present at start-up: {Kind} {Path}", kind, path ?? "(unresolved)");
            }

            _thread = new Thread(Pump)
            {
                IsBackground = true,
                Name = "Nexus Raw Input",
            };

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    private void Pump()
    {
        const string className = "NexusRawInputSink";

        _wndProc = WindowProcedure;

        var wndClass = new WndClassEx
        {
            CbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            HInstance = GetModuleHandleW(null),
            LpszClassName = className,
        };

        if (RegisterClassExW(ref wndClass) == 0)
        {
            var error = Marshal.GetLastWin32Error();

            // 1410 is "class already registered", which happens on a restart within one process and
            // is not a failure.
            if (error != 1410)
            {
                _logger.LogWarning("Raw Input attribution unavailable: window class registration failed ({Error}).", error);
                return;
            }
        }

        _window = CreateWindowExW(0, className, className, 0, 0, 0, 0, 0,
            HwndMessage, IntPtr.Zero, wndClass.HInstance, IntPtr.Zero);

        if (_window == IntPtr.Zero)
        {
            _logger.LogWarning(
                "Raw Input attribution unavailable: message-only window creation failed ({Error}).",
                Marshal.GetLastWin32Error());
            return;
        }

        // Usage page 1 (Generic Desktop), usages 2 (Mouse) and 6 (Keyboard).
        //
        // RIDEV_INPUTSINK delivers events while another application is in the foreground, which is
        // the only state that matters here. RIDEV_DEVNOTIFY adds WM_INPUT_DEVICE_CHANGE, so a mouse
        // plugged in later is picked up without restarting anything.
        var devices = new[]
        {
            new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = RidevInputSink | RidevDevNotify, Target = _window },
            new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RidevInputSink | RidevDevNotify, Target = _window },
        };

        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            _logger.LogWarning(
                "Raw Input attribution unavailable: registration failed ({Error}).",
                Marshal.GetLastWin32Error());
            DestroyWindow(_window);
            _window = IntPtr.Zero;
            return;
        }

        _running = true;
        _logger.LogInformation(
            "Raw Input attribution running: watching {Count} device(s) for source identity.",
            KnownDeviceCount);

        while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }

        _running = false;
    }

    private nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmInput:
                OnRawInput(lParam);
                break;

            case WmInputDeviceChange:
                OnDeviceChange((int)wParam, lParam);
                break;

            case WmClose:
                DestroyWindow(hwnd);
                return 0;

            case WmDestroy:
                PostQuitMessage(0);
                return 0;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void OnRawInput(nint lParam)
    {
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        var size = 0u;

        if (GetRawInputData(lParam, RidInput, IntPtr.Zero, ref size, headerSize) != 0 || size == 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal((int)size);

        try
        {
            if (GetRawInputData(lParam, RidInput, buffer, ref size, headerSize) != size)
            {
                return;
            }

            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);

            var kind = header.Type switch
            {
                RimTypeMouse => (RawInputKind?)RawInputKind.Mouse,
                RimTypeKeyboard => RawInputKind.Keyboard,
                _ => null,
            };

            if (kind is null)
            {
                return;
            }

            var path = PathFor(header.Device);
            var observation = new RawInputObservation(kind.Value, header.Device, path, _time.GetUtcNow());

            // The classification is the registry's, not this class's. This file knows about devices;
            // it deliberately knows nothing about what makes one trustworthy.
            var verdict = _registry.Classify(path);

            Observed?.Invoke(observation, verdict);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void OnDeviceChange(int change, nint deviceHandle)
    {
        if (change == GidcArrival)
        {
            var path = ResolveDevicePath(deviceHandle);

            lock (_gate)
            {
                if (path is not null)
                {
                    _devicePaths[deviceHandle] = path;
                }
            }

            // Arrival never grants trust. A device becomes trusted only when the virtual-HID backend
            // positively claims it, which happens on the backend's own publish path and nowhere
            // else. A device that arrives looking exactly like a Nexus device is still physical.
            var verdict = _registry.Classify(path);

            _logger.LogInformation(
                "Raw Input device added: {Verdict} {Path}",
                verdict.IsNexus ? "trusted Nexus device" : "physical device",
                path ?? "(unresolved)");
        }
        else if (change == GidcRemoval)
        {
            string? path;

            lock (_gate)
            {
                _devicePaths.Remove(deviceHandle, out path);
            }

            // If a device Nexus published disappears, it stops being trusted immediately. Waiting
            // for the backend to notice would leave a window where a reused handle inherits trust.
            if (path is not null && _registry.Forget(path))
            {
                _logger.LogWarning(
                    "A Nexus HID device was removed; its identity is no longer trusted: {Path}", path);
            }
            else
            {
                _logger.LogInformation("Raw Input device removed: {Path}", path ?? "(unresolved)");
            }
        }
    }

    private string? PathFor(nint device)
    {
        lock (_gate)
        {
            if (_devicePaths.TryGetValue(device, out var known))
            {
                return known;
            }
        }

        var resolved = ResolveDevicePath(device);

        if (resolved is not null)
        {
            lock (_gate)
            {
                _devicePaths[device] = resolved;
            }
        }

        return resolved;
    }

    /// <summary>
    /// The device's interface path, or null when Windows cannot name it.
    /// <para>
    /// Null is a real answer and it matters: a device removed between the event and this query
    /// cannot be resolved, and the classifier treats that as physical. Failing to resolve must never
    /// mean failing open.
    /// </para>
    /// </summary>
    private static string? ResolveDevicePath(nint device)
    {
        var size = 0u;

        if (GetRawInputDeviceInfoW(device, RidiDeviceName, IntPtr.Zero, ref size) != 0 || size == 0)
        {
            return null;
        }

        var buffer = new StringBuilder((int)size + 1);

        return GetRawInputDeviceInfoW(device, RidiDeviceName, buffer, ref size) > 0
            ? buffer.ToString()
            : null;
    }

    public void Dispose()
    {
        var window = _window;

        if (window != IntPtr.Zero)
        {
            PostMessageW(window, WmClose, IntPtr.Zero, IntPtr.Zero);
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _window = IntPtr.Zero;
        _running = false;
    }

    // ------------------------------------------------------------------ interop

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceList
    {
        public nint Device;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nint WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint CbSize;
        public uint Style;
        public nint LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public nint HInstance;
        public nint HIcon;
        public nint HCursor;
        public nint HbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string LpszClassName;
        public nint HIconSm;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(nint list, out uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        nint rawInput, uint command, nint data, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoW(
        nint device, uint command, nint data, ref uint size);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoW(
        nint device, uint command, StringBuilder data, ref uint size);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassEx wndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out Msg message, nint hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessageW(ref Msg message);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);
}
