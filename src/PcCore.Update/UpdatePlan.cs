namespace PcCore.Update;

/// <summary>What is installed of a component that needs administrator rights to replace.</summary>
/// <param name="Installed">Whether the component is present on this PC at all.</param>
/// <param name="Version">Its version, when it could be read.</param>
public sealed record InstalledComponent(bool Installed, string? Version)
{
    public static readonly InstalledComponent Absent = new(false, null);
}

/// <summary>Reads the installed versions of the elevated components. Never changes anything.</summary>
public interface IElevatedComponentProbe
{
    InstalledComponent SecureCapture();

    InstalledComponent HidDriver();
}

/// <summary>
/// The updater's record of the last PC Core it installed.
/// <para>
/// Used for two things only: deleting files a newer release no longer ships (never anything the
/// updater did not put there itself), and noticing that the owner has edited <c>appsettings.json</c>
/// since, so an update does not quietly throw that edit away.
/// </para>
/// </summary>
public sealed record InstalledRecord
{
    public required int Release { get; init; }

    public required string Version { get; init; }

    public DateTimeOffset InstalledAtUtc { get; init; }

    public required IReadOnlyList<ReleaseFile> PcCoreFiles { get; init; }
}

public enum FileChangeKind
{
    Add,
    Replace,
    Remove,
}

/// <summary>One PC Core file the update will touch.</summary>
public sealed record FileChange(string Path, FileChangeKind Kind, string? Sha256, long Size);

public enum ComponentAction
{
    Skip,
    Update,
}

public sealed record ComponentDecision(string Name, ComponentAction Action, string? InstalledVersion, string? AvailableVersion, string Reason)
{
    public bool Updates => Action == ComponentAction.Update;
}

/// <summary>Exactly what an update of this PC to one release would change, and what it would leave alone.</summary>
public sealed record UpdatePlan(
    ReleaseManifest Manifest,
    string ReleaseDirectory,
    IReadOnlyList<FileChange> PcCore,
    ComponentDecision SecureCapture,
    ComponentDecision HidDriver,
    IReadOnlyList<string> PreservedFiles)
{
    /// <summary>PC Assistant's files to change, relative to its install folder. Empty unless its version changes.</summary>
    public IReadOnlyList<FileChange> Assistant { get; init; } = [];

    public ComponentDecision AssistantComponent { get; init; } =
        new(AssistantInstall.ComponentName, ComponentAction.Skip, null, null, "not part of this release");

    public bool PcCoreChanges => PcCore.Count > 0;

    public bool AssistantChanges => Assistant.Count > 0;

    public bool NeedsElevation => SecureCapture.Updates || HidDriver.Updates;

    public bool UpToDate => !PcCoreChanges && !NeedsElevation && !AssistantChanges;

    public string Describe()
    {
        if (UpToDate)
        {
            return $"Nexus {Manifest.Version} (release {Manifest.Release}) is already installed.";
        }

        var parts = new List<string>();

        if (PcCoreChanges)
        {
            parts.Add($"PC Core: {PcCore.Count} file(s)");
        }

        if (SecureCapture.Updates)
        {
            parts.Add($"SecureCapture {SecureCapture.InstalledVersion} -> {SecureCapture.AvailableVersion} (needs administrator approval)");
        }

        if (HidDriver.Updates)
        {
            parts.Add($"HID driver {HidDriver.InstalledVersion} -> {HidDriver.AvailableVersion} (needs administrator approval)");
        }

        if (AssistantChanges)
        {
            parts.Add($"{AssistantComponent.Name} {AssistantComponent.InstalledVersion ?? "(not installed)"} -> {AssistantComponent.AvailableVersion}: {Assistant.Count} file(s)");
        }

        return $"Nexus {Manifest.Version} (release {Manifest.Release}): " + string.Join(", ", parts);
    }
}

