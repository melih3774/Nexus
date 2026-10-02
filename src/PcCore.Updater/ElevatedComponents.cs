using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;

using PcCore.Update;
using PcCore.Update.Windows;

namespace PcCore.Updater;

/// <summary>
/// The unelevated half: asks Windows, once, to run the elevated half.
/// <para>
/// <b>One prompt, for exactly the components that need it.</b> The rest of the update never runs
/// elevated. Declining the prompt is an ordinary answer and ends the update with nothing changed.
/// </para>
/// </summary>
internal sealed class UacElevatedInstaller(Action<string> log) : IElevatedInstaller
{
    private const int ErrorCancelled = 1223;

    public async Task<ElevatedResult> InstallAsync(
        UpdatePlan plan,
        string stagedReleaseDirectory,
        Func<CancellationToken, Task> releaseHidDevice,
        CancellationToken cancellationToken)
    {
        // Only a driver update needs the device let go of; nothing else stops PC Core before the swap.
        using var handoff = plan.HidDriver.Updates ? HidDeviceHandoff.Create() : null;

        var arguments = string.Join(' ', new[]
        {
            "--elevated",
            "--staged", Quote(stagedReleaseDirectory),
            "--release", plan.Manifest.Release.ToString(CultureInfo.InvariantCulture),
            plan.SecureCapture.Updates ? "--secure-capture" : null,
            plan.HidDriver.Updates ? "--hid-driver" : null,
            handoff is null ? null : $"--hid-handoff {handoff.Id}",
        }.Where(a => a is not null));

        log("Requesting administrator approval (UAC).");

        try
        {
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments,
                WindowStyle = ProcessWindowStyle.Hidden,
            }) ?? throw new InvalidOperationException("Windows did not start the elevated updater.");

            var exited = process.WaitForExitAsync(cancellationToken);

            if (handoff is not null)
            {
                using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var requested = handoff.WhenRequestedAsync(stopWaiting.Token);

                if (await Task.WhenAny(exited, requested).ConfigureAwait(false) == requested && requested.IsCompletedSuccessfully)
                {
                    try
                    {
                        await releaseHidDevice(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        // Answered even if stopping PC Core went wrong: the elevated half then finds the
                        // device still held, and says a restart is needed, instead of waiting it out.
                        handoff.Release();
                    }
                }

                await stopWaiting.CancelAsync().ConfigureAwait(false);
            }

            await exited.ConfigureAwait(false);

            return process.ExitCode switch
            {
                0 => new ElevatedResult(ElevatedOutcome.Succeeded, "done"),
                ElevatedInstall.RestartRequiredExitCode => new ElevatedResult(
                    ElevatedOutcome.Succeeded, ElevatedInstall.LastLogLine() ?? "Windows needs a restart to load the new HID driver.")
                {
                    RestartRequired = true,
                },
                _ => new ElevatedResult(ElevatedOutcome.Failed, ElevatedInstall.LastLogLine() ?? $"exit code {process.ExitCode}"),
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new ElevatedResult(ElevatedOutcome.Declined, "the UAC prompt was declined");
        }
    }

    private static string Quote(string value) => "\"" + value.TrimEnd('\\') + "\"";
}

/// <summary>
/// The elevated half: installs SecureCapture and/or the HID driver from a staged release.
/// <para>
/// <b>Copied first, verified second, run third.</b> The staged files are in the owner's profile,
/// which any process running as the owner can write. So they are copied into
/// <c>%ProgramFiles%\Nexus\Updates</c>, which only administrators can write, and hashed again
/// there. Whatever is installed is what was verified, not what happened to be in the staging
/// directory a moment later.
/// </para>
/// <para>
/// <b>What it does not touch.</b> The driver certificate and the machine's trust stores, Secure Boot,
/// HVCI, test signing and UAC. A driver update reuses the trust the owner set up at install time; a
/// package Windows will not accept under that trust is refused, and the refusal is reported rather
/// than worked around.
/// </para>
/// </summary>
internal static class ElevatedInstall
{
    /// <summary>
    /// ERROR_SUCCESS_REBOOT_REQUIRED, as pnputil and msiexec use it: everything was installed, and the
    /// new HID driver runs only after Windows restarts. The last log line says what runs until then.
    /// </summary>
    public const int RestartRequiredExitCode = 3010;

