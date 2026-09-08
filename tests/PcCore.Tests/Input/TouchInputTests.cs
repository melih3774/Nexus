using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Input;
using PcCore.Capabilities.Screen;
using PcCore.Capabilities.Security.Detection;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// Direct mode as touch: the mapping onto a display, the bounds, and what happens to a contact that
/// is never lifted.
/// </summary>
public class TouchInputTests
{
    private const string Owner = "conn-1";

    private static InputService Build(
        out RecordingInputController controller,
        IDisplayCatalog? displays = null)
    {
        controller = new RecordingInputController();

        return new InputService(
            controller,
            displays ?? new FakeDisplayCatalog(),
            new NullNexusInputAttribution(),
            new InputOptions(),
            TestLog.For<InputService>());
    }

    [Fact]
    public void TouchNeedsAnOpenInputSession()
    {
        var service = Build(out _);

        var refused = Assert.Throws<CapabilityException>(
            () => service.Touch(Owner, 0, 0, TouchAction.Down, 0.5, 0.5));

        Assert.Equal(ErrorCodes.NotAuthorized, refused.Code);
    }

    [Fact]
    public void ATouchMapsOntoTheDisplayItNames()
    {
        var service = Build(out var controller);
        service.Begin(Owner);

        service.Touch(Owner, 0, 0, TouchAction.Down, 0.5, 0.5);

        // 1920x1080 at the origin. The midpoint is 0.5 * (1920 - 1) = 959.5, which rounds to even
        // at 960 — the same arithmetic an absolute pointer move uses, deliberately, so a tap and a
        // pointer move at the same phone coordinate land on the same pixel.
        Assert.Equal("touch 0 Down 960,540", Assert.Single(controller.Events));
    }

    /// <summary>
    /// The multi-monitor case the prompt for this milestone called out by name: a second display
    /// whose origin is to the left of the primary has negative coordinates, and a hard-coded origin
    /// would put every Direct-mode touch on the wrong screen.
    /// </summary>
    [Fact]
    public void ATouchOnADisplayWithANegativeOriginLandsOnThatDisplay()
    {
        var displays = new FakeDisplayCatalog(
            new DisplayInfo(0, @"\\.\DISPLAY1", 0, 0, 1920, 1080, true),
            new DisplayInfo(1, @"\\.\DISPLAY2", -1600, -200, 1600, 900, false));

        var service = Build(out var controller, displays);
        service.Begin(Owner);

        service.Touch(Owner, 1, 0, TouchAction.Down, 0, 0);
        service.Touch(Owner, 1, 0, TouchAction.Move, 1, 1);

        Assert.Equal(
            ["touch 0 Down -1600,-200", "touch 0 Move -1,699"],
            controller.Events);
    }

