using System.Xml.Linq;

using PcCore.Update;
using PcCore.Update.Windows;

using Xunit;

namespace PcCore.Tests.Update;

/// <summary>
/// The HID driver update against a stand-in for Windows that behaves the way Windows was observed to
/// on this machine (SetupAPI logs, 2026-09-09 to 2026-09-26): a configurable package's UMDF
/// registration is reference-counted, a non-configurable one's is not, and deleting the last counted
/// package deletes the registration whoever else wrote it.
/// </summary>
public sealed class HidDriverLifecycleTests
{
    // Where each release's hashed payload would be staged. Distinct so the stand-in knows which one it was handed.
    private static readonly DriverPayload V0920 = new("0.9.2.0", @"C:\staged\r24\NexusHid.inf", Sha('0'));
    private static readonly DriverPayload V0922 = new("0.9.2.2", @"C:\staged\r26\NexusHid.inf", Sha('2'));
    private static readonly DriverPayload V0923 = new("0.9.2.3", @"C:\staged\r27\NexusHid.inf", Sha('3'));

    private static string Sha(char c) => new(c, 64);

    // ---------------------------------------------------------------- the update

    [Fact]
    public void AnUpgradeLeavesOnlyTheNewPackageAndAHealthyDevice()
    {
        var windows = new FakeWindows();
        windows.Bind(windows.Stage("oem119.inf", V0922));

        var outcome = Lifecycle(windows).Update(V0923, V0922);

        Assert.Equal(HidDriverOutcomeKind.Updated, outcome.Kind);
        Assert.Equal("0.9.2.3", Assert.Single(windows.Packages).Payload.Version);
        Assert.Empty(outcome.StalePackagesLeft);
        Assert.Empty(windows.Health(V0923));
    }

    /// <summary>
    /// Nothing is deleted until the new driver is bound and healthy: a package that never worked is
    /// rolled back from, not cleaned up after.
    /// </summary>
    [Fact]
    public void StalePackagesAreRemovedOnlyAfterTheNewDriverIsHealthy()
    {
        var windows = new FakeWindows();
        windows.Bind(windows.Stage("oem119.inf", V0922));

        Lifecycle(windows).Update(V0923, V0922);

        var install = windows.Timeline.IndexOf("install 0.9.2.3");
        var healthy = windows.Timeline.FindIndex(e => e.StartsWith("log: Check after install", StringComparison.Ordinal) && e.EndsWith("healthy.", StringComparison.Ordinal));
        var delete = windows.Timeline.IndexOf("delete oem119.inf");

        Assert.True(install >= 0 && healthy > install && delete > healthy, string.Join(" | ", windows.Timeline));
    }

    /// <summary>
    /// The case that broke this PC, forced: the only other package is the last one the Driver Store
    /// counted, and the new one is not counted (as 0.9.2.1 and 0.9.2.2 were not), so deleting the old
    /// one deletes the registration. The check after cleanup sees it, and reapplying the active
    /// package - Windows' own install - writes it back.
    /// </summary>
    [Fact]
    public void ARegistrationDeletedByCleanupIsDetectedAndRepairedByReapplying()
    {
        var windows = new FakeWindows();
        windows.Configurable["0.9.2.3"] = false;
        windows.Bind(windows.Stage("oem120.inf", V0920));

        var outcome = Lifecycle(windows).Update(V0923, V0920);

        Assert.Equal(HidDriverOutcomeKind.Updated, outcome.Kind);
        Assert.Equal(["install 0.9.2.3", "delete oem120.inf", "registration wiped", "reapply 0.9.2.3"], windows.Events);
        Assert.Contains(windows.Timeline, e => e.StartsWith("log: The active driver is not healthy after the stale packages were removed", StringComparison.Ordinal));
        Assert.Empty(windows.Health(V0923));
    }

    /// <summary>A declarative 0.9.2.3 holds its own reference, so removing 0.9.2.0 leaves the registration alone.</summary>
    [Fact]
    public void ADeclarativePackageKeepsItsRegistrationThroughCleanup()
    {
        var windows = new FakeWindows();
        windows.Bind(windows.Stage("oem120.inf", V0920));

        var outcome = Lifecycle(windows).Update(V0923, V0920);

        Assert.Equal(HidDriverOutcomeKind.Updated, outcome.Kind);
        Assert.Equal(["install 0.9.2.3", "delete oem120.inf"], windows.Events);
    }

