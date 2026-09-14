using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PcAssistant.Elevation.Host;
using Xunit;

namespace PcAssistant.Tests;

/// <summary>
/// The step that turns "Windows has an administrator token linked to this account" into a
/// token a process can actually be started with.
///
/// This exists because live verification found the helper broken here, and the failure was
/// misleading: every identity check passed, and then every privileged operation reported
/// NotElevatable with ERROR_BAD_IMPERSONATION_LEVEL. The cause is a documented Windows rule
/// with no middle ground. <c>GetTokenInformation(TokenLinkedToken)</c> hands the full
/// administrator token to a caller holding SeTcbPrivilege as a <em>primary</em> token, and to
/// everyone else as an <em>identification-level impersonation</em> token — and an
/// identification-level token cannot be duplicated into a primary token at any impersonation
/// level whatsoever. The helper asked for one anyway.
///
/// What can be proven from an ordinary unelevated test host is the half of that rule that
/// applies here: this process does not hold SeTcbPrivilege, so it gets the unusable token,
/// and the shipped code must say so precisely instead of passing it on. The other half — that
/// LocalSystem, holding the privilege, is given a primary token — is inherently privileged and
/// is proven against the installed service in <see cref="PrivilegedHelperLiveTests"/>.
/// </summary>
public sealed class LinkedAdministratorTokenTests
{
    /// <summary>TOKEN_ELEVATION_TYPE: 1 default, 2 full, 3 limited (UAC-filtered).</summary>
    private const int ElevationTypeLimited = 3;

    [SkippableFact]
    public void WindowsHandsAnUnprivilegedCallerAnAdministratorTokenItCannotUse()
    {
        using var identity = WindowsIdentity.GetCurrent();
        Skip.If(ReadUInt32(identity.AccessToken, NativeMethods.TokenElevationType) != ElevationTypeLimited,
            "This account has no UAC-filtered token, so there is no linked administrator token to read. " +
            "The behaviour under test only exists for split-token administrators.");

        Assert.True(SessionElevation.TryGetLinkedToken(identity.AccessToken, out var linked, out var linkError), linkError);
        using var administrator = linked!;

        // It really is the elevated token...
        Assert.True(SessionElevation.IsElevated(administrator));
        Assert.False(SessionElevation.IsElevated(identity.AccessToken));

        // ...and it really is unusable for starting a process, in exactly the documented way.
        Assert.Equal(
            (uint)NativeMethods.TokenType.Impersonation,
            ReadUInt32(administrator, NativeMethods.TokenType_Information));
        Assert.Equal(
            (uint)NativeMethods.SecurityImpersonationLevel.Identification,
            ReadUInt32(administrator, NativeMethods.TokenImpersonationLevel_Information));
    }

    [SkippableFact]
    public void AnIdentificationLevelTokenIsRefusedByNameRatherThanPassedOn()
    {
        using var identity = WindowsIdentity.GetCurrent();
        Skip.If(ReadUInt32(identity.AccessToken, NativeMethods.TokenElevationType) != ElevationTypeLimited,
            "Split-token administrator accounts only.");

        Assert.True(SessionElevation.TryGetLinkedToken(identity.AccessToken, out var linked, out _));
        using var administrator = linked!;

        Assert.False(SessionElevation.TryDuplicateAsPrimary(administrator, out var primary, out var error));
        Assert.Null(primary);

        // The message has to name the actual condition. "Bad impersonation level" sent the
        // first diagnosis of this bug looking at the wrong layer entirely.
        Assert.Contains("SeTcbPrivilege", error, StringComparison.Ordinal);
        Assert.Contains("identification-level", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnOrdinaryAccountCannotGrantItselfTheTcbPrivilege()
    {
        // AdjustTokenPrivileges enables what a token already holds; it never adds anything.
        // Only the helper service, running as LocalSystem, holds this one.
        var diagnostic = SessionElevation.EnableTcbPrivilege();

        Assert.Contains("SeTcbPrivilege", diagnostic, StringComparison.Ordinal);
        Assert.Contains("not held", diagnostic, StringComparison.OrdinalIgnoreCase);

        // And it reports what it observed rather than only what it concluded. "Not held" on its
        // own sent one live diagnosis down the wrong path; the privilege list settles it.
        Assert.Contains("NOT PRESENT among this process", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyingAPrimaryTokenSucceedsAndInventsNoElevation()
    {
        using var identity = WindowsIdentity.GetCurrent();

        Assert.True(SessionElevation.TryDuplicateAsPrimary(identity.AccessToken, out var primary, out var error), error);
        using var copy = primary!;

        Assert.Equal(
            (uint)NativeMethods.TokenType.Primary,
            ReadUInt32(copy, NativeMethods.TokenType_Information));

        Assert.Equal(SessionElevation.IsElevated(identity.AccessToken), SessionElevation.IsElevated(copy));
    }

    private static uint? ReadUInt32(SafeAccessTokenHandle token, int informationClass)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            return NativeMethods.GetTokenInformation(token, informationClass, buffer, sizeof(uint), out _)
                ? unchecked((uint)Marshal.ReadInt32(buffer))
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
