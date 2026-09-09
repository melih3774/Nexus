using Microsoft.Extensions.Logging;

namespace PcCore.Capabilities.Security.Detection;

/// <summary>
/// A short-lived record of input Nexus has just caused, so the detector can recognise its own work.
/// <para>
/// <b>Why a ledger and not just a tag.</b> Until native input existed, every event Nexus injected
/// carried a signature in <c>dwExtraInfo</c> and the detector matched it exactly. That still works
/// for pointer and keyboard events and remains the primary rule. It cannot work for touch: the
/// touch injection API has no field to put a signature in, and the mouse messages Windows promotes
/// a touch contact into carry Windows' own pen/touch signature instead of ours. Attribution that
/// depended solely on a tag would therefore have a hole exactly where Direct mode lives — and a hole
/// in attribution means Nexus raising a security alarm about the owner using their own PC.
/// </para>
/// <para>
/// <b>What it does.</b> Every time Nexus successfully injects, it declares what it just did and how
/// many events that was. The detector, on seeing activity it would otherwise call a person, asks
/// whether Nexus is owed one of those. If so it consumes the claim and attributes the event to
/// Nexus. Claims expire quickly and are counted exactly, so the worst case is bounded: the ledger
/// can absorb precisely the events Nexus caused, within a window measured in fractions of a second,
/// and not one more.
/// </para>
/// <para>
/// <b>The invariant this protects.</b> Physical local input while ARMED must raise a security
/// session; Nexus's own input while ARMED must not. A false positive here would teach the owner to
/// ignore alarms, which is the failure that kills the feature.
/// </para>
/// </summary>
public interface INexusInputAttribution
{
    /// <summary>Declares that Nexus just injected <paramref name="count"/> events of this kind.</summary>
    void Expect(ActivityKind kind, int count);

    /// <summary>
    /// Takes back <paramref name="count"/> declarations that turned out not to be needed, because
    /// the input they were declared for was refused.
    /// </summary>
    void Withdraw(ActivityKind kind, int count);

    /// <summary>
    /// Consumes one outstanding claim for this kind, if there is a live one.
    /// </summary>
    /// <returns>True when this observation is Nexus's own work.</returns>
    bool TryClaim(ActivityKind kind, DateTimeOffset now);

    /// <summary>Observations attributed to Nexus by correlation rather than by tag.</summary>
    long Claimed { get; }

    /// <summary>Declared events that no observation ever arrived for. Normal, and worth seeing.</summary>
    long Expired { get; }
}

public sealed class AttributionOptions
{
    public const string SectionName = "PcCore:Security:Attribution";

    /// <summary>
    /// How long a declared event stays claimable.
    /// <para>
    /// Long enough for Windows to deliver the resulting event through a low-level hook on a busy
    /// machine, and short enough that it cannot cover for a person who happens to touch the mouse
    /// at the same moment. A few hundred milliseconds is both.
    /// </para>
    /// </summary>
    public int WindowMilliseconds { get; set; } = 400;

    /// <summary>
    /// Most claims of one kind that can be outstanding at once.
    /// <para>
    /// A bound rather than a queue that grows: a burst of typed text declares two events per
    /// character, and without a ceiling a single paste would leave the ledger able to excuse
    /// thousands of observations long after Nexus stopped injecting.
    /// </para>
    /// </summary>
    public int MaxOutstanding { get; set; } = 256;
}

/// <summary>
/// The ledger. Small, exact, and deliberately forgetful.
/// </summary>
public sealed class NexusInputAttribution : INexusInputAttribution
{
    private readonly AttributionOptions _options;
    private readonly ILogger<NexusInputAttribution> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    // A linked list rather than a queue: a partially consumed claim has to stay at the front, and
    // re-enqueuing it would put a claim with an early expiry behind claims with later ones. Pruning
    // then peeks a newer head, the older claim never expires, and the ledger slowly stops forgetting
    // — which is the one failure mode that would let it excuse a person's input.
    private readonly Dictionary<ActivityKind, LinkedList<Claim>> _claims = [];

    private long _claimed;
    private long _expired;

