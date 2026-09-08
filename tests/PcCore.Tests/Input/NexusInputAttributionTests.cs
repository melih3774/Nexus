using PcCore.Capabilities.Security.Detection;
using PcCore.Tests.TestSupport;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// The ledger that lets the security detector recognise Nexus's own input on the paths where no
/// signature survives.
/// <para>
/// The invariant every one of these tests is circling: the ledger may excuse exactly the events
/// Nexus caused, within a short window, and not one more. Too generous and an intruder's input gets
/// attributed to Nexus, which is the failure that makes the whole security feature worthless.
/// </para>
/// </summary>
public class NexusInputAttributionTests
{
    private static NexusInputAttribution Build(
        AdvanceableTimeProvider time,
        int windowMs = 400,
        int maxOutstanding = 256) =>
        new(
            new AttributionOptions { WindowMilliseconds = windowMs, MaxOutstanding = maxOutstanding },
            TestLog.For<NexusInputAttribution>(),
            time);

    [Fact]
    public void NothingIsClaimableUntilSomethingIsExpected()
    {
        var time = new AdvanceableTimeProvider();
        var attribution = Build(time);

        Assert.False(attribution.TryClaim(ActivityKind.PointerActivity, time.GetUtcNow()));
        Assert.Equal(0, attribution.Claimed);
    }

    [Fact]
    public void ExactlyAsManyObservationsAreClaimedAsWereExpected()
    {
        var time = new AdvanceableTimeProvider();
        var attribution = Build(time);

        attribution.Expect(ActivityKind.PointerActivity, 3);

        Assert.True(attribution.TryClaim(ActivityKind.PointerActivity, time.GetUtcNow()));
        Assert.True(attribution.TryClaim(ActivityKind.PointerActivity, time.GetUtcNow()));
        Assert.True(attribution.TryClaim(ActivityKind.PointerActivity, time.GetUtcNow()));

        // The fourth is somebody else's. This is the line between "Nexus moved the mouse" and
        // "a person moved the mouse while Nexus happened to be moving it too".
        Assert.False(attribution.TryClaim(ActivityKind.PointerActivity, time.GetUtcNow()));
        Assert.Equal(3, attribution.Claimed);
    }

    [Fact]
    public void AKindOnlyClaimsItsOwnObservations()
    {
        var time = new AdvanceableTimeProvider();
        var attribution = Build(time);

        attribution.Expect(ActivityKind.KeyboardActivity, 2);

        Assert.False(attribution.TryClaim(ActivityKind.PointerActivity, time.GetUtcNow()));
        Assert.True(attribution.TryClaim(ActivityKind.KeyboardActivity, time.GetUtcNow()));
    }

    [Fact]
    public void ClaimsExpire()
    {
        var time = new AdvanceableTimeProvider();
        var attribution = Build(time, windowMs: 400);

        attribution.Expect(ActivityKind.PointerActivity, 5);
        time.Advance(TimeSpan.FromMilliseconds(401));

        Assert.False(attribution.TryClaim(ActivityKind.PointerActivity, time.GetUtcNow()));
        Assert.Equal(5, attribution.Expired);
        Assert.Equal(0, attribution.Claimed);
    }

    [Fact]
    public void AnExpiredBatchDoesNotHideALiveOne()
    {
        var time = new AdvanceableTimeProvider();
        var attribution = Build(time, windowMs: 400);

        attribution.Expect(ActivityKind.PointerActivity, 2);
        time.Advance(TimeSpan.FromMilliseconds(300));
        attribution.Expect(ActivityKind.PointerActivity, 2);

        // The first batch is now stale, the second is not. Pruning has to walk past the stale one
        // rather than stop at it.
        time.Advance(TimeSpan.FromMilliseconds(150));

        Assert.True(attribution.TryClaim(ActivityKind.PointerActivity, time.GetUtcNow()));
        Assert.True(attribution.TryClaim(ActivityKind.PointerActivity, time.GetUtcNow()));
        Assert.False(attribution.TryClaim(ActivityKind.PointerActivity, time.GetUtcNow()));
        Assert.Equal(2, attribution.Expired);
    }

    /// <summary>
    /// The regression behind the linked list. A partly consumed batch has to stay at the front: put
    /// back at the end, its early expiry sits behind later ones, pruning peeks a newer head, and the
    /// old claim never expires — a ledger that slowly stops forgetting is one that can excuse an
    /// intruder.
    /// </summary>
    [Fact]
    public void APartlyConsumedBatchStillExpiresOnTime()
    {
        var time = new AdvanceableTimeProvider();
        var attribution = Build(time, windowMs: 400);

        attribution.Expect(ActivityKind.PointerActivity, 3);
        Assert.True(attribution.TryClaim(ActivityKind.PointerActivity, time.GetUtcNow()));

        time.Advance(TimeSpan.FromMilliseconds(200));
        attribution.Expect(ActivityKind.PointerActivity, 1);

        // 401 ms after the first batch, 201 ms after the second: the first batch's remaining two
        // must be gone, the second batch's one must survive.
        time.Advance(TimeSpan.FromMilliseconds(201));

        Assert.True(attribution.TryClaim(ActivityKind.PointerActivity, time.GetUtcNow()));
        Assert.False(attribution.TryClaim(ActivityKind.PointerActivity, time.GetUtcNow()));
        Assert.Equal(2, attribution.Expired);
    }

    [Fact]
    public void OutstandingClaimsAreBounded()
    {
        var time = new AdvanceableTimeProvider();
        var attribution = Build(time, maxOutstanding: 4);

        // A long paste declares two events per character. Without a ceiling one message would leave
        // the ledger able to excuse thousands of observations.
        attribution.Expect(ActivityKind.KeyboardActivity, 10_000);

        for (var i = 0; i < 4; i++)
        {
            Assert.True(attribution.TryClaim(ActivityKind.KeyboardActivity, time.GetUtcNow()));
        }

        Assert.False(attribution.TryClaim(ActivityKind.KeyboardActivity, time.GetUtcNow()));
    }

    [Fact]
    public void ExpectingNothingRecordsNothing()
    {
        var time = new AdvanceableTimeProvider();
        var attribution = Build(time);

        attribution.Expect(ActivityKind.PointerActivity, 0);
        attribution.Expect(ActivityKind.PointerActivity, -5);

        Assert.False(attribution.TryClaim(ActivityKind.PointerActivity, time.GetUtcNow()));
    }

    /// <summary>
    /// The null ledger has to fail towards "this was a person". It can then raise an alarm that
    /// turns out to be the owner, but never suppress one that was not.
    /// </summary>
    [Fact]
    public void TheNullLedgerNeverClaimsAnything()
    {
        var attribution = new NullNexusInputAttribution();

        attribution.Expect(ActivityKind.PointerActivity, 100);

        Assert.False(attribution.TryClaim(ActivityKind.PointerActivity, DateTimeOffset.UtcNow));
        Assert.Equal(0, attribution.Claimed);
    }
}
