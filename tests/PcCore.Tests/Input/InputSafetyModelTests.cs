using PcCore.Capabilities.Input;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// Virtual HID eligibility: six conditions, evaluated in one place, reported with a reason.
///
/// Which desktop is live is deliberately not one of them any more. It was, and it meant a UAC prompt
/// demoted Nexus from a HID device to SendInput - dropping the only backend that could have reached
/// the prompt. The desktop question moved to CentralInputPolicy, which asks it about the chosen
/// route.
/// </summary>
public sealed class VirtualHidEligibilityTests
{
    private static VirtualHidConditions AllGood(params InputKind[] kinds) => new()
    {
        DriverConnected = true,
        ProtocolCompatible = true,
        IdentitiesResolved = true,
        AttributionHealthy = true,
        AttributionSubscribed = true,
        AvailableKinds = new HashSet<InputKind>(kinds.Length == 0
            ? [InputKind.Mouse, InputKind.Keyboard, InputKind.Touch]
            : kinds),
    };

    [Fact]
    public void EverythingHealthyIsEligible()
    {
        Assert.True(VirtualHidEligibility.Evaluate(AllGood(), InputKind.Mouse).Eligible);
    }

    /// <summary>Opening a handle is not eligibility. Four more things must hold.</summary>
    [Theory]
    [InlineData(nameof(VirtualHidConditions.ProtocolCompatible), VirtualHidIneligibility.ProtocolIncompatible)]
    [InlineData(nameof(VirtualHidConditions.IdentitiesResolved), VirtualHidIneligibility.IdentityNotResolved)]
    [InlineData(nameof(VirtualHidConditions.AttributionHealthy), VirtualHidIneligibility.AttributionUnhealthy)]
    [InlineData(nameof(VirtualHidConditions.AttributionSubscribed), VirtualHidIneligibility.AttributionNotSubscribed)]
    public void AnyMissingConditionMakesItIneligible(string condition, VirtualHidIneligibility expected)
    {
        var c = AllGood();

        c = condition switch
        {
            nameof(VirtualHidConditions.ProtocolCompatible) => c with { ProtocolCompatible = false },
            nameof(VirtualHidConditions.IdentitiesResolved) => c with { IdentitiesResolved = false },
            nameof(VirtualHidConditions.AttributionHealthy) => c with { AttributionHealthy = false },
            _ => c with { AttributionSubscribed = false },
        };

        var verdict = VirtualHidEligibility.Evaluate(c, InputKind.Mouse);

        Assert.False(verdict.Eligible);
        Assert.Equal(expected, verdict.Reason);
    }

    /// <summary>The condition most likely to be skipped for convenience, pinned.</summary>
    [Fact]
    public void UnresolvedIdentityBlocksRoutingEvenWithAHealthyDriver()
    {
        var verdict = VirtualHidEligibility.Evaluate(AllGood() with { IdentitiesResolved = false }, InputKind.Mouse);

        Assert.False(verdict.Eligible);
        Assert.Equal(VirtualHidIneligibility.IdentityNotResolved, verdict.Reason);
    }

    /// <summary>A backend with a mouse and keyboard but no digitizer carries the two it has.</summary>
    [Fact]
    public void CapabilityIsPerKind()
    {
        var partial = AllGood(InputKind.Mouse, InputKind.Keyboard);

        Assert.True(VirtualHidEligibility.Evaluate(partial, InputKind.Mouse).Eligible);
        Assert.True(VirtualHidEligibility.Evaluate(partial, InputKind.Keyboard).Eligible);
        Assert.False(VirtualHidEligibility.Evaluate(partial, InputKind.Touch).Eligible);
        Assert.Equal(
            VirtualHidIneligibility.CapabilityNotPublished,
            VirtualHidEligibility.Evaluate(partial, InputKind.Touch).Reason);
    }

    [Fact]
    public void UncertainNeutralizationBlocksRouting()
    {
        var verdict = VirtualHidEligibility.Evaluate(
            AllGood() with { NeutralizationUncertain = true }, InputKind.Mouse);

        Assert.False(verdict.Eligible);
        Assert.Equal(VirtualHidIneligibility.NeutralizationUncertain, verdict.Reason);
    }

