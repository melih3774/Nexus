using PcCore.Update;
using Xunit;

namespace PcCore.Tests.Update;

/// <summary>What an update decides to touch - and, more importantly, what it decides to leave alone.</summary>
public sealed class UpdatePlanningTests : IDisposable
{
    private readonly ReleaseFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private UpdatePlan PlanLatest(IElevatedComponentProbe? probe = null)
    {
        var load = _fixture.Store.LoadLatest();
        Assert.True(load.Found, load.Problem);
        return UpdatePlanner.Plan(load.Manifest!, load.ReleaseDirectory!, _fixture.Paths.InstallDirectory, _fixture.Paths.ReadInstalledRecord(), probe ?? new FakeComponentProbe());
    }

    [Fact]
    public void AnInstallThatAlreadyMatchesTheReleaseIsNotAnUpdate()
    {
        var files = ReleaseFixture.BaseFiles();
        _fixture.Publish(15, "1.3.0", files);
        _fixture.Install(files);

        var plan = PlanLatest();

        Assert.True(plan.UpToDate);
        Assert.Empty(plan.PcCore);
    }

    [Fact]
    public void OnlyTheFilesThatDifferAreReplacedOrAdded()
    {
        _fixture.Install(ReleaseFixture.BaseFiles());

        var next = ReleaseFixture.BaseFiles();
        next["PcCore.Host.dll"] = "host library v2";
        next["PcCore.Updater.exe"] = "updater v1";
        _fixture.Publish(16, "1.3.1", next);

        var plan = PlanLatest();

        Assert.Equal(
            [("PcCore.Host.dll", FileChangeKind.Replace), ("PcCore.Updater.exe", FileChangeKind.Add)],
            plan.PcCore.Select(c => (c.Path, c.Kind)).Order());
    }

    [Fact]
    public void AnElevatedComponentWithTheInstalledVersionIsSkippedWhateverItsBytes()
    {
        // A rebuild of unchanged SecureCapture produces different bytes. That must not cost a UAC prompt.
        var files = ReleaseFixture.BaseFiles();
        _fixture.Install(files);
        _fixture.Publish(15, "1.3.0", files,
            secureCapture: ("1.0.0", new Dictionary<string, string> { ["PcCore.SecureCapture.exe"] = "rebuilt" }),
            driver: ("0.9.2.0", new Dictionary<string, string> { ["NexusHid.inf"] = "inf" }));

        var plan = PlanLatest(new FakeComponentProbe(new InstalledComponent(true, "1.0.0"), new InstalledComponent(true, "0.9.2.0")));

        Assert.Equal(ComponentAction.Skip, plan.SecureCapture.Action);
        Assert.Equal(ComponentAction.Skip, plan.HidDriver.Action);
        Assert.False(plan.NeedsElevation);
        Assert.True(plan.UpToDate);
    }

    [Fact]
    public void ANewElevatedVersionNeedsApproval()
    {
        var files = ReleaseFixture.BaseFiles();
        _fixture.Install(files);
        _fixture.Publish(15, "1.3.0", files,
            secureCapture: ("1.1.0", new Dictionary<string, string> { ["PcCore.SecureCapture.exe"] = "new" }));

        var plan = PlanLatest(new FakeComponentProbe(new InstalledComponent(true, "1.0.0")));

        Assert.True(plan.SecureCapture.Updates);
        Assert.True(plan.NeedsElevation);
        Assert.Empty(plan.PcCore);
    }

    [Fact]
    public void AnUpdateNeverInstallsAPrivilegedComponentThatIsNotAlreadyThere()
    {
        var files = ReleaseFixture.BaseFiles();
        _fixture.Install(files);
        _fixture.Publish(15, "1.3.0", files,
            secureCapture: ("1.0.0", new Dictionary<string, string> { ["PcCore.SecureCapture.exe"] = "x" }),
            driver: ("0.9.2.0", new Dictionary<string, string> { ["NexusHid.inf"] = "inf" }));

        var plan = PlanLatest(new FakeComponentProbe());

        Assert.False(plan.NeedsElevation);
        Assert.Equal("not installed on this PC", plan.SecureCapture.Reason);
    }

