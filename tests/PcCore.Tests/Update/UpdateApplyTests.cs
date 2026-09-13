using PcCore.Update;
using Xunit;

namespace PcCore.Tests.Update;

/// <summary>
/// The update itself, end to end against real files: staging and its interruptions, verification,
/// the swap, the health check, and the rollback when the new version does not come up.
/// </summary>
public sealed class UpdateApplyTests : IDisposable
{
    private readonly ReleaseFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private UpdateCoordinator Coordinator(FakeProcesses processes, INexusHealthProbe health, IElevatedInstaller? elevated = null, IElevatedComponentProbe? probe = null) =>
        new(_fixture.Store, _fixture.Paths, probe ?? new FakeComponentProbe(), processes, health, elevated ?? new FakeElevated(), _ => { });

    /// <summary>Version 1 installed and running; version 2 published with one file changed and one added.</summary>
    private (Dictionary<string, string> Installed, Dictionary<string, string> Next) InstallV1PublishV2()
    {
        var installed = ReleaseFixture.BaseFiles();
        _fixture.Install(installed);

        var next = ReleaseFixture.BaseFiles();
        next["PcCore.Host.exe"] = "host v2";
        next["PcCore.Updater.exe"] = "updater";
        _fixture.Publish(16, "1.3.1", next);

        return (installed, next);
    }

    // --- Staging -------------------------------------------------------------------------------------

    [Fact]
    public void AnInterruptedDownloadIsResumedAndNeverInstalledHalfWritten()
    {
        InstallV1PublishV2();
        var plan = Plan();
        var stager = new ReleaseStager();

        var staged = stager.Stage(plan, _fixture.Paths.StagingRoot, TestContext.Current.CancellationToken);
        var stagedHost = Path.Combine(staged, "pccore", "PcCore.Host.exe");

        // The download is cut off: the staged file is truncated and a .part is left behind.
        File.WriteAllText(stagedHost, "host");
        File.WriteAllText(stagedHost + ".part", "hos");

        stager.Stage(plan, _fixture.Paths.StagingRoot, TestContext.Current.CancellationToken);

        Assert.Equal("host v2", File.ReadAllText(stagedHost));
        Assert.False(File.Exists(stagedHost + ".part"));
    }

