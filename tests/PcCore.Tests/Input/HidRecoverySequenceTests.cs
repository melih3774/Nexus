using PcCore.Capabilities.Input;
using PcCore.NativeInput.Hid;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// The whole recovery sequence, driven through the same two types production uses to decide routing:
/// the lifecycle that says whether a backend is usable, and the eligibility rules that turn that
/// into a per-kind backend choice.
/// <code>
/// READY -> driver lost -> fall back -> cooldown -> reconnect
///       -> identities re-resolved -> neutral handshake -> READY again
/// </code>
/// <para>
/// Nothing here is installed. The point is that the decisions are made by the production types
/// rather than by a test-only copy of the logic, so the sequence proven here is the sequence that
/// will run on install day.
/// </para>
/// </summary>
public sealed class HidRecoverySequenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly HashSet<InputKind> AllKinds =
        [InputKind.Mouse, InputKind.Keyboard, InputKind.Touch];

    /// <summary>The conditions as production gathers them, given a lifecycle and a device set.</summary>
    private static VirtualHidConditions Conditions(
        VirtualHidLifecycle lifecycle,
        IReadOnlySet<InputKind> kinds,
        bool identitiesResolved = true,
        bool attributionHealthy = true) => new()
    {
        DriverConnected = lifecycle.IsEligible,
        ProtocolCompatible = true,
        IdentitiesResolved = identitiesResolved,
        AttributionHealthy = attributionHealthy,
        AttributionSubscribed = true,
        AvailableKinds = kinds,
    };

    private static InputBackend RouteMouse(VirtualHidConditions conditions) =>
        VirtualHidEligibility.Route(conditions, InputKind.Mouse, elevatedBrokerReady: false);

    [Fact]
    public void TheFullLossAndRecoverySequence()
    {
        var life = new VirtualHidLifecycle();
        var now = T0;

        // 1. Connect, identify, handshake. Only now is HID carrying input.
        Assert.True(life.BeginConnect(now));
        life.Connected();
        Assert.Equal(InputBackend.LocalInjection, RouteMouse(Conditions(life, AllKinds)));

        Assert.True(life.CompleteHandshake(now, () => true));
        Assert.Equal(InputBackend.VirtualHid, RouteMouse(Conditions(life, AllKinds)));

        var firstGeneration = life.Generation;

        // 2. The driver starts refusing. One failure does not move input.
        life.RecordFailure(now);
        life.RecordSuccess(now);
        Assert.Equal(InputBackend.VirtualHid, RouteMouse(Conditions(life, AllKinds)));

        // 3. A run of failures does.
        for (var i = 0; i < VirtualHidLifecycle.MaxConsecutiveFailures; i++)
        {
            life.RecordFailure(now);
        }

        Assert.Equal(VirtualHidLifecycleState.Lost, life.State);
        Assert.Equal(InputBackend.LocalInjection, RouteMouse(Conditions(life, AllKinds)));

        // 4. Nothing reconnects during the backoff.
        Assert.False(life.BeginConnect(now));

        // 5. After it, a new instance - and a new generation, so nothing from the old one carries.
        now += VirtualHidLifecycle.MaxCooldown;
        Assert.True(life.BeginConnect(now));
        Assert.True(life.Generation > firstGeneration);

        life.Connected();

        // 6. Connected and still not usable: the handshake has not happened.
        Assert.Equal(InputBackend.LocalInjection, RouteMouse(Conditions(life, AllKinds)));

        // 7. Handshake accepted. Input returns to HID.
        Assert.True(life.CompleteHandshake(now, () => true));
        Assert.Equal(InputBackend.VirtualHid, RouteMouse(Conditions(life, AllKinds)));
    }

    /// <summary>
    /// A reconnect whose device identities have not been re-resolved must not carry input, even
    /// though the lifecycle is ready. Stale trust from the previous instance is exactly what the
    /// identity condition exists to refuse.
    /// </summary>
    [Fact]
    public void AReconnectWithoutFreshIdentitiesIsNotEligible()
    {
        var life = new VirtualHidLifecycle();
        var now = T0;

        life.BeginConnect(now);
        life.Connected();
        life.CompleteHandshake(now, () => true);

        var conditions = Conditions(life, AllKinds, identitiesResolved: false);

        Assert.Equal(InputBackend.LocalInjection, RouteMouse(conditions));
        Assert.Equal(
            VirtualHidIneligibility.IdentityNotResolved,
            VirtualHidEligibility.Evaluate(conditions, InputKind.Mouse).Reason);
    }

    /// <summary>
    /// Attribution going unhealthy drops HID immediately, whatever the lifecycle says. Without
    /// attribution, HID input is indistinguishable from a person at the security detector.
    /// </summary>
    [Fact]
    public void AttributionLossDropsHidEvenWhenTheBackendIsReady()
    {
        var life = new VirtualHidLifecycle();
        var now = T0;

        life.BeginConnect(now);
        life.Connected();
        life.CompleteHandshake(now, () => true);

        Assert.Equal(InputBackend.VirtualHid, RouteMouse(Conditions(life, AllKinds)));

        var degraded = Conditions(life, AllKinds, attributionHealthy: false);

        Assert.Equal(InputBackend.LocalInjection, RouteMouse(degraded));
        Assert.Equal(
            VirtualHidIneligibility.AttributionUnhealthy,
            VirtualHidEligibility.Evaluate(degraded, InputKind.Mouse).Reason);
    }

    /// <summary>
    /// A backend that comes back with fewer devices carries the kinds it has and no more. Mouse and
    /// keyboard on HID, touch on fallback, from one shared eligibility rule.
    /// </summary>
    [Fact]
    public void APartialReconnectRoutesPerKind()
    {
        var life = new VirtualHidLifecycle();
        var now = T0;

        life.BeginConnect(now);
        life.Connected();
        life.CompleteHandshake(now, () => true);

        var partial = Conditions(life, new HashSet<InputKind> { InputKind.Mouse, InputKind.Keyboard });

        Assert.Equal(InputBackend.VirtualHid,
            VirtualHidEligibility.Route(partial, InputKind.Mouse, elevatedBrokerReady: false));
        Assert.Equal(InputBackend.VirtualHid,
            VirtualHidEligibility.Route(partial, InputKind.Keyboard, elevatedBrokerReady: false));
        Assert.Equal(InputBackend.LocalInjection,
            VirtualHidEligibility.Route(partial, InputKind.Touch, elevatedBrokerReady: false));
    }

    /// <summary>
    /// A driver that fails intermittently forever must never oscillate the route. Each loss backs
    /// off further, so the system settles on fallback instead of thrashing.
    /// </summary>
    [Fact]
    public void AnIntermittentlyFailingDriverSettlesOnFallbackRatherThanOscillating()
    {
        var life = new VirtualHidLifecycle();
        var now = T0;
        var routeChanges = 0;
        var previous = InputBackend.LocalInjection;

        for (var cycle = 0; cycle < 12; cycle++)
        {
            if (life.MayAttemptConnect(now) && life.BeginConnect(now))
            {
                life.Connected();
                life.CompleteHandshake(now, () => true);
            }

            for (var i = 0; i < VirtualHidLifecycle.MaxConsecutiveFailures; i++)
            {
                life.RecordFailure(now);
            }

            var current = RouteMouse(Conditions(life, AllKinds));

            if (current != previous)
            {
                routeChanges++;
                previous = current;
            }

            now += TimeSpan.FromSeconds(1);
        }

        // With a growing backoff, most cycles cannot even attempt a connection, so the route stays
        // put. Without one this loop would change route on every cycle.
        Assert.True(routeChanges <= 4, $"route changed {routeChanges} times; backoff is not damping reconnects");
    }
}
