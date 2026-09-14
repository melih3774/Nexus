using System;
using System.IO;
using System.Linq;
using PcAssistant.ClaudeCode;
using PcAssistant.Desktop;
using Xunit;

namespace PcAssistant.Tests;

/// <summary>
/// Two claims that are easy to get wrong quietly: that the capabilities panel reports what was
/// measured rather than what was built, and that starting with Windows never registers
/// something other than PC Assistant.
/// </summary>
[Collection(DesktopCollection.Name)]
public sealed class DiagnosticsAndStartupTests
{
    [Fact]
    public async Task TheProbeMeasuresThisMachineThroughTheRealHelper()
    {
        var helper = FindPcTools();
        Assert.True(File.Exists(helper), "The Desktop build must deploy the PC tools helper: " + helper);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var lines = await CapabilityProbeRunner.RunAsync(helper, timeout.Token);

        Assert.NotNull(lines);
        Assert.NotEmpty(lines!);
        Assert.All(lines!, line =>
        {
            Assert.False(string.IsNullOrWhiteSpace(line.Name));
            Assert.Contains(line.State, new[] { "Ready", "Limited", "Unavailable" });
            // A state with no explanation is the failure mode this replaced.
            Assert.False(string.IsNullOrWhiteSpace(line.Detail));
        });

        foreach (var expected in new[] { "Window capture", "Windows UI Automation", "Clipboard", "Input" })
            Assert.Contains(lines!, l => l.Name == expected);

        // Whatever it says about capture, it says which mechanism it measured.
        var capture = lines!.Single(l => l.Name == "Window capture");
        Assert.True(capture.Detail.Contains("Graphics Capture", StringComparison.OrdinalIgnoreCase) ||
                    capture.Detail.Contains("PrintWindow", StringComparison.OrdinalIgnoreCase) ||
                    capture.Detail.Contains("visible desktop pixels", StringComparison.OrdinalIgnoreCase) ||
                    capture.State == "Unavailable",
            "The capture line must name the mechanism it measured: " + capture.Detail);
    }

    [Fact]
    public async Task AMissingOrSilentHelperIsReportedAsNotMeasuredRatherThanReady()
    {
        Assert.Null(await CapabilityProbeRunner.RunAsync(
            Path.Combine(Path.GetTempPath(), "pc-assistant-no-such-helper.exe"), CancellationToken.None));

        var text = CapabilityProbeRunner.Describe(null);
        Assert.Contains("not measured", text);
        Assert.DoesNotContain("Ready", text);
    }

    [Fact]
    public void ADescribedReportShowsEveryStateAndItsReason()
    {
        var text = CapabilityProbeRunner.Describe(
        [
            new CapabilityLine("Window capture", "Ready", "Graphics Capture works."),
            new CapabilityLine("Audio", "Unavailable", "No playback endpoint."),
        ]);

        Assert.Contains("Window capture: Ready", text);
        Assert.Contains("Graphics Capture works.", text);
        Assert.Contains("Audio: Unavailable", text);
        Assert.Contains("No playback endpoint.", text);
    }

    [Fact]
    public void StartupRefusesToRegisterAnythingThatIsNotPcAssistantsOwnExecutable()
    {
        // The test host is not PcAssistant.Desktop.exe, so this is the guard doing its job:
        // registering the current process here would put testhost.exe — or dotnet.exe under
        // 'dotnet run' — into the owner's startup.
        var state = WindowsStartup.Read();
        Assert.False(state.Available);
        Assert.False(state.Enabled);
        Assert.Contains("PcAssistant.Desktop.exe", state.Detail);

        // And attempting to set it changes nothing rather than writing the wrong path.
        var afterEnable = WindowsStartup.Set(true);
        Assert.False(afterEnable.Enabled);
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run");
        Assert.Null(key?.GetValue(WindowsStartup.ValueName));
    }

    [Fact]
    public void TheToggleReportsWhatWasReadBackNotWhatWasClicked()
    {
        using var harness = new TestHarness();
        using var model = new PcAssistant.Desktop.ViewModels.MainViewModel(
            harness.Service, harness.Workspaces, harness.Settings, harness.Paths);
        {
            var state = new StartupState(false, true, "Disabled", "Nothing runs at sign-in.");
            model.StartupStateProvider = () => state;
            model.StartupStateSetter = enabled =>
            {
                // A setter that refuses, as a failed registry write would.
                state = enabled
                    ? new StartupState(false, true, "Could not be changed", "Access denied.")
                    : new StartupState(false, true, "Disabled", "Nothing runs at sign-in.");
                return state;
            };

            model.RefreshStartup();
            Assert.False(model.StartWithWindows);
            Assert.Contains("Disabled", model.StartupSummary);
            Assert.True(model.CanChangeStartup);

            model.StartWithWindows = true;
            Assert.False(model.StartWithWindows);
            Assert.Contains("Could not be changed", model.StartupSummary);

            // A successful setter is reflected, and the summary follows it.
            state = new StartupState(true, true, "Enabled", "Windows starts PC Assistant at sign-in.");
            model.StartupStateSetter = _ => state;
            model.StartWithWindows = true;
            Assert.True(model.StartWithWindows);
            Assert.Contains("Enabled", model.StartupSummary);
        }
    }

    [Fact]
    public void AnUnavailableStartupSettingCannotBeToggled()
    {
        using var harness = new TestHarness();
        using var model = new PcAssistant.Desktop.ViewModels.MainViewModel(
            harness.Service, harness.Workspaces, harness.Settings, harness.Paths);
        {
            model.StartupStateProvider = () => new StartupState(false, false, "Unavailable in this build", "Hosted process.");
            model.StartupStateSetter = _ => throw new Xunit.Sdk.XunitException("An unavailable setting must not be written.");
            model.RefreshStartup();

            Assert.False(model.CanChangeStartup);
            model.StartWithWindows = true;   // the setter above would fail the test if reached
            Assert.False(model.StartWithWindows);
        }
    }

    private static string FindPcTools()
    {
        var deployed = Path.Combine(AppContext.BaseDirectory, "PcTools", "PcAssistant.PcTools.exe");
        if (File.Exists(deployed)) return deployed;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PcAssistant.Desktop", "bin", "Debug",
                "net10.0-windows", "PcTools", "PcAssistant.PcTools.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return deployed;
    }
}
