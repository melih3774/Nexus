using PcCore.NativeInput.Hid;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// The backend lifecycle: the failure budget that stops route flapping, the backoff that stops
/// reconnect flapping, and the neutral handshake that stands between a fresh instance and its first
/// real report.
/// </summary>
public sealed class VirtualHidLifecycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static VirtualHidLifecycle Connected(out DateTimeOffset now)
    {
        now = T0;
        var life = new VirtualHidLifecycle();

        Assert.True(life.BeginConnect(now));
        life.Connected();

        return life;
    }

    private static VirtualHidLifecycle Ready(out DateTimeOffset now)
    {
        var life = Connected(out now);
        Assert.True(life.CompleteHandshake(now, () => true));
        return life;
    }

    // --- the handshake ---------------------------------------------------------------------------

    /// <summary>
    /// A connected backend is not a usable one. The handshake is the difference, and skipping it is
    /// how a device that came back holding something gets sent a keystroke on top of it.
    /// </summary>
    [Fact]
    public void ConnectingIsNotEnoughToBeEligible()
    {
        var life = Connected(out _);

        Assert.Equal(VirtualHidLifecycleState.Handshaking, life.State);
        Assert.False(life.IsEligible);
    }

    [Fact]
    public void AcceptedNeutralReportMakesItReady()
    {
        var life = Ready(out _);

        Assert.Equal(VirtualHidLifecycleState.Ready, life.State);
        Assert.True(life.IsEligible);
    }

    /// <summary>
    /// A device that will not accept a neutral report is not one to send a keystroke to, so a failed
    /// handshake loses the backend rather than degrading it.
    /// </summary>
    [Fact]
    public void ARefusedHandshakeLosesTheBackend()
    {
        var life = Connected(out var now);

        Assert.False(life.CompleteHandshake(now, () => false));
        Assert.Equal(VirtualHidLifecycleState.Lost, life.State);
        Assert.False(life.IsEligible);
    }

    [Fact]
    public void TheHandshakeIsActuallyAttempted()
    {
        var life = Connected(out var now);
        var sent = 0;

        life.CompleteHandshake(now, () => { sent++; return true; });

        Assert.Equal(1, sent);
    }

    // --- the failure budget ----------------------------------------------------------------------

    /// <summary>
    /// One refused report is not a dead driver. Losing the backend on the first failure is exactly
    /// the flapping this budget exists to prevent.
    /// </summary>
    [Fact]
    public void OneFailedReportDegradesRatherThanLosing()
    {
        var life = Ready(out var now);

        var state = life.RecordFailure(now);

        Assert.Equal(VirtualHidLifecycleState.Degraded, state);
        Assert.False(life.IsEligible);
    }

    [Fact]
    public void ASuccessAfterAFailureRestoresReady()
    {
        var life = Ready(out var now);

        life.RecordFailure(now);
        life.RecordSuccess(now);

        Assert.Equal(VirtualHidLifecycleState.Ready, life.State);
        Assert.True(life.IsEligible);
    }

    [Fact]
    public void OnlyARunOfFailuresLosesTheBackend()
    {
        var life = Ready(out var now);

        for (var i = 0; i < VirtualHidLifecycle.MaxConsecutiveFailures - 1; i++)
        {
            Assert.NotEqual(VirtualHidLifecycleState.Lost, life.RecordFailure(now));
        }

        Assert.Equal(VirtualHidLifecycleState.Lost, life.RecordFailure(now));
    }

    /// <summary>
    /// The anti-flapping property stated directly: alternating failure and success never loses the
    /// backend, because each success resets the budget.
    /// </summary>
    [Fact]
    public void AlternatingFailureAndSuccessNeverLoses()
    {
        var life = Ready(out var now);

        for (var i = 0; i < 200; i++)
        {
            life.RecordFailure(now);
            life.RecordSuccess(now);
        }

        Assert.Equal(VirtualHidLifecycleState.Ready, life.State);
    }

    // --- the backoff -----------------------------------------------------------------------------

    [Fact]
    public void AfterALossReconnectionWaitsForTheCooldown()
    {
        var life = Ready(out var now);
        life.Lose(now, "test");

        Assert.False(life.MayAttemptConnect(now));
        Assert.False(life.BeginConnect(now));

        Assert.True(life.MayAttemptConnect(now + VirtualHidLifecycle.BaseCooldown));
    }

    /// <summary>
    /// Repeated losses back off. Retrying a dead driver every few seconds forever produces a
    /// connect-handshake-fail-fallback loop, which is flapping wearing a different hat.
    /// </summary>
    [Fact]
    public void RepeatedLossesBackOff()
    {
        var life = new VirtualHidLifecycle();
        var now = T0;

        life.Lose(now, "first");
        var first = life.RetryNotBefore - now;

        life.Reset();
        life.Lose(now, "a");
        life.Lose(now, "b");
        life.Lose(now, "c");
        var third = life.RetryNotBefore - now;

        Assert.True(third > first, $"backoff did not grow: {first} then {third}");
    }

    [Fact]
    public void TheBackoffIsCapped()
    {
        var life = new VirtualHidLifecycle();
        var now = T0;

        for (var i = 0; i < 40; i++)
        {
            life.Lose(now, "repeated");
        }

        Assert.True(life.RetryNotBefore - now <= VirtualHidLifecycle.MaxCooldown);
    }

    // --- generations and reconnect ---------------------------------------------------------------

    /// <summary>
    /// Every attempt is a new instance. State recorded against a vanished backend must never be
    /// attributed to its successor, and the generation is what makes that checkable.
    /// </summary>
    [Fact]
    public void EachConnectionAttemptTakesAFreshGeneration()
    {
        var life = Ready(out var now);
        var first = life.Generation;

        life.Lose(now, "gone");
        now += VirtualHidLifecycle.MaxCooldown;

        Assert.True(life.BeginConnect(now));
        Assert.True(life.Generation > first);
    }

    /// <summary>
    /// The whole recovery sequence: working, lost, fallen back, cooled down, reconnected,
    /// handshaken, usable again.
    /// </summary>
    [Fact]
    public void LossThenReconnectRequiresAFreshHandshakeBeforeInputResumes()
    {
        var life = Ready(out var now);
        Assert.True(life.IsEligible);

        // The driver disappears.
        life.Lose(now, "driver gone");
        Assert.False(life.IsEligible);

        // Nothing may reconnect during the cooldown.
        Assert.False(life.BeginConnect(now));

        now += VirtualHidLifecycle.MaxCooldown;
        Assert.True(life.BeginConnect(now));
        life.Connected();

        // Connected again, and still not usable.
        Assert.False(life.IsEligible);

        Assert.True(life.CompleteHandshake(now, () => true));
        Assert.True(life.IsEligible);
    }

    /// <summary>A handshake cannot be completed from a state that never connected.</summary>
    [Fact]
    public void HandshakeIsRefusedWhenNotConnecting()
    {
        var life = new VirtualHidLifecycle();

        Assert.False(life.CompleteHandshake(T0, () => true));
        Assert.NotEqual(VirtualHidLifecycleState.Ready, life.State);
    }

    [Fact]
    public void FailuresBeforeReadyAreIgnored()
    {
        var life = Connected(out var now);

        Assert.Equal(VirtualHidLifecycleState.Handshaking, life.RecordFailure(now));
    }
}