    /// <summary>
    /// How long to wait for the unelevated half to stop PC Core. Its own limit is the stop grace period
    /// and a kill; past this, the driver is installed anyway and Windows says whether it needs a restart.
    /// </summary>
    private static readonly TimeSpan HandoffTimeout = TimeSpan.FromMinutes(2);

    private static string UpdatesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nexus", "Updates");

    private static string LogPath => Path.Combine(UpdatesRoot, "elevated.log");

    private static string SecureCaptureInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nexus", "SecureCapture");

    /// <param name="hidHandoff">
    /// Where to ask the unelevated half to stop PC Core before the driver is replaced; null when it was
    /// not offered, such as when this half is run on its own.
    /// </param>
    public static int Run(string staged, int release, bool secureCapture, bool hidDriver, string? hidHandoff)
    {
        using var identity = WindowsIdentity.GetCurrent();

        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            return 5;
        }

        Directory.CreateDirectory(UpdatesRoot);
        Log($"Elevated update of release {release}: SecureCapture={secureCapture}, HID driver={hidDriver}.");

        try
        {
            var manifest = UpdateJson.Read<ReleaseManifest>(Path.Combine(staged, ReleaseStager.ManifestFileName))
                ?? throw new UpdateIntegrityException("The staged manifest is empty.");

            ManifestValidator.ThrowIfInvalid(manifest);

            if (manifest.Release != release)
            {
                throw new UpdateIntegrityException("The staged manifest is for a different release.");
            }

            var work = Path.Combine(UpdatesRoot, release.ToString(CultureInfo.InvariantCulture));
            DeleteDirectory(work);

            var probe = new WindowsComponentProbe();

            if (secureCapture)
            {
                var component = manifest.Components.SecureCapture
                    ?? throw new UpdateIntegrityException("This release has no SecureCapture.");

                InstallSecureCapture(component, CopyVerified(component, Path.Combine(staged, ReleaseStager.SecureCaptureFolder), Path.Combine(work, "securecapture")), probe);
            }

            string? waitingForRestart = null;

            if (hidDriver)
            {
                var component = manifest.Components.HidDriver
                    ?? throw new UpdateIntegrityException("This release has no HID driver.");

                var previous = RollbackDriver(staged, probe);
                var target = HidDriverLifecycle.PayloadFrom(component, CopyVerified(component, Path.Combine(staged, ReleaseStager.HidDriverFolder), Path.Combine(work, "driver")));
                var rollback = HidDriverLifecycle.PayloadFrom(previous, CopyVerified(previous, Path.Combine(staged, ReleaseStager.HidDriverRollbackFolder), Path.Combine(work, "driver-rollback")));

                // Everything that could fail without consequence is done; now Nexus lets go of the device.
                ReleaseDevice(hidHandoff);

                var outcome = InstallDriver(target, rollback);
                waitingForRestart = outcome.RestartRequired ? outcome.Detail : null;
            }

            DeleteDirectory(work);

            if (waitingForRestart is not null)
            {
                Log("Elevated update finished; the new HID driver waits for Windows to restart.");

                // Last, because the unelevated half reports the last line.
                Log(waitingForRestart);
                return RestartRequiredExitCode;
            }

            Log("Elevated update finished.");
            return 0;
        }
        catch (Exception ex) when (ex is UpdateIntegrityException or IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            Log($"Elevated update failed: {ex.Message}");
            return 2;
        }
    }

    public static string? LastLogLine()
    {
        try
        {
            return File.Exists(LogPath) ? File.ReadLines(LogPath).LastOrDefault()?[20..] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string CopyVerified(FileSetComponent component, string from, string to)
    {
        foreach (var file in component.Files)
        {
            var source = ReleaseStore.Contained(from, file.Path) ?? throw new UpdateIntegrityException($"{file.Path} is not a safe path.");
            var target = ReleaseStore.Contained(to, file.Path) ?? throw new UpdateIntegrityException($"{file.Path} is not a safe path.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);

            if (!FileHash.Matches(target, file.Sha256, file.Size))
            {
                throw new UpdateIntegrityException($"{file.Path} does not match the release manifest.");
            }
        }

        return to;
    }

    private static void InstallSecureCapture(FileSetComponent component, string verified, WindowsComponentProbe probe)
    {
        var previous = Path.Combine(UpdatesRoot, "previous-securecapture");
        DeleteDirectory(previous);

        if (Directory.Exists(SecureCaptureInstallDirectory))
        {
            CopyDirectory(SecureCaptureInstallDirectory, previous);
        }

        // Its own installer, which already knows how to stop the service, replace its files in
        // Program Files, re-point the service and start it again.
        var (code, output) = RunTool(Path.Combine(verified, "PcCore.SecureCapture.exe"), "--install");
        var installed = probe.SecureCapture();

        if (code == 0 && string.Equals(installed.Version, component.Version, StringComparison.OrdinalIgnoreCase))
        {
            Log($"SecureCapture {component.Version} installed.");
            DeleteDirectory(previous);
            return;
        }

        Log($"SecureCapture install did not take (exit {code}, installed {installed.Version ?? "unknown"}): {output}");

        if (File.Exists(Path.Combine(previous, "PcCore.SecureCapture.exe")))
        {
            var (restoreCode, restoreOutput) = RunTool(Path.Combine(previous, "PcCore.SecureCapture.exe"), "--install");
            Log($"Restored the previous SecureCapture (exit {restoreCode}): {restoreOutput}");
        }

        throw new InvalidOperationException($"SecureCapture {component.Version} could not be installed.");
    }

    /// <summary>
    /// The driver installed now, from the release the updater staged it from - refused unless it really
    /// is the installed version, because restoring anything else is not a rollback.
    /// </summary>
    private static FileSetComponent RollbackDriver(string staged, WindowsComponentProbe probe)
    {
        var manifest = UpdateJson.Read<ReleaseManifest>(Path.Combine(staged, ReleaseStager.HidDriverRollbackManifestFileName))
            ?? throw new UpdateIntegrityException("No HID driver to roll back to was staged.");

        ManifestValidator.ThrowIfInvalid(manifest);

        var previous = manifest.Components.HidDriver
            ?? throw new UpdateIntegrityException($"Release {manifest.Release} has no HID driver to roll back to.");

        var installed = probe.HidDriver().Version;

        if (!string.Equals(previous.Version, installed, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateIntegrityException($"The staged rollback driver is {previous.Version}, but {installed ?? "an unknown version"} is installed.");
        }

        return previous;
    }

    /// <summary>
    /// Installs the new driver, removes every other Nexus package from the Driver Store, and proves the
    /// device healthy afterwards; puts the previous driver back from the release store if it is not.
    /// Updates the existing Nexus devnode in place. It never creates a second root device, which is the
    /// one mistake the original install notes warn about most.
    /// </summary>
    private static HidDriverOutcome InstallDriver(DriverPayload target, DriverPayload previous)
    {
        var outcome = new HidDriverLifecycle(new WindowsHidDriverHost(), Log).Update(target, previous);

        if (outcome.StalePackagesLeft.Count > 0)
        {
            Log("Nexus driver packages Windows would not remove: " + string.Join(", ", outcome.StalePackagesLeft));
        }

        // A driver waiting for a restart is said last, by Run, because the unelevated half reports the last line.
        if (!outcome.RestartRequired)
        {
            Log(outcome.Detail);
        }

        if (outcome.Kind != HidDriverOutcomeKind.Updated)
        {
            throw new InvalidOperationException(outcome.Detail);
        }

        return outcome;
    }

    /// <summary>
    /// Asks the unelevated half to stop PC Core, which holds the Nexus HID device open, and waits until
    /// it has. Without an answer the driver is installed anyway: the lifecycle then sees whether Windows
    /// could load it, and the outcome says if it could not.
    /// </summary>
    private static void ReleaseDevice(string? id)
    {
        if (id is null)
        {
            Log("No handoff was offered, so nothing was asked to let go of the HID device before the driver update.");
            return;
        }

        using var handoff = HidDeviceHandoff.Open(id);

        if (handoff is null)
        {
            Log("The HID device handoff could not be opened; updating the driver without it.");
            return;
        }

        Log("Asking the updater to stop PC Core, which holds the Nexus HID device open.");

        Log(handoff.Request(HandoffTimeout)
            ? "PC Core has let go of the HID device."
            : $"No answer within {HandoffTimeout.TotalMinutes:0} minutes; updating the driver anyway.");
    }

    private static (int Code, string Output) RunTool(string executable, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException($"{Path.GetFileName(executable)} could not be started.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            process.Kill();
            return (-1, "timed out");
        }

        var text = (stdout.Result + " " + stderr.Result).ReplaceLineEndings(" ").Trim();
        return (process.ExitCode, text.Length > 600 ? text[..600] : text);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static void DeleteDirectory(string path)
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

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
