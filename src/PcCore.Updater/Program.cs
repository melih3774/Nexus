using System.Globalization;
using System.Runtime.Versioning;

using PcCore.Update;
using PcCore.Update.Windows;

[assembly: SupportedOSPlatform("windows")]

namespace PcCore.Updater;

/// <summary>
/// <code>
/// PcCore.Updater.exe --check                  what an update would change, without changing it
/// PcCore.Updater.exe --apply [--release N]    apply the current release to this PC
/// </code>
/// <para>
/// <c>--releases</c>, <c>--install-dir</c> and <c>--work-dir</c> point it somewhere other than the
/// real per-user locations, which is how a rollback is rehearsed without touching the real install.
/// <c>--elevated</c> is internal: it is how this process re-launches itself for the one step that needs
/// administrator rights.
/// </para>
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--elevated"))
        {
            return ElevatedInstall.Run(
                Value(args, "--staged") ?? string.Empty,
                int.TryParse(Value(args, "--release"), NumberStyles.None, CultureInfo.InvariantCulture, out var release) ? release : 0,
                args.Contains("--secure-capture"),
                args.Contains("--hid-driver"));
        }

        var defaults = UpdatePaths.ForCurrentUser();
        var paths = new UpdatePaths(
            Value(args, "--install-dir") is { } install ? Path.GetFullPath(install) : defaults.InstallDirectory,
            Value(args, "--work-dir") is { } work ? Path.GetFullPath(work) : defaults.WorkDirectory);
        var store = new ReleaseStore(Value(args, "--releases") ?? ReleaseStore.DefaultRoot);

        var log = new UpdaterLog(paths.LogPath, attachConsole: true);
        var processes = new WindowsProcessControl(log.Write);

        // The one outcome an update must never have: PC Core stopped and nobody left to start it.
        // Everything expected is handled below; this is for whatever was not.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LeavePcCoreRunning(paths, processes, log, e.ExceptionObject as Exception);

        var coordinator = new UpdateCoordinator(
            store,
            paths,
            new WindowsComponentProbe(),
            processes,
            new LoopbackHealthProbe(processes, paths.InstallDirectory, log.Write),
            new UacElevatedInstaller(log.Write),
            log.Write);

        if (args.Contains("--check"))
        {
            var (plan, problem) = coordinator.Check();
            log.Write(plan?.Describe() ?? problem ?? "No release.");

            foreach (var change in plan?.PcCore ?? [])
            {
                log.Write($"  {change.Kind,-7} {change.Path}");
            }

            foreach (var decision in plan is null ? [] : new[] { plan.SecureCapture, plan.HidDriver })
            {
                log.Write($"  {decision.Name}: {decision.Action} ({decision.Reason}; installed {decision.InstalledVersion ?? "-"}, release {decision.AvailableVersion ?? "-"})");
            }

            return plan is null ? 3 : plan.UpToDate ? 0 : 10;
        }

        if (!args.Contains("--apply"))
        {
            log.Write("Usage: PcCore.Updater.exe --check | --apply [--release N]");
            return 1;
        }

        // One update at a time, whoever started it: the tray, the control panel, or the publish script.
        using var mutex = new Mutex(false, UpdatePaths.UpdateMutexName);

        bool owned;

        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        if (!owned)
        {
            log.Write("Another Nexus update is already running.");
            return 4;
        }

        try
        {
            int? expected = int.TryParse(Value(args, "--release"), NumberStyles.None, CultureInfo.InvariantCulture, out var wanted) ? wanted : null;

            log.Write($"Nexus updater {typeof(Program).Assembly.GetName().Version} starting: install {paths.InstallDirectory}, releases {store.Root}.");

            var outcome = await coordinator.RunAsync(expected, CancellationToken.None).ConfigureAwait(false);

            log.Write($"Update outcome: {outcome}.");

            return outcome switch
            {
                UpdateOutcome.Succeeded or UpdateOutcome.UpToDate => 0,
                UpdateOutcome.NoRelease => 3,
                UpdateOutcome.RolledBack => 2,
                _ => 1,
            };
        }
        catch (Exception ex)
        {
            // Last resort. The coordinator handles every failure it expects; anything reaching here is
            // a bug, and the log is where the owner will look for it.
            LeavePcCoreRunning(paths, processes, log, ex);
            return 1;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Whatever the updater was in the middle of: undo the file swap if one was under way, start PC
    /// Core if it is not running, and say so. Every step is attempted even if an earlier one fails.
    /// </summary>
    private static void LeavePcCoreRunning(UpdatePaths paths, WindowsProcessControl processes, UpdaterLog log, Exception? ex)
    {
        IReadOnlyList<string> stuck = [];

        Attempt(() => log.Write($"The updater failed unexpectedly: {ex}"));
        Attempt(() => stuck = new PcCoreFileSwap(paths.JournalPath, log.Write).RollBack());

        Attempt(() =>
        {
            if (processes.FindRunning().Count == 0 && File.Exists(paths.InstalledExecutable))
            {
                processes.Start(paths.InstalledExecutable);
                log.Write("Started PC Core again after the failure.");
            }
        });

        Attempt(() => paths.WriteStatus(new UpdateStatus
        {
            State = UpdateState.Failed,
            Message = stuck.Count == 0
                ? $"The update failed unexpectedly ({ex?.GetType().Name}); the previous version was kept."
                : $"The update failed unexpectedly ({ex?.GetType().Name}) and could not be fully undone. See {paths.LogPath}.",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        }));

        static void Attempt(Action step)
        {
            try
            {
                step();
            }
            catch
            {
                // Nothing else to fall back to; the remaining steps still run.
            }
        }
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
