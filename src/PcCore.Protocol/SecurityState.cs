using System.Text.Json.Serialization;

namespace PcCore.Protocol;

/// <summary>
/// The owner's presence / security state.
/// <para>
/// This lives in <c>PcCore.Protocol</c> rather than beside the service that owns it because its
/// <em>serialized spelling</em> is a contract: callers switch on these strings exactly as they
/// switch on <see cref="ErrorCodes"/>. Keeping it here also keeps <c>System.Text.Json</c> confined
/// to this assembly (SPEC.md 4.4).
/// </para>
/// <para>
/// The wire values are deliberately SCREAMING_SNAKE_CASE and are pinned by attribute rather than
/// inherited from the codec's naming policy, so renaming a C# member can never silently change
/// the protocol.
/// </para>
/// </summary>
public enum SecurityState
{
    /// <summary>
    /// The owner is considered present and the machine operates normally. No security session is
    /// or can be active.
    /// </summary>
    [JsonStringEnumMemberName("OWNER_PRESENT")]
    OwnerPresent,

    /// <summary>
    /// The owner has deliberately indicated they are away. v0.2 only records the fact — there is
    /// no activity detection, and nothing transitions out of this state on its own.
    /// </summary>
    [JsonStringEnumMemberName("ARMED")]
    Armed,

    /// <summary>
    /// A security session has started. v0.2 attaches no behaviour to this state: no recording,
    /// no capture, no notification. It exists so later milestones have a state to hang those on.
    /// </summary>
    [JsonStringEnumMemberName("SECURITY_SESSION")]
    SecuritySession,
}

/// <summary>Server-originated event names. Kept beside the states they describe.</summary>
public static class SecurityEvents
{
    /// <summary>Published only when the state actually changed. Never for a no-op.</summary>
    public const string StateChanged = "security.state_changed";
}
