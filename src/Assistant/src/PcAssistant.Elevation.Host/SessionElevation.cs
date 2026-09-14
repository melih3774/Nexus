using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PcAssistant.Elevation.Host;

/// <summary>
/// The elevated identity a privileged operation runs as: the owner's own administrator
/// token, in the owner's own interactive session. Never SYSTEM.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ElevatedContext(
    SafeAccessTokenHandle token, string account, string sid, uint sessionId, string source) : IDisposable
{
    public SafeAccessTokenHandle Token { get; } = token;
    public string Account { get; } = account;
    public string Sid { get; } = sid;
    public uint SessionId { get; } = sessionId;

    /// <summary>How the token was obtained, for the audit record and for diagnostics.</summary>
    public string Source { get; } = source;

    public void Dispose() => Token.Dispose();
}

/// <summary>
/// Turning "there is a logged-on owner in session N" into "here is that owner's elevated
/// primary token", using the documented LocalSystem path.
///
/// The whole point of this class is what it does *not* do. It does not create a SYSTEM
/// process, does not log anyone on with credentials, and does not synthesise privilege that
/// the account does not already have. If the interactive account has no administrator token
/// — a standard user — there is nothing to elevate to and the request fails honestly rather
/// than falling back to anything.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SessionElevation
{
    /// <summary>Split-token elevation types, from TOKEN_ELEVATION_TYPE.</summary>
    private const int ElevationTypeDefault = 1;
    private const int ElevationTypeFull = 2;
    private const int ElevationTypeLimited = 3;

    /// <summary>
    /// Drops any impersonation token from this thread, so everything below runs as the service
    /// itself.
    ///
    /// This is the fix for a real failure, not defensive tidying, and the mechanism is worth
    /// spelling out because nothing about it is visible in the code that suffers from it.
    /// Windows resolves privilege checks against the <em>thread's</em> effective token, and
    /// .NET puts one there far more readily than it looks: <c>WindowsIdentity.GetCurrent()</c>
    /// self-impersonates in order to obtain a thread token at all, and reading an identity
    /// built from somebody else's token can leave that somebody on the thread. Once the
    /// owner's ordinary medium-integrity token was sitting on a listener thread, this service —
    /// still LocalSystem as a <em>process</em> — was asking Windows for the linked
    /// administrator token as the owner, which is exactly the caller Windows refuses to hand a
    /// primary token to. Every privileged operation failed as NotElevatable while every
    /// identity check passed, and the Win32 error pointed at the duplication rather than at
    /// the identity doing it.
    ///
    /// RevertToSelf on a thread that is not impersonating is a no-op, so this is safe to call
    /// at the top of anything privileged, which is what it is for.
    /// </summary>
    internal static void ActAsSelf() => NativeMethods.RevertToSelf();

    public static bool TryAcquire(uint sessionId, out ElevatedContext? context, out string error)
    {
        context = null;
        ActAsSelf();

        if (!NativeMethods.WTSQueryUserToken(sessionId, out var sessionToken))
        {
            error = $"No interactive user token for session {sessionId} " +
                    $"({new Win32Exception(Marshal.GetLastWin32Error()).Message}). " +
                    "The owner must be signed in on that session.";
            return false;
        }

        using (sessionToken)
        {
            string account, sid;
            try
            {
                using var identity = new WindowsIdentity(sessionToken.DangerousGetHandle());
                account = identity.Name;
                sid = identity.User?.Value ?? string.Empty;
            }
            catch (Exception ex)
            {
                error = "Could not read the interactive user's identity: " + ex.Message;
                return false;
            }

            if (!TryGetElevatedToken(sessionToken, out var elevated, out var source, out error))
                return false;

            var primary = elevated!;

            // Read the result back from Windows rather than assuming the duplicate kept its
            // elevation. A token that is not actually elevated must never be used to report a
            // successful "administrator" operation.
            if (!IsElevated(primary))
            {
                primary.Dispose();
                error = "The duplicated token is not elevated; refusing to run the operation.";
                return false;
            }

            var tokenSession = ReadUInt32(primary, NativeMethods.TokenSessionId);
            if (tokenSession is not null && tokenSession != sessionId)
            {
                primary.Dispose();
                error = $"The administrator token belongs to session {tokenSession}, not the requesting " +
                        $"session {sessionId}. Refusing to cross sessions.";
                return false;
            }

            context = new ElevatedContext(primary, account, sid, sessionId, source!);
            error = string.Empty;
            return true;
        }
    }

    /// <summary>Produces the owner's elevated <em>primary</em> token, or says why it cannot.</summary>
    private static bool TryGetElevatedToken(
        SafeAccessTokenHandle sessionToken, out SafeAccessTokenHandle? elevated, out string? source, out string error)
    {
        elevated = null;
        source = null;

        // Reading the interactive user's name above can leave their token on this thread, and
        // everything from here down is a privilege check against whatever is on this thread.
        ActAsSelf();

        var elevationType = ReadInt32(sessionToken, NativeMethods.TokenElevationType);

        if (elevationType == ElevationTypeLimited)
        {
            // The ordinary UAC case: the interactive token is the filtered one and Windows
            // keeps the full administrator token linked to it. This is exactly the token a
            // UAC consent prompt would have handed over — but only a caller holding
            // SeTcbPrivilege is given it as a primary token, so claim the privilege first.
            var privilege = EnableTcbPrivilege();

            if (!TryGetLinkedToken(sessionToken, out var linked, out error)) return false;
            using (linked)
            {
                if (!TryDuplicateAsPrimary(linked!, out elevated, out error, privilege)) return false;
            }
            source = "linked administrator token (split-token UAC)";
            return true;
        }

        if (elevationType == ElevationTypeFull || IsElevated(sessionToken))
        {
            // Either the interactive token is already elevated, or UAC filtering is not in
            // effect for this account. Duplicating it is the correct behaviour; nothing is
            // gained by looking for a link that does not exist.
            if (!TryDuplicateAsPrimary(sessionToken, out elevated, out error)) return false;
            source = elevationType == ElevationTypeFull
                ? "already-elevated interactive token"
                : "interactive token (no UAC filtering on this account)";
            return true;
        }

        error = "This account has no administrator token, so no elevation is possible. " +
                "Windows would require administrator credentials, which PC Assistant never asks for or stores. " +
                "Sign in as an administrator, or perform this operation yourself.";
        return false;
    }

    /// <summary>
    /// Copies a token into a primary token, which is what both starting a process and
    /// impersonating actually need.
    ///
    /// This is where live verification found the helper broken, and the reason is worth
    /// stating exactly, because the failure looked like a permissions problem and was not.
    /// Windows only hands the linked administrator token to a caller holding SeTcbPrivilege
    /// as a <em>primary</em> token; to anyone else it arrives as an identification-level
    /// impersonation token, and such a token cannot be turned into a primary token at all —
    /// DuplicateTokenEx refuses it with ERROR_BAD_IMPERSONATION_LEVEL whatever level is
    /// asked for. Every privileged operation then failed as NotElevatable, after passing
    /// every identity check. So the privilege is claimed first, and if the token still is
    /// not usable this says which of the two conditions was not met rather than repeating
    /// the Win32 message.
    /// </summary>
    internal static bool TryDuplicateAsPrimary(
        SafeAccessTokenHandle source, out SafeAccessTokenHandle? primary, out string error,
        string? privilegeDiagnostic = null)
    {
        primary = null;

        var type = ReadUInt32(source, NativeMethods.TokenType_Information);
        var level = ReadUInt32(source, NativeMethods.TokenImpersonationLevel_Information);
        var impersonation = type == (uint)NativeMethods.TokenType.Impersonation;

        if (impersonation && level < (uint)NativeMethods.SecurityImpersonationLevel.Impersonation)
        {
            error = "Windows returned the administrator token as an identification-level impersonation " +
                    "token, which cannot be converted into the primary token a process needs. That happens " +
                    "when the privileged helper does not hold SeTcbPrivilege. " +
                    $"[token type={Describe(type)}, impersonation level={Describe(level)}] " +
                    (privilegeDiagnostic ?? string.Empty);
            return false;
        }

        // A primary source has no impersonation level to be capped by; an impersonation source
        // may not be copied above its own level.
        var requested = impersonation
            ? (NativeMethods.SecurityImpersonationLevel)Math.Min(
                level ?? (uint)NativeMethods.SecurityImpersonationLevel.Impersonation,
                (uint)NativeMethods.SecurityImpersonationLevel.Impersonation)
            : NativeMethods.SecurityImpersonationLevel.Impersonation;

        if (!NativeMethods.DuplicateTokenEx(source, NativeMethods.TokenAllAccess, IntPtr.Zero,
                requested, NativeMethods.TokenType.Primary, out var copy))
        {
            error = "Could not duplicate the administrator token as a primary token: " +
                    new Win32Exception(Marshal.GetLastWin32Error()).Message + " " +
                    $"[token type={Describe(type)}, impersonation level={Describe(level)}, requested={requested}] " +
                    (privilegeDiagnostic ?? string.Empty);
            return false;
        }

        primary = copy;
        error = string.Empty;
        return true;
    }

    private static string Describe(uint? value) => value?.ToString() ?? "unreadable";

    /// <summary>
    /// Enables SeTcbPrivilege in this process's own token and says exactly what happened,
    /// because holding a privilege and having it enabled are different things and only the
    /// second changes what Windows hands back. LocalSystem holds it; nothing else this helper
    /// runs as does, and nothing here grants a privilege the account does not already have —
    /// AdjustTokenPrivileges cannot.
    ///
    /// Two routes to an adjustable handle, because the first live run failed here with a bare
    /// "access denied" and one route gives nothing to compare it against. Whatever happens,
    /// the privilege's observed state is reported, read with query access only so it survives
    /// a refusal to adjust.
    /// </summary>
    internal static string EnableTcbPrivilege()
    {
        // Adjust the *process* token, on a thread carrying nothing of its own. Both halves
        // matter. Windows judges privilege by the thread's effective token, so a leftover
        // impersonation would send the adjustment somewhere harmless and leave the real check
        // failing; and reading an account name is itself enough to put a token there, so the
        // name is resolved last, after everything privileged is done.
        ActAsSelf();

        try
        {
            using var token = RawAdjustableToken(out var openNote) ?? ManagedFallbackToken(ref openNote);
            if (token is null || token.IsInvalid)
                return $"SeTcbPrivilege: no adjustable process token ({openNote}); it is {ObservedState()}.";

            if (!NativeMethods.LookupPrivilegeValue(null, NativeMethods.SeTcbName, out var luid))
                return "SeTcbPrivilege: Windows does not know that privilege name.";

            var state = new NativeMethods.TokenPrivilege
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = NativeMethods.SePrivilegeEnabled,
            };
            var previous = default(NativeMethods.TokenPrivilege);

            var adjusted = NativeMethods.AdjustTokenPrivileges(token, false, ref state,
                Marshal.SizeOf<NativeMethods.TokenPrivilege>(), ref previous, out _);
            var lastError = Marshal.GetLastWin32Error();

            var observed = ObservedState();

            if (!adjusted)
                return $"SeTcbPrivilege: could not be adjusted ({new Win32Exception(lastError).Message}); it is {observed}.";

            if (lastError == NativeMethods.ErrorNotAllAssigned)
                return $"SeTcbPrivilege: not held by this process; it is {observed}. The helper must run as LocalSystem.";

            return $"SeTcbPrivilege: adjusted via {openNote}; it is {observed}.";
        }
        catch (Exception ex)
        {
            return $"SeTcbPrivilege: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            // Reading the privilege state goes through Windows calls that can leave a token of
            // their own behind. Everything after this is privileged too.
            ActAsSelf();
        }
    }

    private static SafeAccessTokenHandle? RawAdjustableToken(out string note)
    {
        if (NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(),
                NativeMethods.TokenAdjustPrivileges | NativeMethods.TokenQuery, out var token))
        {
            note = "OpenProcessToken";
            return token;
        }

        note = "OpenProcessToken: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return null;
    }

    /// <summary>
    /// The runtime's own route, used only when the raw one fails. It can answer with a thread
    /// token rather than the process's, which is why it is the fallback and not the default.
    /// </summary>
    private static SafeAccessTokenHandle? ManagedFallbackToken(ref string note)
    {
        var previous = note;
        try
        {
            var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.AdjustPrivileges | TokenAccessLevels.Query);
            if (identity is null)
            {
                note = previous + "; WindowsIdentity returned no token";
                return null;
            }

            // Detached from the identity so its disposal cannot close what we are still using.
            note = previous + "; WindowsIdentity";
            return TryDuplicateAsPrimary(identity.AccessToken, out var copy, out _) ? copy : null;
        }
        catch (Exception ex)
        {
            note = previous + "; WindowsIdentity: " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Whether this process holds SeTcbPrivilege and whether it is enabled, read from the
    /// process token with query access only, so it answers even when adjusting is refused.
    /// Without this, "not held" is a claim rather than an observation.
    /// </summary>
    internal static string ObservedState()
    {
        try
        {
            if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), NativeMethods.TokenQuery, out var token))
                return "unreadable (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + ")";

            using (token)
            {
                if (!NativeMethods.LookupPrivilegeValue(null, NativeMethods.SeTcbName, out var wanted))
                    return "unreadable (unknown privilege name)";

                NativeMethods.GetTokenInformation(token, NativeMethods.TokenPrivilegesInformation, IntPtr.Zero, 0, out var length);
                if (length <= 0) return "unreadable (no privilege list)";

                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenPrivilegesInformation, buffer, length, out _))
                        return "unreadable (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + ")";

                    // TOKEN_PRIVILEGES: a count, then that many LUID_AND_ATTRIBUTES of 12 bytes.
                    var count = Marshal.ReadInt32(buffer);
                    for (var i = 0; i < count; i++)
                    {
                        var entry = buffer + sizeof(int) + (i * 12);
                        if ((uint)Marshal.ReadInt32(entry) != wanted.LowPart) continue;
                        if (Marshal.ReadInt32(entry, 4) != wanted.HighPart) continue;

                        return (Marshal.ReadInt32(entry, 8) & NativeMethods.SePrivilegeEnabled) != 0
                            ? "present and ENABLED"
                            : "present but DISABLED";
                    }

                    return $"NOT PRESENT among this process's {count} privileges";
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        catch (Exception ex)
        {
            return "unreadable (" + ex.GetType().Name + ")";
        }
    }


    /// <summary>Succeeds only while the thread carries an impersonation token.</summary>
    private static bool ThreadIsImpersonating()
    {
        if (!NativeMethods.OpenThreadToken(NativeMethods.GetCurrentThread(), NativeMethods.TokenQuery,
                openAsSelf: false, out var token))
        {
            return Marshal.GetLastWin32Error() != NativeMethods.ErrorNoToken;
        }

        token.Dispose();
        return true;
    }

    private static string CurrentAccount()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.Name;
        }
        catch (Exception)
        {
            return "this account";
        }
    }

    internal static bool TryGetLinkedToken(SafeAccessTokenHandle token, out SafeAccessTokenHandle? linked, out string error)
    {
        linked = null;

        // Whether this returns a usable primary token or a useless identification-level one is
        // decided by the privileges of whoever is asking — which is the thread, not the process.
        ActAsSelf();
        var size = IntPtr.Size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenLinkedToken, buffer, size, out _))
            {
                error = "Could not read the linked administrator token: " +
                        new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            var handle = Marshal.ReadIntPtr(buffer);
            if (handle == IntPtr.Zero)
            {
                error = "Windows reported no linked administrator token for the interactive user.";
                return false;
            }

            linked = new SafeAccessTokenHandle(handle);
            error = string.Empty;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static bool IsElevated(SafeAccessTokenHandle token) => ReadInt32(token, NativeMethods.TokenElevation) > 0;

    /// <summary>Reports whether elevation is possible at all, without acquiring anything.</summary>
    public static bool ElevationAvailable(uint sessionId, out string account, out string reason)
    {
        account = string.Empty;
        ActAsSelf();

        if (!NativeMethods.WTSQueryUserToken(sessionId, out var sessionToken))
        {
            reason = $"No interactive user token for session {sessionId} " +
                     $"({new Win32Exception(Marshal.GetLastWin32Error()).Message}).";
            return false;
        }

        using (sessionToken)
        {
            try
            {
                using var identity = new WindowsIdentity(sessionToken.DangerousGetHandle());
                account = identity.Name;
            }
            catch (Exception) { /* the elevation answer does not depend on the display name */ }

            // Deliberately the very same code path an operation takes, all the way to a usable
            // primary token. Answering from a cheaper check is exactly how this helper came to
            // report "Ready" while every operation it was asked to perform failed — and the
            // reason travels with the answer, so the owner-facing panel says why rather than
            // leaving it to be discovered one failed operation at a time.
            if (!TryGetElevatedToken(sessionToken, out var elevated, out _, out reason)) return false;

            elevated!.Dispose();
            reason = string.Empty;
            return true;
        }
    }

    private static int ReadInt32(SafeAccessTokenHandle token, int informationClass)
        => (int)(ReadUInt32(token, informationClass) ?? 0);

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
