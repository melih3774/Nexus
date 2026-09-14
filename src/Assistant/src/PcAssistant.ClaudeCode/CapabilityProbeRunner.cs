using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PcAssistant.ClaudeCode;

/// <summary>One measured capability: what was tried and what happened.</summary>
public sealed record CapabilityLine(string Name, string State, string Detail);

/// <summary>
/// Asks the PC tools helper what this machine can actually do, by running its self-check.
///
/// It runs out of process on purpose. PC Assistant's UI has no reference to the native layer
/// and should not grow one — the tools already live behind a process boundary for the model,
/// and reusing it here keeps the WPF app free of Windows Graphics Capture, UI Automation and
/// Core Audio. The cost is one short child process when the owner opens the panel.
///
/// The probe is read-only and takes no approvals: the owner asked, not a model.
/// </summary>
public static class CapabilityProbeRunner
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Runs the self-check, or returns null when the helper is missing, too slow, or answers
    /// with something that is not a report. A null means "not measured" and callers must say so
    /// rather than falling back to claiming things work.
    /// </summary>
    public static async Task<IReadOnlyList<CapabilityLine>?> RunAsync(
        string pcToolsPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(pcToolsPath)) return null;

        using var process = new Process
        {
            StartInfo =
            {
                FileName = pcToolsPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            },
        };
        process.StartInfo.ArgumentList.Add("--probe");

        try
        {
            if (!process.Start()) return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        try
        {
            // Bounded well under the probe's own budget, so a wedged check never wedges the panel.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return null;

            return JsonSerializer.Deserialize<List<CapabilityLine>>(output, Options);
        }
        catch (Exception ex) when (ex is OperationCanceledException or JsonException)
        {
            return null;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception) { /* Already gone. */ }
        }
    }

    /// <summary>Formats a report for the capabilities panel, or says plainly that nothing was measured.</summary>
    public static string Describe(IReadOnlyList<CapabilityLine>? lines) => lines is { Count: > 0 }
        ? string.Join(Environment.NewLine, lines.SelectMany(l => new[] { $"{l.Name}: {l.State}", "  " + l.Detail }))
        : "Native capabilities: not measured. The PC tools helper did not answer its self-check, so nothing below it " +
          "is known to work. Build the Desktop project to deploy the helper, then Refresh.";
}
