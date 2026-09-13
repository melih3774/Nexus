using PcCore.Capabilities.Update;
using PcCore.Tests.TestSupport;
using PcCore.Update;
using PcCore.Update.Windows;
using PcCore.Windows.Update;
using Xunit;

namespace PcCore.Tests.Update;

/// <summary>
/// An update survives PC Core being closed and started again.
/// <para>
/// Every other update test asks the objects that did the update what they think happened. The case
/// that was reported is the next start: a new process, holding nothing but what is on disk, resolves
/// its locations from <c>%LOCALAPPDATA%</c> again and has to arrive at the updated state - not the one
/// from before the update, and not an offer to install what is already installed.
/// </para>
/// </summary>
public sealed class UpdateSurvivesRestartTests
{
    [Fact]
    public async Task AnUpdatedPcIsStillUpdatedAfterPcCoreRestarts()
    {
        using var fixture = new ReleaseFixture(localAppDataLayout: true);
        var localAppData = fixture.LocalAppData!;
        var token = TestContext.Current.CancellationToken;

        // --- The old state: release 15, installed by the updater, and release 16 published after it.
        var old = ReleaseFixture.BaseFiles();
        old["PcCore.Host.exe"] = "host v0";
        fixture.Install(old);
        fixture.Publish(15, "1.3.0", ReleaseFixture.BaseFiles());
        Assert.Equal(UpdateOutcome.Succeeded, await UpdateAsync(fixture, 15, token));

        var next = ReleaseFixture.BaseFiles();
        next["PcCore.Host.exe"] = "host v2";
        next["PcCore.Updater.exe"] = "updater";
        fixture.Publish(16, "1.3.1", next);

        var running = Service(localAppData);
        var before = running.Check(refresh: true, clientVersionCode: null, includePcDetails: true);

        Assert.True(before.PcUpdateAvailable);
        Assert.Equal(15, before.InstalledRelease);
        Assert.Equal(16, before.LatestRelease);

        // --- A successful update, and the state the running PC Core shows for it.
        Assert.Equal(UpdateOutcome.Succeeded, await UpdateAsync(fixture, 16, token));
        AssertUpdatedToRelease16(running.Check(refresh: true, clientVersionCode: null, includePcDetails: true));

        // --- Closed and started again. Nothing survives but the disk, and every location is resolved
        // from %LOCALAPPDATA% once more.
        running.Dispose();

        using var restarted = Service(localAppData);

        Assert.Null(restarted.Last);
        AssertUpdatedToRelease16(restarted.Check(refresh: false, clientVersionCode: null, includePcDetails: true));

        // Not offered the update it has just installed, and not back on the old release.
        var again = restarted.StartPcUpdate();
        Assert.False(again.Started);
        Assert.Contains("already has Nexus 1.3.1 (release 16)", again.Message, StringComparison.Ordinal);
        Assert.Equal("host v2", File.ReadAllText(UpdatePaths.ForLocalAppData(localAppData).InstalledExecutable));

        // And the state it read is the only one: no packaged app holds another copy of it.
        Assert.Null(AppDataRedirection.Find(localAppData, UpdatePaths.StateFolder, DirectoryIdentity.Of));
    }

    private static void AssertUpdatedToRelease16(UpdateCheckResult result)
    {
        Assert.True(result.ReleaseAvailable);
        Assert.False(result.PcUpdateAvailable);
        Assert.Equal(0, result.PcChangedFiles);
        Assert.Equal(16, result.LatestRelease);
        Assert.Equal(16, result.InstalledRelease);
        Assert.Equal("succeeded", result.State);
        Assert.Equal("Updated to Nexus 1.3.1 (release 16).", result.Message);
        Assert.Null(result.Problem);
    }

    /// <summary>A PC Core's update service, over the locations a process started now would resolve.</summary>
    private static NexusUpdateService Service(string localAppData) =>
        new(new ReleaseStore(ReleaseStore.RootIn(localAppData)), UpdatePaths.ForLocalAppData(localAppData), new FakeComponentProbe(), TestLog.For<NexusUpdateService>());

    private static Task<UpdateOutcome> UpdateAsync(ReleaseFixture fixture, int release, CancellationToken token) =>
        new UpdateCoordinator(
                fixture.Store,
                fixture.Paths,
                new FakeComponentProbe(),
                new FakeProcesses(fixture.Paths.InstalledExecutable),
                new FakeHealth(true),
                new FakeElevated(),
                _ => { })
            .RunAsync(release, token);
}
