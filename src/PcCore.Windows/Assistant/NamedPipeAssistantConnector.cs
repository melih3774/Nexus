using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using PcCore.Capabilities.Assistant;
using PcCore.NativeInput;

namespace PcCore.Windows.Assistant;

/// <summary>
/// Connects to PC Assistant's Nexus bridge pipe, and refuses to use it unless the process on the other
/// end is the PC Assistant the owner configured.
/// <para>
/// <b>Why the server is checked, not only trusted to check us.</b> PC Assistant verifies which program
/// connected to it. That protects PC Assistant. It does nothing for Nexus if some other program running
/// as the owner created the pipe name first: that program would receive the owner's prompts and could
/// answer with invented approval cards. So before a single byte is written the pipe must belong to the
/// current user (<see cref="PipeOptions.CurrentUserOnly"/>), the serving process must be in this Windows
/// session, and its executable must be exactly <see cref="AssistantBridgeOptions.ServerExecutable"/>.
/// The same rule Nexus already applies to its own input broker pipe.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class NamedPipeAssistantConnector : IAssistantPipeConnector
{
    private readonly AssistantBridgeOptions options;
    private readonly ILogger<NamedPipeAssistantConnector> logger;
    private readonly string pipeName;

    public NamedPipeAssistantConnector(AssistantBridgeOptions options, ILogger<NamedPipeAssistantConnector> logger)
        : this(options, logger, PipeName())
    {
    }

    /// <summary>Tests connect to a pipe of their own rather than to a PC Assistant that may be running.</summary>
    internal NamedPipeAssistantConnector(AssistantBridgeOptions options, ILogger<NamedPipeAssistantConnector> logger, string pipeName)
    {
        this.options = options;
        this.logger = logger;
        this.pipeName = pipeName;
    }

    /// <summary>PC Assistant's pipe for this user. Its name is the one thing about PC Assistant this project knows.</summary>
    public static string PipeName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return "PcAssistant.NexusBridge." + identity.User!.Value;
    }

    public async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            await pipe.ConnectAsync(Math.Clamp(options.ConnectTimeoutMilliseconds, 100, 30_000), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new AssistantUnavailableException(AssistantAvailability.NotRunning, AssistantBridge.NotRunningDetail, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            // CurrentUserOnly surfaces a pipe owned by another account as an access failure.
            await pipe.DisposeAsync().ConfigureAwait(false);
            logger.LogWarning("PC Assistant's pipe could not be opened: {Reason}", ex.Message);
            throw new AssistantUnavailableException(
                AssistantAvailability.NotTrusted, "The PC Assistant pipe does not belong to this Windows user.", ex);
        }
        catch (IOException ex)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            logger.LogWarning("PC Assistant's pipe could not be opened: {Reason}", ex.Message);
            throw new AssistantUnavailableException(AssistantAvailability.Unavailable, "PC Assistant's pipe could not be opened.", ex);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var server = BrokerPipe.ServerImagePath(pipe.SafePipeHandle);
        var sameSession = GetNamedPipeServerSessionId(pipe.SafePipeHandle, out var sessionId)
            && sessionId == (uint)CurrentSessionId;

        if (!sameSession || !BrokerPipe.SameImage(server, options.ServerExecutable))
        {
            await pipe.DisposeAsync().ConfigureAwait(false);

            // The path goes to PC Core's own log, where the owner can compare it with what they configured.
            logger.LogWarning(
                "Refused the PC Assistant pipe: served by {Server} in {Session}, but PC Core trusts {Configured}.",
                server ?? "an unidentifiable process",
                sameSession ? "this session" : "another session",
                options.ServerExecutable);

            throw new AssistantUnavailableException(
                AssistantAvailability.NotTrusted,
                "The program serving PC Assistant's pipe is not the PC Assistant this PC Core trusts.");
        }

        return pipe;
    }

    private static readonly int CurrentSessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint sessionId);
}