    // --- routing priority ----------------------------------------------------------------------

    [Fact]
    public void RoutingPrefersHidThenElevatedThenLocal()
    {
        Assert.Equal(InputBackend.VirtualHid,
            VirtualHidEligibility.Route(AllGood(), InputKind.Mouse, elevatedBrokerReady: true));

        Assert.Equal(InputBackend.ElevatedInjection,
            VirtualHidEligibility.Route(VirtualHidConditions.None, InputKind.Mouse, elevatedBrokerReady: true));

        Assert.Equal(InputBackend.LocalInjection,
            VirtualHidEligibility.Route(VirtualHidConditions.None, InputKind.Mouse, elevatedBrokerReady: false));
    }

    /// <summary>The state this machine is in today, and must stay in.</summary>
    [Fact]
    public void WithNoDriverAndNoBrokerEverythingRoutesLocal()
    {
        foreach (var kind in new[] { InputKind.Mouse, InputKind.Keyboard, InputKind.Touch })
        {
            Assert.Equal(
                InputBackend.LocalInjection,
                VirtualHidEligibility.Route(VirtualHidConditions.None, kind, elevatedBrokerReady: false));
        }
    }

    /// <summary>Attribution loss must drop HID even when everything else is perfect.</summary>
    [Fact]
    public void AttributionLossFallsBackRatherThanProceeding()
    {
        var degraded = AllGood() with { AttributionHealthy = false };

        Assert.Equal(InputBackend.ElevatedInjection,
            VirtualHidEligibility.Route(degraded, InputKind.Mouse, elevatedBrokerReady: true));
    }
}

/// <summary>
/// The ownership invariant: input held through one backend instance is released through that same
/// instance, or is honestly recorded as unreleased.
/// </summary>
public sealed class RemoteInputOwnershipTests
{
    private static readonly BackendInstance Hid = new(InputBackend.VirtualHid, 1);
    private static readonly BackendInstance HidNextGeneration = new(InputBackend.VirtualHid, 2);
    private static readonly BackendInstance Local = new(InputBackend.LocalInjection, 1);

    [Fact]
    public void NothingHeldMeansAnyBackendMayBeRouted()
    {
        var ownership = new RemoteInputOwnership();

        Assert.True(ownership.CanRouteTo(Hid));
        Assert.True(ownership.CanRouteTo(Local));
        Assert.True(ownership.Held.IsNeutral);
    }

    [Fact]
    public void HoldingStateTakesOwnership()
    {
        var ownership = new RemoteInputOwnership();

        ownership.RecordDown(Hid, HeldInputKind.Modifier);

        Assert.Equal(Hid, ownership.Owner);
        Assert.False(ownership.Held.IsNeutral);
    }

    /// <summary>
    /// The core invariant. A modifier held through HID cannot be released through local injection:
    /// the key-up would travel a different input path, and the HID device would still believe it is
    /// down with nothing able to tell it otherwise.
    /// </summary>
    [Fact]
    public void StateCannotBeReleasedThroughADifferentBackend()
    {
        var ownership = new RemoteInputOwnership();
        ownership.RecordDown(Hid, HeldInputKind.Modifier);

        Assert.False(ownership.TryRecordUp(Local, HeldInputKind.Modifier));
        Assert.False(ownership.Held.IsNeutral);
        Assert.Equal(Hid, ownership.Owner);
    }

    [Fact]
    public void StateIsReleasedThroughItsOwnBackend()
    {
        var ownership = new RemoteInputOwnership();
        ownership.RecordDown(Hid, HeldInputKind.Modifier);

        Assert.True(ownership.TryRecordUp(Hid, HeldInputKind.Modifier));
        Assert.True(ownership.Held.IsNeutral);
        Assert.Equal(BackendInstance.None, ownership.Owner);
    }

    [Fact]
    public void ADifferentBackendCannotBeRoutedWhileStateIsHeld()
    {
        var ownership = new RemoteInputOwnership();
        ownership.RecordDown(Hid, HeldInputKind.Button);

        Assert.False(ownership.CanRouteTo(Local));
        Assert.True(ownership.CanRouteTo(Hid));
    }

