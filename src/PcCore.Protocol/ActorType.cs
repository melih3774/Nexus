using System.Text.Json.Serialization;

namespace PcCore.Protocol;

/// <summary>
/// What kind of caller is on the other end of a request.
/// <para>
/// <b>v0.2 status: typed metadata only.</b> Nothing reads this to make an authorization decision,
/// and nothing in this milestone can produce anything other than <see cref="Local"/>. It exists so
/// that when device pairing and an AI bridge arrive, the identity they need is already threaded
/// through the dispatcher rather than being retrofitted into every capability.
/// </para>
/// </summary>
public enum ActorType
{
    /// <summary>
    /// A caller on this machine, reaching PC Core over loopback or in-process. This is the only
    /// value v0.2 ever produces.
    /// </summary>
    [JsonStringEnumMemberName("LOCAL")]
    Local,

    /// <summary>
    /// A paired remote device — the future Android client. Requires authentication that does not
    /// exist yet, so v0.2 never mints one.
    /// </summary>
    [JsonStringEnumMemberName("DEVICE")]
    Device,

    /// <summary>
    /// A future AI adapter acting on the owner's behalf. Separate from <see cref="Local"/> because
    /// an AI caller will warrant a different policy, not because it arrives differently.
    /// </summary>
    [JsonStringEnumMemberName("AI")]
    Ai,
}
