namespace PcCore.Capabilities.Input;

/// <summary>Which input kind a routing decision is about.</summary>
public enum InputKind
{
    Mouse,
    Keyboard,
    Touch,
}

/// <summary>
/// Everything that has to be true before virtual HID may carry input, gathered in one place.
/// <para>
/// <b>Why a record of conditions rather than checks at the call sites.</b> Seven things must hold,
/// and they are owned by five different components: the driver client, the protocol handshake, the
/// device registry, the Raw Input listener, and the desktop policy. Checking them where input is
/// submitted means seven checks per kind, each of which can be forgotten, reordered, or quietly
/// dropped when someone adds a capability. Gathering them means the answer is computed once and the
/// reasons are reportable.
/// </para>
/// </summary>
public sealed record VirtualHidConditions
{
    /// <summary>The client has a live handle to the driver's control device.</summary>
    public required bool DriverConnected { get; init; }

    /// <summary>The driver's protocol version is one this build knows.</summary>
    public required bool ProtocolCompatible { get; init; }

    /// <summary>
    /// The device identities the driver published have been resolved and recorded.
    /// <para>
    /// <b>This is the condition that must not be skipped.</b> Routing HID input before identity is
    /// resolved opens a window in which Nexus's own input arrives at the security detector from a
    /// device nothing has vouched for — which is to say, looking exactly like a person. The window
    /// would be brief and the consequence would be an incident raised against the owner using their
    /// own PC.
    /// </para>
    /// </summary>
    public required bool IdentitiesResolved { get; init; }

    /// <summary>The Raw Input listener is running and resolving device paths.</summary>
    public required bool AttributionHealthy { get; init; }

    /// <summary>The security monitor is actually consuming the attribution ledger.</summary>
    public required bool AttributionSubscribed { get; init; }

    /// <summary>The current input desktop is the ordinary one.</summary>
    public required bool DesktopAllowsInput { get; init; }

    /// <summary>Whether the backend published a device for this kind at all.</summary>
    public required IReadOnlySet<InputKind> AvailableKinds { get; init; }

    /// <summary>
    /// Held state whose release could not be confirmed. Blocks routing until a fresh backend
    /// instance accepts a neutral report.
    /// </summary>
    public bool NeutralizationUncertain { get; init; }

    /// <summary>
    /// Where the backend is in its connection life, in words. Diagnostic only — never read by
    /// <see cref="VirtualHidEligibility"/>, which decides on the booleans above and nothing else.
    /// <para>
    /// <b>It carries the one fact none of the booleans can.</b> They all describe the backend; none
    /// of them describes whether anything is <em>driving</em> it. When nothing was, every one of
    /// them read false and the reported reason was "the Nexus HID driver is not connected" — the
    /// same sentence a machine with no driver produces, on a machine where the driver was installed,
    /// started and waiting. This says which.
    /// </para>
    /// </summary>
    public string? BackendPhase { get; init; }

    public static VirtualHidConditions None => new()
    {
        DriverConnected = false,
        ProtocolCompatible = false,
        IdentitiesResolved = false,
        AttributionHealthy = false,
        AttributionSubscribed = false,
        DesktopAllowsInput = false,
        AvailableKinds = new HashSet<InputKind>(),
    };
}

/// <summary>Why virtual HID is not carrying one kind of input, when it is not.</summary>
public enum VirtualHidIneligibility
{
    None,
    DriverNotConnected,
    ProtocolIncompatible,
    CapabilityNotPublished,
    IdentityNotResolved,
    AttributionUnhealthy,
    AttributionNotSubscribed,
    DesktopBlocked,
    NeutralizationUncertain,
}