    [Fact]
    public void AFileThatDoesNotMatchItsHashIsNeverStaged()
    {
        InstallV1PublishV2();

        // Changed in the store after the manifest recorded its hash.
        File.WriteAllText(_fixture.ReleaseFile(16, "pccore", "PcCore.Host.exe"), "host v2 tampered");

        var ex = Assert.Throws<UpdateIntegrityException>(() => new ReleaseStager().Stage(Plan(), _fixture.Paths.StagingRoot, TestContext.Current.CancellationToken));

        Assert.Contains("does not match its SHA-256", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(ReleaseStager.DirectoryFor(_fixture.Paths.StagingRoot, 16), "pccore", "PcCore.Host.exe")));
    }

    // --- The whole update ----------------------------------------------------------------------------

    [Fact]
    public async Task ASuccessfulUpdateReplacesOnlyWhatChangedAndRestartsNexus()
    {
        var (_, next) = InstallV1PublishV2();
        var untouched = Path.Combine(_fixture.Paths.InstallDirectory, "PcCore.Capabilities.dll");
        var untouchedWrittenAt = File.GetLastWriteTimeUtc(untouched);

        var processes = new FakeProcesses(_fixture.Paths.InstalledExecutable);
        var health = new FakeHealth(true);

        var outcome = await Coordinator(processes, health).RunAsync(16, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Succeeded, outcome);
        Assert.Equal(["stop 1", "start host v2"], processes.Events);
        Assert.Equal(["1.3.1"], health.ExpectedVersions);

        foreach (var (path, content) in next)
        {
            Assert.Equal(content, File.ReadAllText(Path.Combine(_fixture.Paths.InstallDirectory, path)));
        }

        // Unchanged: not rewritten, not even with identical bytes.
        Assert.Equal(untouchedWrittenAt, File.GetLastWriteTimeUtc(untouched));

        Assert.Equal(16, _fixture.Paths.ReadInstalledRecord()!.Release);
        Assert.Equal(UpdateState.Succeeded, _fixture.Paths.ReadStatus()!.State);
        Assert.False(File.Exists(_fixture.Paths.JournalPath));
    }

    [Fact]
    public async Task ANewVersionThatDoesNotStartIsRolledBackToTheExactPreviousFiles()
    {
        InstallV1PublishV2();
        var before = ReleaseFixture.Snapshot(_fixture.Paths.InstallDirectory);

        var processes = new FakeProcesses(_fixture.Paths.InstalledExecutable);

        // The new version fails its health check; the restored one passes.
        var health = new FakeHealth(false, true);

        var outcome = await Coordinator(processes, health).RunAsync(16, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.RolledBack, outcome);
        Assert.Equal(before, ReleaseFixture.Snapshot(_fixture.Paths.InstallDirectory));

        // Stopped the old one, started the new one, ended it when it failed, started the old one again.
        Assert.Equal("stop 1", processes.Events[0]);
        Assert.Equal("start host v2", processes.Events[1]);
        Assert.StartsWith("kill", processes.Events[2], StringComparison.Ordinal);
        Assert.Equal("start host v1", processes.Events[3]);

        // The restored copy is checked too, against whatever version it is: these test files carry none.
        Assert.Equal(["1.3.1", null], health.ExpectedVersions);

        var status = _fixture.Paths.ReadStatus()!;
        Assert.Equal(UpdateState.RolledBack, status.State);
        Assert.Contains("previous version was restored", status.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(_fixture.Paths.JournalPath));
        Assert.Null(_fixture.Paths.ReadInstalledRecord());
    }

    [Fact]
    public async Task ABadHashFailsTheUpdateBeforeNexusIsEverStopped()
    {
        InstallV1PublishV2();
        File.WriteAllText(_fixture.ReleaseFile(16, "pccore", "PcCore.Host.exe"), "not what the manifest says");
        var before = ReleaseFixture.Snapshot(_fixture.Paths.InstallDirectory);

        var processes = new FakeProcesses(_fixture.Paths.InstalledExecutable);

        var outcome = await Coordinator(processes, new FakeHealth()).RunAsync(16, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Failed, outcome);
        Assert.Empty(processes.Events);
        Assert.Equal(before, ReleaseFixture.Snapshot(_fixture.Paths.InstallDirectory));
        Assert.Contains("could not be prepared", _fixture.Paths.ReadStatus()!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecliningAdministratorApprovalChangesNothingAndKeepsNexusRunning()
    {
        var installed = ReleaseFixture.BaseFiles();
        _fixture.Install(installed);

        var next = ReleaseFixture.BaseFiles();
        next["PcCore.Host.exe"] = "host v2";
        _fixture.Publish(16, "1.3.1", next, secureCapture: ("1.1.0", new Dictionary<string, string> { ["PcCore.SecureCapture.exe"] = "sc" }));

        var before = ReleaseFixture.Snapshot(_fixture.Paths.InstallDirectory);
        var processes = new FakeProcesses(_fixture.Paths.InstalledExecutable);
        var elevated = new FakeElevated(ElevatedOutcome.Declined);

        var outcome = await Coordinator(processes, new FakeHealth(), elevated, new FakeComponentProbe(new InstalledComponent(true, "1.0.0")))
            .RunAsync(16, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Failed, outcome);
        Assert.Equal(1, elevated.Calls);
        Assert.Empty(processes.Events);
        Assert.Equal(before, ReleaseFixture.Snapshot(_fixture.Paths.InstallDirectory));
        Assert.Contains("declined", _fixture.Paths.ReadStatus()!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUserSpaceUpdateNeverAsksForAdministratorApproval()
    {
        InstallV1PublishV2();
        var elevated = new FakeElevated();

        var outcome = await Coordinator(new FakeProcesses(_fixture.Paths.InstalledExecutable), new FakeHealth(true), elevated)
            .RunAsync(16, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Succeeded, outcome);
        Assert.Equal(0, elevated.Calls);
    }

    [Fact]
    public async Task PairingSettingsLogsAndEvidenceSurviveAnUpdateAndARollback()
    {
        // Everything that lives beside PC Core rather than inside it.
        var state = _fixture.StateDirectory;
        ReleaseFixture.Write(Path.Combine(state, "primary-phone.json"), "{ \"device_id\": \"phone\", \"secret\": \"dpapi\" }");
        ReleaseFixture.Write(Path.Combine(state, "instance.json"), "{ \"pc_instance_id\": \"pc\" }");
        ReleaseFixture.Write(Path.Combine(state, "security-state.json"), "{ \"state\": \"ARMED\" }");
        ReleaseFixture.Write(Path.Combine(state, "media-quality.json"), "{ \"bitrate\": 1 }");
        ReleaseFixture.Write(Path.Combine(state, "pccore.log"), "log");
        ReleaseFixture.Write(Path.Combine(state, "Security", "incident-1", "camera.mp4"), "evidence");

        // And the owner's local overrides inside the install directory, which no release names.
        ReleaseFixture.Write(Path.Combine(_fixture.Paths.InstallDirectory, "appsettings.Local.json"), "{ \"mine\": 1 }");

        InstallV1PublishV2();
        var stateBefore = ReleaseFixture.Snapshot(state);

        var updated = await Coordinator(new FakeProcesses(_fixture.Paths.InstalledExecutable), new FakeHealth(true))
            .RunAsync(16, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Succeeded, updated);
        Assert.Equal(stateBefore, ReleaseFixture.Snapshot(state));
        Assert.Equal("{ \"mine\": 1 }", File.ReadAllText(Path.Combine(_fixture.Paths.InstallDirectory, "appsettings.Local.json")));

        // A later release that fails still leaves all of it alone.
        var third = ReleaseFixture.BaseFiles();
        third["PcCore.Host.exe"] = "host v3";
        _fixture.Publish(17, "1.3.2", third);

        var rolledBack = await Coordinator(new FakeProcesses(_fixture.Paths.InstalledExecutable), new FakeHealth(false, true))
            .RunAsync(17, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.RolledBack, rolledBack);
        Assert.Equal(stateBefore, ReleaseFixture.Snapshot(state));
        Assert.Equal("host v2", File.ReadAllText(_fixture.Paths.InstalledExecutable));
        Assert.True(File.Exists(Path.Combine(_fixture.Paths.InstallDirectory, "appsettings.Local.json")));
    }

    [Fact]
    public async Task AnUpdateInterruptedHalfWayIsUndoneBeforeTheNextOneStarts()
    {
        var (installed, _) = InstallV1PublishV2();
        var before = ReleaseFixture.Snapshot(_fixture.Paths.InstallDirectory);

        // The state a crash leaves: the journal says PcCore.Host.exe was moved aside and replaced, and
        // the replacement is on disk.
        var backup = _fixture.Paths.BackupDirectory;
        Directory.CreateDirectory(backup);
        File.Move(_fixture.Paths.InstalledExecutable, Path.Combine(backup, "PcCore.Host.exe"));
        File.WriteAllText(_fixture.Paths.InstalledExecutable, "half an update");

        UpdateJson.WriteAtomic(_fixture.Paths.JournalPath, new SwapJournal
        {
            Release = 16,
            InstallDirectory = _fixture.Paths.InstallDirectory,
            BackupDirectory = backup,
            Entries = [new SwapJournalEntry { Path = "PcCore.Host.exe", Kind = FileChangeKind.Replace, BackedUp = true }],
        });

        // Nothing is published that differs now, so the run only has the recovery to do.
        _fixture.Publish(17, "1.3.0", installed);

        var outcome = await Coordinator(new FakeProcesses(_fixture.Paths.InstalledExecutable), new FakeHealth(true))
            .RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.UpToDate, outcome);
        Assert.Equal(before, ReleaseFixture.Snapshot(_fixture.Paths.InstallDirectory));
        Assert.False(File.Exists(_fixture.Paths.JournalPath));
    }

    [Fact]
    public async Task ADevelopmentCopyOfPcCoreRunningElsewhereBlocksTheUpdate()
    {
        InstallV1PublishV2();
        var processes = new FakeProcesses(_fixture.Paths.InstalledExecutable);
        processes.Running.Add(new RunningPcCore(2, @"C:\src\PcCore.Host\bin\Release\PcCore.Host.exe"));

        var outcome = await Coordinator(processes, new FakeHealth(true)).RunAsync(16, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Failed, outcome);
        Assert.Empty(processes.Events);
        Assert.Contains("Another PC Core is running", _fixture.Paths.ReadStatus()!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingNewIsNotAnUpdate()
    {
        var files = ReleaseFixture.BaseFiles();
        _fixture.Install(files);
        _fixture.Publish(15, "1.3.0", files);

        var processes = new FakeProcesses(_fixture.Paths.InstalledExecutable);
        var outcome = await Coordinator(processes, new FakeHealth()).RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.UpToDate, outcome);
        Assert.Empty(processes.Events);
    }

    private UpdatePlan Plan()
    {
        var load = _fixture.Store.LoadLatest();
        return UpdatePlanner.Plan(load.Manifest!, load.ReleaseDirectory!, _fixture.Paths.InstallDirectory, null, new FakeComponentProbe());
    }
}
