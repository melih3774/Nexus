namespace PcCore.Update;

/// <summary>A PC Core process the updater can see.</summary>
public sealed record RunningPcCore(int ProcessId, string? ExecutablePath);

/// <summary>Starting and stopping PC Core. The only part of an update that touches other processes.</summary>
public interface INexusProcessControl
{
    IReadOnlyList<RunningPcCore> FindRunning();

    /// <summary>Asks PC Core to shut down, waits up to <paramref name="grace"/>, then ends it.</summary>
    Task StopAsync(RunningPcCore process, TimeSpan grace, CancellationToken cancellationToken);

    /// <summary>Starts PC Core the way the Start Menu does and returns its process id.</summary>
    int Start(string executable);

    bool IsRunning(int processId);

    void Kill(int processId);
}

public sealed record HealthResult(bool Healthy, string Detail);

/// <summary>Decides whether a freshly started PC Core is actually working.</summary>
public interface INexusHealthProbe
{
    /// <param name="expectedVersion">The version it must report, or null to accept whichever it is.</param>
    Task<HealthResult> WaitUntilHealthyAsync(int processId, string? expectedVersion, DateTimeOffset startedAtUtc, CancellationToken cancellationToken);
}

public enum ElevatedOutcome
{
    Succeeded,
    Declined,
    Failed,
}

public sealed record ElevatedResult(ElevatedOutcome Outcome, string Detail);

/// <summary>Installs SecureCapture and/or the HID driver, behind one UAC prompt.</summary>
public interface IElevatedInstaller
{
    Task<ElevatedResult> InstallAsync(UpdatePlan plan, string stagedReleaseDirectory, CancellationToken cancellationToken);
}

public enum UpdateOutcome
{
    NoRelease,
    UpToDate,
    Succeeded,

    /// <summary>Nothing on this PC was changed.</summary>
    Failed,

    /// <summary>PC Core was changed, did not come up, and the previous files are back and running.</summary>
    RolledBack,

    /// <summary>Rollback itself ran into trouble. The log says what.</summary>
    RollbackIncomplete,
}

/// <summary>
/// One update, from start to finish:
/// <c>check → stage → verify → (approve) → stop → replace → restart → health check</c>, and back
/// again if the last step fails.
/// <para>
/// <b>Order is the safety argument.</b> Everything that can fail without consequence happens while
/// the old PC Core is still running: finding the release, verifying it, staging it, and asking for
/// administrator approval. Only once all of that has succeeded is PC Core stopped. A missing file, a
/// bad hash or a declined UAC prompt therefore ends the update with the owner's PC exactly as it was,
/// still serving the phone.
/// </para>
/// <para>
/// <b>What it never touches.</b> Pairing, owner keys, settings, logs and evidence live under
/// <c>%LOCALAPPDATA%\Nexus</c>; driver trust lives in the machine certificate stores. The only files
/// this writes are the ones the release manifest names, inside the install directory, plus its own
/// work directory.
/// </para>
/// </summary>
public sealed class UpdateCoordinator
{
    private readonly ReleaseStore _store;
    private readonly UpdatePaths _paths;
    private readonly IElevatedComponentProbe _probe;
    private readonly INexusProcessControl _processes;
    private readonly INexusHealthProbe _health;
    private readonly IElevatedInstaller _elevated;
    private readonly Action<string> _log;

    /// <summary>How long PC Core gets to shut down cleanly before it is ended.</summary>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(20);

    public UpdateCoordinator(
        ReleaseStore store,
        UpdatePaths paths,
        IElevatedComponentProbe probe,
        INexusProcessControl processes,
        INexusHealthProbe health,
        IElevatedInstaller elevated,
        Action<string> log)
    {
        _store = store;
        _paths = paths;
        _probe = probe;
        _processes = processes;
        _health = health;
        _elevated = elevated;
        _log = log;
    }

    /// <summary>What an update would do right now, without doing it.</summary>
    public (UpdatePlan? Plan, string? Problem) Check()
    {
        var load = _store.LoadLatest();

        if (!load.Found)
        {
            return (null, load.Problem);
        }

        return (UpdatePlanner.Plan(load.Manifest!, load.ReleaseDirectory!, _paths.InstallDirectory, _paths.ReadInstalledRecord(), _probe), null);
    }

