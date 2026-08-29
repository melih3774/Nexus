using System.Collections.Frozen;
using PcCore.Protocol;

namespace PcCore.Capabilities.Abstractions;

/// <summary>Outcome of a policy check. Denials carry an internal reason that is logged, not returned.</summary>
public readonly record struct PolicyDecision(bool Allowed, string? DeniedReason)
{
    public static readonly PolicyDecision Allow = new(true, null);

    public static PolicyDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// Decides whether a caller may invoke an action. Evaluated once per request in
/// <see cref="CapabilityDispatcher"/> — the single choke point every transport funnels through.
/// </summary>
public interface ICapabilityPolicy
{
    PolicyDecision Evaluate(string action, CallerDescriptor caller);
}

/// <summary>
/// The v0.4 policy: the first real distinction between a local caller and the Primary Phone.
/// <para>
/// Deliberately a small typed rule set, not a permission framework. There is one owner, one PC and
/// one phone; roles, grants and permission tables would be machinery with no second user to serve.
/// </para>
/// </summary>
public sealed class DefaultCapabilityPolicy : ICapabilityPolicy
{
    /// <summary>
    /// What the Primary Phone may do remotely.
    /// <para>
    /// The phone is a <em>controller for the PC</em>. It is deliberately not the authority that
    /// decides which phone is trusted: pairing, unpairing and device management stay local-only, so
    /// possession of a phone credential can never be escalated into enrolling another device. That
    /// asymmetry is the whole point of this list being an allowlist rather than a denylist — a
    /// capability added in a later milestone is denied to the phone until someone deliberately
    /// adds it here.
    /// </para>
    /// </summary>
    private static readonly FrozenSet<string> DeviceAllowed = new[]
    {
        "system.get_info",
        "system.list_processes",
        "files.list",
        "files.stat",
        "screen.capture",
        "security.get_state",
        "security.arm",
        "security.disarm",
    }.ToFrozenSet(StringComparer.Ordinal);

    public PolicyDecision Evaluate(string action, CallerDescriptor caller)
    {
        ArgumentNullException.ThrowIfNull(caller);

        return caller.ActorType switch
        {
            // Local callers keep every v0.1–v0.3 behaviour unchanged. The owner sitting at the
            // machine already has full control of it; restricting them here would protect nothing.
            ActorType.Local => PolicyDecision.Allow,

            ActorType.Device => DeviceAllowed.Contains(action)
                ? PolicyDecision.Allow
                : PolicyDecision.Deny($"'{action}' is not available to a remote device"),

            // No AI adapter exists yet. Failing closed means the day one arrives, its access is a
            // deliberate decision rather than something it inherited by default.
            ActorType.Ai => PolicyDecision.Deny("AI callers have no capability grants yet"),

            _ => PolicyDecision.Deny("unknown actor type"),
        };
    }

    /// <summary>The remote allowlist, exposed so tests and diagnostics can read it rather than restate it.</summary>
    public static IReadOnlyCollection<string> RemoteAllowedActions => DeviceAllowed;
}

/// <summary>
/// Allows everything. Used only where a policy is structurally required but no restriction applies —
/// notably in-process tests that are not exercising policy.
/// </summary>
public sealed class AllowAllCapabilityPolicy : ICapabilityPolicy
{
    public PolicyDecision Evaluate(string action, CallerDescriptor caller) => PolicyDecision.Allow;
}
