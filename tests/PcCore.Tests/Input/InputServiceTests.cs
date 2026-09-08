using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Input;
using PcCore.Capabilities.Screen;
using PcCore.Capabilities.Security.Detection;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// The gate between "a phone is connected" and "a phone may type".
/// <para>
/// Two things here matter more than the rest. Coordinate conversion, because a mistake in it puts
/// clicks somewhere the owner did not touch and would be nearly impossible to notice in a
/// screenshot; and held-state cleanup, because the failure it prevents — a Ctrl or a mouse button
/// left down on a machine nobody is at any more — outlives the session that caused it.
/// </para>
/// </summary>
public sealed class InputServiceTests
{
    private const string Owner = "conn-1";

    private static InputService Build(
        out RecordingInputController controller,
        IDisplayCatalog? displays = null,
        InputOptions? options = null,
        INexusInputAttribution? attribution = null)
    {
        controller = new RecordingInputController();

        return new InputService(
            controller,
            displays ?? new FakeDisplayCatalog(),
            attribution ?? new NullNexusInputAttribution(),
            options ?? new InputOptions(),
            TestLog.For<InputService>());
    }

    // --- The session gate ---------------------------------------------------------------------------

    [Fact]
    public void InputIsRefusedUntilTheOwnerOpensTheControlSurface()
    {
        // The core rule of the milestone: an authenticated phone that has not opened the control
        // surface cannot move the pointer, and being connected is not a substitute for asking.
        var service = Build(out var controller);

        var refused = Assert.Throws<CapabilityException>(() => service.MoveAbsolute(Owner, 0, 0.5, 0.5));

        Assert.Equal(ErrorCodes.NotAuthorized, refused.Code);
        Assert.Empty(controller.Events);
    }

    [Fact]
    public void OneConnectionCannotUseAnothersInputSession()
    {
        var service = Build(out _);
        service.Begin("conn-a");

        Assert.Throws<CapabilityException>(() => service.MoveAbsolute("conn-b", 0, 0.5, 0.5));
    }

    [Fact]
    public void BeginningTwiceReturnsTheSameSession()
    {
        var service = Build(out _);

        var first = service.Begin(Owner);
        var second = service.Begin(Owner);

        Assert.Equal(first.StartedAtUtc, second.StartedAtUtc);
        Assert.Single(service.Active);
    }

    [Fact]
    public void EndingSomethingThatWasNeverStartedIsNotAnError()
    {
        var service = Build(out _);

        Assert.False(service.End(Owner));
        Assert.Equal(0, service.ReleaseAll(Owner));
    }

    [Fact]
    public void ActivityIsAnnouncedSoTheIndicatorCanFollowIt()
    {
        var service = Build(out _);
        var announced = new List<int>();

        service.ActivityChanged += activity => announced.Add(activity.ActiveSessions);

        service.Begin(Owner);
        service.End(Owner);

        Assert.Equal([1, 0], announced);
    }

    // --- Absolute coordinates -------------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 0.0, 0, 0)]
    [InlineData(1.0, 1.0, 1919, 1079)]
    [InlineData(0.5, 0.5, 960, 540)]
    public void NormalisedPositionsMapOntoTheDisplay(double x, double y, int expectedX, int expectedY)
    {
        // The phone sends a fraction of the display, never pixels: it has no business knowing about
        // DPI scaling or where Windows put this monitor. The far corner has to land on the last
        // pixel rather than one past it, which is what the -1 in the conversion is for.
        var service = Build(out var controller);
        service.Begin(Owner);

        service.MoveAbsolute(Owner, 0, x, y);

        Assert.Equal($"move-absolute {expectedX},{expectedY}", controller.Events[^1]);
    }

