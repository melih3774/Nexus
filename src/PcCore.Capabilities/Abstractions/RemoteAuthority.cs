namespace PcCore.Capabilities.Abstractions;

/// <summary>
/// How much a remote connection is allowed to do.
/// <para>
/// Two values, and the gap between them is the whole point. Authenticating as the Primary Phone
/// proves <em>which device</em> is calling — it is a credential stored on the phone, and a phone can
/// be picked up by somebody else. Full Remote proves <em>who is holding it right now</em>, because
/// it can only be reached by a signature from a key Android will not use until the system biometric
/// or device-credential prompt has been satisfied.
/// </para>
/// </summary>
public enum RemoteAuthority
{
    /// <summary>
    /// Authenticated as the paired device, and nothing more. Enough to see whether the PC is there
    /// and what the security state is; not enough to look at the screen or touch anything.
    /// </summary>
    None,

    /// <summary>
    /// A Full Remote Session: the owner authenticated to Android and the phone proved it. Carries
    /// the authority to view and control the desktop, including elevated windows and the UAC Secure
    /// Desktop.
    /// </summary>
    FullRemote,
}

/// <summary>
/// Answers "how much may this caller do", for the policy.
/// <para>
/// Separate from the policy so the rule and the state live apart: the policy says which actions need
/// Full Remote, and this says whether a given connection has it. The policy is a frozen set that can
/// be read; this is mutable session state that changes when the owner authenticates, when their
/// phone locks, and when a window elapses.
/// </para>
/// </summary>
public interface IRemoteAuthorityProvider
{
    RemoteAuthority AuthorityOf(CallerDescriptor caller);
}

/// <summary>
/// Grants nothing. The correct default anywhere the real service is not wired: a component that
/// cannot tell whether the owner authenticated must not assume they did.
/// </summary>
public sealed class NoRemoteAuthority : IRemoteAuthorityProvider
{
    public static readonly NoRemoteAuthority Instance = new();

    public RemoteAuthority AuthorityOf(CallerDescriptor caller) => RemoteAuthority.None;
}
