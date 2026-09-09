using Microsoft.Extensions.Logging.Abstractions;

using PcCore.Capabilities.Input;
using PcCore.NativeInput;
using PcCore.Windows.Input;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// The one gate every remote input passes, and the transition behaviour that a per-call check
/// cannot provide.
/// </summary>
public sealed class CentralInputPolicyTests
{
    private static readonly BackendInstance Local = new(InputBackend.LocalInjection, 1);

    private sealed class Desktop
    {
        public InputDesktopKind Kind { get; set; } = InputDesktopKind.Default;

        public InputDesktopState State() => new(Kind, Kind.ToString());
    }

    private static (CentralInputPolicy Policy, RemoteInputOwnership Ownership, Desktop Desktop) Build()
    {
        var desktop = new Desktop();
        var ownership = new RemoteInputOwnership();
        var policy = new CentralInputPolicy(ownership, NullLogger<CentralInputPolicy>.Instance, desktop.State);

        return (policy, ownership, desktop);
    }

    [Fact]
    public void TheOrdinaryDesktopAllowsInput()
    {
        var (policy, _, _) = Build();

        Assert.Equal(InputPolicyDecision.Allow, policy.Evaluate(_ => true));
        Assert.Equal("DEFAULT", policy.CurrentDesktop);
    }

    [Theory]
    [InlineData(InputDesktopKind.Secure)]
    [InlineData(InputDesktopKind.Other)]
    [InlineData(InputDesktopKind.Unknown)]
    public void AnythingButTheOrdinaryDesktopBlocks(InputDesktopKind kind)
    {
        var (policy, _, desktop) = Build();
        desktop.Kind = kind;

        Assert.Equal(InputPolicyDecision.BlockedBySecureDesktop, policy.Evaluate(_ => true));
    }

    /// <summary>
    /// Unknown is the case the check exists for. A desktop that cannot be identified is one Nexus
    /// has no business injecting into, and failing open here would exempt exactly the situation
    /// where the gate matters most.
    /// </summary>
    [Fact]
    public void AnUnreadableDesktopFailsClosed()
    {
        var (policy, _, desktop) = Build();
        desktop.Kind = InputDesktopKind.Unknown;

        var decision = policy.Evaluate(_ => true);

        Assert.Equal(InputPolicyDecision.BlockedBySecureDesktop, decision);
        Assert.Equal("UNKNOWN", policy.CurrentDesktop);
    }

    /// <summary>
    /// The case a per-call check cannot cover: a modifier held when the Secure Desktop appears. No
    /// input arrives to be blocked; the state simply stays held unless the transition releases it.
    /// </summary>
    [Fact]
    public void EnteringTheSecureDesktopNeutralizesHeldState()
    {
        var (policy, ownership, desktop) = Build();
        ownership.RecordDown(Local, HeldInputKind.Modifier);

        var neutralized = 0;
        desktop.Kind = InputDesktopKind.Secure;

        policy.Evaluate(_ => { neutralized++; return true; });

        Assert.Equal(1, neutralized);
        Assert.True(ownership.Held.IsNeutral);
    }

    /// <summary>
    /// Only on the edge. A consent prompt left up does not produce a neutral report per pointer
    /// packet that arrives behind it.
    /// </summary>
    [Fact]
    public void NeutralizationHappensOncePerTransitionNotPerCall()
    {
        var (policy, ownership, desktop) = Build();
        ownership.RecordDown(Local, HeldInputKind.Button);

        var neutralized = 0;
        desktop.Kind = InputDesktopKind.Secure;

        for (var i = 0; i < 25; i++)
        {
            policy.Evaluate(_ => { neutralized++; return true; });
        }

        Assert.Equal(1, neutralized);
    }

    /// <summary>
    /// If the owning backend cannot accept the neutral report, the state is not claimed released and
    /// input stays paused after the desktop returns.
    /// </summary>
    [Fact]
    public void UndeliverableNeutralizationLeavesInputPausedAfterTheDesktopReturns()
    {
        var (policy, ownership, desktop) = Build();
        ownership.RecordDown(Local, HeldInputKind.Modifier);

        desktop.Kind = InputDesktopKind.Secure;
        policy.Evaluate(_ => false);

        Assert.True(ownership.IsUncertain);
        Assert.True(policy.NeutralizationUncertain);

        desktop.Kind = InputDesktopKind.Default;

        Assert.Equal(InputPolicyDecision.BlockedByUncertainState, policy.Evaluate(_ => false));
    }

    /// <summary>Returning to the ordinary desktop restores routing but never restores held input.</summary>
    [Fact]
    public void ReturningToTheOrdinaryDesktopResumesWithoutRestoringHeldInput()
    {
        var (policy, ownership, desktop) = Build();
        ownership.RecordDown(Local, HeldInputKind.Key);

        desktop.Kind = InputDesktopKind.Secure;
        policy.Evaluate(_ => true);

        desktop.Kind = InputDesktopKind.Default;

        Assert.Equal(InputPolicyDecision.Allow, policy.Evaluate(_ => true));
        Assert.True(ownership.Held.IsNeutral);
    }

    [Fact]
    public void BlockedDecisionsBecomeExplicitProtocolResults()
    {
        Assert.Equal(
            InputDelivery.BlockedBySecureDesktop,
            CentralInputPolicy.ToResult(InputPolicyDecision.BlockedBySecureDesktop).Delivery);

        Assert.Equal(
            InputDelivery.Failed,
            CentralInputPolicy.ToResult(InputPolicyDecision.BlockedByUncertainState).Delivery);

        Assert.Equal(
            InputDelivery.Delivered,
            CentralInputPolicy.ToResult(InputPolicyDecision.Allow).Delivery);
    }
}
