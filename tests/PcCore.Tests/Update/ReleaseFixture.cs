using System.Text;
using PcCore.Tests.TestSupport;
using PcCore.Update;

namespace PcCore.Tests.Update;

/// <summary>
/// A release store, an install directory and an update work directory, in throwaway folders, built
/// the way <c>tools/publish-release.ps1</c> builds the real ones.
/// </summary>
internal sealed class ReleaseFixture : IDisposable
{
    private readonly TempDirectory _root = new("update");

    public ReleaseFixture()
        : this(localAppDataLayout: false)
    {
    }

    /// <param name="localAppDataLayout">
    /// Lay everything out under a stand-in <c>%LOCALAPPDATA%</c>, where a real process resolves it, so a
    /// test can resolve it all again the way a restarted one does.
    /// </param>
    public ReleaseFixture(bool localAppDataLayout)
    {
        if (!localAppDataLayout)
        {
            Store = new ReleaseStore(_root.CreateDirectory("releases"));
            Paths = new UpdatePaths(_root.CreateDirectory("install"), _root.CreateDirectory("work"));
            StateDirectory = _root.CreateDirectory("state");
            return;
        }

        LocalAppData = _root.CreateDirectory("LocalAppData");
        Store = new ReleaseStore(Directory.CreateDirectory(ReleaseStore.RootIn(LocalAppData)).FullName);
        Paths = UpdatePaths.ForLocalAppData(LocalAppData);
        Directory.CreateDirectory(Paths.InstallDirectory);
        Directory.CreateDirectory(Paths.WorkDirectory);
        StateDirectory = Path.Combine(LocalAppData, UpdatePaths.StateFolder);
    }

    public ReleaseStore Store { get; }

    public UpdatePaths Paths { get; }

    /// <summary>The stand-in <c>%LOCALAPPDATA%</c>, when the fixture was laid out under one.</summary>
    public string? LocalAppData { get; }

    /// <summary>Stands in for <c>%LOCALAPPDATA%\Nexus</c>: pairing, keys, settings, logs, evidence.</summary>
    public string StateDirectory { get; }

    public string Root => _root.Path;

    /// <summary>PC Core files as a first release ships them.</summary>
    public static Dictionary<string, string> BaseFiles() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PcCore.Host.exe"] = "host v1",
        ["PcCore.Host.dll"] = "host library v1",
        ["PcCore.Capabilities.dll"] = "capabilities v1",
        ["appsettings.json"] = "{ \"PcCore\": {} }",
        ["wwwroot/index.html"] = "<html>v1</html>",
    };

    /// <summary>Publishes a release and points <c>latest.json</c> at it.</summary>
    public ReleaseManifest Publish(
        int release,
        string version,
        IReadOnlyDictionary<string, string> pcCore,
        (string Version, IReadOnlyDictionary<string, string> Files)? secureCapture = null,
        (string Version, IReadOnlyDictionary<string, string> Files)? driver = null,
        (int VersionCode, byte[] Bytes)? android = null)
    {
        var directory = Path.Combine(Store.Root, release.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var manifest = new ReleaseManifest
        {
            Version = version,
            Release = release,
            PublishedAtUtc = DateTimeOffset.UtcNow,
            Compatibility = new ReleaseCompatibility { MinAndroidVersionCode = 14 },
            Components = new ReleaseComponents
            {
                PcCore = WriteFileSet(directory, "pccore", version, pcCore),
                SecureCapture = secureCapture is { } sc ? WriteFileSet(directory, "securecapture", sc.Version, sc.Files) : null,
                HidDriver = driver is { } d ? WriteFileSet(directory, "driver", d.Version, d.Files) : null,
                Android = android is { } apk ? WriteApk(directory, version, apk.VersionCode, apk.Bytes) : null,
            },
        };

        var manifestPath = Path.Combine(directory, "manifest.json");
        UpdateJson.WriteAtomic(manifestPath, manifest);

        UpdateJson.WriteAtomic(Store.PointerPath, new ReleasePointer
        {
            Release = release,
            Version = version,
            Manifest = $"{release}/manifest.json",
            ManifestSha256 = FileHash.Sha256OfFile(manifestPath),
        });

        return manifest;
    }

    /// <summary>The path of a file inside a published component, for tampering with it.</summary>
    public string ReleaseFile(int release, string component, string path) =>
        Path.Combine(Store.Root, release.ToString(System.Globalization.CultureInfo.InvariantCulture), component, path.Replace('/', '\\'));

    /// <summary>Lays PC Core files into the install directory, as a previous install left them.</summary>
    public void Install(IReadOnlyDictionary<string, string> files)
    {
        foreach (var (path, content) in files)
        {
            Write(Path.Combine(Paths.InstallDirectory, path.Replace('/', '\\')), content);
        }
    }

    /// <summary>Every file under a directory, path to bytes, for "nothing changed" comparisons.</summary>
    public static Dictionary<string, string> Snapshot(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .ToDictionary(
                    f => Path.GetRelativePath(directory, f),
                    f => FileHash.Sha256OfFile(f),
                    StringComparer.OrdinalIgnoreCase)
            : [];

    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static FileSetComponent WriteFileSet(string releaseDirectory, string folder, string version, IReadOnlyDictionary<string, string> files)
    {
        var list = new List<ReleaseFile>();

        foreach (var (path, content) in files)
        {
            var target = Path.Combine(releaseDirectory, folder, path.Replace('/', '\\'));
            Write(target, content);
            list.Add(new ReleaseFile { Path = path, Sha256 = FileHash.Sha256OfFile(target), Size = new FileInfo(target).Length });
        }

        return new FileSetComponent { Version = version, Directory = folder, Files = list };
    }

    private static AndroidComponent WriteApk(string releaseDirectory, string version, int versionCode, byte[] bytes)
    {
        var relative = $"android/nexus-{version}-{versionCode}.apk";
        var target = Path.Combine(releaseDirectory, relative.Replace('/', '\\'));

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bytes);

        return new AndroidComponent
        {
            VersionName = version,
            VersionCode = versionCode,
            File = relative,
            Sha256 = FileHash.Sha256OfBytes(bytes),
            Size = bytes.Length,
            SigningCertSha256 = new string('a', 64),
            MinSdk = 31,
        };
    }

    public void Dispose() => _root.Dispose();
}

