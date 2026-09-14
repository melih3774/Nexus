using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;

namespace PcAssistant.Elevation;

public sealed record SetupResult(bool Ok, string Message);

/// <summary>
/// The one-time setup step, and its exact opposite.
///
/// This is the only place in PC Assistant that deliberately triggers a Windows UAC prompt,
/// and it is the honest place for one: installing a LocalSystem service genuinely is an
/// administrator action, and it happens once. Everything afterwards — every approved
/// administrator operation — goes through the installed helper and does not prompt again.
///
/// Nothing here is automatic. The owner presses a button; PC Assistant never installs or
/// starts the privileged component on its own.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HelperSetup
{
    /// <summary>Windows' own code for "the owner clicked No on the UAC prompt".</summary>
    private const int ErrorCancelled = 1223;

    public static Task<SetupResult> InstallAsync(
        string payloadDirectory, string clientExecutable, string reportPath, CancellationToken cancellationToken = default)
    {
        var host = Path.Combine(payloadDirectory, "PcAssistant.Elevation.Host.exe");
        if (!File.Exists(host))
        {
            return Task.FromResult(new SetupResult(false,
                "The privileged helper payload is missing at " + host + ". Rebuild PC Assistant."));
        }

        string ownerSid;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            ownerSid = identity.User?.Value ?? throw new InvalidOperationException("No user SID.");
        }
        catch (Exception ex)
        {
            return Task.FromResult(new SetupResult(false, "Could not determine the owner account: " + ex.Message));
        }

        return RunElevatedAsync(host,
            ["--install", "--source", payloadDirectory, "--client", clientExecutable,
             "--owner-sid", ownerSid, "--report", reportPath],
            reportPath, cancellationToken);
    }

    public static Task<SetupResult> UninstallAsync(
        string payloadDirectory, string reportPath, CancellationToken cancellationToken = default)
    {
        // Deliberately the app's own copy of the payload, not the installed one: a process
        // cannot delete the directory it is running from, and uninstall must leave nothing.
        var host = Path.Combine(payloadDirectory, "PcAssistant.Elevation.Host.exe");
        if (!File.Exists(host))
            return Task.FromResult(new SetupResult(false, "The privileged helper payload is missing at " + host + "."));

        return RunElevatedAsync(host, ["--uninstall", "--report", reportPath], reportPath, cancellationToken);
    }

    private static async Task<SetupResult> RunElevatedAsync(
        string host, string[] arguments, string reportPath, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.Delete(reportPath);
        }
        catch (Exception) { /* a stale report is detected by its content, not its absence */ }

        var startInfo = new ProcessStartInfo
        {
            FileName = host,
            UseShellExecute = true,   // required for the runas verb
            Verb = "runas",           // the ordinary Windows elevation prompt, on the secure desktop
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new SetupResult(false,
                "Windows administrator approval was declined, so nothing was installed or changed. " +
                "This confirmation happens on the local PC's secure desktop and cannot be answered remotely.");
        }
        catch (Exception ex)
        {
            return new SetupResult(false, "Could not start the setup step: " + ex.Message);
        }

        if (process is null) return new SetupResult(false, "Windows did not start the setup step.");

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new SetupResult(false, "The setup step did not finish within five minutes.");
            }

            if (TryReadReport(reportPath) is { } report) return report;

            return process.ExitCode == 0
                ? new SetupResult(true, "The setup step finished, but wrote no report. Re-check the helper status.")
                : new SetupResult(false, "The setup step failed with exit code " + process.ExitCode + ".");
        }
    }

    private static SetupResult? TryReadReport(string reportPath)
    {
        try
        {
            if (!File.Exists(reportPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
            var ok = document.RootElement.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
            var message = document.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            return new SetupResult(ok, message ?? (ok ? "Done." : "The setup step failed."));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
