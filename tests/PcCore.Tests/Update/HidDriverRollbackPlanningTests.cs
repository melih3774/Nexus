using PcCore.Update;
using Xunit;

namespace PcCore.Tests.Update;

/// <summary>
/// A HID driver update is planned only with a way back that does not depend on the Driver Store: the
/// release that shipped the installed driver, staged and hashed beside the new one.
/// </summary>
public sealed class HidDriverRollbackPlanningTests : IDisposable
{
    private readonly ReleaseFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static (string, IReadOnlyDictionary<string, string>) Driver(string version, bool withDll = true)
    {
        var files = new Dictionary<string, string>
        {
            ["NexusHid.inf"] = $"inf {version}",
            ["nexushid.cat"] = $"cat {version}",
        };

        if (withDll)
        {
            files["NexusHidDriver.dll"] = $"dll {version}";
        }

        return (version, files);
    }

    private UpdatePlan PlanLatest(string installedDriver)
    {
        var load = _fixture.Store.LoadLatest();
        Assert.True(load.Found, load.Problem);
        _fixture.Install(ReleaseFixture.BaseFiles());

        return UpdatePlanner.Plan(
            load.Manifest!,
            load.ReleaseDirectory!,
            _fixture.Paths.InstallDirectory,
            _fixture.Paths.ReadInstalledRecord(),
            new FakeComponentProbe(driver: new InstalledComponent(true, installedDriver)));
    }

    [Fact]
    public void ADriverUpdateRollsBackToTheNewestReleaseCarryingTheInstalledDriver()
    {
        _fixture.Publish(24, "1.5.0", ReleaseFixture.BaseFiles(), driver: Driver("0.9.2.0"));
        _fixture.Publish(25, "1.5.0", ReleaseFixture.BaseFiles(), driver: Driver("0.9.2.2"));
        _fixture.Publish(26, "1.5.0", ReleaseFixture.BaseFiles(), driver: Driver("0.9.2.2"));
        _fixture.Publish(27, "1.5.1", ReleaseFixture.BaseFiles(), driver: Driver("0.9.2.3"));

        var plan = PlanLatest(installedDriver: "0.9.2.2");

        Assert.True(plan.HidDriver.Updates);
        Assert.Equal(26, plan.HidDriverRollback!.Release);
        Assert.Equal("0.9.2.2", plan.HidDriverRollback.Component.Version);
    }

    [Fact]
    public void AMachineStillOn0920RollsBackTo0920()
    {
        _fixture.Publish(24, "1.5.0", ReleaseFixture.BaseFiles(), driver: Driver("0.9.2.0"));
        _fixture.Publish(26, "1.5.0", ReleaseFixture.BaseFiles(), driver: Driver("0.9.2.2"));
        _fixture.Publish(27, "1.5.1", ReleaseFixture.BaseFiles(), driver: Driver("0.9.2.3"));

        var plan = PlanLatest(installedDriver: "0.9.2.0");

        Assert.Equal(24, plan.HidDriverRollback!.Release);
    }

    [Fact]
    public void WithoutAReleaseCarryingTheInstalledDriverTheDriverIsNotUpdated()
    {
        _fixture.Publish(24, "1.5.0", ReleaseFixture.BaseFiles(), driver: Driver("0.9.2.0"));
        _fixture.Publish(27, "1.5.1", ReleaseFixture.BaseFiles(), driver: Driver("0.9.2.3"));

        var plan = PlanLatest(installedDriver: "0.9.2.2");

        Assert.Equal(ComponentAction.Skip, plan.HidDriver.Action);
        Assert.Contains("no release in the store carries the installed driver 0.9.2.2", plan.HidDriver.Reason, StringComparison.Ordinal);
        Assert.Null(plan.HidDriverRollback);
        Assert.False(plan.NeedsElevation);
    }

    [Fact]
    public void AReleaseWithoutTheDriverDllIsNoRollback()
    {
        _fixture.Publish(26, "1.5.0", ReleaseFixture.BaseFiles(), driver: Driver("0.9.2.2", withDll: false));
        _fixture.Publish(27, "1.5.1", ReleaseFixture.BaseFiles(), driver: Driver("0.9.2.3"));

        Assert.Equal(ComponentAction.Skip, PlanLatest(installedDriver: "0.9.2.2").HidDriver.Action);
    }

    [Fact]
    public void TheTargetReleaseIsNeverItsOwnRollbackAndAnUnreadableInstallHasNone()
    {
        _fixture.Publish(26, "1.5.0", ReleaseFixture.BaseFiles(), driver: Driver("0.9.2.2"));

        Assert.Null(UpdatePlanner.FindHidDriverRollback(_fixture.Store.Root, targetRelease: 26, installedVersion: "0.9.2.2"));
        Assert.Null(UpdatePlanner.FindHidDriverRollback(_fixture.Store.Root, targetRelease: 27, installedVersion: null));
        Assert.NotNull(UpdatePlanner.FindHidDriverRollback(_fixture.Store.Root, targetRelease: 27, installedVersion: "0.9.2.2"));
    }

    [Fact]
    public void TheRollbackDriverIsStagedAndHashedBesideTheNewOne()
    {
        _fixture.Publish(26, "1.5.0", ReleaseFixture.BaseFiles(), driver: Driver("0.9.2.2"));
        _fixture.Publish(27, "1.5.1", ReleaseFixture.BaseFiles(), driver: Driver("0.9.2.3"));

        var staged = new ReleaseStager().Stage(PlanLatest(installedDriver: "0.9.2.2"), _fixture.Paths.StagingRoot, TestContext.Current.CancellationToken);

        Assert.Equal("dll 0.9.2.3", File.ReadAllText(Path.Combine(staged, ReleaseStager.HidDriverFolder, "NexusHidDriver.dll")));
        Assert.Equal("dll 0.9.2.2", File.ReadAllText(Path.Combine(staged, ReleaseStager.HidDriverRollbackFolder, "NexusHidDriver.dll")));

        var rollback = UpdateJson.Read<ReleaseManifest>(Path.Combine(staged, ReleaseStager.HidDriverRollbackManifestFileName));
        Assert.Equal(26, rollback!.Release);
        Assert.Equal("0.9.2.2", rollback.Components.HidDriver!.Version);
    }

    [Fact]
    public void ATamperedRollbackDriverStopsTheUpdateBeforeItStarts()
    {
        _fixture.Publish(26, "1.5.0", ReleaseFixture.BaseFiles(), driver: Driver("0.9.2.2"));
        _fixture.Publish(27, "1.5.1", ReleaseFixture.BaseFiles(), driver: Driver("0.9.2.3"));
        var plan = PlanLatest(installedDriver: "0.9.2.2");

        File.WriteAllText(_fixture.ReleaseFile(26, "driver", "NexusHidDriver.dll"), "not what the manifest hashed");

        Assert.Throws<UpdateIntegrityException>(() => new ReleaseStager().Stage(plan, _fixture.Paths.StagingRoot, TestContext.Current.CancellationToken));
    }
}
