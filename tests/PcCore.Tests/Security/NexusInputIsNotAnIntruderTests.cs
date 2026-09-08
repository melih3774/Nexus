using Microsoft.Extensions.Logging.Abstractions;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Input;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.Security.Detection;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;

using Xunit;

namespace PcCore.Tests.Security;

/// <summary>
/// The invariant the whole native-input change had to preserve.
/// <para>
/// <b>Physical local input while ARMED raises a security session. Nexus's own input while ARMED does
/// not.</b> Before native input there was one rule holding that up — an exact signature in
/// <c>dwExtraInfo</c> that only Nexus writes. Touch injection has nowhere to put that signature, so
/// there are now two rules, and these tests exercise both and the interaction between them.
/// </para>
/// <para>
/// The detector is scripted rather than real here, so the security decision can be tested without a
/// desktop. The real hooks are exercised by <c>PcCore.Host --selftest-native-input</c>, which arms a
/// sandboxed state service and injects through the live Windows path.
/// </para>
/// </summary>
public class NexusInputIsNotAnIntruderTests : IDisposable
{
    private readonly TempStateDirectory _state = new("nexus-input-attribution");
    private readonly ScriptedActivityDetector _detector = new();
    private readonly AdvanceableTimeProvider _time = new();
    private readonly EventBus _events;
    private readonly SecurityStateService _stateService;
    private readonly NexusInputAttribution _attribution;
    private readonly SecurityActivityMonitor _monitor;

    private int _incidents;

    public NexusInputIsNotAnIntruderTests()
    {
        _events = new EventBus(NullLogger<EventBus>.Instance);
        _stateService = new SecurityStateService(_events, _state.Directory, TestLog.For<SecurityStateService>());

        _attribution = new NexusInputAttribution(
            new AttributionOptions { WindowMilliseconds = 400 },
            TestLog.For<NexusInputAttribution>(),
            _time);

        _monitor = new SecurityActivityMonitor(
            _detector,
            _attribution,
            _stateService,
            _events,
            new DetectionOptions { GraceSeconds = 0, PointerMovementThreshold = 0, CoalesceMilliseconds = 0 },
            TestLog.For<SecurityActivityMonitor>(),
            _time);

        _monitor.IncidentDetected += (_, _) => _incidents++;
        _monitor.Start();

        _stateService.RequestTransition(SecurityState.Armed, "test");
    }

    private void Observe(ActivityKind kind, ActivitySource source) =>
        _detector.Emit(new LocalActivity(kind, source, _time.GetUtcNow()));

    // --- Rule one: the signature ---------------------------------------------------------------------

    [Fact]
    public void InputCarryingNexusOwnSignatureNeverRaisesASession()
    {
        for (var i = 0; i < 20; i++)
        {
            Observe(ActivityKind.PointerActivity, ActivitySource.NexusRemote);
            Observe(ActivityKind.KeyboardActivity, ActivitySource.NexusRemote);
        }

        Assert.Equal(0, _incidents);
        Assert.Equal(SecurityState.Armed, _stateService.Current.State);
        Assert.Equal(40, _monitor.IgnoredNexusInput);
    }

    [Fact]
    public void APersonAtTheKeyboardRaisesASession()
    {
        Observe(ActivityKind.KeyboardActivity, ActivitySource.Local);

        Assert.Equal(1, _incidents);
    }

    // --- Rule two: the ledger, for the paths with no signature ------------------------------------

    /// <summary>
    /// The case touch injection creates. The observation looks local — Windows promoted it from a
    /// touch contact and stamped its own pen/touch signature, not Nexus's — but Nexus declared it,
    /// so it is Nexus's.
    /// </summary>
    [Fact]
    public void InputNexusDeclaredIsNotAPersonEvenWhenItLooksLikeOne()
    {
        _attribution.Expect(ActivityKind.PointerActivity, 3);

        Observe(ActivityKind.PointerActivity, ActivitySource.Local);
        Observe(ActivityKind.PointerActivity, ActivitySource.Local);
        Observe(ActivityKind.PointerActivity, ActivitySource.Local);

        Assert.Equal(0, _incidents);
        Assert.Equal(SecurityState.Armed, _stateService.Current.State);
        Assert.Equal(3, _attribution.Claimed);
    }

    /// <summary>
    /// The failure mode that would matter most: the ledger absorbing more than Nexus caused, and so
    /// covering for an intruder who happened to move the mouse at the same moment.
    /// </summary>
    [Fact]
    public void OneMoreObservationThanNexusCausedStillRaisesASession()
    {
        _attribution.Expect(ActivityKind.PointerActivity, 2);

        Observe(ActivityKind.PointerActivity, ActivitySource.Local);
        Observe(ActivityKind.PointerActivity, ActivitySource.Local);

        Assert.Equal(0, _incidents);

        Observe(ActivityKind.PointerActivity, ActivitySource.Local);

        // The monitor raises the incident; moving the state to SECURITY_SESSION is
        // SecurityIncidentService's job, and it is not wired up here. What this test is about is
        // which observations the monitor decides are a person.
        Assert.Equal(1, _incidents);
    }

