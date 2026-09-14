using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PcAssistant.Core.Bridge;
using PcAssistant.Core.Tasks;

namespace PcAssistant.Core.NexusBridge;

public enum NexusBridgeListenState
{
    Stopped = 0,
    Listening = 1,
    Connected = 2,

    /// <summary>Another process already holds the pipe name: a second PC Assistant, or a squatter.</summary>
    NameInUse = 3,
}

/// <summary>What the desktop shows about the bridge. Immutable, replaced on every change.</summary>
public sealed record NexusBridgeStatus(
    NexusBridgeListenState State,
    string? ConnectedClient = null,
    string? RefusedClient = null,
    string? RefusedReason = null)
{
    /// <summary>A Nexus executable that asked to connect and is waiting for the owner to trust it.</summary>
    public string? CandidateClient { get; init; }
}

/// <summary>Who is on the other end of an accepted pipe connection, as Windows reports it.</summary>
public sealed record NexusClientIdentity(int ProcessId, int SessionId, string? ImagePath, string? UserSid);

/// <summary>
/// The listening end of the Nexus bridge.
///
/// Three layers decide who gets in, and each one covers a gap in the one before it. The pipe's DACL
/// admits only the owner, and refuses network logons outright. But "the owner" is also every other
/// program the owner runs, so the connected process is identified from the kernel's own record of
/// the connection — its session, its token's user, and the executable it is running — and only the
/// one Nexus executable the owner explicitly trusted is served. A Nexus executable the owner has not
/// trusted yet is refused and offered to the owner on the desktop; anything else is refused and
/// logged.
///
/// One instance at a time, created with <c>FILE_FLAG_FIRST_PIPE_INSTANCE</c>. While Nexus is
/// connected there is no free instance for anything else to connect to, and if some other process
/// already holds the name when PC Assistant starts listening, creation fails and the desktop says so
/// instead of PC Assistant quietly sharing a name with it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NexusBridgeServer : IAsyncDisposable
{
    private readonly AssistantService _service;
    private readonly Func<string?> _trustedClient;
    private readonly Func<CancellationToken, Task<ClaudeStatus>> _claudeStatus;
    private readonly Action<string>? _log;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();

    private Task? _loop;
    private NexusBridgeStatus _status = new(NexusBridgeListenState.Stopped);

    public NexusBridgeServer(
        AssistantService service,
        Func<string?> trustedClient,
        Func<CancellationToken, Task<ClaudeStatus>> claudeStatus,
        Action<string>? log = null,
        string? pipeName = null)
    {
        _service = service;
        _trustedClient = trustedClient;
        _claudeStatus = claudeStatus;
        _log = log;
        _pipeName = pipeName ?? NexusBridgeProtocol.CurrentPipeName();
    }

    public string PipeName => _pipeName;

    public NexusBridgeStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public event EventHandler<NexusBridgeStatus>? StatusChanged;

    /// <summary>
    /// Test seam: lets a test stand in for the kernel's answer about who connected, so the refusal and
    /// candidate paths can be exercised without a second executable. The composition root never sets it.
    /// </summary>
    public Func<NamedPipeServerStream, NexusClientIdentity>? IdentifyOverride { get; init; }

    public void Start()
    {
        lock (_gate)
        {
            _loop ??= Task.Run(() => ListenLoopAsync(_shutdown.Token));
        }
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Update(Status with { State = NexusBridgeListenState.NameInUse, ConnectedClient = null });
                _log?.Invoke("nexus bridge: the pipe name is already in use (" + ex.Message + "); retrying.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            await using (pipe.ConfigureAwait(false))
            {
                try
                {
                    Update(Status with { State = NexusBridgeListenState.Listening, ConnectedClient = null });
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    await ServeAsync(pipe, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // One bad connection must never take the listener down.
                    _log?.Invoke("nexus bridge connection error: " + ex.GetType().Name + ": " + ex.Message);
                    try { await Task.Delay(200, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                finally
                {
                    try { if (pipe.IsConnected) pipe.Disconnect(); } catch (Exception) { /* already gone */ }
                }
            }
        }

        Update(Status with { State = NexusBridgeListenState.Stopped, ConnectedClient = null });
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var identity = (IdentifyOverride ?? Identify)(pipe);
        var verdict = Judge(identity, _trustedClient());

        if (verdict is not null)
        {
            var candidate = verdict.Code == NexusBridgeProtocol.Errors.ClientNotTrusted &&
                            identity.ImagePath is { } path &&
                            string.Equals(Path.GetFileName(path), NexusBridgeProtocol.NexusExecutableName, StringComparison.OrdinalIgnoreCase)
                ? path
                : null;

            _log?.Invoke($"nexus bridge refused pid {identity.ProcessId} ({identity.ImagePath ?? "unknown image"}): {verdict.Message}");
            Update(Status with
            {
                RefusedClient = identity.ImagePath,
                RefusedReason = verdict.Message,
                CandidateClient = candidate ?? Status.CandidateClient,
            });

            await NexusBridgeSession.RefuseAsync(pipe, verdict.Code, verdict.Message, token).ConfigureAwait(false);
            return;
        }

        _log?.Invoke($"nexus bridge connected: pid {identity.ProcessId} ({identity.ImagePath})");
        Update(Status with
        {
            State = NexusBridgeListenState.Connected,
            ConnectedClient = identity.ImagePath,
            RefusedClient = null,
            RefusedReason = null,
            CandidateClient = null,
        });

        await using var session = new NexusBridgeSession(pipe, _service, _claudeStatus, _log);
        await session.RunAsync(token).ConfigureAwait(false);

        _log?.Invoke("nexus bridge client disconnected");
    }

    /// <summary>
    /// The trust decision, apart from the pipe so it can be tested exactly. Null means serve it.
    /// Every refusal carries the protocol code Nexus will show and a sentence the owner can act on.
    /// </summary>
    public static BridgeError? Judge(NexusClientIdentity client, string? trustedExecutable)
    {
        using var self = WindowsIdentity.GetCurrent();
        var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;

        if (client.SessionId != sessionId)
            return new BridgeError(NexusBridgeProtocol.Errors.ClientNotTrusted, "The caller is in a different Windows session.");

        if (client.UserSid is null || !string.Equals(client.UserSid, self.User?.Value, StringComparison.OrdinalIgnoreCase))
            return new BridgeError(NexusBridgeProtocol.Errors.ClientNotTrusted, "The caller is not running as this Windows user.");

        if (string.IsNullOrEmpty(client.ImagePath))
            return new BridgeError(NexusBridgeProtocol.Errors.ClientNotTrusted, "Windows would not say which program is calling.");

        if (string.IsNullOrWhiteSpace(trustedExecutable))
        {
            return new BridgeError(NexusBridgeProtocol.Errors.ClientNotTrusted,
                "PC Assistant has not been told to trust any Nexus yet. Approve it in PC Assistant's Capabilities panel.");
        }

        return SamePath(client.ImagePath, trustedExecutable)
            ? null
            : new BridgeError(NexusBridgeProtocol.Errors.ClientNotTrusted,
                "This Nexus is not the one PC Assistant trusts. Approve it in PC Assistant's Capabilities panel.");
    }

    public static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Forgets a refused candidate once the owner has decided about it.</summary>
    public void ClearCandidate() => Update(Status with { CandidateClient = null, RefusedClient = null, RefusedReason = null });

    private NamedPipeServerStream CreatePipe()
    {
        using var self = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();

        // Network logons are denied explicitly, ahead of the owner's allow: the owner's own account
        // arriving over SMB is still not a local Nexus.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));

        security.AddAccessRule(new PipeAccessRule(
            self.User!, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            pipeSecurity: security);
    }

    private void Update(NexusBridgeStatus status)
    {
        lock (_gate)
        {
            if (_status == status) return;
            _status = status;
        }

        try { StatusChanged?.Invoke(this, status); }
        catch (Exception ex) { _log?.Invoke("nexus bridge status subscriber failed: " + ex.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_shutdown.IsCancellationRequested) await _shutdown.CancelAsync().ConfigureAwait(false);

        Task? loop;
        lock (_gate) loop = _loop;

        if (loop is not null)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception) { /* best effort */ }
        }

        _shutdown.Dispose();
    }

    // ------------------------------------------------------------ identity

    public static NexusClientIdentity Identify(NamedPipeServerStream pipe)
    {
        var handle = pipe.SafePipeHandle;
        var pid = GetNamedPipeClientProcessId(handle, out var processId) ? (int)processId : 0;
        var session = GetNamedPipeClientSessionId(handle, out var sessionId) ? (int)sessionId : -1;
        return pid == 0
            ? new NexusClientIdentity(0, session, null, null)
            : new NexusClientIdentity(pid, session, ImagePath((uint)pid), UserSid((uint)pid));
    }

    private static string? ImagePath(uint processId)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid) return null;

        var buffer = new StringBuilder(1024);
        var size = buffer.Capacity;
        return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
    }

    private static string? UserSid(uint processId)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid) return null;
        if (!OpenProcessToken(process, TokenQuery, out var token)) return null;

        using (token)
        {
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var length);
            if (length <= 0) return null;

            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, TokenUser, buffer, length, out _)) return null;
                return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, int length, out int returnLength);
}

/// <summary>
/// Claude Code's readiness, asked at most every few seconds. Checking it starts two short
/// <c>claude</c> processes, and the bridge must not turn a phone refreshing its screen into a stream
/// of them.
/// </summary>
public sealed class CachedClaudeStatus(Func<CancellationToken, Task<ClaudeStatus>> probe, TimeSpan? lifetime = null)
{
    private readonly TimeSpan _lifetime = lifetime ?? TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClaudeStatus? _last;
    private DateTimeOffset _at;

    /// <summary>Forgets the cached answer. Called when a turn has just shown Claude Code's state changed.</summary>
    public void Invalidate() => _last = null;

    public async Task<ClaudeStatus> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_last is not null && DateTimeOffset.UtcNow - _at < _lifetime) return _last;
            _last = await probe(cancellationToken).ConfigureAwait(false);
            _at = DateTimeOffset.UtcNow;
            return _last;
        }
        finally
        {
            _gate.Release();
        }
    }
}
