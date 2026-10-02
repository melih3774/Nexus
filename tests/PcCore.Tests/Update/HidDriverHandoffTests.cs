using PcCore.Tests.TestSupport;
using PcCore.Update;
using PcCore.Update.Windows;
using PcCore.Windows.Update;
using Xunit;

namespace PcCore.Tests.Update;

/// <summary>
/// The update around a HID driver change: PC Core must not be holding the Nexus device when the driver
/// is replaced, and an update Windows can only finish after a restart must say so.
/// <para>
/// Release 27 replaced 0.9.2.2 with 0.9.2.3 while PC Core still had the control device open. Windows
/// vetoed the device restart (Kernel-PnP 225 named PcCore.Host.exe), bound 0.9.2.3 and kept running
/// 0.9.2.2; the update reported "Updated to Nexus 1.5.0", and PC Core logged "Nexus HID 9.2.2" until the
/// next restart of the PC.
/// </para>
/// </summary>
public sealed class HidDriverHandoffTests : IDisposable
{
    private readonly ReleaseFixture _fixture = new();
    private readonly List<string> _log = [];

    public void Dispose() => _fixture.Dispose();

    private static (string, IReadOnlyDictionary<string, string>) Driver(string version) =>
        (version, new Dictionary<string, string> { ["NexusHid.inf"] = $"inf {version}", ["NexusHidDriver.dll"] = $"dll {version}" });

    /// <summary>
    /// PC Core v1 installed and running on driver 0.9.2.2, which release 26 carries (the way back);
    /// release 27 brings 0.9.2.3, and a new PC Core too unless <paramref name="driverOnly"/>.
    /// </summary>
    private void InstalledOn0922Offering0923(bool driverOnly = false)
    {
        _fixture.Install(ReleaseFixture.BaseFiles());
        _fixture.Publish(26, "1.5.0", ReleaseFixture.BaseFiles(), driver: Driver("0.9.2.2"));

        var next = ReleaseFixture.BaseFiles();

        if (!driverOnly)
        {
            next["PcCore.Host.exe"] = "host v2";
        }

        _fixture.Publish(27, "1.5.0", next, driver: Driver("0.9.2.3"));
    }

    private UpdateCoordinator Coordinator(FakeProcesses processes, FakeElevated elevated, FakeHealth? health = null) =>
        new(_fixture.Store, _fixture.Paths, new FakeComponentProbe(driver: new InstalledComponent(true, "0.9.2.2")),
            processes, health ?? new FakeHealth(true), elevated, _log.Add);

    private FakeProcesses PcCore() => new(_fixture.Paths.InstalledExecutable);

    [Fact]
    public async Task TheDriverIsReplacedOnlyOncePcCoreHasLetGoOfTheDevice()
    {
        InstalledOn0922Offering0923();
        var processes = PcCore();
        var elevated = new FakeElevated(processes: processes);

        var outcome = await Coordinator(processes, elevated).RunAsync(27, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Succeeded, outcome);
        Assert.Empty(elevated.RunningDuringDriverStep!);

        // Stopped once - for the driver, not again for the swap - and started once, as the new version.
        Assert.Equal(["stop 1", "start host v2"], processes.Events);
        Assert.Equal(UpdateState.Succeeded, _fixture.Paths.ReadStatus()!.State);
        Assert.Contains(_log, l => l.StartsWith("The HID driver is about to be replaced; stopping PC Core", StringComparison.Ordinal));
    }

    /// <summary>A release whose PC Core files did not change still stops PC Core for the driver, and starts the same one again.</summary>
    [Fact]
    public async Task ADriverOnlyUpdateStartsTheSamePcCoreAgainAfterwards()
    {
        InstalledOn0922Offering0923(driverOnly: true);
        var processes = PcCore();
        var health = new FakeHealth(true);
        var elevated = new FakeElevated(processes: processes);

        var outcome = await Coordinator(processes, elevated, health).RunAsync(27, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Succeeded, outcome);
        Assert.Empty(elevated.RunningDuringDriverStep!);
        Assert.Equal(["stop 1", "start host v1"], processes.Events);

        // The version it had before - these test files carry none - not one it never had.
        Assert.Equal([null], health.ExpectedVersions);
        Assert.Equal(27, _fixture.Paths.ReadInstalledRecord()!.Release);
    }