    [Fact]
    public void APositionOnASecondMonitorIncludesItsOrigin()
    {
        // A monitor left of the primary has a negative origin. Dropping it would send every click
        // meant for that screen to the primary instead.
        var displays = new FakeDisplayCatalog(
            new DisplayInfo(0, "primary", 0, 0, 1920, 1080, true),
            new DisplayInfo(1, "left", -2560, -200, 2560, 1440, false));

        var service = Build(out var controller, displays);
        service.Begin(Owner);

        service.MoveAbsolute(Owner, 1, 0.0, 0.0);
        Assert.Equal("move-absolute -2560,-200", controller.Events[^1]);

        service.MoveAbsolute(Owner, 1, 1.0, 1.0);
        Assert.Equal("move-absolute -1,1239", controller.Events[^1]);
    }

    [Theory]
    [InlineData(-0.01, 0.5)]
    [InlineData(1.01, 0.5)]
    [InlineData(0.5, -0.5)]
    [InlineData(0.5, 2.0)]
    [InlineData(double.NaN, 0.5)]
    [InlineData(0.5, double.PositiveInfinity)]
    public void APositionOutsideTheDisplayIsRefusedRatherThanClamped(double x, double y)
    {
        // Clamping would move the pointer to the nearest edge and hide the client bug that produced
        // the value. A touch outside the rendered desktop must do nothing at all.
        var service = Build(out var controller);
        service.Begin(Owner);

        var refused = Assert.Throws<CapabilityException>(() => service.MoveAbsolute(Owner, 0, x, y));

        Assert.Equal(ErrorCodes.InvalidParameters, refused.Code);
        Assert.Empty(controller.Events);
    }

    [Fact]
    public void AnUnknownDisplayIsRefused()
    {
        var service = Build(out _);
        service.Begin(Owner);

        var refused = Assert.Throws<CapabilityException>(() => service.MoveAbsolute(Owner, 9, 0.5, 0.5));
        Assert.Equal(ErrorCodes.InvalidParameters, refused.Code);
    }

    // --- Relative movement ----------------------------------------------------------------------------

    [Fact]
    public void RelativeMovementPassesThroughRounded()
    {
        var service = Build(out var controller);
        service.Begin(Owner);

        service.MoveRelative(Owner, 12.4, -7.6);

        Assert.Equal("move-relative 12,-8", controller.Events[^1]);
    }

    [Fact]
    public void AnAbsurdRelativeDeltaIsClamped()
    {
        // Unlike an absolute position, a large delta has a sensible interpretation — a fast flick —
        // so it is bounded rather than refused.
        var service = Build(out var controller, options: new InputOptions { MaxRelativeDelta = 100 });
        service.Begin(Owner);

        service.MoveRelative(Owner, 999_999, -999_999);

        Assert.Equal("move-relative 100,-100", controller.Events[^1]);
    }

    [Fact]
    public void ANonFiniteDeltaIsRefused()
    {
        var service = Build(out _);
        service.Begin(Owner);

        Assert.Throws<CapabilityException>(() => service.MoveRelative(Owner, double.NaN, 0));
    }

    // --- Buttons, scroll, keys ------------------------------------------------------------------------

    [Fact]
    public void AClickIsADownAndAnUp()
    {
        var service = Build(out var controller);
        service.Begin(Owner);

        service.Button(Owner, PointerButton.Left, ButtonAction.Click);

        Assert.Equal(["button Left down", "button Left up"], controller.Events);
    }

    [Fact]
    public void ADoubleClickIsTwoOfThem()
    {
        var service = Build(out var controller);
        service.Begin(Owner);

        service.Button(Owner, PointerButton.Left, ButtonAction.DoubleClick);

        Assert.Equal(4, controller.Events.Count);
    }

    [Fact]
    public void ScrollIsBoundedInBothDirections()
    {
        var service = Build(out var controller, options: new InputOptions { MaxScrollNotches = 5 });
        service.Begin(Owner);

        service.Scroll(Owner, 99, -99);

        Assert.Equal("scroll 5,-5", controller.Events[^1]);
    }

