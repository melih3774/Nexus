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

    public async Task<ElevatedResult> InstallAsync(UpdatePlan plan, string stagedReleaseDirectory, CancellationToken cancellationToken)
    {
        var arguments = string.Join(' ', new[]
        {
            "--elevated",
            "--staged", Quote(stagedReleaseDirectory),
            "--release", plan.Manifest.Release.ToString(CultureInfo.InvariantCulture),
            plan.SecureCapture.Updates ? "--secure-capture" : null,
            plan.HidDriver.Updates ? "--hid-driver" : null,
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

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return process.ExitCode == 0
                ? new ElevatedResult(ElevatedOutcome.Succeeded, "done")
                : new ElevatedResult(ElevatedOutcome.Failed, ElevatedInstall.LastLogLine() ?? $"exit code {process.ExitCode}");
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
    private static string UpdatesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nexus", "Updates");

    private static string LogPath => Path.Combine(UpdatesRoot, "elevated.log");

    private static string SecureCaptureInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nexus", "SecureCapture");

    public static int Run(string staged, int release, bool secureCapture, bool hidDriver)
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

            if (hidDriver)
            {
                var component = manifest.Components.HidDriver
                    ?? throw new UpdateIntegrityException("This release has no HID driver.");

                var previous = RollbackDriver(staged, probe);

                InstallDriver(
                    HidDriverLifecycle.PayloadFrom(component, CopyVerified(component, Path.Combine(staged, ReleaseStager.HidDriverFolder), Path.Combine(work, "driver"))),
                    HidDriverLifecycle.PayloadFrom(previous, CopyVerified(previous, Path.Combine(staged, ReleaseStager.HidDriverRollbackFolder), Path.Combine(work, "driver-rollback"))));
            }

            DeleteDirectory(work);
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
    private static void InstallDriver(DriverPayload target, DriverPayload previous)
    {
        var outcome = new HidDriverLifecycle(new WindowsHidDriverHost(), Log).Update(target, previous);

        if (outcome.StalePackagesLeft.Count > 0)
        {
            Log("Nexus driver packages Windows would not remove: " + string.Join(", ", outcome.StalePackagesLeft));
        }

        Log(outcome.Detail);

        if (outcome.Kind != HidDriverOutcomeKind.Updated)
        {
            throw new InvalidOperationException(outcome.Detail);
        }
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
