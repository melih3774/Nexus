using System.Diagnostics;

namespace PcCore.NativeInput.Hid;

/// <summary>Which phase a touch contact is in, mirrored so this project stays reference-free.</summary>
public enum HidTouchPhase
{
    Down,
    Move,
    Up,
    Cancel,
}

/// <summary>
/// Turns Nexus input operations into HID reports and submits them through a backend.
/// <para>
/// <b>This is where the difference between injecting and being a device actually shows up.</b>
/// <c>SendInput</c> is stateless — each call describes an event and Windows tracks what is held. HID
/// is the opposite: a report describes the <em>complete current state</em> of the device, so
/// pressing a second key means sending a report containing both keys, and releasing one means
/// sending a report containing the other. This class holds that state, and holding it correctly is
/// the whole job.
/// </para>
/// <para>
/// <b>Absolute pointer movement is deliberately unsupported.</b> The mouse descriptor declares
/// relative axes, because that is what a mouse is; a HID mouse has no way to express "go to this
/// point". Direct mode reaches the screen through the digitizer instead, and when no digitizer is
/// published the caller falls back rather than this class pretending. Reporting
/// <see cref="NativeDelivery.Unsupported"/> is what lets the router make that choice.
/// </para>
/// </summary>
public sealed class VirtualHidController
{
    private readonly IVirtualHidBackend _backend;
    private readonly Lock _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<byte> _keys = new(HidKeyboardReport.MaxKeys);

    private HidKeyboardModifiers _modifiers;
    private HidMouseButtons _buttons;
    private bool _contactDown;
    private ushort _lastX;
    private ushort _lastY;

    public VirtualHidController(IVirtualHidBackend backend) =>
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    /// <summary>Whether the backend currently publishes a device for this kind.</summary>
    public bool Publishes(VirtualHidDeviceKind kind) => _backend.Availability.Publishes(kind);

    /// <summary>How many things are held. Used by ownership tracking and diagnostics.</summary>
    public (int Buttons, int Keys, int Modifiers, bool Contact) Held
    {
        get
        {
            lock (_gate)
            {
                return (CountBits((byte)_buttons), _keys.Count, CountBits((byte)_modifiers), _contactDown);
            }
        }
    }

    public NativeInjectionResult MoveRelative(int dx, int dy)
    {
        if (!Publishes(VirtualHidDeviceKind.Mouse))
        {
            return NativeInjectionResult.Unsupported("the Nexus HID mouse is not published");
        }

        // Saturate rather than wrap. A delta beyond a short is not a real gesture, and wrapping would
        // send the pointer the other way.
        var x = (short)Math.Clamp(dx, short.MinValue, short.MaxValue);
        var y = (short)Math.Clamp(dy, short.MinValue, short.MaxValue);

        lock (_gate)
        {
            return Submit(new HidMouseReport(_buttons, x, y, 0, 0));
        }
    }

    /// <summary>
    /// Absolute positioning, which a relative HID mouse cannot express. Always unsupported, so the
    /// caller falls back to a path that can.
    /// </summary>
    public static NativeInjectionResult MoveAbsolute() =>
        NativeInjectionResult.Unsupported(
            "a HID mouse reports relative movement; absolute positioning needs the digitizer or an injection path");

    public NativeInjectionResult Button(int button, bool down)
    {
        if (!Publishes(VirtualHidDeviceKind.Mouse))
        {
            return NativeInjectionResult.Unsupported("the Nexus HID mouse is not published");
        }

        var bit = button switch
        {
            0 => HidMouseButtons.Left,
            1 => HidMouseButtons.Right,
            2 => HidMouseButtons.Middle,
            3 => HidMouseButtons.Back,
            4 => HidMouseButtons.Forward,
            _ => HidMouseButtons.None,
        };

        if (bit == HidMouseButtons.None)
        {
            return NativeInjectionResult.Unsupported("unknown mouse button");
        }

        lock (_gate)
        {
            _buttons = down ? _buttons | bit : _buttons & ~bit;
            return Submit(new HidMouseReport(_buttons, 0, 0, 0, 0));
        }
    }

    public NativeInjectionResult Scroll(int horizontalNotches, int verticalNotches)
    {
        if (!Publishes(VirtualHidDeviceKind.Mouse))
        {
            return NativeInjectionResult.Unsupported("the Nexus HID mouse is not published");
        }

        var wheel = (sbyte)Math.Clamp(verticalNotches, sbyte.MinValue, sbyte.MaxValue);
        var pan = (sbyte)Math.Clamp(horizontalNotches, sbyte.MinValue, sbyte.MaxValue);

        lock (_gate)
        {
            return Submit(new HidMouseReport(_buttons, 0, 0, wheel, pan));
        }
    }