    public async Task<UpdateOutcome> RunAsync(int? expectedRelease, CancellationToken cancellationToken)
    {
        var swap = new PcCoreFileSwap(_paths.JournalPath, _log);

        // An update that died half way is undone before anything else is considered. Otherwise the
        // plan below would compare against a mixture of two releases and call it the installation.
        if (swap.HasPendingJournal)
        {
            _log("A previous update did not finish. Restoring the files it replaced.");
            var stuck = swap.RollBack();

            if (stuck.Count > 0)
            {
                return Report(UpdateOutcome.RollbackIncomplete, null,
                    $"A previous interrupted update could not be undone: {string.Join(", ", stuck)}.");
            }
        }

        var (plan, problem) = Check();

        if (plan is null)
        {
            _log(problem ?? "No release.");
            return Report(UpdateOutcome.NoRelease, null, problem);
        }

        var manifest = plan.Manifest;

        if (expectedRelease is { } expected && expected != manifest.Release)
        {
            return Report(UpdateOutcome.Failed, manifest,
                $"Release {expected} was requested, but the current release is {manifest.Release}. Check again.");
        }

        _log(plan.Describe());

        foreach (var decision in new[] { plan.SecureCapture, plan.HidDriver })
        {
            _log($"{decision.Name}: {decision.Action} ({decision.Reason}; installed {decision.InstalledVersion ?? "-"}, release {decision.AvailableVersion ?? "-"})");
        }

        foreach (var file in plan.PreservedFiles)
        {
            _log($"Keeping {file}: it was edited on this PC after the last update.");
        }

        if (plan.UpToDate)
        {
            _paths.WriteStatus(Status(UpdateState.Succeeded, manifest, $"Nexus {manifest.Version} is up to date."));
            return UpdateOutcome.UpToDate;
        }

        var running = _processes.FindRunning();
        var installed = Path.GetFullPath(_paths.InstalledExecutable);
        var foreign = running.FirstOrDefault(p =>
            p.ExecutablePath is not null && !string.Equals(Path.GetFullPath(p.ExecutablePath), installed, StringComparison.OrdinalIgnoreCase));

        if (foreign is not null)
        {
            // A development copy would hold the port the updated one needs, and the health check
            // would then roll back a perfectly good update.
            return Report(UpdateOutcome.Failed, manifest,
                $"Another PC Core is running from {foreign.ExecutablePath}. Close it, then update again.");
        }

        var previousVersion = CurrentVersion();

        // --- Stage and verify, while the old PC Core keeps serving -----------------------------
        _paths.WriteStatus(Status(UpdateState.Staging, manifest, "Preparing the update.", previousVersion));

        string staged;

        try
        {
            staged = new ReleaseStager(_log).Stage(plan, _paths.StagingRoot, cancellationToken);
        }
        catch (Exception ex) when (ex is UpdateIntegrityException or IOException or UnauthorizedAccessException)
        {
            return Report(UpdateOutcome.Failed, manifest, $"The update could not be prepared: {ex.Message}", previousVersion);
        }

        // --- Administrator approval, only for what needs it --------------------------------------
        if (plan.NeedsElevation)
        {
            _paths.WriteStatus(Status(UpdateState.WaitingForApproval, manifest,
                "Waiting for administrator approval to update " + ElevatedNames(plan) + ".", previousVersion));

            var elevated = await _elevated.InstallAsync(plan, staged, cancellationToken).ConfigureAwait(false);

            if (elevated.Outcome != ElevatedOutcome.Succeeded)
            {
                var why = elevated.Outcome == ElevatedOutcome.Declined
                    ? "Administrator approval was declined"
                    : $"{ElevatedNames(plan)} could not be updated ({elevated.Detail})";

                return Report(UpdateOutcome.Failed, manifest, $"{why}. Nothing was changed.", previousVersion);
            }

            _log($"{ElevatedNames(plan)} updated.");
        }

        if (!plan.PcCoreChanges)
        {
            return Finish(plan, previousVersion);
        }

        // --- Stop, replace, restart ------------------------------------------------------------
        _paths.WriteStatus(Status(UpdateState.Installing, manifest, "Installing.", previousVersion));

        foreach (var process in running)
        {
            _log($"Stopping PC Core (process {process.ProcessId}).");
            await _processes.StopAsync(process, StopGrace, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            swap.Apply(manifest.Release, plan.PcCore, Path.Combine(staged, ReleaseStager.PcCoreFolder), _paths.InstallDirectory, _paths.BackupDirectory);
        }
        catch (Exception ex) when (ex is UpdateIntegrityException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Apply has already put the previous files back. The previous PC Core was stopped, so it
            // is started again: an update that failed must not also leave the phone without a PC.
            _log($"Replacing files failed: {ex.Message}");
            await RestartPreviousAsync(previousVersion, cancellationToken).ConfigureAwait(false);
            return Report(UpdateOutcome.RolledBack, manifest, $"The files could not be replaced ({ex.Message}). The previous version is back.", previousVersion);
        }

        _paths.WriteStatus(Status(UpdateState.Restarting, manifest, "Starting the new version.", previousVersion));

        var healthy = await StartAndCheckAsync(manifest.Version, cancellationToken).ConfigureAwait(false);

        if (healthy.Healthy)
        {
            swap.Commit();
            return Finish(plan, previousVersion);
        }

        // --- Roll back ---------------------------------------------------------------------------
        _log($"The new PC Core did not start correctly: {healthy.Detail}. Rolling back.");

        foreach (var process in _processes.FindRunning())
        {
            _processes.Kill(process.ProcessId);
        }

        var failed = swap.RollBack();

        if (failed.Count > 0)
        {
            return Report(UpdateOutcome.RollbackIncomplete, manifest,
                $"The update failed ({healthy.Detail}) and these files could not be restored: {string.Join(", ", failed)}.", previousVersion);
        }

        await RestartPreviousAsync(previousVersion, cancellationToken).ConfigureAwait(false);

        return Report(UpdateOutcome.RolledBack, manifest,
            $"Nexus {manifest.Version} did not start correctly ({healthy.Detail}). The previous version was restored.", previousVersion);
    }

    private async Task<HealthResult> StartAndCheckAsync(string? expectedVersion, CancellationToken cancellationToken)
    {
        int processId;
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            processId = _processes.Start(_paths.InstalledExecutable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new HealthResult(false, $"it could not be started: {ex.Message}");
        }

        _log($"Started PC Core (process {processId}); waiting for it to answer.");
        return await _health.WaitUntilHealthyAsync(processId, expectedVersion, startedAt, cancellationToken).ConfigureAwait(false);
    }

    private async Task RestartPreviousAsync(string? previousVersion, CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.InstalledExecutable))
        {
            return;
        }

