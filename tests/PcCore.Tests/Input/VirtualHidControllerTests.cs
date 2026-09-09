using PcCore.Capabilities.Input;
using PcCore.NativeInput;
using PcCore.NativeInput.Hid;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// A backend that records what was submitted instead of publishing devices.
/// </summary>
internal sealed class RecordingHidBackend : IVirtualHidBackend
{
    private readonly VirtualHidAvailability _availability;

    public RecordingHidBackend(params VirtualHidDeviceKind[] kinds)
    {
        _availability = new VirtualHidAvailability
        {
            State = VirtualHidState.Ready,
            Devices = kinds.Select(k => new VirtualHidDeviceIdentity
            {
                Kind = k,
                DevicePath = $@"\\?\HID#VID_F055&PID_000{(int)k + 1}#test",
            }).ToArray(),
        };
    }

    public List<HidMouseReport> Mouse { get; } = [];

    public List<HidKeyboardReport> Keyboard { get; } = [];

    public List<HidDigitizerReport> Digitizer { get; } = [];

    /// <summary>Set to make every submission fail, as a vanished driver would.</summary>
    public bool Refuse { get; set; }

    public VirtualHidAvailability Availability => _availability;

    public VirtualHidAvailability Probe() => _availability;

    public bool SubmitMouse(in HidMouseReport report)
    {
        if (Refuse) { return false; }
        Mouse.Add(report);
        return true;
    }

    public bool SubmitKeyboard(in HidKeyboardReport report)
    {
        if (Refuse) { return false; }
        Keyboard.Add(report);
        return true;
    }

    public bool SubmitDigitizer(in HidDigitizerReport report)
    {
        if (Refuse) { return false; }
        Digitizer.Add(report);
        return true;
    }

    public void Neutralize()
    {
    }

    public void Shutdown() => ShutdownCount++;

    /// <summary>How many times the backend was told to release and disconnect.</summary>
    public int ShutdownCount { get; private set; }

    public void Dispose()
    {
    }
}

/// <summary>
/// The HID submission path: input operations to reports, and the state that has to be carried
/// between them because a HID report describes a device rather than an event.
/// </summary>
public sealed class VirtualHidControllerTests
{
    private static VirtualHidController All(out RecordingHidBackend backend)
    {
        backend = new RecordingHidBackend(
            VirtualHidDeviceKind.Mouse, VirtualHidDeviceKind.Keyboard, VirtualHidDeviceKind.Digitizer);

        return new VirtualHidController(backend);
    }

    // --- the mirrored enum ----------------------------------------------------------------------

    /// <summary>
    /// NexusKeyLike mirrors NexusKey so the dependency-free project can map keys. A mirror that
    /// drifts maps every key past the drift to the wrong one, which produces garbage rather than an
    /// error, so the two are compared name for name and position for position.
    /// </summary>
    [Fact]
    public void HidKeyMapMirrorsNexusKey()
    {
        var real = Enum.GetNames<NexusKey>();
        var mirror = Enum.GetNames<NexusKeyLike>();

        Assert.Equal(real.Length, mirror.Length);

        for (var i = 0; i < real.Length; i++)
        {
            Assert.Equal(real[i], mirror[i]);
            Assert.Equal((int)Enum.Parse<NexusKey>(real[i]), (int)Enum.Parse<NexusKeyLike>(mirror[i]));
        }
    }

    [Fact]
    public void EveryNexusKeyIsExpressibleAsHid()
    {
        foreach (var key in Enum.GetValues<NexusKeyLike>())
        {
            Assert.True(HidKeyMap.IsMappable(key), $"{key} has no HID usage or modifier");
        }
    }

    /// <summary>Digit0 is 0x27, after the nines, not before the ones. A HID quirk worth pinning.</summary>
    [Fact]
    public void DigitUsagesFollowTheHidOrderNotArithmetic()
    {
        Assert.Equal(0x1E, HidKeyMap.Usage(NexusKeyLike.Digit1));
        Assert.Equal(0x26, HidKeyMap.Usage(NexusKeyLike.Digit9));
        Assert.Equal(0x27, HidKeyMap.Usage(NexusKeyLike.Digit0));
    }

    /// <summary>Modifiers are bits, never key-array entries. Mixing them silently does nothing.</summary>
    [Fact]
    public void ModifiersAreBitsAndNotUsages()
    {
        Assert.NotNull(HidKeyMap.Modifier(NexusKeyLike.Control));
        Assert.Equal(0, HidKeyMap.Usage(NexusKeyLike.Control));

        Assert.Null(HidKeyMap.Modifier(NexusKeyLike.A));
        Assert.NotEqual(0, HidKeyMap.Usage(NexusKeyLike.A));
    }

