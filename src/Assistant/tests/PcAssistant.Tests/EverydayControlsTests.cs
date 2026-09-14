using System;
using System.Linq;
using PcAssistant.Windows;
using Xunit;

namespace PcAssistant.Tests;

/// <summary>
/// The everyday controls, exercised against the real machine rather than against a mock,
/// because every one of them is a claim about this PC and a mock cannot be wrong about it.
///
/// Read-only checks always run. Anything the owner would notice — changing the volume,
/// raising a notification — is opt-in, so an ordinary <c>dotnet test</c> never turns the
/// speakers down or pops a toast.
/// </summary>
[Collection(DesktopCollection.Name)]
public sealed class EverydayControlsTests
{
    [Fact]
    public async Task AudioReportsTheRealDefaultPlaybackEndpoint()
    {
        using var audio = new AudioService();
        var state = await audio.GetAsync();
        Assert.InRange(state.VolumePercent, 0, 100);
        Assert.False(string.IsNullOrWhiteSpace(state.DeviceName));
        Assert.False(string.IsNullOrWhiteSpace(state.DeviceId));
    }

    [SkippableFact]
    public async Task VolumeIsSetAndReadBackFromTheDeviceThenRestored()
    {
        Skip.If(Environment.GetEnvironmentVariable("PCASSISTANT_NATIVE_SMOKE") != "1",
            "Set PCASSISTANT_NATIVE_SMOKE=1 to allow this test to change the machine's volume.");

        using var audio = new AudioService();
        var original = await audio.GetAsync();
        try
        {
            var quiet = await audio.SetVolumeAsync(30);
            // Read back from the endpoint, not echoed: an endpoint that quantizes is allowed to
            // land nearby, but it is not allowed to be ignored.
            Assert.InRange(quiet.VolumePercent, 28, 32);
            Assert.Equal(30, (await audio.GetAsync()).VolumePercent, tolerance: 2);
        }
        finally { await audio.SetVolumeAsync(original.VolumePercent); }

        Assert.Equal(original.VolumePercent, (await audio.GetAsync()).VolumePercent, tolerance: 2);
    }