    [Fact]
    public void APressIsADownAndAnUp()
    {
        var service = Build(out var controller);
        service.Begin(Owner);

        service.Key(Owner, NexusKey.Enter, KeyAction.Press);

        Assert.Equal(["key Enter down", "key Enter up"], controller.Events);
    }

    [Fact]
    public void AnUndefinedKeyValueIsRefused()
    {
        // A value cast in from the wire that is not a real member. This is the check that stops
        // arbitrary numbers reaching a Win32 structure.
        var service = Build(out var controller);
        service.Begin(Owner);

        Assert.Throws<CapabilityException>(() => service.Key(Owner, (NexusKey)9999, KeyAction.Press));
        Assert.Empty(controller.Events);
    }

    // --- Text ------------------------------------------------------------------------------------------

    [Fact]
    public void UnicodeTextPassesThroughIntact()
    {
        var service = Build(out var controller);
        service.Begin(Owner);

        service.Text(Owner, "merhaba dünya 🙂");

        Assert.Equal("text merhaba dünya 🙂", controller.Events[^1]);
    }

    [Fact]
    public void OverlongTextIsRefused()
    {
        var service = Build(out _, options: new InputOptions { MaxTextLength = 8 });
        service.Begin(Owner);

        var refused = Assert.Throws<CapabilityException>(() => service.Text(Owner, new string('a', 9)));
        Assert.Equal(ErrorCodes.InvalidParameters, refused.Code);
    }

    [Fact]
    public void ControlCharactersInTextAreRefused()
    {
        // An escape or a form feed arriving as "text" is either a bug or an attempt to reach a key
        // that has its own action. Tab and newline are ordinary typing and stay allowed.
        var service = Build(out _);
        service.Begin(Owner);

        Assert.Throws<CapabilityException>(() => service.Text(Owner, "helloworld"));
        service.Text(Owner, "line\tone\n");
    }

    [Fact]
    public void EmptyTextIsAcceptedAndDoesNothing()
    {
        var service = Build(out var controller);
        service.Begin(Owner);

        service.Text(Owner, string.Empty);

        Assert.Empty(controller.Events);
    }

    // --- Held state ---------------------------------------------------------------------------------------

    [Fact]
    public void EndingASessionReleasesEverythingItHeld()
    {
        // The scenario this exists for: a drag with a modifier held, interrupted. Without this the
        // machine is left believing both are still down.
        var service = Build(out var controller);
        service.Begin(Owner);

        service.Button(Owner, PointerButton.Left, ButtonAction.Down);
        service.Key(Owner, NexusKey.Control, KeyAction.Down);
        service.Key(Owner, NexusKey.Shift, KeyAction.Down);

        controller.Events.Clear();
        service.End(Owner);

        Assert.Contains("button Left up", controller.Events);
        Assert.Contains("key Control up", controller.Events);
        Assert.Contains("key Shift up", controller.Events);

        // Buttons are released before keys: letting a modifier go first can turn an in-flight drag
        // into a shortcut on the way out.
        Assert.True(
            controller.Events.IndexOf("button Left up") < controller.Events.IndexOf("key Control up"),
            "the held button was not released before the modifiers");
    }

    [Fact]
    public void ReleasingWhatWasAlreadyReleasedDoesNothing()
    {
        var service = Build(out var controller);
        service.Begin(Owner);

        service.Button(Owner, PointerButton.Left, ButtonAction.Down);
        service.Button(Owner, PointerButton.Left, ButtonAction.Up);

        controller.Events.Clear();

        Assert.Equal(0, service.ReleaseAll(Owner));

        // The platform is still asked to let go of anything it believes is held — the session's
        // bookkeeping and Windows' can disagree after a broker restart, and only the controller
        // knows the second. What must not happen is a button or key being pressed or released again.
        Assert.Equal(["release-all-held"], controller.Events);
    }