    [Fact]
    public async Task APcCoreThatWasNotRunningIsNotStartedByADriverOnlyUpdate()
    {
        InstalledOn0922Offering0923(driverOnly: true);
        var processes = PcCore();
        processes.Running.Clear();

        var outcome = await Coordinator(processes, new FakeElevated(processes: processes)).RunAsync(27, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Succeeded, outcome);
        Assert.Empty(processes.Events);
    }

    /// <summary>Approval comes first: a declined prompt for a driver update leaves PC Core serving the phone throughout.</summary>
    [Fact]
    public async Task DecliningApprovalForADriverUpdateNeverStopsPcCore()
    {
        InstalledOn0922Offering0923();
        var before = ReleaseFixture.Snapshot(_fixture.Paths.InstallDirectory);
        var processes = PcCore();

        var outcome = await Coordinator(processes, new FakeElevated(ElevatedOutcome.Declined, processes)).RunAsync(27, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Failed, outcome);
        Assert.Empty(processes.Events);
        Assert.Equal(before, ReleaseFixture.Snapshot(_fixture.Paths.InstallDirectory));
        Assert.Contains("Nothing was changed", _fixture.Paths.ReadStatus()!.Message, StringComparison.Ordinal);
    }

    /// <summary>The driver step failed after PC Core was stopped for it: the same PC Core comes back, and its files are untouched.</summary>
    [Fact]
    public async Task AFailedDriverUpdateBringsTheSamePcCoreBack()
    {
        InstalledOn0922Offering0923();
        var before = ReleaseFixture.Snapshot(_fixture.Paths.InstallDirectory);
        var processes = PcCore();

        var outcome = await Coordinator(processes, new FakeElevated(ElevatedOutcome.Failed, processes)).RunAsync(27, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Failed, outcome);
        Assert.Equal(["stop 1", "start host v1"], processes.Events);
        Assert.Equal(before, ReleaseFixture.Snapshot(_fixture.Paths.InstallDirectory));
        Assert.Contains("PC Core and PC Assistant were not changed", _fixture.Paths.ReadStatus()!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Something other than Nexus's own PC Core held the device, so Windows will load the new driver only
    /// after a restart. Everything else is installed and running, and the result says what is not.
    /// </summary>
    [Fact]
    public async Task AnUpdateWindowsCanOnlyFinishAfterARestartSaysSo()
    {
        InstalledOn0922Offering0923();
        var processes = PcCore();

        var outcome = await Coordinator(processes, new FakeElevated(processes: processes) { RestartRequired = true })
            .RunAsync(27, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.RestartRequired, outcome);
        Assert.Equal(["stop 1", "start host v2"], processes.Events);
        Assert.Equal(27, _fixture.Paths.ReadInstalledRecord()!.Release);

        var status = _fixture.Paths.ReadStatus()!;
        Assert.Equal(UpdateState.RestartRequired, status.State);
        Assert.False(status.InProgress);
        Assert.StartsWith("Installed Nexus 1.5.0 (release 27). Restart Windows to finish:", status.Message, StringComparison.Ordinal);
        Assert.Contains("0.9.2.2 keeps running until the PC restarts", status.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Updated to", status.Message, StringComparison.Ordinal);
    }

    /// <summary>A PC Core rollback after the driver step does not undo the driver, and does not hide that it waits for a restart.</summary>
    [Fact]
    public async Task ARollbackAfterAWaitingDriverStillSaysTheDriverWaits()
    {
        InstalledOn0922Offering0923();
        var processes = PcCore();

        var outcome = await Coordinator(processes, new FakeElevated(processes: processes) { RestartRequired = true }, new FakeHealth(false, true))
            .RunAsync(27, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.RolledBack, outcome);
        Assert.Contains("Windows loads it when the PC restarts", _fixture.Paths.ReadStatus()!.Message, StringComparison.Ordinal);
    }

    /// <summary>A development copy holds the device too; it is not Nexus's to stop, so the driver update waits for the owner.</summary>
    [Fact]
    public async Task ADevelopmentPcCoreBlocksADriverUpdate()
    {
        InstalledOn0922Offering0923(driverOnly: true);
        var processes = PcCore();
        processes.Running.Add(new RunningPcCore(2, @"C:\src\PcCore.Host\bin\Release\PcCore.Host.exe"));
        var elevated = new FakeElevated(processes: processes);

        var outcome = await Coordinator(processes, elevated).RunAsync(27, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Failed, outcome);
        Assert.Equal(0, elevated.Calls);
        Assert.Empty(processes.Events);
        Assert.Contains("Another PC Core is running", _fixture.Paths.ReadStatus()!.Message, StringComparison.Ordinal);
    }

    // --- What PC Core shows afterwards ------------------------------------------------------------

    /// <summary>
    /// "Restart required" is recorded once, but whether it still is comes from Windows: the restart
    /// clears the device's pending state, and the update has then simply succeeded.
    /// </summary>
    [Fact]
    public void PcCoreShowsARestartAsRequiredOnlyWhileWindowsStillWaitsForIt()
    {
        InstalledOn0922Offering0923();
        _fixture.Paths.WriteStatus(new UpdateStatus
        {
            State = UpdateState.RestartRequired,
            Release = 27,
            Version = "1.5.0",
            Message = "Installed Nexus 1.5.0 (release 27). Restart Windows to finish: ...",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        var probe = new FakeComponentProbe(driver: new InstalledComponent(true, "0.9.2.3") { RestartPending = true });
        using var service = new NexusUpdateService(_fixture.Store, _fixture.Paths, probe, TestLog.For<NexusUpdateService>());

        Assert.Equal("restart_required", service.Check(refresh: true, clientVersionCode: null, includePcDetails: true).State);

        probe.DriverState = new InstalledComponent(true, "0.9.2.3");

        Assert.Equal("succeeded", service.Check(refresh: true, clientVersionCode: null, includePcDetails: true).State);
    }

    [Fact]
    public void ADriverWaitingForARestartIsNotOfferedAgain()
    {
        InstalledOn0922Offering0923(driverOnly: true);
        var load = _fixture.Store.LoadLatest();

        var plan = UpdatePlanner.Plan(load.Manifest!, load.ReleaseDirectory!, _fixture.Paths.InstallDirectory, null,
            new FakeComponentProbe(driver: new InstalledComponent(true, "0.9.2.3") { RestartPending = true }));

        Assert.False(plan.HidDriver.Updates);
        Assert.Equal("unchanged; Windows loads it when the PC restarts", plan.HidDriver.Reason);
    }
}

/// <summary>The two named events between the updater's halves, exercised within one process.</summary>
public sealed class HidDeviceHandoffTests
{
    [Fact]
    public async Task TheElevatedHalfWaitsUntilTheDeviceIsReleased()
    {
        using var unelevated = HidDeviceHandoff.Create();
        using var elevated = HidDeviceHandoff.Open(unelevated.Id);
        Assert.NotNull(elevated);

        var requested = unelevated.WhenRequestedAsync(TestContext.Current.CancellationToken);
        Assert.False(requested.IsCompleted);

        var granted = Task.Run(() => elevated!.Request(TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);

        await requested.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(granted.IsCompleted);

        unelevated.Release();

        Assert.True(await granted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void WithoutAnAnswerTheRequestTimesOut()
    {
        using var unelevated = HidDeviceHandoff.Create();
        using var elevated = HidDeviceHandoff.Open(unelevated.Id)!;

        Assert.False(elevated.Request(TimeSpan.FromMilliseconds(200)));
    }

    [Theory]
    [InlineData("not-a-handoff")]
    [InlineData(@"..\Global\Something")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void OnlyAnExistingHandoffCanBeOpened(string id)
    {
        Assert.Null(HidDeviceHandoff.Open(id));
    }
}
