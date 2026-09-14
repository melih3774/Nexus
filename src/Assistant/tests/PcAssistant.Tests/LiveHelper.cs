using System.Diagnostics;
using System.Text.Json;
using PcAssistant.Core.Elevation;
using PcAssistant.Elevation;

namespace PcAssistant.Tests;

/// <summary>
/// Starts the real PC Assistant executable in its verification mode and reads back what it
/// measured.
///
/// This indirection is the whole point, and it is not a workaround. The installed helper
/// identifies its caller from the pipe connection — owner SID, integrity level, session, and
/// the image path recorded at install time — and refuses anything else before it parses a
/// byte. A test host connecting directly is therefore denied, correctly. Denied is not the
/// same as verified, so anything that has to prove elevation actually happens is driven
/// through the executable the helper was installed for:
///
///     xUnit  →  PcAssistant.Desktop.exe --verify-privileged  →  pipe  →  service  →  token
///
/// The executable is not guessed at: it is read from the helper's own installed state, so
/// this drives exactly the client the running service trusts, or fails saying it cannot.
/// </summary>
internal static class LiveHelper
{
    public static bool Enabled => Environment.GetEnvironmentVariable("PCASSISTANT_ELEVATION_LIVE") == "1";

    public static string ReportDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcAssistant");

    /// <summary>Null when everything is in place; otherwise why live verification cannot run.</summary>
    public static string? WhyUnavailable()
    {
        if (!HelperPaths.IsInstalled)
            return "No privileged helper is installed at " + HelperPaths.InstallRoot + ".";

        var state = HelperPaths.ReadState();
        if (state is null)
            return "The privileged helper has no readable state file at " + HelperPaths.StateFile + ".";

        if (string.IsNullOrWhiteSpace(state.ClientExecutable))
            return "The installed helper recorded no client executable, so no client can be trusted by it.";

        if (!File.Exists(state.ClientExecutable))
        {
            return "The installed helper trusts " + state.ClientExecutable +
                   ", which does not exist. Rebuild PC Assistant to that path, or reinstall the helper.";
        }

        return null;
    }

    public static string TrustedClientExecutable => HelperPaths.ReadState()?.ClientExecutable
        ?? throw new InvalidOperationException("No installed helper state.");

    /// <summary>
    /// Runs the named probes in the trusted client and returns its report. The probe names
    /// are the closed set the client itself defines; nothing else can be passed through.
    /// </summary>
    public static async Task<VerificationReport> RunAsync(string probes, string reportName, TimeSpan timeout)
    {
        Directory.CreateDirectory(ReportDirectory);
        var reportPath = Path.Combine(ReportDirectory, reportName);
        if (File.Exists(reportPath)) File.Delete(reportPath);

        var start = new ProcessStartInfo(TrustedClientExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(TrustedClientExecutable)!,
        };
        start.ArgumentList.Add(PrivilegedVerification.Switch);
        start.ArgumentList.Add(probes);
        start.ArgumentList.Add(reportPath);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Windows did not start " + TrustedClientExecutable + ".");

        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            throw new TimeoutException(
                $"The trusted client did not finish its verification within {timeout.TotalSeconds:0} seconds. " +
                "A UAC prompt waiting for an answer would look exactly like this.");
        }

        if (!File.Exists(reportPath))
        {
            throw new InvalidOperationException(
                $"The trusted client exited with code {process.ExitCode} without writing {reportPath}.");
        }

        return JsonSerializer.Deserialize<VerificationReport>(
                   File.ReadAllText(reportPath), PrivilegedProtocol.Json)
               ?? throw new InvalidOperationException("The verification report at " + reportPath + " was unreadable.");
    }
}