    [Theory]
    [InlineData(-0.01, 0.5)]
    [InlineData(0.5, 1.01)]
    [InlineData(double.NaN, 0.5)]
    [InlineData(0.5, double.PositiveInfinity)]
    public void ATouchOutsideTheDisplayIsRefused(double x, double y)
    {
        var service = Build(out _);
        service.Begin(Owner);

        var refused = Assert.Throws<CapabilityException>(() => service.Touch(Owner, 0, 0, TouchAction.Down, x, y));

        Assert.Equal(ErrorCodes.InvalidParameters, refused.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    [InlineData(int.MaxValue)]
    public void AContactIdOutsideTheBoundIsRefused(int contactId)
    {
        var service = Build(out _);
        service.Begin(Owner);

        var refused = Assert.Throws<CapabilityException>(
            () => service.Touch(Owner, 0, contactId, TouchAction.Down, 0.5, 0.5));

        Assert.Equal(ErrorCodes.InvalidParameters, refused.Code);
    }

    [Fact]
    public void ATouchOnAnUnknownDisplayIsRefused()
    {
        var service = Build(out _);
        service.Begin(Owner);

        var refused = Assert.Throws<CapabilityException>(
            () => service.Touch(Owner, 7, 0, TouchAction.Down, 0.5, 0.5));

        Assert.Equal(ErrorCodes.InvalidParameters, refused.Code);
    }

    /// <summary>
    /// A contact left down when the connection drops is cancelled where it last was. Windows rejects
    /// a touch release whose position differs from the previous update, so releasing it at the wrong
    /// place would fail — and leave a finger pressed on the desktop with nothing left to lift it.
    /// </summary>
    [Fact]
    public void AContactLeftDownIsCancelledAtItsLastPosition()
    {
        var service = Build(out var controller);
        service.Begin(Owner);

        service.Touch(Owner, 0, 0, TouchAction.Down, 0.25, 0.25);
        service.Touch(Owner, 0, 0, TouchAction.Move, 0.75, 0.5);
        controller.Events.Clear();

        var released = service.ReleaseAll(Owner);

        Assert.Equal(1, released);
        Assert.Equal("touch 0 Cancel 1439,540", controller.Events[0]);
    }

    [Fact]
    public void ALiftedContactIsNotReleasedAgain()
    {
        var service = Build(out var controller);
        service.Begin(Owner);

        service.Touch(Owner, 0, 0, TouchAction.Down, 0.5, 0.5);
        service.Touch(Owner, 0, 0, TouchAction.Up, 0.5, 0.5);
        controller.Events.Clear();

        service.ReleaseAll(Owner);

        Assert.DoesNotContain(controller.Events, e => e.StartsWith("touch", StringComparison.Ordinal));
    }

    /// <summary>
    /// Contacts come up before buttons and keys. A live contact makes Windows treat pointer state as
    /// still in progress, so releasing the other two first can leave a gesture half-finished.
    /// </summary>
    [Fact]
    public void ContactsAreReleasedBeforeButtonsAndKeys()
    {
        var service = Build(out var controller);
        service.Begin(Owner);

        service.Touch(Owner, 0, 0, TouchAction.Down, 0.5, 0.5);
        service.Button(Owner, PointerButton.Left, ButtonAction.Down);
        service.Key(Owner, NexusKey.Control, KeyAction.Down);
        controller.Events.Clear();

        service.ReleaseAll(Owner);

        var contact = controller.Events.FindIndex(e => e.StartsWith("touch", StringComparison.Ordinal));
        var button = controller.Events.IndexOf("button Left up");
        var key = controller.Events.IndexOf("key Control up");

        Assert.True(contact < button, "the contact was not cancelled before the button");
        Assert.True(button < key, "the button was not released before the modifier");
    }

    /// <summary>
    /// When the platform cannot inject touch, the contact becomes a pointer and a button — and
    /// crucially a <em>move</em> presses nothing. Reintroducing that would bring back the bug where
    /// every swipe in Direct mode dragged a selection box across the desktop.
    /// </summary>
    [Fact]
    public void WithoutTouchSupportAMoveDoesNotHoldTheButton()
    {
        var controller = new RecordingInputController { TouchAvailable = false };
        var service = new InputService(
            controller,
            new FakeDisplayCatalog(),
            new NullNexusInputAttribution(),
            new InputOptions(),
            TestLog.For<InputService>());

        service.Begin(Owner);

        // The service asks the controller for touch; the controller reports it unsupported. The
        // fallback lives in the Windows layer, so here the refusal is simply visible and no button
        // is pressed by the service itself.
        var result = service.Touch(Owner, 0, 0, TouchAction.Move, 0.5, 0.5);

        Assert.Equal(InputDelivery.Unsupported, result.Delivery);
        Assert.DoesNotContain(controller.Events, e => e.StartsWith("button", StringComparison.Ordinal));
    }

    [Fact]
    public void TheStatusComesFromTheController()
    {
        var service = Build(out var controller);

        controller.Status = new NativeInputStatus
        {
            Path = InputPath.NativeBroker,
            State = NativeInputState.Ready,
            ReachesElevatedWindows = true,
            TouchAvailable = true,
            PhysicalDigitizer = false,
            Desktop = "DEFAULT",
            BrokerVersion = "9.9.9",
        };

        Assert.Equal(InputPath.NativeBroker, service.Status.Path);
        Assert.Equal("Native input: ready", service.Status.Summary);
        Assert.False(service.Status.SecureDesktopActive);
    }

    [Fact]
    public void ASecureDesktopIsVisibleInTheStatus()
    {
        var service = Build(out var controller);

        controller.Status = controller.Status with { Desktop = "SECURE" };

        Assert.True(service.Status.SecureDesktopActive);
    }
}