    public NexusInputAttribution(
        AttributionOptions options,
        ILogger<NexusInputAttribution> logger,
        TimeProvider? time = null)
    {
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public long Claimed => Interlocked.Read(ref _claimed);

    public long Expired => Interlocked.Read(ref _expired);

    public void Expect(ActivityKind kind, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var expiresAt = now.AddMilliseconds(Math.Max(1, _options.WindowMilliseconds));

        lock (_gate)
        {
            if (!_claims.TryGetValue(kind, out var claims))
            {
                claims = new LinkedList<Claim>();
                _claims[kind] = claims;
            }

            Prune(claims, now);

            var room = Math.Max(0, _options.MaxOutstanding - Count(claims));

            if (room == 0)
            {
                return;
            }

            claims.AddLast(new Claim(expiresAt, Math.Min(count, room)));
        }
    }

    /// <summary>
    /// Takes back declarations for input that never happened.
    /// <para>
    /// The pairing with <see cref="Expect"/> is what lets a declaration be made <em>before</em> the
    /// input rather than after it. Declaring afterwards is a race Nexus loses on the virtual HID
    /// path: the report reaches Windows inside the call, the low-level hook fires while the call is
    /// still returning, and the claim is created after the detector has already decided. Declaring
    /// first and withdrawing on failure keeps the invariant that mattered — no outstanding claim for
    /// input that was refused — without depending on winning a race.
    /// </para>
    /// <para>
    /// Withdrawal is best-effort by construction: a claim already spent cannot be taken back, and a
    /// claim that expired first is gone anyway. Both leave the ledger holding less than it was told
    /// about, which is the safe direction.
    /// </para>
    /// </summary>
    public void Withdraw(ActivityKind kind, int count)
    {
        if (count <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (!_claims.TryGetValue(kind, out var claims))
            {
                return;
            }

            // Newest first: the withdrawal is for the declaration just made, and taking an older
            // one would spend a claim that still has input coming for it.
            while (count > 0 && claims.Last is { } last)
            {
                if (last.Value.Remaining > count)
                {
                    claims.Last!.Value = last.Value with { Remaining = last.Value.Remaining - count };
                    return;
                }

                count -= last.Value.Remaining;
                claims.RemoveLast();
            }
        }
    }

    public bool TryClaim(ActivityKind kind, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_claims.TryGetValue(kind, out var claims))
            {
                return false;
            }

            Prune(claims, now);

            var head = claims.First;

            if (head is null)
            {
                return false;
            }

            if (head.Value.Remaining > 1)
            {
                head.Value = head.Value with { Remaining = head.Value.Remaining - 1 };
            }
            else
            {
                claims.RemoveFirst();
            }
        }

        Interlocked.Increment(ref _claimed);
        return true;
    }

    private void Prune(LinkedList<Claim> claims, DateTimeOffset now)
    {
        while (claims.First is { } head && head.Value.ExpiresAtUtc <= now)
        {
            Interlocked.Add(ref _expired, head.Value.Remaining);
            claims.RemoveFirst();
        }
    }

    private static int Count(LinkedList<Claim> claims)
    {
        var total = 0;

        foreach (var claim in claims)
        {
            total += claim.Remaining;
        }

        return total;
    }

    /// <summary>Logged once at start-up so the window in force is visible rather than assumed.</summary>
    public void LogConfiguration() => _logger.LogInformation(
        "Nexus input attribution: {Window} ms window, at most {Max} outstanding claims per kind.",
        _options.WindowMilliseconds,
        _options.MaxOutstanding);

    private readonly record struct Claim(DateTimeOffset ExpiresAtUtc, int Remaining);
}

/// <summary>
/// A ledger that never claims anything.
/// <para>
/// For hosts with no input path at all, and for the tests that need to prove the tag rule on its
/// own. Attribution failing open — towards "this was a person" — is the safe direction: it can
/// raise an alarm that turns out to be the owner, never suppress one that was not.
/// </para>
/// </summary>
public sealed class NullNexusInputAttribution : INexusInputAttribution
{
    public void Expect(ActivityKind kind, int count)
    {
    }

    public void Withdraw(ActivityKind kind, int count)
    {
    }

    public bool TryClaim(ActivityKind kind, DateTimeOffset now) => false;

    public long Claimed => 0;

    public long Expired => 0;
}
