using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Identity;

namespace PcCore.Windows;

/// <summary>
/// Protects the Primary Phone's shared secret at rest using DPAPI in
/// <see cref="DataProtectionScope.CurrentUser"/> scope.
/// <para>
/// DPAPI is the built-in Windows mechanism for exactly this: a per-user key managed by the OS,
/// derived from the user's credentials, with no key material for this application to store,
/// rotate, or leak. No cryptography is invented here — the protected blob is opaque to us.
/// </para>
/// <para>
/// What this buys: the secret file is useless if copied to another machine or opened under another
/// Windows account. What it does not buy: protection from code already running as this user.
/// Nothing at this layer can defend against that, and for a single-user personal system it is the
/// right boundary — an attacker with the owner's session can already act as the owner.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSecretProtector : ISecretProtector
{
    /// <summary>
    /// Additional entropy mixed into the DPAPI key. It is not itself a secret — it lives in this
    /// source file — but it scopes the blob to PC Core, so a blob protected by some other
    /// application on this account cannot be substituted for ours.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Nexus.PcCore.PrimaryPhone.v1");

    private readonly ILogger<WindowsSecretProtector> _logger;

    public WindowsSecretProtector(ILogger<WindowsSecretProtector> logger) => _logger = logger;

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[]? Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        try
        {
            return ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            // Wrong Windows account, a restored profile, or a tampered blob. The exception is
            // logged without any part of the ciphertext.
            _logger.LogError(ex, "A protected secret could not be unprotected for this Windows account.");
            return null;
        }
    }
}