/// <summary>
/// Compares a release with what is on this PC.
/// <para>
/// <b>By content for PC Core, by version for the rest.</b> PC Core files are compared by hash, so a
/// file that did not change is not copied and an update that changed nothing is not an update.
/// SecureCapture and the driver are compared by their own version numbers, because replacing either
/// costs the owner a UAC prompt, and a rebuild that happens to produce different bytes from the same
/// source must not cost one.
/// </para>
/// </summary>
public static class UpdatePlanner
{
    /// <summary>Settings the owner may edit in the install directory. Never overwritten once edited.</summary>
    public static readonly IReadOnlySet<string> OwnerEditableFiles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "appsettings.json" };

    public static UpdatePlan Plan(
        ReleaseManifest manifest,
        string releaseDirectory,
        string installDirectory,
        InstalledRecord? installed,
        IElevatedComponentProbe probe,
        InstalledAssistantRecord? installedAssistant = null)
    {
        var (assistantChanges, assistantDecision) = PlanAssistant(
            manifest.Components.Assistant, Path.Combine(installDirectory, AssistantInstall.Folder), installedAssistant);

        return PlanPcCore(manifest, releaseDirectory, installDirectory, installed, probe) with
        {
            Assistant = assistantChanges,
            AssistantComponent = assistantDecision,
        };
    }

    /// <summary>
    /// PC Assistant is installed the first time a release carries it and replaced only when its version
    /// changes. When it is replaced, only files whose bytes differ are copied, and only files this
    /// updater installed before are ever removed.
    /// </summary>
    private static (IReadOnlyList<FileChange> Changes, ComponentDecision Decision) PlanAssistant(
        FileSetComponent? offered,
        string assistantDirectory,
        InstalledAssistantRecord? installed)
    {
        const string Name = AssistantInstall.ComponentName;

        if (offered is null)
        {
            return ([], new ComponentDecision(Name, ComponentAction.Skip, installed?.Version, null, "not part of this release"));
        }

        var executable = Path.Combine(assistantDirectory, AssistantInstall.ExecutableName);
        var installedVersion = File.Exists(executable) ? installed?.Version ?? VersionOf(executable) : null;

        if (installedVersion is not null && string.Equals(installedVersion, offered.Version, StringComparison.OrdinalIgnoreCase))
        {
            return ([], new ComponentDecision(Name, ComponentAction.Skip, installedVersion, offered.Version, "unchanged"));
        }

        var changes = new List<FileChange>();
        var shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in offered.Files)
        {
            shipped.Add(file.Path);

            var target = ReleaseStore.Contained(assistantDirectory, file.Path)
                ?? throw new UpdateIntegrityException($"{file.Path} would resolve outside PC Assistant's install folder.");

            if (!File.Exists(target))
            {
                changes.Add(new FileChange(file.Path, FileChangeKind.Add, file.Sha256, file.Size));
            }
            else if (!FileHash.Matches(target, file.Sha256, file.Size))
            {
                changes.Add(new FileChange(file.Path, FileChangeKind.Replace, file.Sha256, file.Size));
            }
        }

        foreach (var old in installed?.Files ?? [])
        {
            if (!shipped.Contains(old.Path) && ReleaseStore.Contained(assistantDirectory, old.Path) is { } path && File.Exists(path))
            {
                changes.Add(new FileChange(old.Path, FileChangeKind.Remove, null, 0));
            }
        }

        return (changes, new ComponentDecision(
            Name,
            ComponentAction.Update,
            installedVersion,
            offered.Version,
            installedVersion is null ? "not installed on this PC" : "a new version is available"));
    }

    /// <summary>The product version of an executable, without build metadata.</summary>
    private static string? VersionOf(string executable)
    {
        try
        {
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).ProductVersion?.Split('+')[0];
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            return null;
        }
    }

    private static UpdatePlan PlanPcCore(
        ReleaseManifest manifest,
        string releaseDirectory,
        string installDirectory,
        InstalledRecord? installed,
        IElevatedComponentProbe probe)
    {
        var changes = new List<FileChange>();
        var preserved = new List<string>();
        var shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in manifest.Components.PcCore.Files)
        {
            shipped.Add(file.Path);

            var target = ReleaseStore.Contained(installDirectory, file.Path)
                ?? throw new UpdateIntegrityException($"{file.Path} would resolve outside the install directory.");

            if (!File.Exists(target))
            {
                changes.Add(new FileChange(file.Path, FileChangeKind.Add, file.Sha256, file.Size));
                continue;
            }

            if (FileHash.Matches(target, file.Sha256, file.Size))
            {
                continue;
            }

            if (OwnerEditableFiles.Contains(file.Path) && WasEditedSinceInstall(target, file.Path, installed))
            {
                preserved.Add(file.Path);
                continue;
            }

            changes.Add(new FileChange(file.Path, FileChangeKind.Replace, file.Sha256, file.Size));
        }

        // Only what this updater itself installed last time, and only if it is still there. A file
        // the owner dropped into the directory was never in a manifest and is never touched.
        foreach (var old in installed?.PcCoreFiles ?? [])
        {
            if (shipped.Contains(old.Path) || OwnerEditableFiles.Contains(old.Path))
            {
                continue;
            }

            if (ReleaseStore.Contained(installDirectory, old.Path) is { } path && File.Exists(path))
            {
                changes.Add(new FileChange(old.Path, FileChangeKind.Remove, null, 0));
            }
        }

        return new UpdatePlan(
            manifest,
            releaseDirectory,
            changes,
            Decide("SecureCapture", manifest.Components.SecureCapture, probe.SecureCapture()),
            Decide("HID driver", manifest.Components.HidDriver, probe.HidDriver()),
            preserved);
    }

    private static bool WasEditedSinceInstall(string target, string path, InstalledRecord? installed)
    {
        var recorded = installed?.PcCoreFiles.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));

        // Without a record there is nothing to compare with, and the file the build shipped wins:
        // that is the first update, from a copy --install-app made.
        return recorded is not null && !FileHash.Matches(target, recorded.Sha256);
    }

    private static ComponentDecision Decide(string name, FileSetComponent? offered, InstalledComponent installed)
    {
        if (offered is null)
        {
            return new ComponentDecision(name, ComponentAction.Skip, installed.Version, null, "not part of this release");
        }

        if (!installed.Installed)
        {
            // Installing a privileged component for the first time is a decision the owner takes at
            // an elevated terminal, not something an update does on their behalf.
            return new ComponentDecision(name, ComponentAction.Skip, null, offered.Version, "not installed on this PC");
        }

        if (string.Equals(installed.Version, offered.Version, StringComparison.OrdinalIgnoreCase))
        {
            return new ComponentDecision(name, ComponentAction.Skip, installed.Version, offered.Version, "unchanged");
        }

        return new ComponentDecision(name, ComponentAction.Update, installed.Version, offered.Version, "a new version is available");
    }
}