        var result = await StartAndCheckAsync(previousVersion, cancellationToken).ConfigureAwait(false);
        _log(result.Healthy ? "The previous PC Core is running again." : $"The previous PC Core did not come back cleanly: {result.Detail}");
    }

    private UpdateOutcome Finish(UpdatePlan plan, string? previousVersion)
    {
        var manifest = plan.Manifest;

        UpdateJson.WriteAtomic(_paths.InstalledRecordPath, new InstalledRecord
        {
            Release = manifest.Release,
            Version = manifest.Version,
            InstalledAtUtc = DateTimeOffset.UtcNow,

            // What is there now, which for a preserved settings file is the owner's copy rather than
            // the release's - so the next update still recognises it as theirs.
            PcCoreFiles = manifest.Components.PcCore.Files
                .Select(f => plan.PreservedFiles.Contains(f.Path, StringComparer.OrdinalIgnoreCase)
                    && ReleaseStore.Contained(_paths.InstallDirectory, f.Path) is { } kept
                        ? f with { Sha256 = FileHash.Sha256OfFile(kept), Size = new FileInfo(kept).Length }
                        : f)
                .ToList(),
        });

        TryDeleteDirectory(_paths.StagingRoot);

        return Report(UpdateOutcome.Succeeded, manifest, $"Updated to Nexus {manifest.Version} (release {manifest.Release}).", previousVersion);
    }

    private string? CurrentVersion()
    {
        try
        {
            return File.Exists(_paths.InstalledExecutable)
                ? System.Diagnostics.FileVersionInfo.GetVersionInfo(_paths.InstalledExecutable).ProductVersion?.Split('+')[0]
                : null;
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            return null;
        }
    }

    private UpdateOutcome Report(UpdateOutcome outcome, ReleaseManifest? manifest, string? message, string? previousVersion = null)
    {
        _log(message ?? outcome.ToString());

        var state = outcome switch
        {
            UpdateOutcome.Succeeded or UpdateOutcome.UpToDate => UpdateState.Succeeded,
            UpdateOutcome.RolledBack => UpdateState.RolledBack,
            UpdateOutcome.NoRelease => UpdateState.Idle,
            _ => UpdateState.Failed,
        };

        try
        {
            _paths.WriteStatus(Status(state, manifest, message, previousVersion));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"The update status could not be written: {ex.Message}");
        }

        return outcome;
    }

    private static UpdateStatus Status(UpdateState state, ReleaseManifest? manifest, string? message, string? previousVersion = null) => new()
    {
        State = state,
        Release = manifest?.Release,
        Version = manifest?.Version,
        PreviousVersion = previousVersion,
        Message = message,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static string ElevatedNames(UpdatePlan plan) =>
        string.Join(" and ", new[] { plan.SecureCapture, plan.HidDriver }.Where(d => d.Updates).Select(d => d.Name));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
