using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using PcAssistant.Elevation;
using PcAssistant.Elevation.Host;
using Xunit;

namespace PcAssistant.Tests;

/// <summary>
/// Which identity the privileged work actually runs as.
///
/// This is not a detail, and getting it wrong is invisible in the code that suffers from it.
/// Windows resolves privilege checks against the <em>thread's</em> effective token, not the
/// process's. The helper service is LocalSystem, but a listener thread carrying somebody
/// else's token asks Windows for the owner's linked administrator token as that somebody — and
/// Windows correctly answers with an identification-level impersonation token, which cannot
/// become the primary token a process needs. Live verification saw the result as "this account
/// has no administrator token": a true statement about the wrong account, reported after every
/// identity check had passed.
///
/// Two things put a token on the thread here, and both did. The pipe server impersonates its
/// client in order to identify it, and .NET's <c>WindowsIdentity.GetCurrent()</c>
/// self-impersonates just to obtain a thread token at all. So the state is checked after each,
/// with <c>OpenThreadToken</c> — which succeeds only while a thread carries an impersonation
/// token, and reports ERROR_NO_TOKEN otherwise. Comparing account names cannot see any of this
/// from a test host, where the server, the client and the process are all one user.
/// </summary>
public sealed class PipeServerImpersonationTests
{
    private const uint TokenQuery = 0x0008;

    [Fact]
    public async Task TheRequestHandlerRunsAsTheServiceAndNotAsTheCallingClient()
    {
        var pipeName = "PcAssistant.Elevation.Test." + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();

        var impersonating = true;
        var detail = string.Empty;
        ClientIdentity? seen = null;
        var log = new List<string>();

        var server = new PrivilegedPipeServer(identity.User!, (client, _) =>
        {
            seen = client;
            impersonating = ThreadIsImpersonating(out detail);
            return "{}";
        }, log.Add, pipeName);

        server.Start();
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);

            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            await writer.WriteLineAsync("{}");
            await reader.ReadLineAsync();
        }
        finally
        {
            server.Dispose();
        }

        // First: identification really happened. Without this the assertion below would pass
        // for the wrong reason — a server that never impersonated cannot leak an impersonation.
        Assert.NotNull(seen);
        Assert.Equal(identity.User!.Value, seen!.UserSid);
        Assert.DoesNotContain(log, entry => entry.Contains("identify", StringComparison.OrdinalIgnoreCase));

        Assert.False(impersonating, "The request handler ran while impersonating the pipe client. " + detail);

        // Hosted here, the extra listener threads log ERROR_ACCESS_DENIED creating their pipe
        // instances, and that is the DACL working rather than a fault: the owner SID is granted
        // read/write but deliberately not CreateNewInstance, so nothing running as the owner can
        // add an instance of this pipe name and intercept a later connection. The real service
        // runs as SYSTEM, which the same DACL grants full control, so it gets its whole pool.
        Assert.All(
            log.Where(entry => entry.Contains("Could not create", StringComparison.Ordinal)),
            entry => Assert.Contains("Windows error 5", entry, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePrivilegedPathActsAsItselfEvenAfterIdentityIsRead()
    {
        var pipeName = "PcAssistant.Elevation.Test." + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();

        var afterIdentityRead = false;
        var afterActAsSelf = true;
        var diagnostic = string.Empty;
        var detail = string.Empty;

        var server = new PrivilegedPipeServer(identity.User!, (_, _) =>
        {
            // Exactly what the helper does before it reaches for the administrator token: it
            // reads who the interactive user is. On a thread that has served a pipe client,
            // that read leaves a token behind — which is what quietly turned every later
            // privilege check into a question about the wrong account.
            using (var current = WindowsIdentity.GetCurrent()) _ = current.Name;
            afterIdentityRead = ThreadIsImpersonating(out detail);

            SessionElevation.ActAsSelf();
            afterActAsSelf = ThreadIsImpersonating(out _);

            // And the diagnostic must not manufacture the condition it reports.
            diagnostic = SessionElevation.EnableTcbPrivilege();
            return "{}";
        }, _ => { }, pipeName);

        server.Start();
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);

            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            await writer.WriteLineAsync("{}");
            await reader.ReadLineAsync();
        }
        finally
        {
            server.Dispose();
        }

        // The hazard is real in this context, so the test would be worthless without it.
        Assert.True(afterIdentityRead,
            "Reading the current identity no longer leaves a token on a pipe listener thread. " +
            "That is good news, but check whether the revert below is still load-bearing. " + detail);

        Assert.False(afterActAsSelf, "The privileged path would have run against someone else's token.");
        Assert.DoesNotContain("while impersonating", diagnostic, StringComparison.Ordinal);
    }

    /// <summary>True when this thread carries an impersonation token rather than the process's.</summary>
    private static bool ThreadIsImpersonating(out string detail)
    {
        if (NativeMethods.OpenThreadToken(NativeMethods.GetCurrentThread(), TokenQuery, openAsSelf: false, out var token))
        {
            using (token)
            {
                detail = "OpenThreadToken succeeded, so an impersonation token is on the thread.";
                return true;
            }
        }

        var error = Marshal.GetLastWin32Error();
        detail = "OpenThreadToken failed with " + error +
                 (error == NativeMethods.ErrorNoToken ? " (ERROR_NO_TOKEN, which is the correct state)." : ".");
        return error != NativeMethods.ErrorNoToken;
    }
}