    [Fact]
    public void OnlyFilesTheUpdaterItselfInstalledAreEverRemoved()
    {
        var files = ReleaseFixture.BaseFiles();
        files["Obsolete.dll"] = "old";
        _fixture.Install(files);

        // Installed by the updater last time, so it knows Obsolete.dll was its own.
        UpdateJson.WriteAtomic(_fixture.Paths.InstalledRecordPath, new InstalledRecord
        {
            Release = 15,
            Version = "1.3.0",
            PcCoreFiles = files.Select(f => new ReleaseFile { Path = f.Key, Sha256 = PcCore.Update.FileHash.Sha256OfBytes(System.Text.Encoding.UTF8.GetBytes(f.Value)), Size = f.Value.Length }).ToList(),
        });

        // And something the owner put there, which no manifest ever named.
        ReleaseFixture.Write(Path.Combine(_fixture.Paths.InstallDirectory, "my-notes.txt"), "mine");

        var next = ReleaseFixture.BaseFiles();
        _fixture.Publish(16, "1.3.1", next);

        var plan = PlanLatest();

        Assert.Equal([("Obsolete.dll", FileChangeKind.Remove)], plan.PcCore.Select(c => (c.Path, c.Kind)));
    }

    [Fact]
    public void ASettingsFileEditedSinceTheLastUpdateIsKept()
    {
        var files = ReleaseFixture.BaseFiles();
        _fixture.Install(files);

        UpdateJson.WriteAtomic(_fixture.Paths.InstalledRecordPath, new InstalledRecord
        {
            Release = 15,
            Version = "1.3.0",
            PcCoreFiles = files.Select(f => new ReleaseFile { Path = f.Key, Sha256 = PcCore.Update.FileHash.Sha256OfBytes(System.Text.Encoding.UTF8.GetBytes(f.Value)), Size = f.Value.Length }).ToList(),
        });

        // The owner changes a setting after that install...
        ReleaseFixture.Write(Path.Combine(_fixture.Paths.InstallDirectory, "appsettings.json"), "{ \"PcCore\": { \"mine\": true } }");

        // ...and the next release ships a different default.
        var next = ReleaseFixture.BaseFiles();
        next["appsettings.json"] = "{ \"PcCore\": { \"new\": true } }";
        _fixture.Publish(16, "1.3.1", next);

        var plan = PlanLatest();

        Assert.Empty(plan.PcCore);
        Assert.Equal(["appsettings.json"], plan.PreservedFiles);
    }
}

/// <summary>The store refuses anything that is not exactly what the publish wrote.</summary>
public sealed class ReleaseStoreTests : IDisposable
{
    private readonly ReleaseFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void NoPublishedReleaseIsSaidPlainly()
    {
        var load = _fixture.Store.LoadLatest();

        Assert.False(load.Found);
        Assert.Contains("No Nexus release", load.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestChangedAfterPublishingIsRefused()
    {
        _fixture.Publish(15, "1.3.0", ReleaseFixture.BaseFiles());

        var manifest = Path.Combine(_fixture.Store.Root, "15", "manifest.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("1.3.0", "9.9.9", StringComparison.Ordinal));

        var load = _fixture.Store.LoadLatest();

        Assert.False(load.Found);
        Assert.Contains("does not match latest.json", load.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../PcCore.Host.exe")]
    [InlineData("..\\..\\Windows\\evil.dll")]
    [InlineData("C:\\Windows\\evil.dll")]
    [InlineData("wwwroot/../../evil.dll")]
    [InlineData("file.dll:stream")]
    [InlineData("")]
    public void AManifestPathThatCouldEscapeIsInvalid(string path)
    {
        Assert.False(ManifestValidator.IsSafeRelativePath(path));
        Assert.Null(ReleaseStore.Contained(_fixture.Root, path));
    }

    [Theory]
    [InlineData("PcCore.Host.exe")]
    [InlineData("wwwroot/index.html")]
    [InlineData("runtimes/win/lib/net10.0/System.Drawing.Common.dll")]
    public void OrdinaryPathsAreAccepted(string path) => Assert.True(ManifestValidator.IsSafeRelativePath(path));
}