    [Fact]
    public void AClickDoesNotLeaveAButtonMarkedAsHeld()
    {
        var service = Build(out var controller);
        service.Begin(Owner);

        service.Button(Owner, PointerButton.Left, ButtonAction.Click);
        controller.Events.Clear();

        Assert.Equal(0, service.ReleaseAll(Owner));
    }

    [Fact]
    public void ReleaseAllLeavesTheSessionOpen()
    {
        // The phone calls this whenever a gesture is interrupted. It must not have to re-open the
        // control surface afterwards.
        var service = Build(out _);
        service.Begin(Owner);
        service.Key(Owner, NexusKey.Alt, KeyAction.Down);

        Assert.Equal(1, service.ReleaseAll(Owner));
        Assert.True(service.IsActive(Owner));
    }

    [Fact]
    public void AKeyWindowsRefusedIsNotRecordedAsHeld()
    {
        // If the down never landed, releasing it later would send a stray key-up to whatever has
        // focus at that point.
        var service = Build(out var controller);
        service.Begin(Owner);

        controller.BlockedByIntegrity = true;
        service.Key(Owner, NexusKey.Control, KeyAction.Down);

        controller.BlockedByIntegrity = false;
        controller.Events.Clear();

        Assert.Equal(0, service.ReleaseAll(Owner));
    }

    // --- Windows refusals ----------------------------------------------------------------------------------

    [Fact]
    public void AnIntegrityRefusalIsReportedRatherThanThrown()
    {
        // Not an error: PC Core runs as a standard user and Windows will not let it type into an
        // elevated window. The phone needs to be told, not to see an exception.
        var service = Build(out var controller);
        service.Begin(Owner);
        controller.BlockedByIntegrity = true;

        var result = service.Button(Owner, PointerButton.Left, ButtonAction.Click);

        Assert.False(result.Succeeded);
        Assert.Equal(InputDelivery.BlockedByIntegrity, result.Delivery);
        Assert.NotNull(result.Diagnostic);
    }

    // --- Rate limiting -------------------------------------------------------------------------------------

    [Fact]
    public void ARunawayClientIsRateLimited()
    {
        var service = Build(
            out _,
            options: new InputOptions { MaxEventsPerSecond = 10, BurstEvents = 5 });

        service.Begin(Owner);

        for (var i = 0; i < 5; i++)
        {
            service.MoveRelative(Owner, 1, 1);
        }

        var refused = Assert.Throws<CapabilityException>(() => service.MoveRelative(Owner, 1, 1));

        // A distinct code from not_authorized: this caller may do this, just not this fast.
        Assert.Equal(ErrorCodes.RateLimited, refused.Code);
    }

    [Fact]
    public void TheBudgetRefillsOverTime()
    {
        // Twenty events a second is one token every fifty milliseconds. Deliberately slow: at a
        // thousand a second the bucket refilled within the microseconds between two calls, so the
        // refusal this test is about depended on how fast the machine happened to be that day.
        var service = Build(
            out _,
            options: new InputOptions { MaxEventsPerSecond = 20, BurstEvents = 2 });

        service.Begin(Owner);
        service.MoveRelative(Owner, 1, 1);
        service.MoveRelative(Owner, 1, 1);

        Assert.Throws<CapabilityException>(() => service.MoveRelative(Owner, 1, 1));

        Thread.Sleep(150);
        service.MoveRelative(Owner, 1, 1);
    }

    [Fact]
    public void EachSessionHasItsOwnBudget()
    {
        var service = Build(out _, options: new InputOptions { MaxEventsPerSecond = 10, BurstEvents = 2 });

        service.Begin("conn-a");
        service.Begin("conn-b");

        service.MoveRelative("conn-a", 1, 1);
        service.MoveRelative("conn-a", 1, 1);
        Assert.Throws<CapabilityException>(() => service.MoveRelative("conn-a", 1, 1));

        service.MoveRelative("conn-b", 1, 1);
    }
}