    [Fact]
    public async Task VolumeOutsideZeroToOneHundredIsRefusedBeforeTouchingTheDevice()
    {
        using var audio = new AudioService();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => audio.SetVolumeAsync(101));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => audio.SetVolumeAsync(-1));
    }

    [Fact]
    public void PowerStateIsReportedHonestlyForThisMachine()
    {
        var power = SystemControls.Power();
        Assert.Contains(power.PowerSource, new[] { "AC power", "Battery", "Unknown" });
        if (power.BatteryPercent is { } percent) Assert.InRange(percent, 0, 100);
        else Assert.Contains("no system battery", power.BatteryState + power.Note);
    }

    [Fact]
    public void NetworkStateListsRealAdaptersAndSeparatesVirtualOnes()
    {
        var network = SystemControls.Network();
        Assert.Equal(Environment.MachineName, network.HostName);
        Assert.NotEmpty(network.Adapters);
        Assert.All(network.Adapters, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Name));
            Assert.False(string.IsNullOrWhiteSpace(a.Status));
            Assert.True(a.Addresses.Count <= 8);
        });
        // A tunnel listed as a virtual network must not also be reported as the active adapter.
        if (network.ActiveInterface is { } active)
            Assert.DoesNotContain(network.VirtualNetworks, v => v.StartsWith(active + " (", StringComparison.Ordinal));
    }

    [Fact]
    public void RecycleBinIsCountedWithoutBeingTouched()
    {
        var before = SystemControls.RecycleBin();
        Assert.True(before.ItemCount >= 0);
        Assert.True(before.TotalBytes >= 0);
        // Reading it twice must not change it: there is no path here that empties anything.
        Assert.Equal(before.ItemCount, SystemControls.RecycleBin().ItemCount);
    }

    [Fact]
    public void SettingsPagesResolveToDocumentedMsSettingsUris()
    {
        Assert.Equal("ms-settings:bluetooth", KnownTargets.SettingsUriFor("bluetooth"));
        Assert.Equal("ms-settings:windowsupdate", KnownTargets.SettingsUriFor("Windows Update"));
        Assert.Equal("ms-settings:display", KnownTargets.SettingsUriFor("display"));
        Assert.Equal("ms-settings:sound", KnownTargets.SettingsUriFor("sound"));
        Assert.Equal("ms-settings:appsfeatures", KnownTargets.SettingsUriFor("apps and features"));
        Assert.Equal("ms-settings:storage", KnownTargets.SettingsUriFor("ms-settings:storage"));
        Assert.Null(KnownTargets.SettingsUriFor("definitely not a settings page"));
        Assert.All(KnownTargets.All, t => Assert.False(string.IsNullOrWhiteSpace(t.Uri)));
    }

    [Fact]
    public void DiscoveryFindsRealApplicationsAndWindowsTargetsByName()
    {
        using var launcher = new AppLauncher();

        var bluetooth = launcher.Discover("bluetooth", 10);
        Assert.Contains(bluetooth, a => a.Target == "ms-settings:bluetooth");

        var taskManager = launcher.Discover("task manager", 10);
        Assert.Contains(taskManager, a => a.Name == "Task Manager");

        var downloads = launcher.Discover("downloads", 10);
        Assert.Contains(downloads, a => a.Target == "shell:Downloads");

        // Ordinary desktop and packaged applications are both found, with launch references.
        Assert.Contains(launcher.Discover("Notepad", 10), a => a.Name == "Notepad" && a.Reference.Length > 0);
        Assert.Contains(launcher.Discover(null, 200), a => a.Kind == "Windows packaged application");

        // Installed software answers "what is on this PC" without pretending to be launchable.
        var software = launcher.Discover(null, 200).Where(a => a.Kind == "Installed software").ToArray();
        Assert.All(software, a => Assert.Null(a.Target));

        // Every entry carries a usable reference, and references are distinct.
        var all = launcher.Discover(null, 60);
        Assert.NotEmpty(all);
        Assert.All(all, a => Assert.StartsWith("app-", a.Reference));
        Assert.Equal(all.Count, all.Select(a => a.Reference).Distinct().Count());
    }

    [Fact]
    public async Task UnknownApplicationsAndShellsAreRefusedRatherThanLaunched()
    {
        using var launcher = new AppLauncher();
        await Assert.ThrowsAsync<ArgumentException>(() => launcher.OpenAsync("no such application exists here"));
        await Assert.ThrowsAsync<ArgumentException>(() => launcher.OpenAsync("app-expired000"));

        // A shell is refused at launch, which is the guarantee that matters: a Start Menu
        // shortcut to one is discoverable by name like anything else, and resolving it is
        // exactly what proves it points at a shell.
        var shells = new[] { "Windows PowerShell", "Windows PowerShell ISE", "Command Prompt" };
        foreach (var shell in shells.Where(s => launcher.Discover(s, 4).Any(a => a.Name.Equals(s, StringComparison.OrdinalIgnoreCase))))
        {
            var refusal = await Assert.ThrowsAsync<ArgumentException>(() => launcher.OpenAsync(shell));
            Assert.Contains("shell or script host", refusal.Message);
        }

        await Assert.ThrowsAsync<ArgumentException>(() => launcher.OpenFolderAsync(@"\\server\share"));
        await Assert.ThrowsAsync<ArgumentException>(() => launcher.OpenFolderAsync(@"C:\definitely\not\here"));
        await Assert.ThrowsAsync<ArgumentException>(() => launcher.OpenPathWithAsync("Notepad", "relative/path"));
    }

    [Fact]
    public async Task LaunchingAnApplicationIsVerifiedByAWindowActuallyAppearing()
    {
        using var launcher = new AppLauncher();
        var desktop = new DesktopWindows();

        var outcome = await launcher.OpenAsync("Character Map");
        try
        {
            Assert.True(outcome.WindowAppeared,
                "Character Map should have produced a window within the wait. Note: " + outcome.Note);
            Assert.NotNull(outcome.Hwnd);
            Assert.False(string.IsNullOrWhiteSpace(outcome.WindowTitle));

            // The reported window is a real, live window belonging to a real process.
            var window = desktop.ListWindows(400).SingleOrDefault(w => w.Hwnd == outcome.Hwnd);
            Assert.NotNull(window);
            Assert.Equal(outcome.ProcessId, (int)window!.ProcessId);
        }
        finally
        {
            if (outcome.Hwnd is { } hwnd)
                await desktop.ChangeWindowAsync(hwnd, WindowAction.Close);
        }
    }
}