    // --- mouse ----------------------------------------------------------------------------------

    [Fact]
    public void RelativeMovementBecomesAMouseReport()
    {
        var controller = All(out var backend);

        controller.MoveRelative(30, -20);

        var report = Assert.Single(backend.Mouse);
        Assert.Equal(30, report.DeltaX);
        Assert.Equal(-20, report.DeltaY);
        Assert.Equal(HidMouseButtons.None, report.Buttons);
    }

    /// <summary>
    /// A HID report is device state, not an event, so a move while a button is held must keep
    /// reporting the button. Dropping it would release the drag mid-gesture.
    /// </summary>
    [Fact]
    public void MovementWhileAButtonIsHeldKeepsReportingTheButton()
    {
        var controller = All(out var backend);

        controller.Button(0, down: true);
        controller.MoveRelative(5, 5);

        Assert.Equal(HidMouseButtons.Left, backend.Mouse[^1].Buttons);
    }

    [Fact]
    public void ButtonsAccumulateAndRelease()
    {
        var controller = All(out var backend);

        controller.Button(0, true);
        controller.Button(1, true);
        Assert.Equal(HidMouseButtons.Left | HidMouseButtons.Right, backend.Mouse[^1].Buttons);

        controller.Button(0, false);
        Assert.Equal(HidMouseButtons.Right, backend.Mouse[^1].Buttons);
    }

    [Fact]
    public void ScrollUsesWheelAndPanSeparately()
    {
        var controller = All(out var backend);

        controller.Scroll(horizontalNotches: -2, verticalNotches: 3);

        Assert.Equal(3, backend.Mouse[^1].Wheel);
        Assert.Equal(-2, backend.Mouse[^1].Pan);
    }

    /// <summary>Saturating rather than wrapping: a wrapped delta sends the pointer the other way.</summary>
    [Fact]
    public void HugeDeltasSaturateRatherThanWrap()
    {
        var controller = All(out var backend);

        controller.MoveRelative(int.MaxValue, int.MinValue);

        Assert.Equal(short.MaxValue, backend.Mouse[^1].DeltaX);
        Assert.Equal(short.MinValue, backend.Mouse[^1].DeltaY);
    }

    /// <summary>A relative mouse cannot express absolute position, and says so rather than guessing.</summary>
    [Fact]
    public void AbsoluteMovementIsUnsupportedSoTheRouterCanFallBack()
    {
        Assert.Equal(NativeDelivery.Unsupported, VirtualHidController.MoveAbsolute().Delivery);
    }

    // --- keyboard -------------------------------------------------------------------------------

    [Fact]
    public void KeysAccumulateInTheReport()
    {
        var controller = All(out var backend);

        controller.Key(NexusKeyLike.A, true);
        controller.Key(NexusKeyLike.B, true);

        Span<byte> buffer = stackalloc byte[HidReportDescriptors.KeyboardReportLength];
        backend.Keyboard[^1].Write(buffer);

        Assert.Equal(HidKeyMap.Usage(NexusKeyLike.A), buffer[2]);
        Assert.Equal(HidKeyMap.Usage(NexusKeyLike.B), buffer[3]);
    }

    [Fact]
    public void ReleasingOneKeyKeepsTheOther()
    {
        var controller = All(out var backend);

        controller.Key(NexusKeyLike.A, true);
        controller.Key(NexusKeyLike.B, true);
        controller.Key(NexusKeyLike.A, false);

        Span<byte> buffer = stackalloc byte[HidReportDescriptors.KeyboardReportLength];
        backend.Keyboard[^1].Write(buffer);

        Assert.Equal(HidKeyMap.Usage(NexusKeyLike.B), buffer[2]);
        Assert.Equal(0, buffer[3]);
    }

    [Fact]
    public void ModifiersRideInTheModifierByte()
    {
        var controller = All(out var backend);

        controller.Key(NexusKeyLike.Control, true);
        controller.Key(NexusKeyLike.C, true);

        Assert.Equal(HidKeyboardModifiers.LeftControl, backend.Keyboard[^1].Modifiers);
    }