    [Fact]
    public void ANewDriverThatNeverBecomesHealthyIsRolledBackFromTheReleaseStore()
    {
        var windows = new FakeWindows();
        windows.Configurable["0.9.2.3"] = false;
        windows.Bind(windows.Stage("oem119.inf", V0922));
        windows.StackFor["0.9.2.3"] = ["WUDFRd", "vhf", "vhf", "PnpManager"];

        var outcome = Lifecycle(windows).Update(V0923, V0922);

        Assert.Equal(HidDriverOutcomeKind.RolledBack, outcome.Kind);
        Assert.Contains("0.9.2.2 was restored", outcome.Detail, StringComparison.Ordinal);
        Assert.Empty(windows.Health(V0922));

        // One repair attempt, then the rollback; the old package was never deleted, the failed one was.
        Assert.Equal(["install 0.9.2.3", "reapply 0.9.2.3", "reapply 0.9.2.2", "delete oem200.inf"], windows.Events);
        Assert.Equal("oem119.inf", Assert.Single(windows.Packages).Name);
    }

    /// <summary>
    /// Rolling back to a non-configurable 0.9.2.2 and deleting the configurable 0.9.2.3 that replaced it
    /// is the same trap in the other direction, so the rollback checks after its cleanup too.
    /// </summary>
    [Fact]
    public void ARollbackRepairsTheRegistrationItsOwnCleanupDeleted()
    {
        var windows = new FakeWindows();
        windows.Bind(windows.Stage("oem119.inf", V0922));
        windows.StackFor["0.9.2.3"] = ["vhf", "PnpManager"];

        var outcome = Lifecycle(windows).Update(V0923, V0922);

        Assert.Equal(HidDriverOutcomeKind.RolledBack, outcome.Kind);
        Assert.Equal(
            ["install 0.9.2.3", "reapply 0.9.2.3", "reapply 0.9.2.2", "delete oem200.inf", "registration wiped", "reapply 0.9.2.2"],
            windows.Events);
        Assert.Empty(windows.Health(V0922));
    }

    [Fact]
    public void AFailedRepairAfterCleanupRollsBack()
    {
        var windows = new FakeWindows();
        windows.Configurable["0.9.2.3"] = false;
        windows.Bind(windows.Stage("oem120.inf", V0920));
        windows.ReapplyFailsFor.Add("0.9.2.3");

        var outcome = Lifecycle(windows).Update(V0923, V0920);

        Assert.Equal(HidDriverOutcomeKind.RolledBack, outcome.Kind);
        Assert.Contains("did not stay healthy after stale packages were removed", outcome.Detail, StringComparison.Ordinal);
        Assert.Empty(windows.Health(V0920));
    }

