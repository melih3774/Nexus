namespace PcCore.Capabilities.Security;

/// <summary>What a verification attempt produced.</summary>
public enum OwnerVerificationResult
{
    /// <summary>Windows confirmed the person at the machine is the owner.</summary>
    Verified,

    /// <summary>The prompt was shown and not satisfied — cancelled, or the wrong face or finger.</summary>
    Declined,

    /// <summary>
    /// No verification is configured on this machine. Treated as a refusal for actions that require
    /// it: a check that silently passes when it cannot run is not a check.
    /// </summary>
    Unavailable,

    /// <summary>Windows returned an error. Also a refusal.</summary>
    Failed,
}

/// <summary>
/// Asks Windows to confirm the owner is present, for the small set of actions that weaken security.
/// <para>
/// <b>Windows owns the biometrics entirely.</b> Nexus does not read a fingerprint, does not see a
/// face, does not store a template, and does not learn which method was used. It asks a yes-or-no
/// question through the standard consent API and receives a yes or a no. Anything more would mean
/// this project holding biometric data, which it has no business doing and no way to protect
/// better than the operating system already does.
/// </para>
/// <para>
/// It gates <em>local</em> actions at the PC. It is not an authentication mechanism for the phone —
/// the phone proves itself with its shared secret and, for acknowledgement, its own device
/// biometrics.
/// </para>
/// </summary>
public interface IOwnerVerifier
{
    /// <summary>Whether Windows can prompt at all. Cheap; may still fail when actually asked.</summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Prompts the person at the machine.
    /// </summary>
    /// <param name="reason">
    /// Shown in the Windows prompt so the owner knows what they are approving. Never a secret.
    /// </param>
    ValueTask<OwnerVerificationResult> VerifyAsync(string reason, CancellationToken cancellationToken);
}

/// <summary>
/// A verifier for hosts without Windows Hello.
/// <para>
/// Reports <see cref="OwnerVerificationResult.Unavailable"/> rather than pretending to succeed. The
/// actions that need it then refuse, which is the safe direction: an owner who cannot verify is
/// asked to set up Windows Hello, not quietly given the keys.
/// </para>
/// </summary>
public sealed class UnavailableOwnerVerifier : IOwnerVerifier
{
    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);

    public ValueTask<OwnerVerificationResult> VerifyAsync(string reason, CancellationToken cancellationToken) =>
        ValueTask.FromResult(OwnerVerificationResult.Unavailable);
}
