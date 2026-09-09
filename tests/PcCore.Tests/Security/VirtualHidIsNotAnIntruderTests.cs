using Microsoft.Extensions.Logging.Abstractions;

using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.Security.Detection;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;

using Xunit;

namespace PcCore.Tests.Security;

/// <summary>
/// The ARMED invariant for virtual HID, which is what decides whether virtual HID may ever ship:
/// <code>
/// ARMED + Nexus HID input      -> stays ARMED
/// ARMED + physical mouse/keys  -> SECURITY_SESSION
/// </code>
/// <para>
/// <b>Why a third attribution rule was needed at all.</b> Rule one is the <c>dwExtraInfo</c>
/// signature, which works because Nexus sets it on every <c>SendInput</c> call. Rule two is the
/// declaration ledger, which covers injected touch, where there is no field to put a tag in. Neither
/// can see a HID report: it arrives through the real HID stack, indistinguishable from hardware, and
/// Nexus is not the one producing it — Windows is, from a device. What remains distinguishable is
/// <em>which device</em>, and that is rule three.
/// </para>
/// <para>
/// <b>The correction this file encodes.</b> The Raw Input listener was first built believing it and
/// the low-level hook never observe the same event. That is true only for <c>SendInput</c>, which
/// goes through no device and raises no <c>WM_INPUT</c>. A physical click raises both, and so will a
/// Nexus HID report. Wiring Raw Input in as a second event source would therefore have doubled every
/// physical action. So it is not a source: the hook remains the only stream into the monitor, and
/// Raw Input feeds a ledger the monitor consults. <see cref="OneActionRaisesOneIncident"/> is the
/// test that would have caught the other design.
/// </para>
/// </summary>
public sealed class VirtualHidIsNotAnIntruderTests : IDisposable
{
    private readonly TempStateDirectory _state = new("virtual-hid-attribution");
    private readonly ScriptedActivityDetector _detector = new();
    private readonly AdvanceableTimeProvider _time = new();
    private readonly EventBus _events;
    private readonly SecurityStateService _stateService;
    private readonly RawInputSourceLedger _ledger = new();
    private readonly SecurityActivityMonitor _monitor;

    private int _incidents;

    public VirtualHidIsNotAnIntruderTests()
    {
        _events = new EventBus(NullLogger<EventBus>.Instance);
        _stateService = new SecurityStateService(_events, _state.Directory, TestLog.For<SecurityStateService>());

        _ledger.SetHealthy(true);

        _monitor = new SecurityActivityMonitor(
            _detector,
            new NullNexusInputAttribution(),
            _stateService,
            _events,
            new DetectionOptions { GraceSeconds = 0, PointerMovementThreshold = 0, CoalesceMilliseconds = 0 },
            TestLog.For<SecurityActivityMonitor>(),
            _time,
            _ledger);

        _monitor.IncidentDetected += (_, _) => _incidents++;
        _monitor.Start();

        _stateService.RequestTransition(SecurityState.Armed, "test");
    }

    /// <summary>A Nexus HID device produced input: Raw Input observed it and the ledger recorded it.</summary>
    private void NexusHidInput(ActivityKind kind = ActivityKind.PointerActivity)
    {
        _ledger.RecordTrustedNexusInput(kind, _time.GetUtcNow());
        _detector.Emit(new LocalActivity(kind, ActivitySource.Local, _time.GetUtcNow()));
    }

    /// <summary>A person at the machine. Nothing records anything; the hook simply fires.</summary>
    private void PhysicalInput(ActivityKind kind = ActivityKind.PointerActivity) =>
        _detector.Emit(new LocalActivity(kind, ActivitySource.Local, _time.GetUtcNow()));

    // --- the invariant -------------------------------------------------------------------------

    [Fact]
    public void ArmedPlusNexusHidInputStaysArmed()
    {
        for (var i = 0; i < 20; i++)
        {
            NexusHidInput(ActivityKind.PointerActivity);
            NexusHidInput(ActivityKind.KeyboardActivity);
        }

        Assert.Equal(0, _incidents);
    }

    [Fact]
    public void ArmedPlusPhysicalInputRaisesAnIncident()
    {
        PhysicalInput(ActivityKind.KeyboardActivity);

        Assert.Equal(1, _incidents);
    }

    /// <summary>
    /// The monitor's actual de-duplication contract, which is worth stating because the first
    /// version of this test asserted the wrong layer.
    /// <para>
    /// <c>IncidentDetected</c> is a signal, not a transition: <c>SecurityIncidentService</c> is what
    /// moves the state, and it is not in this harness. So the monitor keeps signalling while the
    /// state stays ARMED, and what stops a second incident is the state machine refusing a second
    /// transition. Once the state is SECURITY_SESSION the monitor stops raising incidents and feeds
    /// the timeline instead, which is the part this layer owns and this test checks.
    /// </para>
    /// </summary>
    [Fact]
    public void OnceASessionIsOpenNoFurtherIncidentsAreRaised()
    {
        PhysicalInput(ActivityKind.KeyboardActivity);
        Assert.Equal(1, _incidents);

        _stateService.RequestTransition(SecurityState.SecuritySession, "test");

        PhysicalInput(ActivityKind.KeyboardActivity);
        PhysicalInput(ActivityKind.PointerActivity);

        Assert.Equal(1, _incidents);
    }

