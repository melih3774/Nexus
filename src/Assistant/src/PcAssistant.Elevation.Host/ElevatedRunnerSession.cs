using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace PcAssistant.Elevation.Host;

/// <summary>
/// Runs one approved command elevated and brings back its output and exit code.
///
/// Why there is a second process at all: Windows does not allow handle inheritance across
/// sessions, so the session-0 service cannot hand a child in session 1 a pipe to write its
/// output into. Instead the service starts a copy of itself in the owner's session with the
/// owner's elevated token, and *that* process does an ordinary same-session redirected
/// launch. The runner is created suspended and assigned to a kill-on-close job object before
/// it is allowed to run, so cancelling this operation can only ever reach the process tree
/// this operation created.
///
/// The channel to the runner is a fresh, randomly named pipe whose DACL admits only SYSTEM
/// and Administrators — an unelevated process running as the owner cannot open it — and the
/// service additionally checks that the process which connected is the exact runner it just
/// started.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ElevatedRunnerSession : IDisposable
{
    private readonly ElevatedContext _context;
    private readonly string _pipeName;
    private readonly Action<string> _log;
    private readonly Lock _gate = new();
    private IntPtr _job;
    private LaunchedProcess? _runner;
    private bool _disposed;

    public ElevatedRunnerSession(ElevatedContext context, string operationId, Action<string> log)
    {
        _context = context;
        _log = log;
        _pipeName = "PcAssistant.Elevation.Run." + operationId;
        _job = ElevatedLauncher.CreateKillOnCloseJob();
    }

    /// <summary>
    /// Terminates this operation's process tree, or reports that there is nothing left to
    /// terminate.
    ///
    /// Taking the same lock as <see cref="Dispose"/> is the point. A cancel arriving just as
    /// the operation finishes would otherwise call TerminateJobObject on a handle that had
    /// already been closed — and Windows reuses handle values, so the worst case is killing
    /// something entirely unrelated. Cancellation must only ever reach what this operation
    /// created.
    /// </summary>
    public bool TryTerminate()
    {
        lock (_gate)
        {
            if (_disposed || _job == IntPtr.Zero) return false;
            return NativeMethods.TerminateJobObject(_job, 1);
        }
    }

    public PrivilegedResponse Execute(PrivilegedRequest request, TimeSpan commandTimeout)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        using var pipe = NamedPipeServerStreamAcl.Create(
            _pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 0, outBufferSize: 0, pipeSecurity: security);

        var host = Environment.ProcessPath ?? HelperPaths.HostExecutable;
        _runner = ElevatedLauncher.Launch(
            _context, host,
            OperationValidator.BuildCommandLine(host, ["--runner", _pipeName]),
            workingDirectory: null, visible: false, suspended: true);

        if (_job != IntPtr.Zero && !NativeMethods.AssignProcessToJobObject(_job, _runner.ProcessHandle))
            _log("could not assign the elevated runner to its job object; cancellation will be unavailable");

        NativeMethods.ResumeThread(_runner.ThreadHandle);

        // The whole operation gets the command's own timeout plus a fixed margin for
        // process start-up and the two short pipe hops.
        var deadline = commandTimeout + TimeSpan.FromSeconds(20);
        using var cancellation = new CancellationTokenSource(deadline);

        try
        {
            pipe.WaitForConnectionAsync(cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Terminate();
            return PrivilegedResponse.For(request, PrivilegedStatus.Failed,
                "The elevated runner did not start within " + deadline.TotalSeconds + " seconds.");
        }

        // The pipe ACL already excludes unelevated callers; this pins it to the one process
        // this session created, so no other elevated process can answer in its place.
        if (!NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var connectedPid) ||
            connectedPid != _runner.ProcessId)
        {
            Terminate();
            return PrivilegedResponse.For(request, PrivilegedStatus.Denied,
                "A process other than the elevated runner answered its private channel; the operation was abandoned.");
        }

        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        writer.WriteLine(JsonSerializer.Serialize(request, PrivilegedProtocol.Json));

        string? line;
        try
        {
            line = reader.ReadLineAsync(cancellation.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Terminate();
            return PrivilegedResponse.For(request, PrivilegedStatus.TimedOut,
                "The elevated command exceeded its approved time budget and its process tree was terminated.");
        }
        catch (IOException)
        {
            line = null;
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            // The runner going away without answering is what a cancellation looks like from
            // this side, because terminating its job closes the pipe.
            return PrivilegedResponse.For(request, PrivilegedStatus.Cancelled,
                "The elevated operation ended without reporting a result; it was cancelled or terminated.");
        }

        return JsonSerializer.Deserialize<PrivilegedResponse>(line, PrivilegedProtocol.Json)
               ?? PrivilegedResponse.For(request, PrivilegedStatus.Failed, "The elevated runner sent an unreadable result.");
    }

    private void Terminate() => TryTerminate();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            // Closing a kill-on-close job ends anything still inside it, so a runner that is
            // somehow still alive cannot outlive the operation that created it.
            if (_job != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_job);
                _job = IntPtr.Zero;
            }
        }

        _runner?.CloseHandles();
        _runner = null;
    }
}
