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
        (int VersionCode, byte[] Bytes)? android = null,
        (string Version, IReadOnlyDictionary<string, string> Files)? assistant = null)
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
                Assistant = assistant is { } a ? WriteFileSet(directory, "assistant", a.Version, a.Files) : null,
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

    /// <summary>Lays PC Assistant files into its install folder, as a previous install left them.</summary>
    public void InstallAssistant(IReadOnlyDictionary<string, string> files)
    {
        foreach (var (path, content) in files)
        {
            Write(Path.Combine(Paths.AssistantDirectory, path.Replace('/', '\\')), content);
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

/// <summary>
/// PC Assistant as pretend processes. Each start records the text of the executable it started, so a
/// test can tell which version came up; whether it then stays up is the test's decision.
/// </summary>
internal sealed class FakeAssistant : IAssistantProcessControl
{
    private int _nextId = 5000;

    public List<RunningPcCore> Running { get; } = [];

    public List<string> Events { get; } = [];

    /// <summary>What a running PC Assistant answers when asked to close: false is "a task is running".</summary>
    public bool AgreesToClose { get; set; } = true;

    /// <summary>Per start, in order, whether it stays up. Starts beyond the list stay up.</summary>
    public Queue<bool> StartsStayUp { get; } = new();

    public RunningPcCore RunFrom(string executable)
    {
        var process = new RunningPcCore(++_nextId, executable);
        Running.Add(process);
        return process;
    }

    public IReadOnlyList<RunningPcCore> FindRunning() => [.. Running];

    public Task<bool> AskToCloseAsync(RunningPcCore process, string executable, TimeSpan grace, CancellationToken cancellationToken)
    {
        Events.Add($"ask {process.ProcessId}");

        if (!AgreesToClose)
        {
            return Task.FromResult(false);
        }

        Running.RemoveAll(p => p.ProcessId == process.ProcessId);
        return Task.FromResult(true);
    }

    public int Start(string executable)
    {
        var id = ++_nextId;
        Events.Add($"start {File.ReadAllText(executable)}");

        if (!StartsStayUp.TryDequeue(out var staysUp) || staysUp)
        {
            Running.Add(new RunningPcCore(id, executable));
        }

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

/// <summary>
/// The elevated half as the coordinator sees it. Once approved, and only for a driver update, it asks for
/// the HID device the way the real one does, then notes which PC Core processes were still running when
/// it replaced the driver - each of those would have held the device open and vetoed loading it.
/// </summary>
internal sealed class FakeElevated(ElevatedOutcome outcome = ElevatedOutcome.Succeeded, FakeProcesses? processes = null) : IElevatedInstaller
{
    public int Calls { get; private set; }

    /// <summary>What the driver step reports: installed, but loaded only after a restart.</summary>
    public bool RestartRequired { get; init; }

    /// <summary>Whether the elevated half asks for the device at all, as one predating the handoff would not.</summary>
    public bool AsksForDevice { get; init; } = true;

    /// <summary>PC Core processes running when the driver was replaced; null when no driver was.</summary>
    public IReadOnlyList<RunningPcCore>? RunningDuringDriverStep { get; private set; }

    public async Task<ElevatedResult> InstallAsync(
        UpdatePlan plan,
        string stagedReleaseDirectory,
        Func<CancellationToken, Task> releaseHidDevice,
        CancellationToken cancellationToken)
    {
        Calls++;

        // A declined prompt means the elevated half never ran.
        if (outcome != ElevatedOutcome.Declined && plan.HidDriver.Updates)
        {
            if (AsksForDevice)
            {
                await releaseHidDevice(cancellationToken);
            }

            RunningDuringDriverStep = processes?.FindRunning() ?? [];
        }

        return outcome == ElevatedOutcome.Succeeded && RestartRequired
            ? new ElevatedResult(outcome, "HID driver 0.9.2.3 is installed and configured, but Windows could not restart the device to load it: 0.9.2.2 keeps running until the PC restarts.")
            {
                RestartRequired = true,
            }
            : new ElevatedResult(outcome, outcome.ToString());
    }
}