    /// <summary>
    /// Rollover is refused rather than silently dropping a held key, which would release something
    /// the owner is still holding.
    /// </summary>
    [Fact]
    public void ASeventhKeyIsRefusedRatherThanReplacingAHeldOne()
    {
        var controller = All(out _);

        foreach (var key in new[]
        {
            NexusKeyLike.A, NexusKeyLike.B, NexusKeyLike.C,
            NexusKeyLike.D, NexusKeyLike.E, NexusKeyLike.F,
        })
        {
            Assert.Equal(NativeDelivery.Delivered, controller.Key(key, true).Delivery);
        }

        Assert.Equal(NativeDelivery.Unsupported, controller.Key(NexusKeyLike.G, true).Delivery);
    }

    [Fact]
    public void ARepeatedKeyDownIsNotAStateChange()
    {
        var controller = All(out var backend);

        controller.Key(NexusKeyLike.A, true);
        var after = backend.Keyboard.Count;
        controller.Key(NexusKeyLike.A, true);

        Assert.Equal(after, backend.Keyboard.Count);
    }

    // --- touch ----------------------------------------------------------------------------------

    [Fact]
    public void TouchDownReportsTipAndRange()
    {
        var controller = All(out var backend);

        controller.Touch(HidTouchPhase.Down, 0.5, 0.25);

        var report = backend.Digitizer[^1];
        Assert.True(report.TipSwitch);
        Assert.True(report.InRange);
        Assert.InRange(report.X, (DigitizerSurface.LogicalMaximum / 2) - 2, (DigitizerSurface.LogicalMaximum / 2) + 2);
    }

    /// <summary>
    /// The lift must report at the last position with the tip clear. Reporting elsewhere makes
    /// Windows infer a flick; not reporting at all leaves the contact alive.
    /// </summary>
    [Fact]
    public void TouchUpClearsTheTipAtTheLastPosition()
    {
        var controller = All(out var backend);

        controller.Touch(HidTouchPhase.Down, 0.75, 0.75);
        var downX = backend.Digitizer[^1].X;

        controller.Touch(HidTouchPhase.Up, 0.1, 0.1);

        var up = backend.Digitizer[^1];
        Assert.False(up.TipSwitch);
        Assert.False(up.InRange);
        Assert.Equal(downX, up.X);
    }

    [Fact]
    public void CancelBehavesLikeUp()
    {
        var controller = All(out var backend);

        controller.Touch(HidTouchPhase.Down, 0.5, 0.5);
        controller.Touch(HidTouchPhase.Cancel, 0.5, 0.5);

        Assert.False(backend.Digitizer[^1].TipSwitch);
    }

    // --- partial capability and neutralisation --------------------------------------------------

    /// <summary>A backend with no digitizer refuses touch and says why, so the router falls back.</summary>
    [Fact]
    public void MissingCapabilitiesAreUnsupportedNotFailures()
    {
        var backend = new RecordingHidBackend(VirtualHidDeviceKind.Mouse);
        var controller = new VirtualHidController(backend);

        Assert.Equal(NativeDelivery.Delivered, controller.MoveRelative(1, 1).Delivery);
        Assert.Equal(NativeDelivery.Unsupported, controller.Key(NexusKeyLike.A, true).Delivery);
        Assert.Equal(NativeDelivery.Unsupported, controller.Touch(HidTouchPhase.Down, 0.5, 0.5).Delivery);
    }

    [Fact]
    public void NeutralizeReleasesEverythingAndReportsSuccess()
    {
        var controller = All(out var backend);

        controller.Button(0, true);
        controller.Key(NexusKeyLike.Control, true);
        controller.Touch(HidTouchPhase.Down, 0.5, 0.5);

        Assert.True(controller.Neutralize());

        Assert.Equal(HidMouseButtons.None, backend.Mouse[^1].Buttons);
        Assert.Equal(HidKeyboardModifiers.None, backend.Keyboard[^1].Modifiers);
        Assert.False(backend.Digitizer[^1].TipSwitch);

        var held = controller.Held;
        Assert.Equal(0, held.Buttons);
        Assert.Equal(0, held.Keys);
        Assert.False(held.Contact);
    }

    /// <summary>
    /// A backend that refuses reports is one that has gone away. Neutralize must say so rather than
    /// claim success, because that false becomes NEUTRALIZATION_UNCERTAIN upstream.
    /// </summary>
    [Fact]
    public void NeutralizeReportsFailureWhenTheBackendRefuses()
    {
        var controller = All(out var backend);
        controller.Button(0, true);

        backend.Refuse = true;

        Assert.False(controller.Neutralize());

        // Local state is cleared regardless: rebuilding a later report from stale state would
        // re-press something on what may be a different device.
        Assert.Equal(0, controller.Held.Buttons);
    }
}
