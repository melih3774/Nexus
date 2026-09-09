namespace PcCore.Capabilities.Security.Detection;

/// <summary>
/// What Raw Input saw a moment ago, so the low-level hook's events can be attributed to a device.
/// <para>
/// <b>This exists because of a mistake worth writing down.</b> The Raw Input listener was built on
/// the belief that it and the low-level hook never see the same event — true for <c>SendInput</c>,
/// which goes through no device and so raises no <c>WM_INPUT</c>, and <b>false for everything
/// else</b>. A physical mouse click raises a hook event <em>and</em> a <c>WM_INPUT</c>, and so will
/// a Nexus HID report once the driver is installed, because a virtual HID device is a real device.
/// Feeding Raw Input into the monitor as a second event source would therefore have doubled every
/// physical action — the exact duplication the design forbids.
/// </para>
/// <para>
/// <b>So Raw Input is not an event source. It is an oracle.</b> The hook remains the single stream
/// into <see cref="SecurityActivityMonitor"/>, which keeps every existing coalescing, noise and
/// incident rule untouched. This ledger records only one thing: that a device Nexus published
/// produced input of some kind, just now. The monitor asks, and a hook event with a matching recent
/// observation is Nexus's own.
/// </para>
/// <para>
/// <b>It is deliberately the same shape as <c>INexusInputAttribution</c></b>, which solves the same
/// problem for injected touch: a bounded window, an explicit kind, and a claim that is consumed
/// rather than merely matched. Two mechanisms with one shape are easier to reason about than two
/// shapes, and the reasoning here is the part that has to be right.
/// </para>
/// <para>
/// <b>Only trusted observations are recorded.</b> A physical device producing input records nothing,
/// because there is nothing to excuse — the hook event should count, and it does. That asymmetry is
/// what makes this fail closed: an empty ledger attributes nothing, so a ledger that is broken,
/// stopped, or never started causes every event to be treated as a person. The failure direction is
/// a false alarm, never a missed intruder.
/// </para>
/// </summary>
public interface IRawInputSourceLedger
{
    /// <summary>
    /// Records that a device Nexus published produced input of this kind.
    /// <para>
    /// Called only from the Raw Input listener, and only for an observation whose device path
    /// matched a published Nexus identity.
    /// </para>
    /// </summary>
    void RecordTrustedNexusInput(ActivityKind kind, DateTimeOffset atUtc);

    /// <summary>
    /// Consumes an outstanding observation of this kind, if one is within the window.
    /// <para>
    /// Consuming rather than peeking is what bounds the effect: one observation excuses one hook
    /// event. A burst of Nexus HID reports excuses exactly as many hook events as it produced, and a
    /// person moving a real mouse during that burst still produces an event nothing excuses.
    /// </para>
    /// </summary>
    bool TryAttributeToNexus(ActivityKind kind, DateTimeOffset atUtc);

    /// <summary>True while the ledger is receiving observations from a running listener.</summary>
    bool IsHealthy { get; }

    /// <summary>How many observations are outstanding right now. Diagnostics only.</summary>
    int Outstanding { get; }
}

/// <summary>The in-memory implementation. One per PC Core process.</summary>
public sealed class RawInputSourceLedger : IRawInputSourceLedger
{
    /// <summary>
    /// How long an observation can excuse a hook event.
    /// <para>
    /// The two arrive from different Windows mechanisms for one action, so they are close but not
    /// simultaneous. This is the same order as the 400 ms <c>INexusInputAttribution</c> window and
    /// chosen for the same reason: long enough to cover the gap between two deliveries of one
    /// event, short enough that it cannot span a person's separate action.
    /// </para>
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The most outstanding observations of one kind that will ever be held.
    /// <para>
    /// A cap matters: without one, a stuck or flooding device could bank excuses indefinitely and
    /// then absorb a person's input long afterwards. Beyond this, the oldest is dropped.
    /// </para>
    /// </summary>
    public const int MaxOutstandingPerKind = 256;

    private readonly Lock _gate = new();
    private readonly Dictionary<ActivityKind, Queue<DateTimeOffset>> _observations = [];

    private bool _healthy;

    public bool IsHealthy
    {
        get
        {
            lock (_gate)
            {
                return _healthy;
            }
        }
    }

    /// <summary>
    /// Marks the ledger as fed by a running listener, or not.
    /// <para>
    /// Set false when the listener stops or fails to start. An unhealthy ledger attributes nothing —
    /// see the type remarks — and virtual HID must not be eligible for routing while it is unhealthy,
    /// because HID input would then be indistinguishable from a person.
    /// </para>
    /// </summary>
    public void SetHealthy(bool healthy)
    {
        lock (_gate)
        {
            _healthy = healthy;

            if (!healthy)
            {
                _observations.Clear();
            }
        }
    }

    public void RecordTrustedNexusInput(ActivityKind kind, DateTimeOffset atUtc)
    {
        lock (_gate)
        {
            if (!_healthy)
            {
                return;
            }

            if (!_observations.TryGetValue(kind, out var queue))
            {
                queue = new Queue<DateTimeOffset>();
                _observations[kind] = queue;
            }

            queue.Enqueue(atUtc);

            while (queue.Count > MaxOutstandingPerKind)
            {
                queue.Dequeue();
            }
        }
    }

    public bool TryAttributeToNexus(ActivityKind kind, DateTimeOffset atUtc)
    {
        lock (_gate)
        {
            if (!_healthy || !_observations.TryGetValue(kind, out var queue))
            {
                return false;
            }

            // Expired observations are discarded rather than matched. An observation older than the
            // window belonged to an action that has already been accounted for.
            while (queue.Count > 0 && atUtc - queue.Peek() > Window)
            {
                queue.Dequeue();
            }

            if (queue.Count == 0)
            {
                return false;
            }

            // Also refuse an observation from the future beyond the window, which would mean the
            // clocks disagree; attributing on that basis is guessing.
            if (queue.Peek() - atUtc > Window)
            {
                return false;
            }

            queue.Dequeue();
            return true;
        }
    }

    /// <summary>How many observations are outstanding right now. Diagnostics only.</summary>
    public int Outstanding
    {
        get
        {
            lock (_gate)
            {
                return _observations.Values.Sum(q => q.Count);
            }
        }
    }
}
