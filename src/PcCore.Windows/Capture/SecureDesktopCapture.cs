using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using PcCore.Capabilities.Screen;
using PcCore.SecureCapture;

namespace PcCore.Windows.Capture;

/// <summary>What a request to the privileged helper produced.</summary>
public sealed record SecureCaptureHealth(bool ServiceInstalled, bool WorkerConnected, string Diagnostic)
{
    public static SecureCaptureHealth Unknown => new(false, false, "the Nexus Secure Desktop Capture service has not been asked yet");
}

/// <summary>
/// PC Core's half of Secure Desktop capture.
/// <para>
/// <b>Why any of this exists.</b> Desktop Duplication stops working the instant Windows switches to
/// the Secure Desktop, and it is supposed to: the consent prompt is isolated from everything running
/// in the interactive session, which is the property that makes UAC worth having. Nexus does not
/// weaken that. It does what every legitimate remote-support product does — asks a service running
/// as LocalSystem to put a capture process into the console session, where the Winlogon desktop's
/// DACL actually admits it, and receives pixels back.
/// </para>
/// <para>
/// <b>The trust in both directions.</b> This process creates the pipe and chooses its name and a
/// 32-byte nonce, so a worker that turns up without the nonce is not the one we asked for and is
/// refused. It then checks that the connecting peer really is SYSTEM, by impersonating it and
/// looking at the token — because a pipe name is not a secret and the only thing that should be able
/// to answer this pipe is the thing Windows itself started. In the other direction the service
/// checks that the caller is in the console session, which is the only session whose desktop it can
/// photograph.
/// </para>
/// <para>
/// <b>What it does not do.</b> No frames are requested unless the input desktop is genuinely not the
/// ordinary one and an owner-authenticated session is live. The worker is asked one question at a
/// time and answers with an image; there is no channel here for anything else, in either direction.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecureDesktopCapture : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<SecureDesktopCapture> _logger;
    private readonly Lock _gate = new();

    private NamedPipeServerStream? _pipe;
    private BinaryReader? _reader;
    private BinaryWriter? _writer;
    private byte[] _pixels = [];

    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;

    public SecureDesktopCapture(ILogger<SecureDesktopCapture> logger) => _logger = logger;

    public SecureCaptureHealth Health { get; private set; } = SecureCaptureHealth.Unknown;

    /// <summary>
    /// Whether the privileged helper is running, without asking it for anything.
    /// <para>
    /// Named pipes are enumerable as a directory on Windows, so this is a listing rather than a
    /// connection. Worth the small oddity: a startup diagnostic that had to open the control pipe
    /// would be a startup diagnostic that could hang on it.
    /// </para>
    /// </summary>
    public static bool IsHelperListening()
    {
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\")
                .Any(p => p.EndsWith(SecureCaptureProtocol.ControlPipeName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True when a worker is connected and answering.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _pipe is { IsConnected: true };
            }
        }
    }

    /// <summary>
    /// Asks the worker for one frame, starting it first if necessary.
    /// <para>
    /// Returns null when there is nothing to show — no service installed, the worker refused, the
    /// desktop could not be read. The caller keeps showing the last frame rather than treating that
    /// as a stream failure: a second of the previous image is a better answer than a black screen.
    /// </para>
    /// </summary>
    public RawFrame? TryCapture()
    {
        lock (_gate)
        {
            if (_pipe is not { IsConnected: true })
            {
                if (DateTimeOffset.UtcNow < _nextAttempt)
                {
                    return null;
                }

                // Backoff before the attempt, not after it: a machine with no service installed must
                // not try to start a worker fifteen times a second for as long as UAC is up.
                _nextAttempt = DateTimeOffset.UtcNow.AddSeconds(3);

                if (!Start())
                {
                    return null;
                }
            }

            try
            {
                return Request();
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
            {
                _logger.LogInformation("The Secure Desktop capture worker went away ({Reason}).", ex.Message);
                Close();
                return null;
            }
        }
    }

    /// <summary>Ends the worker. Called when the stream stops or the desktop returns to normal.</summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_pipe is { IsConnected: true } && _writer is not null)
            {
                try
                {
                    _writer.Write(SecureCaptureProtocol.Commands.Stop);
                    _writer.Flush();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // It is going away either way.
                }
            }

            Close();
        }
    }

    private bool Start()
    {
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var name = SecureCaptureProtocol.FramePipePrefix + suffix;
        var nonce = RandomNumberGenerator.GetBytes(SecureCaptureProtocol.NonceBytes);

        NamedPipeServerStream? pipe = null;

        try
        {
            pipe = new NamedPipeServerStream(
                name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 0);

            var reply = Ask(name, Convert.ToBase64String(nonce));

            if (reply is null || !reply.Ok)
            {
                Health = new SecureCaptureHealth(
                    reply is not null,
                    false,
                    reply?.Reason ?? "the Nexus Secure Desktop Capture service is not installed or not running");

                _logger.LogWarning("Secure Desktop capture is unavailable: {Reason}", Health.Diagnostic);
                pipe.Dispose();
                return false;
            }

            // Ten seconds: the worker is being created in another session, on another desktop, by a
            // service that has just been woken up.
            var connected = pipe.WaitForConnectionAsync().Wait(TimeSpan.FromSeconds(10));

            if (!connected)
            {
                Health = new SecureCaptureHealth(true, false, "the capture worker did not connect");
                _logger.LogWarning("The Secure Desktop capture worker did not connect.");
                pipe.Dispose();
                return false;
            }

            var reader = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);
            var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);

            // The greeting is read before the identity is checked, and that order is forced rather
            // than chosen: Windows refuses to impersonate a named-pipe client until something the
            // client wrote has been read. Reading first is safe because nothing is trusted yet - the
            // nonce is compared, and then the peer is asked to prove it is SYSTEM before a single
            // capture is requested.
            if (!Greeted(reader, nonce))
            {
                Health = new SecureCaptureHealth(true, false, "the capture worker did not identify itself correctly");
                _logger.LogError("The Secure Desktop capture worker failed its handshake; refusing it.");
                reader.Dispose();
                writer.Dispose();
                pipe.Dispose();
                return false;
            }

            var peer = PeerSid(pipe);

            if (!string.Equals(peer, LocalSystemSid, StringComparison.OrdinalIgnoreCase))
            {
                // Somebody else answered a pipe we created. They learn nothing and get nothing.
                Health = new SecureCaptureHealth(
                    true, false, $"the capture worker was running as {peer ?? "an account that could not be read"}, not SYSTEM");
                _logger.LogError(
                    "A process running as {Sid} answered the Secure Desktop capture pipe; refusing it.",
                    peer ?? "(unknown)");
                reader.Dispose();
                writer.Dispose();
                pipe.Dispose();
                return false;
            }

            _pipe = pipe;
            _reader = reader;
            _writer = writer;
            pipe = null;

            Health = new SecureCaptureHealth(true, true, "a Secure Desktop capture worker is connected");
            _logger.LogInformation("Secure Desktop capture worker {Pid} connected.", reply.WorkerProcessId);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or AggregateException)
        {
            Health = new SecureCaptureHealth(false, false, $"the capture worker could not be started ({ex.GetType().Name})");
            _logger.LogWarning(ex, "Secure Desktop capture could not be started.");
            return false;
        }
        finally
        {
            pipe?.Dispose();
        }
    }

    /// <summary>
    /// One request on the service's control pipe.
    /// <para>
    /// Deliberately a fresh connection per request and no long-lived channel to the SYSTEM service:
    /// there is nothing to keep open, and a socket held to a privileged process is a thing that can
    /// be reused for something it was not opened for.
    /// </para>
    /// </summary>
    private StartWorkerReply? Ask(string pipeName, string nonce)
    {
        try
        {
            using var control = new NamedPipeClientStream(
                ".", SecureCaptureProtocol.ControlPipeName, PipeDirection.InOut, PipeOptions.None);

            control.Connect(2000);

            var body = JsonSerializer.SerializeToUtf8Bytes(
                new StartWorkerRequest { Pipe = pipeName, Nonce = nonce }, JsonOptions);

            control.Write(BitConverter.GetBytes(body.Length));
            control.Write(body);
            control.Flush();

            var buffer = new byte[4096];
            var read = control.Read(buffer);

            return read <= 0
                ? null
                : JsonSerializer.Deserialize<StartWorkerReply>(buffer.AsSpan(0, read), JsonOptions);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The SID the process on the other end of the pipe is running as.
    /// <para>
    /// Through <c>ImpersonateNamedPipeClient</c>, because the obvious alternative does not work:
    /// opening the peer process to read its token needs rights over a SYSTEM process that a
    /// medium-integrity process correctly does not have. A named pipe server is granted the ability
    /// to impersonate its client precisely so it can answer this question without them.
    /// </para>
    /// <para>
    /// Impersonation requires that something the client wrote has already been read, which is why
    /// the greeting is read first. The token is read and reverted on the same thread, with no
    /// awaits in between, so a SYSTEM identity is never left on a thread that goes on to do
    /// something else.
    /// </para>
    /// </summary>
    private static string? PeerSid(NamedPipeServerStream pipe)
    {
        var handle = pipe.SafePipeHandle.DangerousGetHandle();

        if (!ImpersonateNamedPipeClient(handle))
        {
            return null;
        }

        try
        {
            if (!OpenThreadToken(GetCurrentThread(), TokenQuery, true, out var token))
            {
                return null;
            }

            try
            {
                GetTokenInformation(token, TokenUserClass, nint.Zero, 0, out var needed);

                if (needed == 0)
                {
                    return null;
                }

                var buffer = Marshal.AllocHGlobal((int)needed);

                try
                {
                    if (!GetTokenInformation(token, TokenUserClass, buffer, needed, out _))
                    {
                        return null;
                    }

                    // TOKEN_USER is a SID_AND_ATTRIBUTES: the SID pointer comes first.
                    if (!ConvertSidToStringSidW(Marshal.ReadIntPtr(buffer), out var text))
                    {
                        return null;
                    }

                    try
                    {
                        return Marshal.PtrToStringUni(text);
                    }
                    finally
                    {
                        LocalFree(text);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            RevertToSelf();
        }
    }

    private const string LocalSystemSid = "S-1-5-18";
    private const uint TokenQuery = 0x0008;
    private const int TokenUserClass = 1;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ImpersonateNamedPipeClient(nint pipe);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool RevertToSelf();

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenThreadToken(nint thread, uint access, bool openAsSelf, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        nint token, int tokenInformationClass, nint information, uint length, out uint returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertSidToStringSidW(nint sid, out nint text);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LocalFree(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    private static bool Greeted(BinaryReader reader, byte[] nonce)
    {
        try
        {
            if (reader.ReadUInt32() != SecureCaptureProtocol.FrameMagic
                || reader.ReadInt32() != SecureCaptureProtocol.Version)
            {
                return false;
            }

            var length = reader.ReadInt32();

            if (length != nonce.Length)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(reader.ReadBytes(length), nonce);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            return false;
        }
    }

    private RawFrame? Request()
    {
        if (_reader is null || _writer is null)
        {
            return null;
        }

        _writer.Write(SecureCaptureProtocol.Commands.Capture);
        _writer.Flush();

        var reply = _reader.ReadByte();

        if (reply == SecureCaptureProtocol.Replies.Unavailable)
        {
            var reason = Encoding.UTF8.GetString(_reader.ReadBytes(Bounded(_reader.ReadInt32(), 4096)));
            Health = Health with { Diagnostic = $"the worker could not read the desktop: {reason}" };
            return null;
        }

        if (reply != SecureCaptureProtocol.Replies.Frame)
        {
            throw new IOException("The capture worker sent something that is not a frame.");
        }

        var width = _reader.ReadInt32();
        var height = _reader.ReadInt32();
        var stride = _reader.ReadInt32();
        var cursorX = _reader.ReadInt32();
        var cursorY = _reader.ReadInt32();
        var cursorVisible = _reader.ReadBoolean();
        var desktop = Encoding.UTF8.GetString(_reader.ReadBytes(Bounded(_reader.ReadInt32(), 256)));
        var length = _reader.ReadInt32();

        if (width <= 0 || height <= 0 || stride < width * 4
            || length != stride * height
            || length is <= 0 or > SecureCaptureProtocol.MaxFrameBytes)
        {
            throw new IOException("The capture worker described a frame that does not make sense.");
        }

        if (_pixels.Length != length)
        {
            _pixels = new byte[length];
        }

        _reader.ReadExactly(_pixels);

        Health = Health with { Diagnostic = $"capturing the {desktop} desktop" };

        return new RawFrame(
            _pixels, width, height, stride, DateTimeOffset.UtcNow, cursorX, cursorY, cursorVisible);
    }

    private static int Bounded(int length, int limit) =>
        length >= 0 && length <= limit ? length : throw new IOException("The capture worker sent an implausible length.");

    private void Close()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();

        _reader = null;
        _writer = null;
        _pipe = null;
    }

    public void Dispose() => Release();
}