    [Fact]
    public void WithoutAPayloadToRollBackToAFailureIsReportedAsSuch()
    {
        var windows = new FakeWindows();
        windows.Bind(windows.Stage("oem119.inf", V0922));
        windows.StackFor["0.9.2.3"] = ["vhf", "PnpManager"];

        var outcome = Lifecycle(windows).Update(V0923, rollback: null);

        Assert.Equal(HidDriverOutcomeKind.Failed, outcome.Kind);
        Assert.Contains("no previous driver payload", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ADriverPnputilWouldNotBindIsForcedOnceThroughTheDriverUpdateApi()
    {
        var windows = new FakeWindows { InstallDoesNotBind = true };
        windows.Bind(windows.Stage("oem119.inf", V0922));

        var outcome = Lifecycle(windows).Update(V0923, V0922);

        Assert.Equal(HidDriverOutcomeKind.Updated, outcome.Kind);
        Assert.Equal(["install 0.9.2.3", "reapply 0.9.2.3", "delete oem119.inf"], windows.Events);
    }

    [Fact]
    public void APackageWindowsRefusesToDeleteIsReportedAndLeftAlone()
    {
        var windows = new FakeWindows();
        windows.Bind(windows.Stage("oem119.inf", V0922));
        windows.Undeletable.Add("oem119.inf");

        var outcome = Lifecycle(windows).Update(V0923, V0922);

        Assert.Equal(HidDriverOutcomeKind.Updated, outcome.Kind);
        Assert.Equal(["oem119.inf"], outcome.StalePackagesLeft);
    }

    // ---------------------------------------------------------------- health

    [Fact]
    public void AHealthyDeviceHasNoProblems()
    {
        Assert.Empty(HidDriverLifecycle.Problems(Healthy(), Registration(), V0923.DriverDllSha256, V0923));
    }

    [Theory]
    [InlineData("WUDFRd,vhf,vhf,PnpManager")] // 0.9.2.0's duplicate vhf: the Sx bugcheck stack
    [InlineData("vhf,PnpManager")]            // no reflector: the stack Storage Sense left behind
    [InlineData("WUDFRd,vhf")]
    [InlineData("")]
    public void AnyOtherStackIsRejected(string stack)
    {
        var device = Healthy() with { Stack = stack.Split(',', StringSplitOptions.RemoveEmptyEntries) };

        var problems = HidDriverLifecycle.Problems(device, Registration(), V0923.DriverDllSha256, V0923);

        Assert.Contains(problems, p => p.Contains("stack", StringComparison.Ordinal));
    }

    [Fact]
    public void ARegisteredDllWithTheWrongHashIsRejected()
    {
        var problems = HidDriverLifecycle.Problems(Healthy(), Registration(), V0922.DriverDllSha256, V0923);

        Assert.Contains(problems, p => p.Contains("is not the one the release manifest lists", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnreadableRegisteredDllIsRejected()
    {
        var problems = HidDriverLifecycle.Problems(Healthy(), Registration(), registeredDllSha256: null, V0923);

        Assert.Contains(problems, p => p.Contains("cannot be read", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptiedOrMissingRegistrationIsRejected()
    {
        // What this PC had from 2026-09-19: the key, with no values in it.
        var emptied = new UmdfRegistration(null, null, null, []);

        Assert.Contains(HidDriverLifecycle.Problems(Healthy(), emptied, null, V0923), p => p.Contains("no ImagePath", StringComparison.Ordinal));
        Assert.Contains(HidDriverLifecycle.Problems(Healthy(), null, null, V0923), p => p.Contains("no ImagePath", StringComparison.Ordinal));
    }

    [Fact]
    public void ARegistrationWithoutTheHidVhfExtensionOrWdf2IsRejected()
    {
        Assert.Contains(
            HidDriverLifecycle.Problems(Healthy(), Registration() with { Extensions = [] }, V0923.DriverDllSha256, V0923),
            p => p.Contains("HidVhf", StringComparison.Ordinal));

        Assert.Contains(
            HidDriverLifecycle.Problems(Healthy(), Registration() with { WdfMajorVersion = null }, V0923.DriverDllSha256, V0923),
            p => p.Contains("WDF 2", StringComparison.Ordinal));
    }

    [Fact]
    public void AProblemCodeAnotherVersionOrNoDeviceIsRejected()
    {
        Assert.NotEmpty(HidDriverLifecycle.Problems(Healthy() with { ProblemCode = 10 }, Registration(), V0923.DriverDllSha256, V0923));
        Assert.NotEmpty(HidDriverLifecycle.Problems(Healthy() with { DriverVersion = "0.9.2.2" }, Registration(), V0923.DriverDllSha256, V0923));
        Assert.NotEmpty(HidDriverLifecycle.Problems(HidDeviceState.Absent, Registration(), V0923.DriverDllSha256, V0923));
    }

    // ---------------------------------------------------------------- stale packages

    [Fact]
    public void OnlyUnboundNexusPackagesForTheNexusDeviceAreStale()
    {
        DriverPackage[] matching =
        [
            new("oem119.inf", "nexushid.inf", "Nexus", "0.9.2.2", Installed: false),
            new("oem120.inf", "NexusHid.inf", "Nexus", "0.9.2.3", Installed: true),
            new("oem121.inf", "nexushid.inf", "Nexus", "0.9.2.0", Installed: false),
            new("oem7.inf", "nexushid.inf", "Someone Else", "1.0.0", Installed: false),
            new("oem8.inf", "other.inf", "Nexus", "1.0.0", Installed: false),
        ];

        var stale = HidDriverLifecycle.StalePackages(matching, boundInf: "oem120.inf");

        Assert.Equal(["oem119.inf", "oem121.inf"], stale.Select(p => p.PublishedName));
    }

    [Fact]
    public void TheBoundPackageIsNeverStaleEvenIfWindowsDoesNotMarkItInstalled()
    {
        DriverPackage[] matching = [new("oem119.inf", "nexushid.inf", "Nexus", "0.9.2.2", Installed: false)];

        Assert.Empty(HidDriverLifecycle.StalePackages(matching, boundInf: "OEM119.INF"));
    }

    [Fact]
    public void PnputilsDriverListIsRead()
    {
        // Captured on this PC from pnputil /enum-devices /instanceid ROOT\DEVGEN\NEXUSHID /drivers
        // /format xml, with a second, outranked package added.
        var xml = XDocument.Parse("""
            <?xml version="1.0" encoding="utf-8"?>
            <PnpUtil Version="10.0.26200" Command="/enum-devices /instanceid ROOT\DEVGEN\NEXUSHID /drivers /format xml">
                <Device InstanceId="ROOT\DEVGEN\NEXUSHID">
                    <DeviceDescription>Nexus HID Control</DeviceDescription>
                    <Status>Started</Status>
                    <DriverName>oem119.inf</DriverName>
                    <MatchingDrivers>
                        <DriverName DriverName="oem119.inf">
                            <OriginalName>nexushid.inf</OriginalName>
                            <ProviderName>Nexus</ProviderName>
                            <DriverVersion>09/15/2026 0.9.2.2</DriverVersion>
                            <MatchingDeviceId>Root\NexusHid</MatchingDeviceId>
                            <Rank>00FF0000</Rank>
                            <Status>BestRanked/Installed</Status>
                        </DriverName>
                        <DriverName DriverName="oem120.inf">
                            <OriginalName>nexushid.inf</OriginalName>
                            <ProviderName>Nexus</ProviderName>
                            <DriverVersion>09/09/2026 0.9.2.0</DriverVersion>
                            <MatchingDeviceId>Root\NexusHid</MatchingDeviceId>
                            <Rank>00FF0000</Rank>
                            <Status>Outranked</Status>
                        </DriverName>
                    </MatchingDrivers>
                </Device>
            </PnpUtil>
            """);

        Assert.Equal(
            [
                new DriverPackage("oem119.inf", "nexushid.inf", "Nexus", "0.9.2.2", true),
                new DriverPackage("oem120.inf", "nexushid.inf", "Nexus", "0.9.2.0", false),
            ],
            PnputilDriverList.Parse(xml));
    }

    [Fact]
    public void AStackIsReadAsServiceNames()
    {
        Assert.Equal("WUDFRd", WindowsHidDriverHost.ServiceName(@"\Driver\WUDFRd"));
        Assert.Equal("PnpManager", WindowsHidDriverHost.ServiceName(@"\Driver\PnpManager"));
        Assert.Equal("vhf", WindowsHidDriverHost.ServiceName("vhf"));
    }

    // ---------------------------------------------------------------- payloads

    [Fact]
    public void APayloadNamesTheInfAndTheDllsManifestHash()
    {
        var payload = HidDriverLifecycle.PayloadFrom(Driver("0.9.2.3"), @"C:\work\driver");

        Assert.Equal(new DriverPayload("0.9.2.3", @"C:\work\driver\NexusHid.inf", Sha('d')), payload);
    }

    [Fact]
    public void APayloadWithoutTheDriverDllIsRefused()
    {
        var component = Driver("0.9.2.3") with { Files = [new ReleaseFile { Path = "NexusHid.inf", Sha256 = Sha('i'), Size = 1 }] };

        Assert.Throws<UpdateIntegrityException>(() => HidDriverLifecycle.PayloadFrom(component, @"C:\work\driver"));
    }

    // ---------------------------------------------------------------- helpers

    private static FileSetComponent Driver(string version) => new()
    {
        Version = version,
        Directory = "driver",
        Files =
        [
            new ReleaseFile { Path = "nexushid.cat", Sha256 = Sha('c'), Size = 1 },
            new ReleaseFile { Path = "NexusHid.inf", Sha256 = Sha('i'), Size = 1 },
            new ReleaseFile { Path = "NexusHidDriver.dll", Sha256 = Sha('d'), Size = 1 },
        ],
    };

    private static HidDriverLifecycle Lifecycle(FakeWindows windows) =>
        new(windows, line => windows.Timeline.Add("log: " + line));

    private static HidDeviceState Healthy() =>
        new(true, 0, "oem120.inf", "0.9.2.3", ["WUDFRd", "vhf", "PnpManager"]);

    private static UmdfRegistration Registration() =>
        new(@"C:\Windows\System32\DriverStore\FileRepository\nexushid.inf_amd64_x\NexusHidDriver.dll", 2, 33, ["HidVhf"]);

    /// <summary>The Driver Store, the devnode and the UMDF registration, as far as the update can see them.</summary>
    private sealed class FakeWindows : IHidDriverHost
    {
        private static readonly DriverPayload[] Known = [V0920, V0922, V0923];

        private readonly HashSet<StoredPackage> _counted = [];
        private int _nextOem = 200;
        private StoredPackage? _bound;
        private StoredPackage? _registered;

        public List<StoredPackage> Packages { get; } = [];

        /// <summary>What was done to Windows, in order.</summary>
        public List<string> Events { get; } = [];

        /// <summary><see cref="Events"/> and the lifecycle's log, interleaved.</summary>
        public List<string> Timeline { get; } = [];

        /// <summary>Whether the Driver Store counts a version's package as a reference (it was configurable).</summary>
        public Dictionary<string, bool> Configurable { get; } = new()
        {
            ["0.9.2.0"] = true,
            ["0.9.2.2"] = false,
            ["0.9.2.3"] = true,
        };

        public Dictionary<string, string[]> StackFor { get; } = [];

        public HashSet<string> ReapplyFailsFor { get; } = [];

        public HashSet<string> Undeletable { get; } = [];

        public bool InstallDoesNotBind { get; set; }

        public StoredPackage Stage(string name, DriverPayload payload)
        {
            var package = new StoredPackage(name, payload);
            Packages.Add(package);
            return package;
        }

        /// <summary>
        /// Windows' own install of a package: the only thing that writes the registration. A
        /// configurable package's configuration counts as a reference; a co-installer write does not.
        /// </summary>
        public void Bind(StoredPackage package)
        {
            _bound = package;
            _registered = package;

            if (Configurable[package.Payload.Version])
            {
                _counted.Add(package);
            }
        }

        public (int Code, string Output) AddAndInstall(string infPath)
        {
            var package = StageFor(infPath);
            Record($"install {package.Payload.Version}");

            if (!InstallDoesNotBind && (_bound is null || Version.Parse(package.Payload.Version) > Version.Parse(_bound.Payload.Version)))
            {
                Bind(package);
            }

            return (0, string.Empty);
        }

        public (bool Succeeded, string Detail) Reapply(string infPath)
        {
            var package = StageFor(infPath);
            Record($"reapply {package.Payload.Version}");

            if (ReapplyFailsFor.Contains(package.Payload.Version))
            {
                return (false, "failed");
            }

            Bind(package);
            return (true, string.Empty);
        }

        public HidDeviceState ReadDevice() => _bound is null
            ? new HidDeviceState(true, 28, null, null, [])
            : new HidDeviceState(
                true,
                0,
                _bound.Name,
                _bound.Payload.Version,
                StackFor.TryGetValue(_bound.Payload.Version, out var stack) ? stack : ["WUDFRd", "vhf", "PnpManager"]);

        public UmdfRegistration? ReadUmdfRegistration() => _registered is null
            ? new UmdfRegistration(null, null, null, [])
            : new UmdfRegistration(ImagePath(_registered), 2, 33, ["HidVhf"]);

        public IReadOnlyList<DriverPackage>? MatchingPackages() =>
            Packages.Select(p => new DriverPackage(p.Name, "nexushid.inf", "Nexus", p.Payload.Version, p == _bound)).ToList();

        public (bool Deleted, string Detail) DeletePackage(string publishedName)
        {
            var package = Packages.Single(p => p.Name == publishedName);

            if (package == _bound || Undeletable.Contains(publishedName))
            {
                return (false, "in use");
            }

            Record($"delete {publishedName}");
            Packages.Remove(package);

            if (_counted.Remove(package) && _counted.Count == 0 && _registered is not null)
            {
                // The last counted reference is gone, and Windows deletes the values - whoever wrote them last.
                _registered = null;
                Record("registration wiped");
            }

            return (true, string.Empty);
        }

        public string? Sha256OfFile(string path) =>
            Packages.FirstOrDefault(p => ImagePath(p) == path)?.Payload.DriverDllSha256;

        public IReadOnlyList<string> Health(DriverPayload expected)
        {
            var registration = ReadUmdfRegistration();
            return HidDriverLifecycle.Problems(ReadDevice(), registration, registration?.ImagePath is { } p ? Sha256OfFile(p) : null, expected);
        }

        private void Record(string what)
        {
            Events.Add(what);
            Timeline.Add(what);
        }

        private StoredPackage StageFor(string infPath) =>
            Packages.FirstOrDefault(p => p.Payload.InfPath == infPath)
            ?? Stage($"oem{_nextOem++}.inf", Known.Single(k => k.InfPath == infPath));

        private static string ImagePath(StoredPackage package) =>
            $@"C:\Windows\System32\DriverStore\FileRepository\{package.Name}\NexusHidDriver.dll";
    }

    private sealed record StoredPackage(string Name, DriverPayload Payload);
}