    [Fact]
    public void SwitchingNeutralizesFirst()
    {
        var ownership = new RemoteInputOwnership();
        ownership.RecordDown(Hid, HeldInputKind.Key);

        var result = ownership.NeutralizeForSwitch(_ => true);

        Assert.Equal(RouteSwitchResult.Neutralized, result);
        Assert.True(ownership.Held.IsNeutral);
        Assert.True(ownership.CanRouteTo(Local));
    }

    [Fact]
    public void SwitchingWithNothingHeldIsClean()
    {
        var ownership = new RemoteInputOwnership();

        Assert.Equal(RouteSwitchResult.Clean, ownership.NeutralizeForSwitch(_ => true));
    }

    /// <summary>
    /// The backend vanished before the neutral report could be delivered. Nothing may claim the
    /// state was released, because nothing knows.
    /// </summary>
    [Fact]
    public void UndeliverableNeutralizationIsRecordedAsUncertain()
    {
        var ownership = new RemoteInputOwnership();
        ownership.RecordDown(Hid, HeldInputKind.Modifier);

        var result = ownership.NeutralizeForSwitch(_ => false);

        Assert.Equal(RouteSwitchResult.Uncertain, result);
        Assert.True(ownership.IsUncertain);
    }

    /// <summary>Uncertainty blocks that backend until a fresh instance confirms a neutral report.</summary>
    [Fact]
    public void UncertaintyBlocksTheSameBackendUntilConfirmed()
    {
        var ownership = new RemoteInputOwnership();
        ownership.RecordDown(Hid, HeldInputKind.Modifier);
        ownership.NeutralizeForSwitch(_ => false);

        Assert.False(ownership.CanRouteTo(Hid));
        Assert.False(ownership.CanRouteTo(HidNextGeneration));

        // Falling back to a different backend is still allowed: it owns nothing and never did.
        Assert.True(ownership.CanRouteTo(Local));
    }

    [Fact]
    public void AFreshInstanceAcceptingNeutralClearsUncertainty()
    {
        var ownership = new RemoteInputOwnership();
        ownership.RecordDown(Hid, HeldInputKind.Modifier);
        ownership.NeutralizeForSwitch(_ => false);

        Assert.True(ownership.ConfirmNeutral(HidNextGeneration));
        Assert.False(ownership.IsUncertain);
        Assert.True(ownership.CanRouteTo(HidNextGeneration));
    }

    /// <summary>
    /// The same instance coming back is the same device. Accepting it would clear the uncertainty
    /// without resolving it.
    /// </summary>
    [Fact]
    public void TheSameInstanceCannotClearItsOwnUncertainty()
    {
        var ownership = new RemoteInputOwnership();
        ownership.RecordDown(Hid, HeldInputKind.Modifier);
        ownership.NeutralizeForSwitch(_ => false);

        Assert.False(ownership.ConfirmNeutral(Hid));
        Assert.True(ownership.IsUncertain);
    }

    [Fact]
    public void ForgetDropsEverythingWithoutClaimingRelease()
    {
        var ownership = new RemoteInputOwnership();
        ownership.RecordDown(Hid, HeldInputKind.TouchContact);
        ownership.NeutralizeForSwitch(_ => false);

        ownership.Forget();

        Assert.False(ownership.IsUncertain);
        Assert.True(ownership.Held.IsNeutral);
    }

    [Fact]
    public void MultipleHeldItemsAllRequireRelease()
    {
        var ownership = new RemoteInputOwnership();
        ownership.RecordDown(Hid, HeldInputKind.Key);
        ownership.RecordDown(Hid, HeldInputKind.Key);
        ownership.RecordDown(Hid, HeldInputKind.Modifier);

        Assert.True(ownership.TryRecordUp(Hid, HeldInputKind.Key));
        Assert.False(ownership.Held.IsNeutral);

        Assert.True(ownership.TryRecordUp(Hid, HeldInputKind.Key));
        Assert.True(ownership.TryRecordUp(Hid, HeldInputKind.Modifier));
        Assert.True(ownership.Held.IsNeutral);
    }
}
