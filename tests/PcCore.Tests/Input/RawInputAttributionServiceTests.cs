using Microsoft.Extensions.Logging.Abstractions;

using PcCore.Capabilities.Security.Detection;
using PcCore.NativeInput.Hid;
using PcCore.Windows.Input;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// The production wiring: listener to ledger, and the health flag virtual HID eligibility hangs on.
/// <para>
/// This is the joint that was missing. Every link either side of it had tests and worked; nothing
/// connected them, so at runtime the ledger learned nothing and every rule built on it was inert.
/// A chain correct in every link and unconnected at one joint passes every test written about the
/// links, which is why these tests are about the joint.
/// </para>
/// <para>
/// No sleeps and no polling anywhere below. The listener now settles before <c>Start</c> returns,
/// which is the fix for the flaky test rather than a longer timeout.
/// </para>
/// </summary>
public sealed class RawInputAttributionServiceTests
{
    private static (RawInputAttributionService Service, RawInputSourceLedger Ledger) Build()
    {
        var registry = new NexusHidDeviceRegistry();
        var ledger = new RawInputSourceLedger();
        var listener = new RawInputListener(NullLogger<RawInputListener>.Instance, registry);

        return (new RawInputAttributionService(listener, ledger, NullLogger<RawInputAttributionService>.Instance), ledger);
    }

    private static RawInputObservation Observation(RawInputKind kind) =>
        new(kind, 1234, @"\?\HID#VID_F055&PID_0001#nexus", DateTimeOffset.UtcNow);

    private static readonly InputSourceVerdict Trusted =
        new(InputSource.NexusVirtualHid, InputSourceReason.MatchedPublishedDevice);

    private static readonly InputSourceVerdict PhysicalVerdict =
        new(InputSource.Physical, InputSourceReason.NotAPublishedDevice);

    // --- startup and shutdown, deterministically ------------------------------------------------

    /// <summary>
    /// Start returns settled: no polling, no sleep. This is the flake fix, and asserting it here is
    /// what stops the old pattern coming back.
    /// </summary>
    [Fact]
    public void StartReturnsOnlyOnceTheListenerHasSettled()
    {
        var (service, _) = Build();

        try
        {
            var health = service.Start();

            Assert.True(health.ListenerRunning, health.Diagnostic ?? "listener did not start");
            Assert.True(health.SubscriptionActive);
            Assert.True(health.IsReady, health.Diagnostic ?? "attribution not ready");
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public void StartingTwiceCreatesNoSecondListenerOrSubscription()
    {
        var (service, ledger) = Build();

        try
        {
            service.Start();
            service.Start();

            // One observation must still be recorded once, not twice.
            service.OnObserved(Observation(RawInputKind.Mouse), Trusted);

            Assert.Equal(1, ledger.Outstanding);
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public void DisposeIsDeterministicAndSafeTwice()
    {
        var (service, ledger) = Build();

        service.Start();
        service.Dispose();
        service.Dispose();

        Assert.False(service.Health.IsReady);
        Assert.False(ledger.IsHealthy);
    }

    [Fact]
    public void DisposeWithoutStartIsSafe()
    {
        var (service, _) = Build();

        service.Dispose();

        Assert.False(service.Health.IsReady);
    }

    // --- the forwarding rule --------------------------------------------------------------------

    [Fact]
    public void TrustedObservationsReachTheLedger()
    {
        var (service, ledger) = Build();

        try
        {
            service.Start();

            service.OnObserved(Observation(RawInputKind.Mouse), Trusted);
            service.OnObserved(Observation(RawInputKind.Keyboard), Trusted);

            Assert.Equal(2, ledger.Outstanding);
        }
        finally
        {
            service.Dispose();
        }
    }

    /// <summary>
    /// Physical input records nothing, because there is nothing to excuse. That asymmetry is the
    /// whole reason the mechanism fails closed rather than open.
    /// </summary>
    [Fact]
    public void PhysicalObservationsRecordNothing()
    {
        var (service, ledger) = Build();

        try
        {
            service.Start();

            for (var i = 0; i < 10; i++)
            {
                service.OnObserved(Observation(RawInputKind.Mouse), PhysicalVerdict);
            }

            Assert.Equal(0, ledger.Outstanding);
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public void MouseAndKeyboardMapToTheirOwnActivityKinds()
    {
        var (service, ledger) = Build();

        try
        {
            service.Start();
            service.OnObserved(Observation(RawInputKind.Mouse), Trusted);

            // A pointer observation must not excuse a keyboard event.
            Assert.False(ledger.TryAttributeToNexus(ActivityKind.KeyboardActivity, DateTimeOffset.UtcNow));
            Assert.True(ledger.TryAttributeToNexus(ActivityKind.PointerActivity, DateTimeOffset.UtcNow));
        }
        finally
        {
            service.Dispose();
        }
    }

    /// <summary>
    /// Health going down must discard banked observations rather than leave them to be spent on a
    /// person's input later.
    /// </summary>
    [Fact]
    public void StoppingDiscardsBankedObservations()
    {
        var (service, ledger) = Build();

        service.Start();
        service.OnObserved(Observation(RawInputKind.Mouse), Trusted);
        Assert.Equal(1, ledger.Outstanding);

        service.Dispose();

        Assert.Equal(0, ledger.Outstanding);
        Assert.False(ledger.TryAttributeToNexus(ActivityKind.PointerActivity, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// A disposed service records nothing even if something still holds a reference and calls it.
    /// </summary>
    [Fact]
    public void AfterDisposeNothingIsRecorded()
    {
        var (service, ledger) = Build();

        service.Start();
        service.Dispose();

        service.OnObserved(Observation(RawInputKind.Mouse), Trusted);

        Assert.Equal(0, ledger.Outstanding);
    }
}
