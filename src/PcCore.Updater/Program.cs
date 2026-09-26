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
    /// <summary>Nothing was done: this process would only have reached a packaged app's private copy of Nexus's folders.</summary>
    private const int RedirectedExitCode = 5;

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

        // Before anything is read or stopped. An updater that sees a packaged app's private copy of the
        // update state installs from a release store, and records into a status file, that the PC Core
        // started from the Start Menu never reads: its next restart comes up as if nothing happened.
        if (AppDataRedirection.FirstUsedByThisProcess(AppDataRedirection.LocalAppData, DefaultFoldersIn(args), DirectoryIdentity.Of) is { } redirected)
        {
            log.Write(redirected.Describe());
            return RedirectedExitCode;
        }

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
            log.Write,
            new WindowsAssistantControl(log.Write));

        if (args.Contains("--check"))
        {
            var (plan, problem) = coordinator.Check();
            log.Write(plan?.Describe() ?? problem ?? "No release.");

            foreach (var change in plan?.PcCore ?? [])
            {
                log.Write($"  {change.Kind,-7} {change.Path}");
            }

            foreach (var change in plan?.Assistant ?? [])
            {
                log.Write($"  {change.Kind,-7} {AssistantInstall.Folder}/{change.Path}");
            }

            foreach (var decision in plan is null ? [] : new[] { plan.SecureCapture, plan.HidDriver, plan.AssistantComponent })
            {
                log.Write($"  {decision.Name}: {decision.Action} ({decision.Reason}; installed {decision.InstalledVersion ?? "-"}, release {decision.AvailableVersion ?? "-"})");
            }

            if (plan?.HidDriverRollback is { } rollback)
            {
                log.Write($"  HID driver rollback: {rollback.Component.Version} from release {rollback.Release}");
            }

            return plan is null ? 3 : plan.UpToDate ? 0 : 10;
        }

        if (!args.Contains("--apply"))
        {
            log.Write("Usage: PcCore.Updater.exe --check | --apply [--release N]");
            return 1;
        }

        // One update at a time, whoever started it: the tray, the control panel, or the publish script.
        // A semaphore rather than a mutex: a mutex belongs to the thread that took it, and the update
        // awaits - so the release ran on another thread and threw, after a successful update.
        using var gate = new Semaphore(1, 1, UpdatePaths.UpdateMutexName);

        if (!gate.WaitOne(TimeSpan.Zero))
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
            gate.Release();
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
        Attempt(() => stuck = [.. stuck, .. new PcCoreFileSwap(paths.AssistantJournalPath, log.Write).RollBack()]);

        Attempt(() =>
        {
            if (processes.FindRunning().Count == 0 && File.Exists(paths.InstalledExecutable))
            {
                processes.Start(paths.InstalledExecutable);
                log.Write("Started PC Core again after the failure.");
            }
        });

        Attempt(() =>
        {
            // An outcome already recorded stands. A failure after the update finished - in cleanup -
            // must not turn "updated" into "failed" for the owner.
            if (paths.ReadStatus() is { InProgress: false, State: not UpdateState.Idle })
            {
                return;
            }

            paths.WriteStatus(new UpdateStatus
            {
                State = UpdateState.Failed,
                Message = stuck.Count == 0
                    ? $"The update failed unexpectedly ({ex?.GetType().Name}); the previous version was kept."
                    : $"The update failed unexpectedly ({ex?.GetType().Name}) and could not be fully undone. See {paths.LogPath}.",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        });

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

    /// <summary>The per-user folders this run uses at their default locations; an explicit path is the caller's to vouch for.</summary>
    private static IEnumerable<string> DefaultFoldersIn(string[] args)
    {
        if (Value(args, "--work-dir") is null || Value(args, "--releases") is null)
        {
            yield return UpdatePaths.StateFolder;
        }

        if (Value(args, "--install-dir") is null)
        {
            yield return UpdatePaths.InstallFolder;
        }
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