    [Fact]
    public void TheLedgerStopsCoveringOnceItsClaimsExpire()
    {
        _attribution.Expect(ActivityKind.PointerActivity, 5);

        _time.Advance(TimeSpan.FromMilliseconds(401));
        Observe(ActivityKind.PointerActivity, ActivitySource.Local);

        Assert.Equal(1, _incidents);
    }

    [Fact]
    public void ADeclarationForOneKindDoesNotCoverAnother()
    {
        _attribution.Expect(ActivityKind.PointerActivity, 10);

        Observe(ActivityKind.KeyboardActivity, ActivitySource.Local);

        Assert.Equal(1, _incidents);
    }

    // --- The two rules together ----------------------------------------------------------------------

    /// <summary>
    /// The end-to-end shape: a real input service injecting through a controller, with the ledger
    /// wired exactly as the host wires it. Nothing the phone does may wake the alarm.
    /// </summary>
    [Fact]
    public void AWholeRemoteSessionWhileArmedRaisesNothing()
    {
        var controller = new RecordingInputController();
        var input = new InputService(
            controller,
            new FakeDisplayCatalog(),
            _attribution,
            new InputOptions(),
            TestLog.For<InputService>());

        input.Begin("phone");

        // A plausible minute of remote work: moving, clicking, dragging, scrolling, typing.
        for (var i = 0; i < 10; i++)
        {
            input.MoveAbsolute("phone", 0, 0.4 + (i * 0.01), 0.5);
            Observe(ActivityKind.PointerActivity, ActivitySource.Local);
        }

        input.Button("phone", PointerButton.Left, ButtonAction.Click);
        Observe(ActivityKind.PointerActivity, ActivitySource.Local);
        Observe(ActivityKind.PointerActivity, ActivitySource.Local);

        input.Touch("phone", 0, 0, TouchAction.Down, 0.5, 0.5);
        Observe(ActivityKind.PointerActivity, ActivitySource.Local);

        input.Touch("phone", 0, 0, TouchAction.Up, 0.5, 0.5);
        Observe(ActivityKind.PointerActivity, ActivitySource.Local);

        input.Text("phone", "merhaba");
        for (var i = 0; i < 14; i++)
        {
            Observe(ActivityKind.KeyboardActivity, ActivitySource.Local);
        }

        input.End("phone");

        Assert.Equal(0, _incidents);
        Assert.Equal(SecurityState.Armed, _stateService.Current.State);
    }

    /// <summary>
    /// And the other half of the same story: once the phone stops, a person is a person again.
    /// </summary>
    [Fact]
    public void APersonArrivingAfterTheRemoteSessionEndsStillRaisesASession()
    {
        var controller = new RecordingInputController();
        var input = new InputService(
            controller,
            new FakeDisplayCatalog(),
            _attribution,
            new InputOptions(),
            TestLog.For<InputService>());

        input.Begin("phone");
        input.MoveAbsolute("phone", 0, 0.5, 0.5);
        Observe(ActivityKind.PointerActivity, ActivitySource.Local);
        input.End("phone");

        Assert.Equal(0, _incidents);

        _time.Advance(TimeSpan.FromSeconds(2));
        Observe(ActivityKind.PointerActivity, ActivitySource.Local);

        Assert.Equal(1, _incidents);
    }

    /// <summary>
    /// Injected input from some other program is still not a person, and still not Nexus. It is
    /// counted separately so the distinction stays visible in diagnostics.
    /// </summary>
    [Fact]
    public void SomeOtherProgramsSyntheticInputIsNotAPersonEither()
    {
        Observe(ActivityKind.PointerActivity, ActivitySource.Synthetic);

        Assert.Equal(0, _incidents);
        Assert.Equal(1, _monitor.IgnoredSyntheticInput);
        Assert.Equal(0, _monitor.IgnoredNexusInput);
    }

    /// <summary>
    /// Nothing is recorded, claimed or decided while the owner is present. The ledger must not even
    /// be consulted, because in OWNER_PRESENT there is nothing downstream to protect.
    /// </summary>
    [Fact]
    public void NothingHappensAtAllWhileTheOwnerIsPresent()
    {
        _stateService.RequestTransition(SecurityState.OwnerPresent, "test");
        _attribution.Expect(ActivityKind.PointerActivity, 5);

        Observe(ActivityKind.PointerActivity, ActivitySource.Local);

        Assert.Equal(0, _incidents);
        Assert.Equal(0, _attribution.Claimed);
    }

    public void Dispose()
    {
        _monitor.Dispose();
        _state.Dispose();
        GC.SuppressFinalize(this);
    }
}
