using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcCore.NativeInput;

/// <summary>
/// Injects mouse, keyboard and touch input into the current session's desktop.
/// <para>
/// One class, used by two processes. PC Core creates one to drive the desktop itself; the Nexus
/// Input Broker creates one to do the same at a higher integrity level. Keeping the injection in a
/// single place is what stops the elevated path and the fallback path drifting into two different
/// sets of behaviour — a bug there would show up as "the mouse behaves differently once you install
/// native input", which is exactly the failure mode worth designing out.
/// </para>
/// <para>
/// <b>Everything arriving here is already validated.</b> Range checks, rate limits and the closed
/// key set belong to the capability layer. What this class still enforces is the part that is a
/// property of the machine rather than of the request: the Secure Desktop gate, the virtual-key
/// allow-list, and the bookkeeping that lets every held button, key and contact be released.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NativeInjector
{
    /// <summary>Simultaneous touch contacts. Windows allows far more; Nexus has no use for them.</summary>
    public const int MaxTouchContacts = 10;

    private readonly Lock _gate = new();
    private readonly Dictionary<uint, TouchContact> _contacts = [];
    private readonly HashSet<uint> _heldButtons = [];
    private readonly HashSet<(ushort VirtualKey, bool Extended)> _heldKeys = [];

    private bool _touchInitialised;
    private string? _touchDiagnostic;

    public NativeInjector()
    {
        // Failing to initialise touch is not fatal: pointer and keyboard still work, and the caller
        // is told that touch is unavailable rather than being handed a broken contact.
        if (NativeInputInterop.InitializeTouchInjection(MaxTouchContacts, NativeInputInterop.TouchFeedbackIndirect))
        {
            _touchInitialised = true;
        }
        else
        {
            _touchDiagnostic = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }
    }

    /// <summary>
    /// Makes this process see real pixels on a scaled display.
    /// <para>
    /// Every process that computes or consumes Nexus coordinates has to agree about what a pixel is.
    /// PC Core does this at start-up; the broker has to do the same, or a point PC Core worked out
    /// on a 150%-scaled monitor would land somewhere else once the broker injected it.
    /// </para>
    /// </summary>
    public static bool EnablePerMonitorDpiAwareness() =>
        NativeInputInterop.SetProcessDpiAwarenessContext(NativeInputInterop.PerMonitorAwareV2);

    /// <summary>Whether real Windows touch injection is available in this process.</summary>
    public bool TouchAvailable => _touchInitialised;

    public string? TouchDiagnostic => _touchDiagnostic;

    /// <summary>
    /// True when this machine has a pen or touch-screen digitizer of its own.
    /// <para>
    /// Answered by enumerating raw input devices rather than by <c>SM_DIGITIZER</c>. That metric is
    /// system-wide and counts injection: it read <c>0x0</c> on this machine, <c>0xC1</c> after the
    /// first <c>InitializeTouchInjection</c> call, and <c>0xC1</c> in a freshly started process
    /// simply because the Nexus Input Broker was running in another one. See
    /// <see cref="PhysicalDigitizer"/>.
    /// </para>
    /// </summary>
    public static bool HasPhysicalDigitizer => PhysicalDigitizer.IsPresent();

    /// <summary>The whole virtual desktop in physical pixels. The left edge can be negative.</summary>
    public static (int Left, int Top, int Width, int Height) VirtualDesktop => (
        NativeInputInterop.GetSystemMetrics(NativeInputInterop.SmXVirtualScreen),
        NativeInputInterop.GetSystemMetrics(NativeInputInterop.SmYVirtualScreen),
        NativeInputInterop.GetSystemMetrics(NativeInputInterop.SmCxVirtualScreen),
        NativeInputInterop.GetSystemMetrics(NativeInputInterop.SmCyVirtualScreen));

    public static bool TryGetCursorPosition(out int x, out int y)
    {
        var ok = NativeInputInterop.GetCursorPos(out var point);
        x = point.X;
        y = point.Y;
        return ok;
    }

    /// <summary>How many buttons, keys and contacts are currently held down.</summary>
    public (int Buttons, int Keys, int Contacts) Held
    {
        get
        {
            lock (_gate)
            {
                return (_heldButtons.Count, _heldKeys.Count, _contacts.Count);
            }
        }
    }

    // --- Pointer -------------------------------------------------------------------------------------

    /// <param name="x">Virtual-desktop pixels. May be negative on a multi-monitor layout.</param>
    public NativeInjectionResult MoveAbsolute(int x, int y)
    {
        var (left, top, width, height) = VirtualDesktop;

        if (width <= 0 || height <= 0)
        {
            return NativeInjectionResult.Failed("the virtual desktop has no size");
        }

        // SendInput's absolute space is 0..65535 across the whole virtual desktop, not pixels. The
        // half-unit offset lands the pointer in the middle of the target pixel rather than on its
        // boundary, which is what stops a tap on the far right edge rounding to the pixel before it.
        var normalisedX = (int)Math.Round((x - left + 0.5) * NativeInputInterop.AbsoluteRange / width);
        var normalisedY = (int)Math.Round((y - top + 0.5) * NativeInputInterop.AbsoluteRange / height);

        return Send(new Input
        {
            Type = NativeInputInterop.InputMouse,
            Mouse = new MouseInput
            {
                Dx = Math.Clamp(normalisedX, 0, NativeInputInterop.AbsoluteRange),
                Dy = Math.Clamp(normalisedY, 0, NativeInputInterop.AbsoluteRange),
                Flags = NativeInputInterop.MouseEventMove
                    | NativeInputInterop.MouseEventAbsolute
                    | NativeInputInterop.MouseEventVirtualDesk,
            },
        });
    }

    public NativeInjectionResult MoveRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
        {
            return NativeInjectionResult.Ok;
        }

        return Send(new Input
        {
            Type = NativeInputInterop.InputMouse,
            Mouse = new MouseInput { Dx = dx, Dy = dy, Flags = NativeInputInterop.MouseEventMove },
        });
    }

    /// <param name="button">0 left, 1 right, 2 middle. Anything else is refused.</param>
    public NativeInjectionResult Button(int button, bool down)
    {
        var flags = (button, down) switch
        {
            (0, true) => NativeInputInterop.MouseEventLeftDown,
            (0, false) => NativeInputInterop.MouseEventLeftUp,
            (1, true) => NativeInputInterop.MouseEventRightDown,
            (1, false) => NativeInputInterop.MouseEventRightUp,
            (2, true) => NativeInputInterop.MouseEventMiddleDown,
            (2, false) => NativeInputInterop.MouseEventMiddleUp,
            _ => 0u,
        };

        if (flags == 0)
        {
            return NativeInjectionResult.Failed("unknown pointer button");
        }

        var result = Send(new Input
        {
            Type = NativeInputInterop.InputMouse,
            Mouse = new MouseInput { Flags = flags },
        });

        // Tracked whatever the caller does with its own bookkeeping: the broker must be able to
        // neutralise the desktop on a dropped connection without being told what was held.
        if (result.Succeeded)
        {
            lock (_gate)
            {
                if (down)
                {
                    _heldButtons.Add((uint)button);
                }
                else
                {
                    _heldButtons.Remove((uint)button);
                }
            }
        }

        return result;
    }

    public NativeInjectionResult Scroll(int horizontalNotches, int verticalNotches)
    {
        var events = new List<Input>(2);

        if (verticalNotches != 0)
        {
            events.Add(new Input
            {
                Type = NativeInputInterop.InputMouse,
                Mouse = new MouseInput
                {
                    MouseData = unchecked((uint)(verticalNotches * NativeInputInterop.WheelDelta)),
                    Flags = NativeInputInterop.MouseEventWheel,
                },
            });
        }

        if (horizontalNotches != 0)
        {
            events.Add(new Input
            {
                Type = NativeInputInterop.InputMouse,
                Mouse = new MouseInput
                {
                    MouseData = unchecked((uint)(horizontalNotches * NativeInputInterop.WheelDelta)),
                    Flags = NativeInputInterop.MouseEventHWheel,
                },
            });
        }

        return events.Count == 0 ? NativeInjectionResult.Ok : Send([.. events]);
    }

    // --- Keyboard -------------------------------------------------------------------------------------

    /// <summary>
    /// Presses or releases one key by virtual-key code.
    /// <para>
    /// The code is checked against <see cref="NativeKeys"/> rather than trusted. A virtual-key code
    /// arriving from outside this process — which is exactly what happens over the broker pipe — is
    /// the shape of input that gets forwarded straight into a Win32 call without anyone looking at
    /// it, and the allow-list is what makes that impossible here.
    /// </para>
    /// </summary>
    public NativeInjectionResult Key(ushort virtualKey, bool extended, bool down)
    {
        if (!NativeKeys.IsAllowed(virtualKey))
        {
            return NativeInjectionResult.Failed($"virtual-key 0x{virtualKey:X2} is not one Nexus may press");
        }

        var flags = down ? 0u : NativeInputInterop.KeyEventKeyUp;

        if (extended)
        {
            flags |= NativeInputInterop.KeyEventExtendedKey;
        }

        var result = Send(new Input
        {
            Type = NativeInputInterop.InputKeyboard,
            Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = flags },
        });

        if (result.Succeeded)
        {
            lock (_gate)
            {
                if (down)
                {
                    _heldKeys.Add((virtualKey, extended));
                }
                else
                {
                    _heldKeys.Remove((virtualKey, extended));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Types text as Unicode.
    /// <para>
    /// <c>KEYEVENTF_UNICODE</c> takes UTF-16 code units, so a surrogate pair is two events and an
    /// emoji arrives intact. This is what makes Turkish — and every other layout the phone's own
    /// keyboard can produce — arrive as typed rather than as the nearest US-keyboard approximation.
    /// A HID keyboard cannot do this at all: HID carries key positions, not characters.
    /// </para>
    /// </summary>
    public NativeInjectionResult Text(string text)
    {
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
                Type = NativeInputInterop.InputKeyboard,
                Keyboard = new KeyboardInput { ScanCode = unit, Flags = NativeInputInterop.KeyEventUnicode },
            });

            events.Add(new Input
            {
                Type = NativeInputInterop.InputKeyboard,
                Keyboard = new KeyboardInput
                {
                    ScanCode = unit,
                    Flags = NativeInputInterop.KeyEventUnicode | NativeInputInterop.KeyEventKeyUp,
                },
            });
        }

        return events.Count == 0 ? NativeInjectionResult.Ok : Send([.. events]);
    }

    // --- Touch ----------------------------------------------------------------------------------------

    /// <summary>
    /// Drives one touch contact.
    /// <para>
    /// Windows wants every live contact described in every frame, so the frame is rebuilt from the
    /// tracked contacts each time rather than sent as a delta. That is also what makes a second
    /// finger possible later without changing anything here.
    /// </para>
    /// </summary>
    /// <param name="contactId">Which finger. Bounded to <see cref="MaxTouchContacts"/>.</param>
    /// <param name="x">Virtual-desktop pixels.</param>
    public NativeInjectionResult Touch(uint contactId, TouchPhase phase, int x, int y)
    {
        if (!_touchInitialised)
        {
            return NativeInjectionResult.Unsupported(
                _touchDiagnostic is null
                    ? "touch injection is not available on this system"
                    : $"touch injection is not available on this system ({_touchDiagnostic})");
        }

        if (contactId >= MaxTouchContacts)
        {
            return NativeInjectionResult.Failed("touch contact id out of range");
        }

        var desktop = InputDesktop.Current();

        if (!desktop.IsInteractive)
        {
            return SecureDesktopRefusal(desktop);
        }

        PointerTouchInfo[] frame;

        lock (_gate)
        {
            switch (phase)
            {
                case TouchPhase.Down:
                    _contacts[contactId] = new TouchContact(x, y, Phase: TouchPhase.Down);
                    break;

                case TouchPhase.Move:
                    if (!_contacts.ContainsKey(contactId))
                    {
                        // A move with no contact down is not an error worth failing a gesture over;
                        // it means a down was lost. Start the contact rather than dropping the finger.
                        _contacts[contactId] = new TouchContact(x, y, Phase: TouchPhase.Down);
                    }
                    else
                    {
                        _contacts[contactId] = new TouchContact(x, y, Phase: TouchPhase.Move);
                    }

                    break;

                case TouchPhase.Up:
                case TouchPhase.Cancel:
                    if (!_contacts.TryGetValue(contactId, out var existing))
                    {
                        return NativeInjectionResult.Ok;
                    }

                    // Windows requires the UP frame to carry the same position as the last UPDATE.
                    // Sending anything else fails the call and cancels every live contact.
                    _contacts[contactId] = existing with { Phase = phase };
                    break;
            }

            frame = BuildFrame();

            // The contact is gone as soon as its terminating frame has been built, so a failed
            // injection cannot leave a finger stuck down in our own bookkeeping.
            if (phase is TouchPhase.Up or TouchPhase.Cancel)
            {
                _contacts.Remove(contactId);
            }
        }

        return InjectTouchFrame(frame);
    }

    /// <summary>
    /// Releases everything this injector is holding: buttons, keys and touch contacts.
    /// <para>
    /// The order matters. Touch contacts go first because a live contact makes Windows ignore some
    /// mouse state; buttons go before keys because releasing a modifier mid-drag turns the drag into
    /// a shortcut on the way out.
    /// </para>
    /// </summary>
    /// <returns>How many held things were released.</returns>
    public int ReleaseAll()
    {
        uint[] contacts;
        uint[] buttons;
        (ushort VirtualKey, bool Extended)[] keys;

        lock (_gate)
        {
            contacts = [.. _contacts.Keys];
            buttons = [.. _heldButtons];
            keys = [.. _heldKeys];
        }

        var released = 0;

        foreach (var contact in contacts)
        {
            TouchContact state;

            lock (_gate)
            {
                if (!_contacts.TryGetValue(contact, out state))
                {
                    continue;
                }
            }

            Safely(() => Touch(contact, TouchPhase.Cancel, state.X, state.Y));
            released++;
        }

        foreach (var button in buttons)
        {
            Safely(() => Button((int)button, down: false));
            released++;
        }

        foreach (var (virtualKey, extended) in keys)
        {
            Safely(() => Key(virtualKey, extended, down: false));
            released++;
        }

        lock (_gate)
        {
            // Cleared unconditionally. If Windows refused a release there is nothing further this
            // process can do about it, and continuing to believe the key is held helps nobody.
            _contacts.Clear();
            _heldButtons.Clear();
            _heldKeys.Clear();
        }

        return released;
    }

    // --- Internals -------------------------------------------------------------------------------------

    private PointerTouchInfo[] BuildFrame()
    {
        var frame = new PointerTouchInfo[_contacts.Count];
        var i = 0;

        foreach (var (id, contact) in _contacts)
        {
            var flags = contact.Phase switch
            {
                TouchPhase.Down => NativeInputInterop.PointerFlagDown
                    | NativeInputInterop.PointerFlagInRange
                    | NativeInputInterop.PointerFlagInContact,
                TouchPhase.Move => NativeInputInterop.PointerFlagUpdate
                    | NativeInputInterop.PointerFlagInRange
                    | NativeInputInterop.PointerFlagInContact,
                TouchPhase.Up => NativeInputInterop.PointerFlagUp,
                _ => NativeInputInterop.PointerFlagUp | NativeInputInterop.PointerFlagCanceled,
            };

            frame[i++] = new PointerTouchInfo
            {
                PointerInfo = new PointerInfo
                {
                    PointerType = NativeInputInterop.PointerTypeTouch,
                    PointerId = id,
                    PointerFlags = flags,
                    PtPixelLocation = new NativePoint { X = contact.X, Y = contact.Y },
                },
                TouchMask = NativeInputInterop.TouchMaskContactArea | NativeInputInterop.TouchMaskPressure,

                // A small contact rectangle centred on the point. Windows uses it to decide what was
                // touched; a zero-size one makes precise targets unreliable.
                Contact = new NativeRect
                {
                    Left = contact.X - ContactRadius,
                    Top = contact.Y - ContactRadius,
                    Right = contact.X + ContactRadius,
                    Bottom = contact.Y + ContactRadius,
                },
                Pressure = DefaultPressure,
            };
        }

        return frame;
    }

    private NativeInjectionResult InjectTouchFrame(PointerTouchInfo[] frame)
    {
        if (frame.Length == 0)
        {
            return NativeInjectionResult.Ok;
        }

        // Windows rejects two frames that land in the same 0.1 ms window with ERROR_NOT_READY and
        // asks for the same frame again. That is a retry, not a failure: the documented contract is
        // that the caller re-sends until it takes.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (NativeInputInterop.InjectTouchInput((uint)frame.Length, frame))
            {
                return NativeInjectionResult.Ok;
            }

            var error = Marshal.GetLastWin32Error();

            if (error != ErrorNotReady)
            {
                return TranslateError(error);
            }

            Thread.SpinWait(200);
        }

        return NativeInjectionResult.Failed("Windows was not ready for the touch frame");
    }

    private static Input KeyEvent(ushort virtualKey, bool up) => new()
    {
        Type = NativeInputInterop.InputKeyboard,
        Keyboard = new KeyboardInput
        {
            VirtualKey = virtualKey,
            Flags = up ? NativeInputInterop.KeyEventKeyUp : 0,
        },
    };

    private NativeInjectionResult Send(params Input[] events)
    {
        // Checked on every batch rather than once per session. The Secure Desktop can come up between
        // one event and the next, and "we checked a moment ago" is not a boundary.
        var desktop = InputDesktop.Current();

        if (!desktop.IsInteractive)
        {
            return SecureDesktopRefusal(desktop);
        }

        // Stamped here rather than at each call site, so nothing Nexus injects can ever reach the
        // desktop unmarked and be read back as local human activity.
        for (var i = 0; i < events.Length; i++)
        {
            if (events[i].Type == NativeInputInterop.InputMouse)
            {
                events[i].Mouse.ExtraInfo = NexusInputTag.Signature;
            }
            else
            {
                events[i].Keyboard.ExtraInfo = NexusInputTag.Signature;
            }
        }

        uint inserted;

        try
        {
            inserted = NativeInputInterop.SendInput((uint)events.Length, events, Marshal.SizeOf<Input>());
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return NativeInjectionResult.Failed("input injection is unavailable on this system");
        }

        return inserted == (uint)events.Length
            ? NativeInjectionResult.Ok
            : TranslateError(Marshal.GetLastWin32Error());
    }

    private static NativeInjectionResult SecureDesktopRefusal(InputDesktopState desktop) =>
        desktop.Kind == InputDesktopKind.Other
            ? NativeInjectionResult.SecureDesktop(
                $"Windows is showing another desktop ({desktop.Name}); Nexus does not send input there.")
            : NativeInjectionResult.SecureDesktop(
                "Windows is showing a secure prompt. Local administrator confirmation is required on the PC.");

    private static NativeInjectionResult TranslateError(int error)
    {
        // ERROR_ACCESS_DENIED here means UIPI blocked the event, which is the expected outcome when
        // the foreground window is elevated and the injecting process is not.
        if (error == ErrorAccessDenied)
        {
            return NativeInjectionResult.Blocked(
                "Windows blocked this input. The window in focus is running as administrator.");
        }

        return NativeInjectionResult.Failed($"Windows rejected the input ({new Win32Exception(error).Message})");
    }

    private static void Safely(Func<NativeInjectionResult> action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Cleanup runs on the failure path. It must never throw a second failure over the first.
        }
    }

    private const int ErrorAccessDenied = 5;
    private const int ErrorNotReady = 21;

    /// <summary>Half the width of the contact rectangle, in pixels. Roughly a fingertip.</summary>
    private const int ContactRadius = 4;

    /// <summary>Mid-scale on the 0-1024 HID pressure range Windows expects.</summary>
    private const uint DefaultPressure = 512;

    private readonly record struct TouchContact(int X, int Y, TouchPhase Phase);
}
