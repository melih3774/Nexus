using System.Diagnostics;
using System.Runtime.Versioning;

using Microsoft.Extensions.Logging;

using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Update;
using PcCore.Protocol;
using PcCore.Update;

namespace PcCore.Windows.Update;

/// <summary>
/// PC Core's side of updating: knowing whether there is something newer, handing the phone its APK,
/// and starting the updater for this PC.
/// <para>
/// <b>It never replaces a file itself.</b> PC Core cannot overwrite the executable it is running
/// from, and an update that stops its own process half way has nobody left to roll it back. So the
/// most this does to the installation is copy the release's updater into a runner directory and start
/// it. The updater stops PC Core, and the next PC Core reads what happened from <c>status.json</c>.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NexusUpdateService : IUpdateService, IDisposable
{
    /// <summary>The updater and the one library it needs, out of the PC Core file set.</summary>
    private static readonly string[] RunnerFiles =
    [
        "PcCore.Updater.exe",
        "PcCore.Updater.dll",
        "PcCore.Updater.deps.json",
        "PcCore.Updater.runtimeconfig.json",
        "PcCore.Update.dll",
    ];

    private readonly ReleaseStore _store;
    private readonly UpdatePaths _paths;
    private readonly IElevatedComponentProbe _probe;
    private readonly ILogger<NexusUpdateService> _logger;
    private readonly Lock _gate = new();

    private Snapshot? _snapshot;
    private string? _verifiedApk;
    private Timer? _poll;
    private int? _announcedRelease;
    private Process? _updater;

    public NexusUpdateService(ReleaseStore store, UpdatePaths paths, IElevatedComponentProbe probe, ILogger<NexusUpdateService> logger)
    {
        _store = store;
        _paths = paths;
        _probe = probe;
        _logger = logger;
    }

    /// <summary>Raised on a thread-pool thread when a release this PC has not installed appears.</summary>
    public event Action<UpdateCheckResult>? UpdateAvailable;

    /// <summary>The last check, without doing another one. For the tray, which must not hash files on its thread.</summary>
    public UpdateCheckResult? Last => _snapshot?.Result is { } result ? WithStatus(result) : null;

    public string InstallDirectory => _paths.InstallDirectory;

    /// <summary>What the last update did, as the updater recorded it.</summary>
    public UpdateStatus? LastStatus => _paths.ReadStatus();

    /// <summary>
    /// Raised once when the updater records how an update ended - success, failure or rollback - after
    /// this process started or after it launched the updater.
    /// </summary>
    public event Action<UpdateStatus>? UpdateFinished;

    /// <summary>Looks for new releases in the background. Reading <c>latest.json</c>'s timestamp is all a quiet tick costs.</summary>
    public void StartWatching(TimeSpan interval)
    {
        _poll ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), interval);
        WatchForOutcome(since: Process.GetCurrentProcess().StartTime.ToUniversalTime().AddMinutes(-2));
    }

    /// <summary>
    /// Watches <c>status.json</c> for a few minutes. The updater writes the outcome only after the new
    /// PC Core has passed its health check - that is, after this process has started - so a PC Core
    /// that was just updated, or just restored by a rollback, learns what happened by waiting briefly.
    /// </summary>
    private void WatchForOutcome(DateTimeOffset since)
    {
        _ = Task.Run(async () =>
        {
            var until = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);

            while (DateTimeOffset.UtcNow < until)
            {
                if (_paths.ReadStatus() is { InProgress: false, State: not UpdateState.Idle } status && status.UpdatedAtUtc >= since)
                {
                    lock (_gate)
                    {
                        _snapshot = null;
                    }

                    UpdateFinished?.Invoke(status);
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        });
    }

    public UpdateCheckResult Check(bool refresh, int? clientVersionCode, bool includePcDetails)
    {
        var snapshot = Current(refresh);
        return Shape(snapshot.Result, snapshot.Manifest, clientVersionCode, includePcDetails);
    }

    public async ValueTask<AndroidPackageChunk> ReadAndroidPackageAsync(int versionCode, long offset, int maxBytes, CancellationToken cancellationToken)
    {
        var snapshot = Current(refresh: false);

        var manifest = snapshot.Manifest
            ?? throw new CapabilityException(ErrorCodes.NotFound, "No Nexus release is published on this PC.");

        var android = manifest.Components.Android
            ?? throw new CapabilityException(ErrorCodes.NotFound, "The current release has no Android package.");

        if (android.VersionCode != versionCode)
        {
            // A new release was published during the download. Mixing bytes of two packages would
            // only fail the digest at the end; saying so now lets the phone start again at once.
            throw new CapabilityException(
                ErrorCodes.NotFound,
                $"Nexus version code {versionCode} is no longer offered; the current package is {android.VersionCode}. Check again.");
        }

        var path = ReleaseStore.AndroidFile(snapshot.ReleaseDirectory!, android);

        if (!string.Equals(_verifiedApk, path + "|" + android.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            // Checked once per package, before the first byte leaves the PC. A package that does not
            // match its own manifest is never offered, however the phone would have reacted.
            if (!FileHash.Matches(path, android.Sha256, android.Size))
            {
                _logger.LogError("The Android package {Path} does not match release {Release}'s manifest; not serving it.", path, manifest.Release);
                throw new CapabilityException(ErrorCodes.Unsupported, "The Android package on the PC does not match its release manifest.");
            }

            _verifiedApk = path + "|" + android.Sha256;
        }

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);

        if (offset >= file.Length)
        {
            return new AndroidPackageChunk
            {
                VersionCode = android.VersionCode,
                SizeBytes = file.Length,
                Sha256 = android.Sha256,
                Offset = offset,
                ChunkBytes = 0,
                EndOfFile = true,
            };
        }

        var count = (int)Math.Min(maxBytes, file.Length - offset);
        var buffer = new byte[count];

        file.Seek(offset, SeekOrigin.Begin);
        await file.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);

        return new AndroidPackageChunk
        {
            VersionCode = android.VersionCode,
            SizeBytes = file.Length,
            Sha256 = android.Sha256,
            Offset = offset,
            ChunkBytes = count,
            EndOfFile = offset + count >= file.Length,
            Data = Convert.ToBase64String(buffer),
        };
    }

    public InstallUpdateResult StartPcUpdate()
    {
        lock (_gate)
        {
            if (_updater is { HasExited: false } || _paths.ReadStatus() is { InProgress: true } status && IsFresh(status))
            {
                return new InstallUpdateResult(false, "An update is already running.", _snapshot?.Manifest?.Release);
            }

            var snapshot = Current(refresh: true);

            if (snapshot.Manifest is null)
            {
                return new InstallUpdateResult(false, snapshot.Result.Problem ?? "No Nexus release is published on this PC.", null);
            }

            var manifest = snapshot.Manifest;

            if (!snapshot.Result.PcUpdateAvailable)
            {
                return new InstallUpdateResult(false, $"This PC already has Nexus {manifest.Version} (release {manifest.Release}).", manifest.Release);
            }

            string runner;

            try
            {
                runner = PrepareRunner(manifest, snapshot.ReleaseDirectory!);
            }
            catch (Exception ex) when (ex is UpdateIntegrityException or IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "The updater for release {Release} could not be prepared.", manifest.Release);
                return new InstallUpdateResult(false, $"The updater could not be prepared: {ex.Message}", manifest.Release);
            }

            _paths.WriteStatus(new UpdateStatus
            {
                State = UpdateState.Staging,
                Release = manifest.Release,
                Version = manifest.Version,
                PreviousVersion = ProductInfo.Version,
                Message = "Starting the updater.",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });

            _logger.LogWarning(
                "Updating this PC to Nexus {Version} (release {Release}). PC Core will stop and restart; the updater logs to {Log}.",
                manifest.Version,
                manifest.Release,
                _paths.LogPath);

            var launchedAt = DateTimeOffset.UtcNow;

            _updater = Process.Start(new ProcessStartInfo(runner, $"--apply --release {manifest.Release}")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(runner)!,
            });

            // Most outcomes arrive after this process has been replaced. The ones that do not - a
            // declined UAC prompt, a release that failed verification - are reported to this one.
            WatchForOutcome(launchedAt);

            return new InstallUpdateResult(true, $"Updating to Nexus {manifest.Version}. PC Core will restart.", manifest.Release);
        }
    }

    private static bool IsFresh(UpdateStatus status) => DateTimeOffset.UtcNow - status.UpdatedAtUtc < TimeSpan.FromMinutes(10);

    /// <summary>
    /// The updater runs from a copy, never from the install directory it is about to change, and never
    /// from the release store, which a later publish may prune while it is still running.
    /// </summary>
    private string PrepareRunner(ReleaseManifest manifest, string releaseDirectory)
    {
        var component = manifest.Components.PcCore;
        var directory = Path.Combine(_paths.RunnerRoot, manifest.Release.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Directory.CreateDirectory(directory);

        foreach (var name in RunnerFiles)
        {
            var file = component.Files.FirstOrDefault(f => string.Equals(f.Path, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new UpdateIntegrityException($"Release {manifest.Release} has no {name}.");

            var target = Path.Combine(directory, name);

            if (!FileHash.Matches(target, file.Sha256, file.Size))
            {
                File.Copy(ReleaseStore.ComponentFile(releaseDirectory, component, file), target, overwrite: true);

                if (!FileHash.Matches(target, file.Sha256, file.Size))
                {
                    throw new UpdateIntegrityException($"{name} does not match the release manifest.");
                }
            }
        }

        // Older runners are only disk space once this one exists.
        foreach (var old in Directory.EnumerateDirectories(_paths.RunnerRoot).Where(d => !string.Equals(d, directory, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Directory.Delete(old, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return Path.Combine(directory, UpdatePaths.UpdaterExecutableName);
    }

    private void Tick()
    {
        try
        {
            var snapshot = Current(refresh: false);
            var result = snapshot.Result;

            if (result.PcUpdateAvailable && result.LatestRelease is { } release && release != _announcedRelease)
            {
                _announcedRelease = release;
                _logger.LogInformation(
                    "Nexus {Version} (release {Release}) is available for this PC ({Files} file(s){Approval}).",
                    result.LatestVersion,
                    release,
                    result.PcChangedFiles,
                    result.NeedsAdministratorApproval ? ", needs administrator approval" : string.Empty);

                UpdateAvailable?.Invoke(result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The background update check failed.");
        }
    }

    /// <summary>
    /// The cached check, redone when asked, when <c>latest.json</c> has changed, or when it is a few
    /// minutes old - the install directory can change without the release store changing.
    /// </summary>
    private Snapshot Current(bool refresh)
    {
        lock (_gate)
        {
            var stamp = PointerStamp();

            if (!refresh && _snapshot is { } cached && cached.PointerStamp == stamp && DateTimeOffset.UtcNow - cached.TakenAtUtc < TimeSpan.FromMinutes(5))
            {
                return cached with { Result = WithStatus(cached.Result) };
            }

            _snapshot = Take(stamp);
            return _snapshot;
        }
    }

    private Snapshot Take(DateTime? stamp)
    {
        var now = DateTimeOffset.UtcNow;
        var load = _store.LoadLatest();
        var installed = _paths.ReadInstalledRecord();

        if (!load.Found)
        {
            return new Snapshot(null, null, stamp, now, new UpdateCheckResult
            {
                RunningVersion = ProductInfo.Version,
                InstalledRelease = installed?.Release,
                MinimumAndroidVersionCode = ClientCompatibility.MinimumAndroidVersionCode,
                State = StateWord(),
                Message = StatusMessage(),
                Problem = load.Problem,
                CheckedAtUtc = now,
            });
        }

        var manifest = load.Manifest!;
        UpdatePlan? plan = null;
        string? problem = null;

        try
        {
            plan = UpdatePlanner.Plan(manifest, load.ReleaseDirectory!, _paths.InstallDirectory, installed, _probe, _paths.ReadAssistantRecord());
        }
        catch (Exception ex) when (ex is UpdateIntegrityException or IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
        }

        var result = new UpdateCheckResult
        {
            RunningVersion = ProductInfo.Version,
            ReleaseAvailable = true,
            LatestVersion = manifest.Version,
            LatestRelease = manifest.Release,
            InstalledRelease = installed?.Release,
            PcUpdateAvailable = plan is { UpToDate: false },
            PcChangedFiles = (plan?.PcCore.Count ?? 0) + (plan?.Assistant.Count ?? 0),
            NeedsAdministratorApproval = plan?.NeedsElevation ?? false,
            Components = plan is null
                ? []
                :
                [
                    Describe(plan.SecureCapture),
                    Describe(plan.HidDriver),
                    Describe(plan.AssistantComponent),
                ],
            MinimumAndroidVersionCode = ClientCompatibility.MinimumAndroidVersionCode,
            State = StateWord(),
            Message = StatusMessage(),
            Problem = problem,
            CheckedAtUtc = now,
        };

        return new Snapshot(manifest, load.ReleaseDirectory, stamp, now, result);
    }

    private static UpdateCheckResult Shape(UpdateCheckResult result, ReleaseManifest? manifest, int? clientVersionCode, bool includePcDetails)
    {
        var android = manifest?.Components.Android is { } apk
            ? new AndroidUpdateInfo
            {
                VersionName = apk.VersionName,
                VersionCode = apk.VersionCode,
                SizeBytes = apk.Size,
                Sha256 = apk.Sha256,
                SigningCertSha256 = apk.SigningCertSha256,
                MinSdk = apk.MinSdk,
                UpdateAvailable = clientVersionCode is { } code && apk.VersionCode > code,
                Required = clientVersionCode is not null && ClientCompatibility.RequiresUpdate(clientVersionCode),
            }
            : null;

        return includePcDetails
            ? result with { Android = android }
            : result with { Android = android, Components = [], PcChangedFiles = 0 };
    }

    private static ElevatedComponentInfo Describe(ComponentDecision decision) =>
        new(decision.Name, decision.InstalledVersion, decision.AvailableVersion, decision.Updates, decision.Reason);

    private UpdateCheckResult WithStatus(UpdateCheckResult result)
    {
        var status = _paths.ReadStatus();
        return result with { State = StateWord(status), Message = status?.Message };
    }

    private string StateWord() => StateWord(_paths.ReadStatus());

    private string? StatusMessage() => _paths.ReadStatus()?.Message;

    private string StateWord(UpdateStatus? status) => (status?.State ?? UpdateState.Idle) switch
    {
        UpdateState.WaitingForApproval => "waiting_for_approval",
        UpdateState.RolledBack => "rolled_back",

        // Recorded once by the updater; whether the restart is still outstanding is Windows' to say. A
        // restart clears it, and the update then simply succeeded.
        UpdateState.RestartRequired => _probe.HidDriver().RestartPending ? "restart_required" : "succeeded",
        var state => state.ToString().ToLowerInvariant(),
    };

    private DateTime? PointerStamp()
    {
        try
        {
            return File.Exists(_store.PointerPath) ? File.GetLastWriteTimeUtc(_store.PointerPath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _poll?.Dispose();
        _updater?.Dispose();
    }

    private sealed record Snapshot(
        ReleaseManifest? Manifest,
        string? ReleaseDirectory,
        DateTime? PointerStamp,
        DateTimeOffset TakenAtUtc,
        UpdateCheckResult Result);
}
