using System.Diagnostics;
using System.Linq;
using System.Text;
using PcAssistant.Windows;
using Xunit;

namespace PcAssistant.Tests;

/// <summary>
/// What an unelevated PC Assistant can and cannot do with an elevated window it just opened.
///
/// This exists to produce an honest answer rather than an assumption. Windows User Interface
/// Privilege Isolation blocks a medium-integrity process from sending input to, or automating,
/// a high-integrity window. The point of this probe is to say exactly which capabilities
/// survive that boundary and which do not, and to write the measurement to a file so the
/// documentation quotes real results.
///
/// Nothing here weakens integrity policy. The elevated window is a PowerShell that sleeps and
/// exits on its own, so the probe leaves nothing behind.
///
/// The window is opened by the real PC Assistant executable, because that is the only client
/// the installed helper trusts. The measuring is then done from here, and that is not a
/// compromise: this test host is an ordinary unelevated medium-integrity process, which is
/// exactly the vantage point the measurement is about.
///
///     $env:PCASSISTANT_ELEVATION_LIVE = "1"; dotnet test --filter ElevatedWindowBoundaryTests
/// </summary>
[Collection(DesktopCollection.Name)]
public sealed class ElevatedWindowBoundaryTests
{
    private static string ReportPath => Path.Combine(
        LiveHelper.ReportDirectory, "elevated-window-boundaries.txt");

    [SkippableFact]
    public async Task MeasureWhatAnUnelevatedProcessCanDoWithAnElevatedWindow()
    {
        Skip.IfNot(LiveHelper.Enabled, "Set PCASSISTANT_ELEVATION_LIVE=1 with the helper installed.");
        var why = LiveHelper.WhyUnavailable();
        Skip.If(why is not null, why ?? string.Empty);

        // A visible elevated console that ends by itself, so the probe leaves nothing open.
        // It is opened by the trusted client, since the helper refuses anything else.
        var report = await LiveHelper.RunAsync(
            "elevated-window", "elevated-window-launch.json", TimeSpan.FromMinutes(2));

        var launch = report.Findings.Single(f => f.Name == "elevated-window");
        Assert.True(launch.Outcome == "Pass", launch.Detail);
        Assert.Equal("True", launch.Facts["elevated"]);
        var elevatedPid = int.Parse(launch.Facts["processId"]);

        var lines = new List<string>
        {
            "Elevated-window interaction boundary, measured " + DateTimeOffset.Now.ToString("u"),
            "Probe process elevated: " + IsElevated(Environment.ProcessId),
            "Target process elevated: " + IsElevated(elevatedPid) + "  (pid " + elevatedPid + ")",
            "",
        };

        try
        {
            var windows = new DesktopWindows();
            var target = await WaitForWindowAsync(windows, elevatedPid);

            // --- enumeration --------------------------------------------------
            lines.Add(target is null
                ? "Window enumeration      : NO window found for the elevated process"
                : $"Window enumeration      : OK - hwnd {target.Hwnd}, title \"{target.Title}\", bounds {target.Bounds}");
            Assert.NotNull(target);

            // --- screen capture -----------------------------------------------
            try
            {
                var shot = windows.Capture(target!.Bounds);
                lines.Add($"Screen capture          : OK - {shot.Png.Length} byte PNG of its on-screen region " +
                          "(desktop pixels are not integrity-protected)");
            }
            catch (Exception ex)
            {
                lines.Add("Screen capture          : FAILED - " + ex.Message);
            }

            // --- foreground ---------------------------------------------------
            try
            {
                var foreground = await ForegroundService.EnsureAsync(target!.Hwnd);
                lines.Add($"Foreground activation   : {(foreground.Verified ? "OK" : "FAILED")} - {foreground.Message}");
            }
            catch (Exception ex)
            {
                lines.Add("Foreground activation   : FAILED - " + ex.Message);
            }

            // --- UI Automation read -------------------------------------------
            using var automation = new UiAutomationService();
            try
            {
                var snapshot = await automation.SnapshotAsync(target!.Hwnd, maxDepth: 3, maxNodes: 40);
                lines.Add($"UI Automation snapshot  : returned {snapshot.Nodes.Count} node(s)" +
                          (snapshot.Nodes.Count <= 1
                              ? " - only the window itself; the elevated provider's tree is not readable"
                              : " - tree readable"));
            }
            catch (Exception ex)
            {
                lines.Add("UI Automation snapshot  : FAILED - " + ex.Message);
            }

            // --- synthetic input ----------------------------------------------
            using var input = new InputService(new PcAssistant.Windows.Capture.CaptureRegistry());
            try
            {
                await input.TypeTextAsync(target!.Hwnd, "probe");
                lines.Add("Synthetic keyboard input: ACCEPTED by SendInput - verify separately whether the " +
                          "elevated window received it");
            }
            catch (Exception ex)
            {
                lines.Add("Synthetic keyboard input: REFUSED - " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                await input.ClickAsync(target!.Hwnd, target.Bounds.X + 20, target.Bounds.Y + 20);
                lines.Add("Synthetic mouse click   : ACCEPTED by SendInput - verify separately whether the " +
                          "elevated window received it");
            }
            catch (Exception ex)
            {
                lines.Add("Synthetic mouse click   : REFUSED - " + ex.GetType().Name + ": " + ex.Message);
            }

            // --- window commands ----------------------------------------------
            try
            {
                var result = await windows.ChangeWindowAsync(target!.Hwnd, WindowAction.Minimize);
                lines.Add("Window minimize         : " + result);
            }
            catch (Exception ex)
            {
                lines.Add("Window minimize         : FAILED - " + ex.Message);
            }
        }
        finally
        {
            lines.Add("");
            lines.Add("The elevated console exits on its own; PC Assistant deliberately cannot close it.");

            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
            File.WriteAllLines(ReportPath, lines, new UTF8Encoding(false));
        }

        // The probe's value is the recorded measurement, so it is written unconditionally and
        // surfaced here too.
        Assert.True(File.Exists(ReportPath));
    }

    private static async Task<WindowInfo?> WaitForWindowAsync(DesktopWindows windows, int processId)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var match = windows.ListWindows(400).FirstOrDefault(w => w.ProcessId == (uint)processId);
            if (match is not null) return match;
            await Task.Delay(250);
        }
        return null;
    }

    private static string IsElevated(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var handle = process.SafeHandle;
            return NativeElevation.Query(process.Handle);
        }
        catch (Exception ex)
        {
            return "unknown (" + ex.GetType().Name + ")";
        }
    }
}

/// <summary>Reads a process's elevation from Windows rather than inferring it.</summary>
internal static class NativeElevation
{
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public static string Query(IntPtr process)
    {
        if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token)) return "unreadable";
        try
        {
            return GetTokenInformation(token, 20 /* TokenElevation */, out var elevated, sizeof(int), out _)
                ? (elevated != 0).ToString()
                : "unreadable";
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
