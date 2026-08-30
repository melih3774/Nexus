using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Input;
using PcCore.Windows.Interop;

namespace PcCore.Windows;

/// <summary>
/// Injects input with <c>SendInput</c>, as a standard-user application.
/// <para>
/// <b>What this deliberately cannot do.</b> Windows refuses input sent from a lower integrity level
/// to a higher one, so nothing here reaches an elevated window, the UAC prompt, the lock screen, or
/// the Ctrl+Alt+Del secure desktop. That is a boundary, not a defect: getting past it would mean
/// running PC Core elevated, installing a privileged helper, or a driver — three ways of making a
/// remote-control tool considerably more dangerous than the thing it controls. When Windows refuses,
/// this reports it, and the phone says so.
/// </para>
/// <para>
/// It receives already-validated values. Range checks, rate limits and the closed key set live in
/// <see cref="InputService"/>, so the translation from a protocol value to a Win32 structure is the
/// only thing happening here.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsInputController : IInputController
{
    private readonly IDisplayGeometry _geometry;
    private readonly ILogger<WindowsInputController> _logger;

    public WindowsInputController(IDisplayGeometry geometry, ILogger<WindowsInputController> logger)
    {
        _geometry = geometry;
        _logger = logger;
    }

    public (int Left, int Top, int Width, int Height) VirtualDesktop
    {
        get
        {
            var bounds = _geometry.VirtualBounds;
            return (bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
    }

    /// <summary>
    /// Where the pointer is now, in virtual-desktop pixels.
    /// <para>
    /// Exposed for diagnostics rather than for the protocol: nothing remote needs to ask, and the
    /// self-test needs it to prove that an injected move actually moved something.
    /// </para>
    /// </summary>
    public static bool TryGetPointerPosition(out int x, out int y)
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        var ok = InputInterop.GetCursorInfo(ref info);

        x = info.ScreenX;
        y = info.ScreenY;

        return ok;
    }

    public InputResult MovePointerAbsolute(int x, int y)
    {
        var bounds = _geometry.VirtualBounds;

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return InputResult.Failed("the virtual desktop has no size");
        }

        // SendInput's absolute space is 0..65535 across the whole virtual desktop, not pixels. The
        // half-unit offset lands the pointer in the middle of the target pixel rather than on its
        // boundary, which is what stops a tap on the far right edge rounding to the pixel before it.
        var normalisedX = (int)Math.Round((x - bounds.X + 0.5) * InputInterop.AbsoluteRange / bounds.Width);
        var normalisedY = (int)Math.Round((y - bounds.Y + 0.5) * InputInterop.AbsoluteRange / bounds.Height);

        return Send(new Input
        {
            Type = InputInterop.InputMouse,
            Mouse = new MouseInput
            {
                Dx = Math.Clamp(normalisedX, 0, InputInterop.AbsoluteRange),
                Dy = Math.Clamp(normalisedY, 0, InputInterop.AbsoluteRange),
                Flags = InputInterop.MouseEventMove
                    | InputInterop.MouseEventAbsolute
                    | InputInterop.MouseEventVirtualDesk,
            },
        });
    }

    public InputResult MovePointerRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
        {
            return InputResult.Ok;
        }

        return Send(new Input
        {
            Type = InputInterop.InputMouse,
            Mouse = new MouseInput { Dx = dx, Dy = dy, Flags = InputInterop.MouseEventMove },
        });
    }

    public InputResult Button(PointerButton button, bool down)
    {
        var flags = (button, down) switch
        {
            (PointerButton.Left, true) => InputInterop.MouseEventLeftDown,
            (PointerButton.Left, false) => InputInterop.MouseEventLeftUp,
            (PointerButton.Right, true) => InputInterop.MouseEventRightDown,
            (PointerButton.Right, false) => InputInterop.MouseEventRightUp,
            (PointerButton.Middle, true) => InputInterop.MouseEventMiddleDown,
            _ => InputInterop.MouseEventMiddleUp,
        };

        return Send(new Input
        {
            Type = InputInterop.InputMouse,
            Mouse = new MouseInput { Flags = flags },
        });
    }

    public InputResult Scroll(int horizontalNotches, int verticalNotches)
    {
        var events = new List<Input>(2);

        if (verticalNotches != 0)
        {
            events.Add(new Input
            {
                Type = InputInterop.InputMouse,
                Mouse = new MouseInput
                {
                    MouseData = unchecked((uint)(verticalNotches * InputInterop.WheelDelta)),
                    Flags = InputInterop.MouseEventWheel,
                },
            });
        }

        if (horizontalNotches != 0)
        {
            events.Add(new Input
            {
                Type = InputInterop.InputMouse,
                Mouse = new MouseInput
                {
                    MouseData = unchecked((uint)(horizontalNotches * InputInterop.WheelDelta)),
                    Flags = InputInterop.MouseEventHWheel,
                },
            });
        }

        return events.Count == 0 ? InputResult.Ok : Send([.. events]);
    }

    public InputResult Key(NexusKey key, bool down)
    {
        var (virtualKey, extended) = Map(key);

        var flags = down ? 0u : InputInterop.KeyEventKeyUp;
        if (extended)
        {
            flags |= InputInterop.KeyEventExtendedKey;
        }

        return Send(new Input
        {
            Type = InputInterop.InputKeyboard,
            Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = flags },
        });
    }

    public InputResult Text(string text)
    {
        // KEYEVENTF_UNICODE takes UTF-16 code units, so a surrogate pair is two events and an emoji
        // arrives intact. Sending virtual-key codes instead would silently reduce anything outside
        // the current keyboard layout to nothing.
        var events = new List<Input>(text.Length * 2);

        foreach (var unit in text)
        {
            if (unit == '\n')
            {
                // A newline typed into a Windows control is Enter, not U+000A.
                events.Add(KeyEvent(0x0D, false));
                events.Add(KeyEvent(0x0D, true));
                continue;
            }

            if (unit == '\r')
            {
                continue;
            }

            events.Add(new Input
            {
                Type = InputInterop.InputKeyboard,
                Keyboard = new KeyboardInput { ScanCode = unit, Flags = InputInterop.KeyEventUnicode },
            });

            events.Add(new Input
            {
                Type = InputInterop.InputKeyboard,
                Keyboard = new KeyboardInput
                {
                    ScanCode = unit,
                    Flags = InputInterop.KeyEventUnicode | InputInterop.KeyEventKeyUp,
                },
            });
        }

        return events.Count == 0 ? InputResult.Ok : Send([.. events]);
    }

    private static Input KeyEvent(ushort virtualKey, bool up) => new()
    {
        Type = InputInterop.InputKeyboard,
        Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = up ? InputInterop.KeyEventKeyUp : 0 },
    };

    private InputResult Send(params Input[] events)
    {
        uint inserted;

        try
        {
            inserted = InputInterop.SendInput((uint)events.Length, events, Marshal.SizeOf<Input>());
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return InputResult.Failed("input injection is unavailable on this system");
        }

        if (inserted == (uint)events.Length)
        {
            return InputResult.Ok;
        }

        var error = Marshal.GetLastWin32Error();

        // ERROR_ACCESS_DENIED here means UIPI blocked the event, which is the expected outcome when
        // the foreground window is elevated. Everything else is an ordinary failure.
        if (error == 5)
        {
            return InputResult.Blocked(
                "Windows blocked this input. The window in focus is running as administrator, "
                + "and Nexus runs as a standard user.");
        }

        var message = new Win32Exception(error).Message;
        _logger.LogDebug("SendInput inserted {Inserted} of {Total} events: {Message}", inserted, events.Length, message);

        return InputResult.Failed($"Windows rejected the input ({message})");
    }

    /// <summary>
    /// Maps a protocol key to a virtual-key code, and says whether it needs the extended-key flag.
    /// <para>
    /// The extended flag matters: without it, the arrow keys and navigation cluster are read as
    /// their numeric-keypad twins, so Home types 7 and the up arrow types 8 whenever Num Lock is on.
    /// </para>
    /// </summary>
    private static (ushort VirtualKey, bool Extended) Map(NexusKey key) => key switch
    {
        NexusKey.Escape => (0x1B, false),
        NexusKey.Tab => (0x09, false),
        NexusKey.Enter => (0x0D, false),
        NexusKey.Backspace => (0x08, false),
        NexusKey.Delete => (0x2E, true),
        NexusKey.Insert => (0x2D, true),
        NexusKey.Space => (0x20, false),

        NexusKey.Home => (0x24, true),
        NexusKey.End => (0x23, true),
        NexusKey.PageUp => (0x21, true),
        NexusKey.PageDown => (0x22, true),

        NexusKey.ArrowLeft => (0x25, true),
        NexusKey.ArrowUp => (0x26, true),
        NexusKey.ArrowRight => (0x27, true),
        NexusKey.ArrowDown => (0x28, true),

        NexusKey.Control => (0x11, false),
        NexusKey.Alt => (0x12, false),
        NexusKey.Shift => (0x10, false),

        // Left Windows key. The right one behaves identically for every shortcut worth sending.
        NexusKey.Meta => (0x5B, true),

        NexusKey.F1 => (0x70, false),
        NexusKey.F2 => (0x71, false),
        NexusKey.F3 => (0x72, false),
        NexusKey.F4 => (0x73, false),
        NexusKey.F5 => (0x74, false),
        NexusKey.F6 => (0x75, false),
        NexusKey.F7 => (0x76, false),
        NexusKey.F8 => (0x77, false),
        NexusKey.F9 => (0x78, false),
        NexusKey.F10 => (0x79, false),
        NexusKey.F11 => (0x7A, false),
        NexusKey.F12 => (0x7B, false),

        // A-Z share their virtual-key codes with their ASCII values, and 0-9 with theirs.
        >= NexusKey.A and <= NexusKey.Z => ((ushort)(0x41 + (key - NexusKey.A)), false),
        >= NexusKey.Digit0 and <= NexusKey.Digit9 => ((ushort)(0x30 + (key - NexusKey.Digit0)), false),

        _ => (0x1B, false),
    };
}
