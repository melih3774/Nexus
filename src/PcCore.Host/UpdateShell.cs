using System.Runtime.Versioning;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using PcCore.Update;
using PcCore.Update.Windows;

namespace PcCore.Host;

/// <summary>What PC Core does for the updater, which runs as a separate process.</summary>
[SupportedOSPlatform("windows")]
internal static class UpdateShell
{
    /// <summary>
    /// Waits on the named stop event and shuts down normally when it is set.
    /// <para>
    /// The ordinary shutdown path, not a kill: held HID input is neutralised, live sensors are
    /// released, the tray icon is removed and the security state is already on disk. The updater only
    /// ends the process itself if this has not happened within its grace period.
    /// </para>
    /// </summary>
    public static IDisposable ListenForStop(IHostApplicationLifetime lifetime, ILogger logger)
    {
        try
        {
            return UpdateStopSignal.Listen(() =>
            {
                logger.LogWarning("Nexus is stopping: the updater asked it to, so it can be replaced.");
                lifetime.StopApplication();
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Not fatal: the updater falls back to Ctrl+C and then to ending the process.
            logger.LogWarning(ex, "The update stop signal could not be created; an update will have to end PC Core instead.");
            return new NoListener();
        }
    }

    /// <summary>The balloon for an update's outcome, or null when it is not one worth interrupting for.</summary>
    public static (string Title, string Text)? Describe(UpdateStatus status) => status.State switch
    {
        UpdateState.Succeeded when status.Message?.StartsWith("Updated", StringComparison.Ordinal) == true
            => ("Nexus updated", status.Message),
        UpdateState.RestartRequired => ("Restart Windows to finish the Nexus update", status.Message ?? "The new HID driver runs after a restart."),
        UpdateState.RolledBack => ("Nexus update rolled back", status.Message ?? "The previous version was restored."),
        UpdateState.Failed => ("Nexus update failed", status.Message ?? "The update did not complete."),
        _ => null,
    };

    private sealed class NoListener : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