    /// <summary>
    /// Presses or releases one key, by rebuilding the whole keyboard report.
    /// <para>
    /// A boot report holds six keys. Pressing a seventh is refused rather than silently dropping one
    /// of the six: rollover is a property of real keyboards, and quietly replacing a held key would
    /// release something the owner is still holding.
    /// </para>
    /// </summary>
    public NativeInjectionResult Key(NexusKeyLike key, bool down)
    {
        if (!Publishes(VirtualHidDeviceKind.Keyboard))
        {
            return NativeInjectionResult.Unsupported("the Nexus HID keyboard is not published");
        }

        lock (_gate)
        {
            var modifier = HidKeyMap.Modifier(key);

            if (modifier is not null)
            {
                _modifiers = down ? _modifiers | modifier.Value : _modifiers & ~modifier.Value;
                return SubmitKeyboard();
            }

            var usage = HidKeyMap.Usage(key);

            if (usage == 0)
            {
                return NativeInjectionResult.Unsupported($"no HID usage for {key}");
            }

            if (down)
            {
                if (_keys.Contains(usage))
                {
                    return NativeInjectionResult.Ok;   // already held; a repeat is not a change
                }

                if (_keys.Count >= HidKeyboardReport.MaxKeys)
                {
                    return NativeInjectionResult.Unsupported("six keys are already held");
                }

                _keys.Add(usage);
            }
            else
            {
                _keys.Remove(usage);
            }

            return SubmitKeyboard();
        }
    }

    /// <summary>
    /// One touch contact, positioned as a fraction of the target display.
    /// <para>
    /// The lift is the part that goes wrong. A contact must send one final report with the tip clear
    /// at the position it last occupied before it stops reporting, or Windows keeps the contact alive
    /// and reads the next press as a continuation of this one.
    /// </para>
    /// </summary>
    public NativeInjectionResult Touch(HidTouchPhase phase, double fractionX, double fractionY)
    {
        if (!Publishes(VirtualHidDeviceKind.Digitizer))
        {
            return NativeInjectionResult.Unsupported("the Nexus HID digitizer is not published");
        }

        lock (_gate)
        {
            var scan = DigitizerSurface.ScanTime(_clock.Elapsed);

            switch (phase)
            {
                case HidTouchPhase.Down:
                case HidTouchPhase.Move:
                    (_lastX, _lastY) = DigitizerSurface.FromDisplayFraction(fractionX, fractionY);
                    _contactDown = true;
                    return Submit(new HidDigitizerReport(true, true, 0, _lastX, _lastY, scan));

                default:
                    // Up and Cancel are the same report: tip clear, out of range, at the last known
                    // position. Reporting a lift at a new position makes Windows infer a flick.
                    _contactDown = false;
                    return Submit(new HidDigitizerReport(false, false, 0, _lastX, _lastY, scan));
            }
        }
    }

    /// <summary>
    /// Releases everything through this backend, and reports whether the backend accepted it.
    /// <para>
    /// The return value is the whole point: ownership tracking needs to know whether the release was
    /// delivered or merely attempted, because a backend that has gone away leaves state nothing can
    /// clear. False here becomes <c>NEUTRALIZATION_UNCERTAIN</c> upstream rather than an assumption
    /// that all is well.
    /// </para>
    /// </summary>
    public bool Neutralize()
    {
        lock (_gate)
        {
            var ok = true;

            if (Publishes(VirtualHidDeviceKind.Mouse))
            {
                ok &= _backend.SubmitMouse(HidMouseReport.Neutral);
            }

            if (Publishes(VirtualHidDeviceKind.Keyboard))
            {
                ok &= _backend.SubmitKeyboard(HidKeyboardReport.Neutral);
            }

            if (Publishes(VirtualHidDeviceKind.Digitizer))
            {
                ok &= _backend.SubmitDigitizer(HidDigitizerReport.Neutral);
            }

            // Local state is cleared either way. Keeping it would mean a later report rebuilt from
            // stale state re-pressing something on a device that may not be the same device.
            _buttons = HidMouseButtons.None;
            _modifiers = HidKeyboardModifiers.None;
            _keys.Clear();
            _contactDown = false;

            return ok;
        }
    }

    private NativeInjectionResult SubmitKeyboard()
    {
        Span<byte> usages = stackalloc byte[HidKeyboardReport.MaxKeys];

        for (var i = 0; i < _keys.Count; i++)
        {
            usages[i] = _keys[i];
        }

        return _backend.SubmitKeyboard(new HidKeyboardReport(_modifiers, usages[.._keys.Count]))
            ? NativeInjectionResult.Ok
            : NativeInjectionResult.Failed("the Nexus HID backend refused a keyboard report");
    }

    private NativeInjectionResult Submit(in HidMouseReport report) =>
        _backend.SubmitMouse(report)
            ? NativeInjectionResult.Ok
            : NativeInjectionResult.Failed("the Nexus HID backend refused a mouse report");

    private NativeInjectionResult Submit(in HidDigitizerReport report) =>
        _backend.SubmitDigitizer(report)
            ? NativeInjectionResult.Ok
            : NativeInjectionResult.Failed("the Nexus HID backend refused a digitizer report");

    private static int CountBits(byte value) => System.Numerics.BitOperations.PopCount(value);
}
