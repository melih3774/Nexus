using PcCore.Update;
using Xunit;

namespace PcCore.Tests.Update;

/// <summary>
/// PC Assistant as a component of a Nexus release: installed the first time a release carries it,
/// replaced only when its version changes, asked - never forced - to close, reopened, and restored when
/// the new copy does not start, while PC Core keeps running and nothing outside the install folder moves.
/// </summary>
public sealed class AssistantUpdateTests : IDisposable
{
    private readonly ReleaseFixture _fixture = new();
    private readonly List<string> _log = [];

    public void Dispose() => _fixture.Dispose();

    private static Dictionary<string, string> AssistantV1() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PcAssistant.Desktop.exe"] = "assistant v1",
        ["PcAssistant.Core.dll"] = "core v1",
        ["PcAssistantSkill.md"] = "skill",
        ["PcTools/PcAssistant.PcTools.exe"] = "tools v1",
        ["Elevation/PcAssistant.Elevation.Host.exe"] = "helper payload v1",
    };

    private static Dictionary<string, string> AssistantV2()
    {
        var files = AssistantV1();
        files["PcAssistant.Desktop.exe"] = "assistant v2";
        return files;
    }

    private UpdateCoordinator Coordinator(
        FakeProcesses pcCore,
        FakeAssistant assistant,
        IElevatedInstaller? elevated = null,
        IElevatedComponentProbe? probe = null) =>
        new(_fixture.Store, _fixture.Paths, probe ?? new FakeComponentProbe(), pcCore, new FakeHealth(true), elevated ?? new FakeElevated(), _log.Add, assistant)
        {
            AssistantSettle = TimeSpan.Zero,
        };

    private FakeProcesses PcCore() => new(_fixture.Paths.InstalledExecutable);

    /// <summary>PC Core 1 installed, and every release below ships it unchanged, so PC Assistant is seen on its own.</summary>
    private void InstallPcCore() => _fixture.Install(ReleaseFixture.BaseFiles());

    private async Task InstallAssistantV1Async()
    {
        InstallPcCore();
        _fixture.Publish(20, "1.6.0", ReleaseFixture.BaseFiles(), assistant: ("0.10.1", AssistantV1()));

        Assert.Equal(UpdateOutcome.Succeeded, await Coordinator(PcCore(), new FakeAssistant()).RunAsync(20, TestContext.Current.CancellationToken));
    }

    private string AssistantFile(string path) => File.ReadAllText(Path.Combine(_fixture.Paths.AssistantDirectory, path.Replace('/', '\\')));

    // --- First install -------------------------------------------------------------------------------

    [Fact]
    public async Task AReleaseInstallsPcAssistantTheFirstTimeItCarriesIt()
    {
        InstallPcCore();
        _fixture.Publish(20, "1.6.0", ReleaseFixture.BaseFiles(), assistant: ("0.10.1", AssistantV1()));
        var state = ReleaseFixture.Snapshot(_fixture.StateDirectory);

        var (plan, _) = Coordinator(PcCore(), new FakeAssistant()).Check();

        Assert.Equal(ComponentAction.Update, plan!.AssistantComponent.Action);
        Assert.Equal("not installed on this PC", plan.AssistantComponent.Reason);
        Assert.All(plan.Assistant, c => Assert.Equal(FileChangeKind.Add, c.Kind));
        Assert.False(plan.PcCoreChanges);
        Assert.False(plan.UpToDate);

        var pcCore = PcCore();
        var assistant = new FakeAssistant();
        var outcome = await Coordinator(pcCore, assistant).RunAsync(20, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Succeeded, outcome);

        foreach (var (path, content) in AssistantV1())
        {
            Assert.Equal(content, AssistantFile(path));
        }

        // PC Core was neither stopped nor started, and PC Assistant was not opened: it was not open.
        Assert.Empty(pcCore.Events);
        Assert.Empty(assistant.Events);

        Assert.Equal("0.10.1", _fixture.Paths.ReadAssistantRecord()!.Version);
        Assert.Equal(state, ReleaseFixture.Snapshot(_fixture.StateDirectory));
        Assert.False(File.Exists(_fixture.Paths.AssistantJournalPath));
        Assert.Contains(_log, l => l.Contains("PcCore:Assistant:ServerExecutable", StringComparison.Ordinal));
    }

    // --- Later releases ------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnchangedVersionIsNotReplacedEvenWhenItsBytesDiffer()
    {
        await InstallAssistantV1Async();

        // The same source rebuilt: different build identity, same version.
        var rebuilt = AssistantV1();
        rebuilt["PcAssistant.Core.dll"] = "core v1, rebuilt";
        _fixture.Publish(21, "1.6.0", ReleaseFixture.BaseFiles(), assistant: ("0.10.1", rebuilt));

        var assistant = new FakeAssistant();
        assistant.RunFrom(_fixture.Paths.AssistantExecutable);

        var outcome = await Coordinator(PcCore(), assistant).RunAsync(21, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.UpToDate, outcome);
        Assert.Empty(assistant.Events);
        Assert.Equal("core v1", AssistantFile("PcAssistant.Core.dll"));
    }

    [Fact]
    public async Task ANewVersionCopiesOnlyWhatChangedAndRemovesOnlyWhatNexusInstalled()
    {
        await InstallAssistantV1Async();

        var ownerFile = Path.Combine(_fixture.Paths.AssistantDirectory, "notes.txt");
        File.WriteAllText(ownerFile, "the owner's");
        var skill = Path.Combine(_fixture.Paths.AssistantDirectory, "PcAssistantSkill.md");
        var skillWrittenAt = File.GetLastWriteTimeUtc(skill);

        var next = AssistantV2();
        next.Remove("PcTools/PcAssistant.PcTools.exe");
        next["PcAssistant.New.dll"] = "new in v2";
        _fixture.Publish(21, "1.6.0", ReleaseFixture.BaseFiles(), assistant: ("0.10.2", next));

        var (plan, _) = Coordinator(PcCore(), new FakeAssistant()).Check();

        Assert.Equal(
            [
                (FileChangeKind.Add, "PcAssistant.New.dll"),
                (FileChangeKind.Replace, "PcAssistant.Desktop.exe"),
                (FileChangeKind.Remove, "PcTools/PcAssistant.PcTools.exe"),
            ],
            plan!.Assistant.Select(c => (c.Kind, c.Path)).OrderBy(c => c.Kind).ToList());

        Assert.Equal(UpdateOutcome.Succeeded, await Coordinator(PcCore(), new FakeAssistant()).RunAsync(21, TestContext.Current.CancellationToken));

        Assert.Equal("assistant v2", AssistantFile("PcAssistant.Desktop.exe"));
        Assert.Equal("new in v2", AssistantFile("PcAssistant.New.dll"));
        Assert.False(File.Exists(Path.Combine(_fixture.Paths.AssistantDirectory, "PcTools", "PcAssistant.PcTools.exe")));
        Assert.Equal("the owner's", File.ReadAllText(ownerFile));
        Assert.Equal(skillWrittenAt, File.GetLastWriteTimeUtc(skill));
        Assert.Equal("0.10.2", _fixture.Paths.ReadAssistantRecord()!.Version);
    }

    // --- A running PC Assistant ----------------------------------------------------------------------

    [Fact]
    public async Task ARunningPcAssistantIsAskedToCloseThenReplacedAndReopened()
    {
        await InstallAssistantV1Async();
        _fixture.Publish(21, "1.6.0", ReleaseFixture.BaseFiles(), assistant: ("0.10.2", AssistantV2()));

        var pcCore = PcCore();
        var assistant = new FakeAssistant();
        var running = assistant.RunFrom(_fixture.Paths.AssistantExecutable);

        var outcome = await Coordinator(pcCore, assistant).RunAsync(21, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Succeeded, outcome);
        Assert.Equal([$"ask {running.ProcessId}", "start assistant v2"], assistant.Events);
        Assert.Empty(pcCore.Events);
        Assert.Single(assistant.Running);
    }

    [Fact]
    public async Task ABusyPcAssistantEndsTheUpdateBeforeAnythingChanges()
    {
        await InstallAssistantV1Async();

        // A release that would also replace PC Core and the driver - none of which may happen.
        var pcCoreNext = ReleaseFixture.BaseFiles();
        pcCoreNext["PcCore.Host.exe"] = "host v2";
        _fixture.Publish(21, "1.6.0", pcCoreNext,
            driver: ("0.9.2.3", new Dictionary<string, string> { ["NexusHid.inf"] = "inf" }),
            assistant: ("0.10.2", AssistantV2()));

        var before = ReleaseFixture.Snapshot(_fixture.Paths.InstallDirectory);
        var pcCore = PcCore();
        var elevated = new FakeElevated();
        var assistant = new FakeAssistant { AgreesToClose = false };
        assistant.RunFrom(_fixture.Paths.AssistantExecutable);

        var outcome = await Coordinator(pcCore, assistant, elevated, new FakeComponentProbe(driver: new InstalledComponent(true, "0.9.2.2")))
            .RunAsync(21, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Failed, outcome);
        Assert.Equal(before, ReleaseFixture.Snapshot(_fixture.Paths.InstallDirectory));
        Assert.Equal(0, elevated.Calls);
        Assert.Empty(pcCore.Events);
        Assert.Single(assistant.Running);
        Assert.DoesNotContain(assistant.Events, e => e.StartsWith("kill", StringComparison.Ordinal));
        Assert.Contains("did not close", _fixture.Paths.ReadStatus()!.Message, StringComparison.Ordinal);
        Assert.Equal("0.10.1", _fixture.Paths.ReadAssistantRecord()!.Version);
    }

    [Fact]
    public async Task ANewPcAssistantThatDoesNotStartIsRolledBackAndThePreviousReopened()
    {
        await InstallAssistantV1Async();
        _fixture.Publish(21, "1.6.0", ReleaseFixture.BaseFiles(), assistant: ("0.10.2", AssistantV2()));

        var before = ReleaseFixture.Snapshot(_fixture.Paths.AssistantDirectory);
        var assistant = new FakeAssistant();
        var running = assistant.RunFrom(_fixture.Paths.AssistantExecutable);

        // The new copy exits during start-up; the restored one stays up.
        assistant.StartsStayUp.Enqueue(false);
        assistant.StartsStayUp.Enqueue(true);

        var outcome = await Coordinator(PcCore(), assistant).RunAsync(21, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.RolledBack, outcome);
        Assert.Equal(before, ReleaseFixture.Snapshot(_fixture.Paths.AssistantDirectory));
        Assert.Equal([$"ask {running.ProcessId}", "start assistant v2", "start assistant v1"], assistant.Events);
        Assert.Equal("0.10.1", _fixture.Paths.ReadAssistantRecord()!.Version);
        Assert.False(File.Exists(_fixture.Paths.AssistantJournalPath));
        Assert.Contains("previous PC Assistant was restored", _fixture.Paths.ReadStatus()!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeclinedApprovalReopensThePcAssistantItClosed()
    {
        await InstallAssistantV1Async();
        _fixture.Publish(21, "1.6.0", ReleaseFixture.BaseFiles(),
            driver: ("0.9.2.3", new Dictionary<string, string> { ["NexusHid.inf"] = "inf" }),
            assistant: ("0.10.2", AssistantV2()));

        var assistant = new FakeAssistant();
        var running = assistant.RunFrom(_fixture.Paths.AssistantExecutable);

        var outcome = await Coordinator(PcCore(), assistant, new FakeElevated(ElevatedOutcome.Declined), new FakeComponentProbe(driver: new InstalledComponent(true, "0.9.2.2")))
            .RunAsync(21, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Failed, outcome);
        Assert.Equal([$"ask {running.ProcessId}", "start assistant v1"], assistant.Events);
        Assert.Equal("assistant v1", AssistantFile("PcAssistant.Desktop.exe"));
    }

    [Fact]
    public async Task ACopyRunningFromElsewhereIsNeitherAskedNorStarted()
    {
        await InstallAssistantV1Async();
        _fixture.Publish(21, "1.6.0", ReleaseFixture.BaseFiles(), assistant: ("0.10.2", AssistantV2()));

        var assistant = new FakeAssistant();
        assistant.RunFrom(Path.Combine(_fixture.Root, "dev", "bin", "Release", AssistantInstall.ExecutableName));

        var outcome = await Coordinator(PcCore(), assistant).RunAsync(21, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Succeeded, outcome);
        Assert.Empty(assistant.Events);
        Assert.Single(assistant.Running);
        Assert.Equal("assistant v2", AssistantFile("PcAssistant.Desktop.exe"));
    }

    [Fact]
    public async Task AnInterruptedPcAssistantSwapIsUndoneBeforeTheNextUpdate()
    {
        await InstallAssistantV1Async();
        _fixture.Publish(21, "1.6.0", ReleaseFixture.BaseFiles(), assistant: ("0.10.2", AssistantV2()));

        // The updater dies after replacing the files and before committing them.
        var (plan, _) = Coordinator(PcCore(), new FakeAssistant()).Check();
        var staged = new ReleaseStager().Stage(plan!, _fixture.Paths.StagingRoot, TestContext.Current.CancellationToken);
        new PcCoreFileSwap(_fixture.Paths.AssistantJournalPath).Apply(
            21, plan!.Assistant, Path.Combine(staged, ReleaseStager.AssistantFolder), _fixture.Paths.AssistantDirectory, _fixture.Paths.AssistantBackupDirectory);

        Assert.True(File.Exists(_fixture.Paths.AssistantJournalPath));

        var outcome = await Coordinator(PcCore(), new FakeAssistant()).RunAsync(21, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Succeeded, outcome);
        Assert.Contains(_log, l => l.Contains("did not finish", StringComparison.Ordinal));
        Assert.Equal("assistant v2", AssistantFile("PcAssistant.Desktop.exe"));
        Assert.False(File.Exists(_fixture.Paths.AssistantJournalPath));
        Assert.Equal("0.10.2", _fixture.Paths.ReadAssistantRecord()!.Version);
    }

    // --- The manifest and the stop request -----------------------------------------------------------

    [Fact]
    public void AReleaseCarryingPcAssistantMustShipItsExecutable()
    {
        var files = AssistantV1();
        files.Remove(AssistantInstall.ExecutableName);
        var manifest = _fixture.Publish(20, "1.6.0", ReleaseFixture.BaseFiles(), assistant: ("0.10.1", files));

        Assert.Contains(ManifestValidator.Problems(manifest), p => p.Contains(AssistantInstall.ExecutableName, StringComparison.Ordinal));
    }

    /// <summary>
    /// PC Assistant derives the same name (UpdateStopListener.EventNameFor) and its suite pins this same
    /// literal, so the two can only drift by failing one of the two tests.
    /// </summary>
    [Fact]
    public void TheStopRequestIsNamedTheWayPcAssistantListensForIt()
    {
        Assert.Equal(
            @"Local\PcAssistant.UpdateStop.BA6C187C6D18F441",
            AssistantInstall.StopEventName(@"C:\Users\Example\AppData\Local\Programs\Nexus\Assistant\PcAssistant.Desktop.exe"));
    }
}
