using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PcAssistant.Desktop;

/// <summary>
/// Lets Nexus's updater ask this copy of PC Assistant to close so its files can be replaced — and lets
/// PC Assistant say no.
///
/// The event is named after this executable's full path, so the updater reaches exactly the copy it
/// installed and never a development build running from somewhere else. It is session-local, so only
/// something already running as this user in this session can set it, and all it can ask for is what
/// closing the window already does.
///
/// PC Assistant refuses while any task is running or any approval is waiting: closing then would
/// interrupt the owner's work, and an update can wait. The updater does not kill it; it reports that
/// PC Assistant is busy and changes nothing. The same name is derived in Nexus
/// (PcCore.Update.AssistantInstall.StopEventName); both test suites pin it to one literal.
/// </summary>
public sealed class UpdateStopListener : IDisposable
{
    public const string EventPrefix = @"Local\PcAssistant.UpdateStop.";

    private readonly EventWaitHandle _signal;
    private readonly RegisteredWaitHandle _registration;

    private UpdateStopListener(EventWaitHandle signal, Action onStopRequested)
    {
        _signal = signal;

        // Not once only: a refused request leaves PC Assistant running, and the next update must still
        // be able to ask.
        _registration = ThreadPool.RegisterWaitForSingleObject(
            signal, (_, _) => onStopRequested(), state: null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>The event name for one installed executable.</summary>
    public static string EventNameFor(string executablePath)
    {
        var full = Path.GetFullPath(executablePath).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(full));
        return EventPrefix + Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>Why PC Assistant will not close for an update right now, or null when it can.</summary>
    public static string? RefusalReason(int runningTasks, int pendingApprovals) =>
        runningTasks > 0 || pendingApprovals > 0
            ? $"{runningTasks} task(s) running and {pendingApprovals} approval(s) waiting"
            : null;

    /// <summary>
    /// Starts listening for this process, or returns null when it is not running as
    /// PcAssistant.Desktop.exe (a 'dotnet run' host) or the event could not be created.
    /// </summary>
    public static UpdateStopListener? TryStart(string? executablePath, Action onStopRequested, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(executablePath)
            || !Path.GetFileName(executablePath).Equals("PcAssistant.Desktop.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return new UpdateStopListener(
                new EventWaitHandle(false, EventResetMode.AutoReset, EventNameFor(executablePath)),
                onStopRequested);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            log("The Nexus update stop signal could not be created; a Nexus update will ask you to close PC Assistant: " + ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        // Not Unregister(_signal): that argument is a handle to set once unregistering completes, and
        // this one is shared by name.
        _registration.Unregister(null);
        _signal.Dispose();
    }
}