/// <summary>One decision, with the reason attached.</summary>
public readonly record struct VirtualHidVerdict(bool Eligible, VirtualHidIneligibility Reason)
{
    public string Describe() => Reason switch
    {
        VirtualHidIneligibility.None => "eligible",
        VirtualHidIneligibility.DriverNotConnected => "the Nexus HID driver is not connected",
        VirtualHidIneligibility.ProtocolIncompatible => "the driver speaks an incompatible protocol version",
        VirtualHidIneligibility.CapabilityNotPublished => "the driver published no device for this input kind",
        VirtualHidIneligibility.IdentityNotResolved => "the Nexus device identity has not been resolved",
        VirtualHidIneligibility.AttributionUnhealthy => "Raw Input attribution is not healthy",
        VirtualHidIneligibility.AttributionNotSubscribed => "security attribution is not subscribed",
        VirtualHidIneligibility.DesktopBlocked => "the current input desktop does not permit remote input",
        _ => "held input state from a previous backend has not been confirmed released",
    };
}

/// <summary>
/// Decides, per input kind, whether virtual HID may carry input.
/// <para>
/// <b>Order matters, and it is not arbitrary.</b> The conditions are evaluated from the most
/// fundamental outward, so the reported reason is the most useful one: a machine with no driver says
/// "not connected" rather than "identity not resolved", which would be true but would send the owner
/// looking in the wrong place.
/// </para>
/// <para>
/// <b>Attribution is a hard gate, not a preference.</b> Virtual HID input is indistinguishable from
/// a person's at the security detector unless attribution is working. Routing through it while
/// attribution is degraded would mean Nexus quietly raising incidents about its own owner, so the
/// answer is to fall back rather than to proceed and hope.
/// </para>
/// </summary>
public static class VirtualHidEligibility
{
    public static VirtualHidVerdict Evaluate(VirtualHidConditions conditions, InputKind kind)
    {
        ArgumentNullException.ThrowIfNull(conditions);

        if (!conditions.DriverConnected)
        {
            return new VirtualHidVerdict(false, VirtualHidIneligibility.DriverNotConnected);
        }

        if (!conditions.ProtocolCompatible)
        {
            return new VirtualHidVerdict(false, VirtualHidIneligibility.ProtocolIncompatible);
        }

        if (!conditions.AvailableKinds.Contains(kind))
        {
            // Per-kind, deliberately. A backend with a mouse and a keyboard and no digitizer is a
            // realistic install, and it should carry the two it has rather than none of the three.
            return new VirtualHidVerdict(false, VirtualHidIneligibility.CapabilityNotPublished);
        }

        if (!conditions.IdentitiesResolved)
        {
            return new VirtualHidVerdict(false, VirtualHidIneligibility.IdentityNotResolved);
        }

        if (!conditions.AttributionHealthy)
        {
            return new VirtualHidVerdict(false, VirtualHidIneligibility.AttributionUnhealthy);
        }

        if (!conditions.AttributionSubscribed)
        {
            return new VirtualHidVerdict(false, VirtualHidIneligibility.AttributionNotSubscribed);
        }

        if (!conditions.DesktopAllowsInput)
        {
            return new VirtualHidVerdict(false, VirtualHidIneligibility.DesktopBlocked);
        }

        if (conditions.NeutralizationUncertain)
        {
            return new VirtualHidVerdict(false, VirtualHidIneligibility.NeutralizationUncertain);
        }

        return new VirtualHidVerdict(true, VirtualHidIneligibility.None);
    }

    /// <summary>
    /// The backend that should carry one kind of input, given HID eligibility and whether the
    /// elevated broker is available.
    /// <para>
    /// The whole routing priority, in one expression, so there is exactly one place where it can be
    /// wrong. The owner never chooses; this does.
    /// </para>
    /// </summary>
    public static InputBackend Route(
        VirtualHidConditions conditions,
        InputKind kind,
        bool elevatedBrokerReady) =>
        Evaluate(conditions, kind).Eligible
            ? InputBackend.VirtualHid
            : elevatedBrokerReady
                ? InputBackend.ElevatedInjection
                : InputBackend.LocalInjection;
}
