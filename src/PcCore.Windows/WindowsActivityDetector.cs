using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Security.Detection;
using PcCore.Windows.Interop;

// System.Diagnostics defines its own ActivityKind and ActivitySource for tracing. Aliased rather
// than resolved by import order, so a future using directive cannot silently change what these mean.
using ActivityKind = PcCore.Capabilities.Security.Detection.ActivityKind;
using ActivitySource = PcCore.Capabilities.Security.Detection.ActivitySource;
using Process = System.Diagnostics.Process;

namespace PcCore.Windows;

/// <summary>
/// Watches for a person using this machine, and can tell them apart from Nexus itself.
/// <para>
/// <b>Attribution is the point of this class.</b> Nexus injects input on the owner's behalf, and if
/// that input read back as somebody at the keyboard, using the phone during an incident would keep
/// the incident alive forever — and arming the PC then working on it remotely would raise an alarm
/// about yourself. Every injected event carries a signature in <c>dwExtraInfo</c> that only
/// <see cref="WindowsInputController"/> writes, and the hooks below match it exactly. No timing
/// window, no guessing: the event either carries Nexus's own mark or it does not.
/// </para>
/// <para>
/// <b>What is never read.</b> The keyboard hook reads two fields — the injection flags and the
/// signature — by offset. The virtual-key code and scan code are never read into anything, so no
/// part of this process ever holds which key was pressed. The mouse hook likewise never reads the
/// pointer position. The strongest guarantee against a keylogger is code that has no keystroke to
/// log.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsActivityDetector : ISecurityActivityDetector
{
    private const string WindowClassName = "NexusSecurityDetector";

    private readonly ILogger<WindowsActivityDetector> _logger;

    // Held for the lifetime of the detector: Windows keeps raw pointers to these, and letting the
    // delegates be collected would crash the process on the next input event.
    private readonly HookProcedure _keyboardProcedure;
    private readonly HookProcedure _mouseProcedure;
    private readonly WinEventProcedure _foregroundProcedure;
    private readonly WindowProcedure _windowProcedure;

    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private nint _keyboardHook;
    private nint _mouseHook;
    private nint _foregroundHook;
    private nint _window;
    private uint _lastForegroundProcess;

    public WindowsActivityDetector(ILogger<WindowsActivityDetector> logger)
    {
        _logger = logger;
        _keyboardProcedure = OnKeyboard;
        _mouseProcedure = OnMouse;
        _foregroundProcedure = OnForeground;
        _windowProcedure = OnWindowMessage;
    }

    public event Action<LocalActivity>? Observed;

    public bool IsRunning { get; private set; }

    public string? Diagnostic { get; private set; }

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        // Low-level hooks are delivered to the thread that installed them, and that thread must be
        // pumping messages. A dedicated thread keeps input detection off every other code path: a
        // slow capture or a busy request loop must never delay the system's input queue.
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Nexus security detection",
            Priority = ThreadPriority.AboveNormal,
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            Diagnostic = "the detection thread did not start";
        }
    }

    private void Run()
    {
        try
        {
            var module = DetectionInterop.GetModuleHandle(null);

            _keyboardHook = DetectionInterop.SetWindowsHookEx(
                DetectionInterop.WhKeyboardLowLevel,
                Marshal.GetFunctionPointerForDelegate(_keyboardProcedure),
                module,
                0);

            _mouseHook = DetectionInterop.SetWindowsHookEx(
                DetectionInterop.WhMouseLowLevel,
                Marshal.GetFunctionPointerForDelegate(_mouseProcedure),
                module,
                0);

            // Skipping our own process keeps Nexus's own windows out of the foreground timeline.
            _foregroundHook = DetectionInterop.SetWinEventHook(
                DetectionInterop.EventSystemForeground,
                DetectionInterop.EventSystemForeground,
                IntPtr.Zero,
                Marshal.GetFunctionPointerForDelegate(_foregroundProcedure),
                0,
                0,
                DetectionInterop.WinEventOutOfContext | DetectionInterop.WinEventSkipOwnProcess);

            CreateMessageWindow(module);

            if (_keyboardHook == IntPtr.Zero && _mouseHook == IntPtr.Zero)
            {
                Diagnostic = "Windows refused the input hooks";
                _logger.LogWarning("Security detection could not install input hooks.");
            }
            else
            {
                IsRunning = true;
                Diagnostic = null;
            }
        }
        catch (Exception ex)
        {
            Diagnostic = "the detection hooks could not be installed";
            _logger.LogWarning(ex, "Security detection failed to start.");
        }
        finally
        {
            _ready.Set();
        }

        while (IndicatorInterop.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            IndicatorInterop.TranslateMessage(ref message);
            IndicatorInterop.DispatchMessage(ref message);
        }
    }

    /// <summary>
    /// A message-only window, purely so Windows has somewhere to deliver session lock and sign-in
    /// notifications. It is never shown and cannot be interacted with.
    /// </summary>
    private void CreateMessageWindow(nint module)
    {
        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            WindowProc = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
            Instance = module,
            ClassName = WindowClassName,
        };

        IndicatorInterop.RegisterClassEx(ref windowClass);

        _window = IndicatorInterop.CreateWindowEx(
            0,
            WindowClassName,
            "Nexus security detection",
            0,
            0,
            0,
            0,
            0,
            DetectionInterop.MessageOnlyWindowParent,
            IntPtr.Zero,
            module,
            IntPtr.Zero);

        if (_window != IntPtr.Zero)
        {
            DetectionInterop.WTSRegisterSessionNotification(_window, DetectionInterop.NotifyForThisSession);
        }
    }

    // --- Hook callbacks -----------------------------------------------------------------------------

    /// <summary>
    /// Keyboard activity.
    /// <para>
    /// Reads exactly two fields, both by offset: whether the event was injected, and whether it
    /// carries Nexus's signature. It does not read, copy, or discard the key — the key is simply
    /// never looked at.
    /// </para>
    /// </summary>
    private nint OnKeyboard(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            try
            {
                var flags = DetectionInterop.ReadKeyboardFlags(lParam);
                var extra = DetectionInterop.ReadKeyboardExtraInfo(lParam);

                Report(ActivityKind.KeyboardActivity, Attribute(extra, (flags & DetectionInterop.LlkhfInjected) != 0));
            }
            catch (Exception ex) when (ex is AccessViolationException or NullReferenceException)
            {
                // Never let a malformed payload break the input chain for the whole desktop.
            }
        }

        return DetectionInterop.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private nint OnMouse(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            try
            {
                var message = (int)wParam;

                // Movement and buttons both count; the wheel does too. What is deliberately absent is
                // where any of it happened.
                if (message is DetectionInterop.WmMouseMove
                    or DetectionInterop.WmLeftButtonDown
                    or DetectionInterop.WmRightButtonDown
                    or DetectionInterop.WmMiddleButtonDown
                    or DetectionInterop.WmMouseWheel)
                {
                    var flags = DetectionInterop.ReadMouseFlags(lParam);
                    var extra = DetectionInterop.ReadMouseExtraInfo(lParam);

                    var source = Attribute(extra, (flags & DetectionInterop.LlmhfInjected) != 0);

                    // A button carries a detail marker so the monitor can skip the movement threshold:
                    // a click is unambiguous in a way that a pixel of drift is not.
                    var detail = message == DetectionInterop.WmMouseMove ? null : "button";

                    Report(ActivityKind.PointerActivity, source, detail);
                }
            }
            catch (Exception ex) when (ex is AccessViolationException or NullReferenceException)
            {
                // As above.
            }
        }

        return DetectionInterop.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>
    /// The exact attribution rule.
    /// <para>
    /// The signature is written only by <see cref="WindowsInputController"/> and is checked for
    /// equality. Anything else that is flagged injected came from some other program, which is also
    /// not a person at the keyboard.
    /// </para>
    /// </summary>
    private static ActivitySource Attribute(nint extraInfo, bool injected)
    {
        // Compared on the low 32 bits, which is all Windows carries through the mouse hook. See
        // InputInterop.NexusInjectionTag for the measurement behind that.
        if ((extraInfo.ToInt64() & 0xFFFFFFFF) == InputInterop.NexusInjectionTag)
        {
            return ActivitySource.NexusRemote;
        }

        return injected ? ActivitySource.Synthetic : ActivitySource.Local;
    }

    private void OnForeground(
        nint hook,
        uint eventId,
        nint window,
        int objectId,
        int childId,
        uint threadId,
        uint time)
    {
        try
        {
            DetectionInterop.GetWindowThreadProcessId(window, out var processId);

            if (processId == 0 || processId == _lastForegroundProcess)
            {
                return;
            }

            _lastForegroundProcess = processId;

            // The process name only. Never the window title, which on a browser or an editor is the
            // content the owner is looking at.
            string? name = null;

            try
            {
                name = Process.GetProcessById((int)processId).ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // The process ended between the notification and the lookup.
            }

            Report(ActivityKind.ForegroundWindowChanged, ActivitySource.Local, name);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A foreground change could not be described.");
        }
    }

    private nint OnWindowMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == IndicatorInterop.WmAppQuit)
        {
            IndicatorInterop.DestroyWindow(window);
            return IntPtr.Zero;
        }

        if (message == IndicatorInterop.WmDestroy)
        {
            IndicatorInterop.PostQuitMessage(0);
            return IntPtr.Zero;
        }

        if (message == DetectionInterop.WmWtsSessionChange)
        {
            var kind = (int)wParam switch
            {
                DetectionInterop.WtsSessionUnlock => ActivityKind.SessionUnlocked,
                DetectionInterop.WtsSessionLogon => ActivityKind.InteractiveLogon,
                _ => (ActivityKind?)null,
            };

            if (kind is not null)
            {
                Report(kind.Value, ActivitySource.Local);
            }
        }

        return IndicatorInterop.DefWindowProc(window, message, wParam, lParam);
    }

    private void Report(ActivityKind kind, ActivitySource source, string? detail = null)
    {
        try
        {
            Observed?.Invoke(new LocalActivity(kind, source, DateTimeOffset.UtcNow, detail));
        }
        catch (Exception ex)
        {
            // A subscriber that throws must not take down the input hook, which every keystroke on
            // the machine passes through.
            _logger.LogError(ex, "A security activity subscriber failed.");
        }
    }

    public void Dispose()
    {
        IsRunning = false;

        if (_window != IntPtr.Zero)
        {
            DetectionInterop.WTSUnRegisterSessionNotification(_window);
            IndicatorInterop.PostMessage(_window, IndicatorInterop.WmAppQuit, IntPtr.Zero, IntPtr.Zero);
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            DetectionInterop.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            DetectionInterop.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_foregroundHook != IntPtr.Zero)
        {
            DetectionInterop.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