    /// <summary>
    /// One observation excuses exactly one event. A person moving a real mouse in the middle of a
    /// burst of Nexus HID reports still produces an event nothing accounts for.
    /// </summary>
    [Fact]
    public void OneObservationExcusesExactlyOneEvent()
    {
        _ledger.RecordTrustedNexusInput(ActivityKind.PointerActivity, _time.GetUtcNow());

        PhysicalInput(ActivityKind.PointerActivity);   // absorbed by the observation
        Assert.Equal(0, _incidents);

        PhysicalInput(ActivityKind.PointerActivity);   // nothing left to absorb it
        Assert.Equal(1, _incidents);
    }

    /// <summary>An observation older than the window belongs to an action already accounted for.</summary>
    [Fact]
    public void AnExpiredObservationDoesNotExcuseAnything()
    {
        _ledger.RecordTrustedNexusInput(ActivityKind.PointerActivity, _time.GetUtcNow());

        _time.Advance(RawInputSourceLedger.Window + TimeSpan.FromMilliseconds(50));

        PhysicalInput(ActivityKind.PointerActivity);

        Assert.Equal(1, _incidents);
    }

    /// <summary>Observations of one kind must not excuse events of another.</summary>
    [Fact]
    public void AnObservationOfOneKindDoesNotExcuseAnother()
    {
        _ledger.RecordTrustedNexusInput(ActivityKind.PointerActivity, _time.GetUtcNow());

        PhysicalInput(ActivityKind.KeyboardActivity);

        Assert.Equal(1, _incidents);
    }

    // --- failing closed ------------------------------------------------------------------------

    /// <summary>
    /// The direction of failure that matters. A listener that stopped means Nexus HID input is
    /// treated as a person — a false alarm the owner dismisses — never the reverse.
    /// </summary>
    [Fact]
    public void AnUnhealthyLedgerAttributesNothing()
    {
        _ledger.SetHealthy(false);
        _ledger.RecordTrustedNexusInput(ActivityKind.PointerActivity, _time.GetUtcNow());

        PhysicalInput(ActivityKind.PointerActivity);

        Assert.Equal(1, _incidents);
    }

    /// <summary>Losing health discards banked observations rather than leaving them to be spent later.</summary>
    [Fact]
    public void LosingHealthDiscardsOutstandingObservations()
    {
        _ledger.RecordTrustedNexusInput(ActivityKind.PointerActivity, _time.GetUtcNow());
        Assert.Equal(1, _ledger.Outstanding);

        _ledger.SetHealthy(false);

        Assert.Equal(0, _ledger.Outstanding);
    }

    /// <summary>
    /// A monitor with no ledger at all is the state PC Core is in today, and it must behave exactly
    /// as it did before this rule existed.
    /// </summary>
    [Fact]
    public void WithNoLedgerAtAllPhysicalInputStillTriggers()
    {
        using var state = new TempStateDirectory("virtual-hid-no-ledger");
        var events = new EventBus(NullLogger<EventBus>.Instance);
        var stateService = new SecurityStateService(events, state.Directory, TestLog.For<SecurityStateService>());
        var detector = new ScriptedActivityDetector();

        using var monitor = new SecurityActivityMonitor(
            detector,
            new NullNexusInputAttribution(),
            stateService,
            events,
            new DetectionOptions { GraceSeconds = 0, PointerMovementThreshold = 0, CoalesceMilliseconds = 0 },
            TestLog.For<SecurityActivityMonitor>(),
            _time);

        var incidents = 0;
        monitor.IncidentDetected += (_, _) => incidents++;
        monitor.Start();
        stateService.RequestTransition(SecurityState.Armed, "test");

        detector.Emit(new LocalActivity(ActivityKind.KeyboardActivity, ActivitySource.Local, _time.GetUtcNow()));

        Assert.Equal(1, incidents);
    }

    /// <summary>The cap stops a flooding device banking excuses it can spend much later.</summary>
    [Fact]
    public void OutstandingObservationsAreCapped()
    {
        for (var i = 0; i < RawInputSourceLedger.MaxOutstandingPerKind * 3; i++)
        {
            _ledger.RecordTrustedNexusInput(ActivityKind.PointerActivity, _time.GetUtcNow());
        }

        Assert.Equal(RawInputSourceLedger.MaxOutstandingPerKind, _ledger.Outstanding);
    }

    public void Dispose()
    {
        _monitor.Dispose();
        _state.Dispose();
    }
}
