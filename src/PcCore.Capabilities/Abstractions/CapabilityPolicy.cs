using System.Collections.Frozen;
using PcCore.Protocol;

namespace PcCore.Capabilities.Abstractions;

/// <summary>
/// Outcome of a policy check. Denials carry an internal reason that is logged, not returned, and the
/// protocol error code the caller receives (<c>not_authorized</c> unless a denial says otherwise).
/// </summary>
public readonly record struct PolicyDecision(bool Allowed, string? DeniedReason, string? Code = null)
{
    public static readonly PolicyDecision Allow = new(true, null);

    public static PolicyDecision Deny(string reason, string? code = null) => new(false, reason, code);
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
    /// <remarks>
    /// v0.7 lets the phone inspect and resolve security incidents, and nothing more. Deleting
    /// evidence and changing security configuration stay local-only and behind Windows Hello: a
    /// stolen phone must not be able to destroy the recording of the theft, or quietly turn the
    /// camera off before it happens.
    /// </remarks>
    /// <remarks>
    /// v0.6 adds live viewing and remote input. Both are a genuine step up in power — one lets the
    /// phone watch the desktop continuously, the other lets it type — and both are listed here
    /// explicitly rather than arriving as a side effect of existing. What stayed out is as
    /// deliberate: <c>indicator.*</c> is local-only, so the phone cannot quiet the sign that it is
    /// there, and <c>pairing.*</c> and <c>device.*</c> remain local-only exactly as before.
    /// </remarks>
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

        // v0.6 — live screen
        "screen.list_displays",
        "screen.stream.start",
        "screen.stream.stop",

        // v1.2 — the video pipeline. Every one of these is about the picture the phone is already
        // allowed to see: ask for a keyframe, report what it measured, read or change how much
        // bandwidth and CPU its own stream may use, and read the clock those measurements are in.
        // None of them reaches anything the phone could not already reach.
        "screen.stream.keyframe",
        "screen.stream.report",
        "media.quality.get",
        "media.quality.set",
        "media.time",

        // v0.6 — remote input. input.begin is what actually turns typing on; the rest do nothing
        // without it (SPEC.md 57).
        "input.begin",
        "input.end",
        "input.pointer",
        "input.button",
        "input.scroll",
        "input.key",
        "input.text",
        "input.release_all",

        // v0.7 - the security workflow. The phone receives the alert, so it must be able to see what
        // happened and answer it. Inspecting and resolving an incident is the whole point of the
        // notification it just got.
        "security.sessions.list",
        "security.sessions.get",
        "security.evidence.get",
        "security.this_was_me",
        "security.not_me",

        // v1.0 — the owner-authenticated session. These are the only things a phone that has not
        // proved its owner may reach, and they exist to let it do exactly one thing: prove it.
        "session.status",
        "session.challenge",
        "session.authorize",
        "session.resume",
        "session.lock",
        "session.enroll_key",

        // Live monitoring. Deliberately absent from WithoutOwnerAuthorization below: opening the
        // camera is the last thing a stolen-but-paired phone should be able to do.
        "media.live.start",
        "media.live.stop",
        "media.live.status",
        "media.live.keyframe",
        "media.live.report",

        // v1.3 - the phone updates its own app from its PC. Reading the release and downloading the
        // APK are the whole of it; installing anything on the PC (update.install) stays local.
        "update.check",
        "update.apk.get",

        // v1.4 - Files and Clipboard. Browsing, downloading and uploading within the filesystem
        // boundary, and text to and from the clipboard. Every one of these needs Full Remote - none is
        // in WithoutOwnerAuthorization below - and none reaches further than that session already
        // does: a session that can type into Explorer can already open, copy and save any of these
        // files. What they add is doing it without driving a desktop from a phone screen. Windows
        // permissions still apply, because PC Core runs as the owner and never elevated.
        "files.roots",
        "files.read",
        "files.hash",
        "files.upload.begin",
        "files.upload.chunk",
        "files.upload.finish",
        "files.upload.cancel",
        "clipboard.get",
        "clipboard.set",

        // v1.5 - PC Assistant. The owner, authenticated on the phone, can instruct PC Assistant: open and
        // continue tasks, start General ones, stop them, switch AUTO/ASK and answer ordinary approval
        // cards. Every one needs Full Remote, and not one of them runs anything itself. PC Assistant
        // runs Claude Code under its own policy and approvals, and administrator approvals stay on the
        // PC. This is still not a shell: there is no way through here to name a command or a process.
        "assistant.status",
        "assistant.subscribe",
        "assistant.unsubscribe",
        "assistant.tasks.list",
        "assistant.tasks.get",
        "assistant.tasks.create",
        "assistant.tasks.send",
        "assistant.tasks.stop",
        "assistant.tasks.set_mode",
        "assistant.approvals.resolve",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// What a phone may do before its owner has authenticated on it.
    /// <para>
    /// <b>An allowlist inside an allowlist, and deliberately tiny.</b> A stolen phone holds a valid
    /// device credential — that is what a stored credential is — so device authentication cannot be
    /// the thing that unlocks the desktop. What is left here is the bootstrap needed to authenticate
    /// (<c>session.*</c>), the two status reads the locked screen shows so it can say whether the PC
    /// is even reachable, and nothing else. No screen, no input, no files, no processes, no
    /// evidence.
    /// </para>
    /// <para>
    /// <c>security.get_state</c> stays because a locked Nexus must still be able to say "your PC is
    /// armed"; it returns a state word and no evidence. Answering an incident is not here: standing
    /// down an alarm is a decision, and decisions need the owner.
    /// </para>
    /// </summary>
    private static readonly FrozenSet<string> WithoutOwnerAuthorization = new[]
    {
        "system.get_info",
        "security.get_state",
        "session.status",
        "session.challenge",
        "session.authorize",
        "session.resume",
        "session.lock",
        "session.enroll_key",

        // The update path must not depend on the owner session: the case it exists for is a phone
        // whose app is too old to complete that session at all. What it reaches is a version number
        // and the bytes of the Nexus APK, which is not a secret and which Android will still refuse
        // to install without the owner confirming it on the phone.
        "update.check",
        "update.apk.get",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Everything a phone whose app is too old for this PC Core may do: find out, and fetch the fix.
    /// <para>
    /// Nothing else, whatever the owner session says. An old client is by definition one whose
    /// behaviour against the current protocol is untested, and access to screen, input, files or
    /// evidence is not granted just because it can reach the update.
    /// </para>
    /// </summary>
    private static readonly FrozenSet<string> WhenUpdateRequired = new[]
    {
        "update.check",
        "update.apk.get",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly IRemoteAuthorityProvider _authority;

    /// <summary>
    /// <paramref name="authority"/> decides whether a connection has completed owner
    /// authentication. It defaults to granting nothing, which is what every construction that does
    /// not care about the distinction should get: a policy that cannot tell must not assume.
    /// </summary>
    public DefaultCapabilityPolicy(IRemoteAuthorityProvider? authority = null) =>
        _authority = authority ?? NoRemoteAuthority.Instance;

    public PolicyDecision Evaluate(string action, CallerDescriptor caller)
    {
        ArgumentNullException.ThrowIfNull(caller);

        return caller.ActorType switch
        {
            // Local callers keep every v0.1–v0.3 behaviour unchanged. The owner sitting at the
            // machine already has full control of it; restricting them here would protect nothing.
            ActorType.Local => PolicyDecision.Allow,

            ActorType.Device => EvaluateDevice(action, caller),

            // No AI adapter exists yet. Failing closed means the day one arrives, its access is a
            // deliberate decision rather than something it inherited by default.
            ActorType.Ai => PolicyDecision.Deny("AI callers have no capability grants yet"),

            _ => PolicyDecision.Deny("unknown actor type"),
        };
    }

    private PolicyDecision EvaluateDevice(string action, CallerDescriptor caller)
    {
        if (caller.RequiresUpdate)
        {
            return WhenUpdateRequired.Contains(action)
                ? PolicyDecision.Allow
                : PolicyDecision.Deny(
                    $"'{action}' refused: the app (version code {caller.ClientVersionCode?.ToString() ?? "unreported"}) is older than this PC Core serves",
                    ErrorCodes.UpdateRequired);
        }

        if (!DeviceAllowed.Contains(action))
        {
            return PolicyDecision.Deny($"'{action}' is not available to a remote device");
        }

        if (WithoutOwnerAuthorization.Contains(action))
        {
            return PolicyDecision.Allow;
        }

        return _authority.AuthorityOf(caller) == RemoteAuthority.FullRemote
            ? PolicyDecision.Allow
            : PolicyDecision.Deny($"'{action}' needs an owner-authenticated session");
    }

    /// <summary>The remote allowlist, exposed so tests and diagnostics can read it rather than restate it.</summary>
    public static IReadOnlyCollection<string> RemoteAllowedActions => DeviceAllowed;

    /// <summary>The subset a phone may reach before its owner has authenticated on it.</summary>
    public static IReadOnlyCollection<string> ActionsWithoutOwnerAuthorization => WithoutOwnerAuthorization;
}

/// <summary>
/// Allows everything. Used only where a policy is structurally required but no restriction applies —
/// notably in-process tests that are not exercising policy.
/// </summary>
public sealed class AllowAllCapabilityPolicy : ICapabilityPolicy
{
    public PolicyDecision Evaluate(string action, CallerDescriptor caller) => PolicyDecision.Allow;
}
