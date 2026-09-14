using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PcAssistant.Elevation.Host;

/// <summary>Who is on the other end of the pipe, established by Windows rather than claimed by the caller.</summary>
internal sealed record ClientIdentity(
    uint ProcessId,
    string UserSid,
    string UserName,
    uint SessionId,
    int IntegrityLevel,
    string? ImagePath);

/// <summary>
/// The privileged boundary's front door.
///
/// Four things are enforced here, in order, before a request is even parsed:
///
/// 1. The pipe's DACL names exactly SYSTEM, Administrators and the one owner SID recorded at
///    install time. Another user account on this PC cannot open it at all.
/// 2. PIPE_REJECT_REMOTE_CLIENTS. This helper is local-only, enforced by Windows and not by
///    a hostname check.
/// 3. FILE_FLAG_FIRST_PIPE_INSTANCE on the first instance, so if anything already owns this
///    pipe name the service fails loudly instead of quietly sharing it.
/// 4. The connected client is identified by impersonation: its user SID, its session, its
///    integrity level and its image path. A caller that is not the installed owner is
///    refused before any operation is considered.
///
/// What this cannot do, stated plainly: it cannot distinguish PC Assistant from another
/// process running as the same owner at the same integrity level, because on Windows a
/// same-user medium-integrity process can already act as its peers. The image-path check
/// raises the bar; the real narrowing is that the surface behind this door is four validated
/// operations, all of which run as the owner's own administrator token, and none of which
/// runs as SYSTEM.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PrivilegedPipeServer : IDisposable
{
    // More listeners than the handler's concurrent-operation limit, deliberately. A long
    // Run occupies its listener thread for the whole command, so if the two numbers matched,
    // four concurrent commands would leave nothing free to serve the Cancel that stops them.
    private const int MaxInstances = 16;
    private const int ListenerThreads = 8;

    private readonly SecurityIdentifier _ownerSid;
    private readonly Func<ClientIdentity, string, string> _handler;
    private readonly Action<string> _log;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Thread> _threads = [];
    private readonly Lock _firstInstanceGate = new();
    private readonly ConcurrentQueue<SafePipeHandle> _reserved = new();
    private bool _firstInstanceCreated;

    /// <summary>
    /// <paramref name="pipeName"/> exists so the boundary tests can host this exact server on
    /// a name of their own instead of the production one. The service always passes null.
    /// </summary>
    public PrivilegedPipeServer(
        SecurityIdentifier ownerSid, Func<ClientIdentity, string, string> handler, Action<string> log,
        string? pipeName = null)
    {
        _ownerSid = ownerSid;
        _handler = handler;
        _log = log;
        _pipeName = pipeName ?? PrivilegedProtocol.PipeName;
    }

    public void Start()
    {
        // Claim the name before reporting the service as started - and keep the instance
        // rather than closing it. Closing it first would leave a window with no instance of
        // this pipe, in which another process could claim the name for itself.
        _reserved.Enqueue(CreatePipeInstance());

        for (var i = 0; i < ListenerThreads; i++)
        {
            var thread = new Thread(ListenLoop) { IsBackground = true, Name = "PcAssistantElevation-" + i };
            _threads.Add(thread);
            thread.Start();
        }
    }

    private void ListenLoop()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            SafePipeHandle? handle = null;
            try
            {
                if (!_reserved.TryDequeue(out handle)) handle = CreatePipeInstance();

                if (!NativeMethods.ConnectNamedPipe(handle, IntPtr.Zero))
                {
                    // ERROR_PIPE_CONNECTED (535) means a client raced in and is already there.
                    var error = Marshal.GetLastWin32Error();
                    if (error != 535 && error != 0)
                    {
                        if (_shutdown.IsCancellationRequested) break;
                        throw new Win32Exception(error, "ConnectNamedPipe failed.");
                    }
                }

                if (_shutdown.IsCancellationRequested) break;
                Serve(handle);
            }
            catch (Exception ex)
            {
                if (_shutdown.IsCancellationRequested) break;
                _log("pipe listener error: " + ex.Message);
                Thread.Sleep(200);
            }
            finally
            {
                if (handle is not null && !handle.IsInvalid)
                {
                    try { NativeMethods.FlushFileBuffers(handle); } catch (Exception) { }
                    try { NativeMethods.DisconnectNamedPipe(handle); } catch (Exception) { }
                    handle.Dispose();
                }
            }
        }
    }

    private void Serve(SafePipeHandle handle)
    {
        using var stream = new NamedPipeServerStream(PipeDirection.InOut, isAsync: false, isConnected: true, handle);

        // Read first, then identify. Windows will not let a pipe server impersonate its client
        // until it has read from the pipe, so identifying before the read races the client's
        // write and intermittently breaks the connection.
        var request = ReadBoundedLine(stream);
        var identity = Identify(handle, stream);

        // Identifying the caller means impersonating them, and the handler underneath does
        // privileged work that Windows judges by the thread's token rather than the process's.
        // Whoever impersonates reverts: this server started it, so this server ends it, rather
        // than leaving the handler to discover the owner's token on its thread.
        NativeMethods.RevertToSelf();

        string response;
        try
        {
            response = _handler(identity, request);
        }
        catch (Exception ex)
        {
            _log("handler error: " + ex);
            response = System.Text.Json.JsonSerializer.Serialize(
                new PrivilegedResponse { Status = PrivilegedStatus.Failed, Message = "The privileged helper failed to handle the request." },
                PrivilegedProtocol.Json);
        }

        var bytes = Encoding.UTF8.GetBytes(response + "\n");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();

        // Let the client finish reading before the listener disconnects. Without this, the
        // client's own stream teardown can hit a pipe that is already gone, turning a
        // perfectly good refusal into an IOException.
        try { stream.WaitForPipeDrain(); } catch (Exception) { /* client already left */ }
    }

    private ClientIdentity Identify(SafePipeHandle handle, NamedPipeServerStream stream)
    {
        NativeMethods.GetNamedPipeClientProcessId(handle, out var pid);

        var sid = string.Empty;
        var name = string.Empty;
        uint session = 0;
        var integrity = 0;

        try
        {
            stream.RunAsClient(() =>
            {
                using var client = WindowsIdentity.GetCurrent();
                sid = client.User?.Value ?? string.Empty;
                name = client.Name;
                session = ReadUInt32(client.AccessToken, NativeMethods.TokenSessionId) ?? 0;
                integrity = ReadIntegrityLevel(client.AccessToken);
            });
        }
        catch (Exception ex)
        {
            // Fail closed. An unidentifiable caller keeps an empty SID, which the request
            // handler refuses before it considers any operation.
            _log("could not identify pipe client: " + ex.Message);
        }

        return new ClientIdentity(pid, sid, name, session, integrity, ImagePathOf(pid));
    }

    private static string? ImagePathOf(uint processId)
    {
        if (processId == 0) return null;
        var process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
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
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>The mandatory integrity RID, e.g. 0x2000 for medium, 0x3000 for high.</summary>
    private static int ReadIntegrityLevel(SafeAccessTokenHandle token)
    {
        NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, IntPtr.Zero, 0, out var length);
        if (length <= 0) return 0;

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, buffer, length, out _))
                return 0;

            var sidPointer = Marshal.ReadIntPtr(buffer);
            if (sidPointer == IntPtr.Zero) return 0;

            var value = new SecurityIdentifier(sidPointer).Value;
            var lastDash = value.LastIndexOf('-');
            return lastDash >= 0 && int.TryParse(value[(lastDash + 1)..], out var rid) ? rid : 0;
        }
        catch (Exception)
        {
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private SafePipeHandle CreatePipeInstance()
    {
        // The owner is left implicit: whoever creates the pipe owns it, which for the service
        // is LocalSystem. Naming an owner explicitly would additionally require the privilege
        // to set it, which buys nothing here — the DACL below is what actually restricts access.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // The owner gets exactly what a client needs: connect, write a request, read the answer.
        // Not CreateNewInstance, so nothing running as the owner can add an instance of this
        // pipe name and intercept a later connection.
        security.AddAccessRule(new PipeAccessRule(
            _ownerSid, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var unmanaged = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, unmanaged, descriptor.Length);

            var attributes = new NativeMethods.SecurityAttributes
            {
                nLength = Marshal.SizeOf<NativeMethods.SecurityAttributes>(),
                lpSecurityDescriptor = unmanaged,
                bInheritHandle = false,
            };

            var openMode = NativeMethods.PipeAccessDuplex;
            lock (_firstInstanceGate)
            {
                if (!_firstInstanceCreated) openMode |= NativeMethods.FileFlagFirstPipeInstance;

                var handle = NativeMethods.CreateNamedPipe(
                    @"\\.\pipe\" + _pipeName,
                    openMode,
                    NativeMethods.PipeTypeByte | NativeMethods.PipeReadModeByte | NativeMethods.PipeWait |
                    NativeMethods.PipeRejectRemoteClients,
                    MaxInstances,
                    outBufferSize: 8192,
                    inBufferSize: 8192,
                    defaultTimeoutMs: 0,
                    ref attributes);

                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new Win32Exception(error, error == 231
                        ? "The pipe name is already owned by another process; refusing to share it."
                        : "Could not create the privileged helper pipe (Windows error " + error + ").");
                }

                _firstInstanceCreated = true;
                return handle;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(unmanaged);
        }
    }

    private static string ReadBoundedLine(Stream stream)
    {
        var buffer = new List<byte>(1024);
        var single = new byte[1];
        while (buffer.Count <= PrivilegedProtocol.MaxMessageBytes)
        {
            int read;
            try { read = stream.Read(single, 0, 1); }
            catch (IOException) { break; }
            if (read <= 0) break;
            if (single[0] == (byte)'\n') break;
            if (single[0] != (byte)'\r') buffer.Add(single[0]);
        }

        return buffer.Count > PrivilegedProtocol.MaxMessageBytes
            ? string.Empty        // Oversized input is discarded, never partially parsed.
            : Encoding.UTF8.GetString(buffer.ToArray());
    }

    public void Stop()
    {
        if (_shutdown.IsCancellationRequested) return;
        _shutdown.Cancel();

        // Unblock listeners parked in ConnectNamedPipe.
        for (var i = 0; i < ListenerThreads; i++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
                client.Connect(50);
            }
            catch (Exception) { /* nothing listening is the desired end state */ }
        }

        foreach (var thread in _threads) thread.Join(TimeSpan.FromSeconds(2));

        while (_reserved.TryDequeue(out var unused)) unused.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _shutdown.Dispose();
    }
}
