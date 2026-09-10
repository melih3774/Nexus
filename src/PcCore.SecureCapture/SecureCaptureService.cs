using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using Microsoft.Win32.SafeHandles;

namespace PcCore.SecureCapture;

/// <summary>
/// The LocalSystem half: a launcher, and nothing more.
/// <para>
/// <b>What it is allowed to do is the whole design.</b> It listens on one pipe, whose DACL admits
/// SYSTEM, Administrators and interactive users and no one else. It accepts one message, whose only
/// parameters are the name of a pipe to connect back to and a nonce to prove which request the
/// worker belongs to. The pipe name has to match a fixed prefix and be hexadecimal, so the helper
/// cannot be talked into connecting to a name chosen to impersonate something else. And the caller
/// has to be in the console session — the session whose desktop is about to be photographed —
/// which is checked against Windows' own answer rather than anything in the request.
/// </para>
/// <para>
/// It does not read files, run commands, inject input, or return anything but a yes or a no. The
/// pixels never pass through it: the worker connects directly to the pipe PC Core is serving, so
/// the SYSTEM process holding the frames talks only to the user who is sitting at the machine.
/// </para>
/// </summary>
internal sealed class SecureCaptureService
{
    public const string ServiceName = "NexusSecureCapture";
    public const string DisplayName = "Nexus Secure Desktop Capture";