/// <summary>Installed versions, as the test says they are.</summary>
internal sealed class FakeComponentProbe(InstalledComponent? secureCapture = null, InstalledComponent? driver = null) : IElevatedComponentProbe
{
    public InstalledComponent SecureCaptureState { get; set; } = secureCapture ?? InstalledComponent.Absent;

    public InstalledComponent DriverState { get; set; } = driver ?? InstalledComponent.Absent;

    public InstalledComponent SecureCapture() => SecureCaptureState;

    public InstalledComponent HidDriver() => DriverState;
}

/// <summary>PC Core as a list of pretend processes the updater can stop and start.</summary>
internal sealed class FakeProcesses(string installedExecutable) : INexusProcessControl
{
    private int _nextId = 1000;

    public List<RunningPcCore> Running { get; } = [new RunningPcCore(1, installedExecutable)];

    public List<string> Events { get; } = [];

    public IReadOnlyList<RunningPcCore> FindRunning() => [.. Running];

    public Task StopAsync(RunningPcCore process, TimeSpan grace, CancellationToken cancellationToken)
    {
        Events.Add($"stop {process.ProcessId}");
        Running.RemoveAll(p => p.ProcessId == process.ProcessId);
        return Task.CompletedTask;
    }

    public int Start(string executable)
    {
        var id = ++_nextId;
        Events.Add($"start {File.ReadAllText(executable)}");
        Running.Add(new RunningPcCore(id, executable));
        return id;
    }

    public bool IsRunning(int processId) => Running.Any(p => p.ProcessId == processId);

    public void Kill(int processId)
    {
        Events.Add($"kill {processId}");
        Running.RemoveAll(p => p.ProcessId == processId);
    }
}

/// <summary>A health check whose answer the test decides, per start.</summary>
internal sealed class FakeHealth(params bool[] answers) : INexusHealthProbe
{
    private int _calls;

    public List<string?> ExpectedVersions { get; } = [];

    public Task<HealthResult> WaitUntilHealthyAsync(int processId, string? expectedVersion, DateTimeOffset startedAtUtc, CancellationToken cancellationToken)
    {
        ExpectedVersions.Add(expectedVersion);
        var healthy = _calls < answers.Length ? answers[_calls] : true;
        _calls++;
        return Task.FromResult(new HealthResult(healthy, healthy ? "answered" : "it exited during start-up"));
    }
}

internal sealed class FakeElevated(ElevatedOutcome outcome = ElevatedOutcome.Succeeded) : IElevatedInstaller
{
    public int Calls { get; private set; }

    public Task<ElevatedResult> InstallAsync(UpdatePlan plan, string stagedReleaseDirectory, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new ElevatedResult(outcome, outcome.ToString()));
    }
}