    /// <summary>
    /// SYSTEM and Administrators in full; interactive users may open and talk to it.
    /// <para>
    /// <c>IU</c> rather than a specific account on purpose: it is the well-known group Windows puts
    /// a token in when the logon is interactive, so a service account, a scheduled task or a remote
    /// logon does not qualify no matter whose credentials it holds.
    /// </para>
    /// </summary>
    private const string PipeSecurityDescriptor = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)";

    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;
    private const uint PipeRejectRemoteClients = 0x00000008;

    private readonly Action<string> _log;

    public SecureCaptureService(Action<string> log) => _log = log;

    public void Run(CancellationToken cancellationToken)
    {
        _log($"{DisplayName} started.");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                ServeOne(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log($"Control pipe error: {ex.GetType().Name}: {ex.Message}");
                Thread.Sleep(1000);
            }
        }

        _log($"{DisplayName} stopping.");
    }

    private void ServeOne(CancellationToken cancellationToken)
    {
        using var handle = CreatePipe();
        using var pipe = new NamedPipeServerStream(PipeDirection.InOut, false, false, handle);

        pipe.WaitForConnection();

        try
        {
            var reply = Handle(pipe);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(reply, JsonOptions);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            pipe.WaitForPipeDrain();
        }
        finally
        {
            try
            {
                pipe.Disconnect();
            }
            catch (IOException)
            {
                // The client left first. Nothing to disconnect.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private StartWorkerReply Handle(NamedPipeServerStream pipe)
    {
        var console = Interop.WTSGetActiveConsoleSessionId();

        if (console == 0xFFFFFFFF)
        {
            return Refuse("no console session is attached");
        }

        if (!Interop.GetNamedPipeClientSessionId(pipe.SafePipeHandle.DangerousGetHandle(), out var clientSession))
        {
            return Refuse("the caller's session could not be determined");
        }

        if (clientSession != console)
        {
            // The only desktop this component can photograph is the console session's. A caller from
            // any other session is asking for something that does not exist.
            return Refuse($"the caller is in session {clientSession}, not the console session {console}");
        }

        var request = Read(pipe);

        if (request is null)
        {
            return Refuse("the request could not be read");
        }

        if (!SecureCaptureProtocol.IsValidFramePipeName(request.Pipe))
        {
            return Refuse("the frame pipe name is not one this service will connect to");
        }

        if (string.IsNullOrWhiteSpace(request.Nonce) || request.Nonce.Length > 128)
        {
            return Refuse("the nonce is missing or implausible");
        }

        try
        {
            var pid = Launch(console, request.Pipe!, request.Nonce!);
            _log($"Capture worker {pid} started in session {console}.");

            return new StartWorkerReply { Ok = true, WorkerProcessId = pid };
        }
        catch (Exception ex)
        {
            _log($"Capture worker could not be started: {ex.Message}");
            return Refuse("the capture worker could not be started");
        }
    }

    private StartWorkerReply Refuse(string reason)
    {
        _log($"Refused a capture request: {reason}.");
        return new StartWorkerReply { Ok = false, Reason = reason };
    }

    private static StartWorkerRequest? Read(NamedPipeServerStream pipe)
    {
        // One small message, framed by its own length so the read cannot hang waiting for an end the
        // client never sends.
        var header = new byte[4];

        if (pipe.ReadAtLeast(header, 4, throwOnEndOfStream: false) < 4)
        {
            return null;
        }

        var length = BitConverter.ToInt32(header);

        if (length is <= 0 or > 4096)
        {
            return null;
        }

        var body = new byte[length];

        if (pipe.ReadAtLeast(body, length, throwOnEndOfStream: false) < length)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StartWorkerRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts a SYSTEM process into the console session, on the Winlogon desktop.
    /// <para>
    /// The token is this service's own, duplicated and re-stamped with the console session id. It is
    /// not the user's token and must not be: a process running as the logged-on user cannot open the
    /// Secure Desktop however it is launched, which is the reason this component exists at all.
    /// </para>
    /// </summary>
    private static int Launch(uint session, string pipeName, string nonce)
    {
        if (!Interop.OpenProcessToken(
                Interop.GetCurrentProcess(),
                Interop.TokenDuplicate | Interop.TokenQuery,
                out var own))
        {
            throw new InvalidOperationException($"OpenProcessToken failed ({Marshal.GetLastWin32Error()}).");
        }

        var duplicate = nint.Zero;
        var environment = nint.Zero;

        try
        {
            if (!Interop.DuplicateTokenEx(
                    own,
                    Interop.TokenAssignPrimary | Interop.TokenDuplicate | Interop.TokenQuery
                    | Interop.TokenAdjustDefault | Interop.TokenAdjustSessionId,
                    nint.Zero,
                    Interop.SecurityImpersonation,
                    Interop.TokenPrimary,
                    out duplicate))
            {
                throw new InvalidOperationException($"DuplicateTokenEx failed ({Marshal.GetLastWin32Error()}).");
            }

            var target = session;

            if (!Interop.SetTokenInformation(duplicate, Interop.TokenSessionIdClass, ref target, sizeof(uint)))
            {
                throw new InvalidOperationException($"The token could not be moved to the console session ({Marshal.GetLastWin32Error()}).");
            }

            Interop.CreateEnvironmentBlock(out environment, duplicate, false);

            var startup = new Interop.StartupInfo
            {
                Size = Marshal.SizeOf<Interop.StartupInfo>(),

                // The interactive window station, and the desktop UAC uses. The worker re-opens
                // whatever desktop is receiving input on every frame; this is only where it starts.
                Desktop = "WinSta0\\Winlogon",
            };

            var image = Environment.ProcessPath
                ?? throw new InvalidOperationException("The service could not determine its own path.");

            var commandLine = $"\"{image}\" --worker \"{pipeName}\" \"{nonce}\"";

            if (!Interop.CreateProcessAsUserW(
                    duplicate,
                    image,
                    commandLine,
                    nint.Zero,
                    nint.Zero,
                    false,
                    Interop.CreateUnicodeEnvironment | Interop.CreateNoWindow,
                    environment,
                    Path.GetDirectoryName(image),
                    ref startup,
                    out var information))
            {
                throw new InvalidOperationException($"CreateProcessAsUser failed ({Marshal.GetLastWin32Error()}).");
            }

            Interop.CloseHandle(information.Thread);
            Interop.CloseHandle(information.Process);

            return (int)information.ProcessId;
        }
        finally
        {
            if (environment != nint.Zero)
            {
                Interop.DestroyEnvironmentBlock(environment);
            }

            if (duplicate != nint.Zero)
            {
                Interop.CloseHandle(duplicate);
            }

            Interop.CloseHandle(own);
        }
    }

    /// <summary>
    /// Creates the control pipe with an explicit security descriptor.
    /// <para>
    /// Built by hand rather than through <c>PipeSecurity</c> so the descriptor is one readable string
    /// in this file, and so this project needs no package beyond the framework. It also rejects
    /// remote clients outright: this is a local mechanism and there is no configuration that makes
    /// it not one.
    /// </para>
    /// </summary>
    private static SafePipeHandle CreatePipe()
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                PipeSecurityDescriptor, 1, out var descriptor, out _))
        {
            throw new InvalidOperationException(
                $"The pipe security descriptor could not be built ({Marshal.GetLastWin32Error()}).");
        }

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = false,
            };

            var handle = CreateNamedPipeW(
                $@"\\.\pipe\{SecureCaptureProtocol.ControlPipeName}",
                PipeAccessDuplex | FileFlagFirstPipeInstance,
                PipeTypeByte | PipeWait | PipeRejectRemoteClients,
                1,
                4096,
                4096,
                0,
                ref attributes);

            if (handle.IsInvalid)
            {
                throw new InvalidOperationException(
                    $"The control pipe could not be created ({Marshal.GetLastWin32Error()}).");
            }

            return handle;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;

        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out nint descriptor, out uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LocalFree(nint memory);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(
        string name,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        uint outBufferSize,
        uint inBufferSize,
        uint defaultTimeout,
        ref SecurityAttributes attributes);

    /// <summary>
    /// Diagnostics, as a file under ProgramData.
    /// <para>
    /// Not the event log, which would need a package and an installed source, and not the console,
    /// which a service does not have. One small file, appended to, that the owner can read without
    /// tooling — and which never contains a pixel, a path, or anything the caller sent.
    /// </para>
    /// </summary>
    public static void Log(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nexus");

            Directory.CreateDirectory(directory);

            File.AppendAllText(
                Path.Combine(directory, "secure-capture.log"),
                $"{DateTimeOffset.UtcNow:u}  {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A component that cannot write its log still has a job to do.
        }
    }
}
