namespace PcCore.Update;

/// <summary>Where an update is, as the tray and the phone describe it.</summary>
public enum UpdateState
{
    Idle,
    Staging,
    WaitingForApproval,
    Installing,
    Restarting,
    Succeeded,
    Failed,
    RolledBack,
}

/// <summary>
/// <c>status.json</c>: the updater's account of the last update, for PC Core to show once it is
/// running again. The updater is the only writer.
/// </summary>
public sealed record UpdateStatus
{
    public UpdateState State { get; init; } = UpdateState.Idle;

    public int? Release { get; init; }

    public string? Version { get; init; }

    public string? PreviousVersion { get; init; }

    public string? Message { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }

    public bool InProgress => State is UpdateState.Staging or UpdateState.WaitingForApproval
        or UpdateState.Installing or UpdateState.Restarting;
}

/// <summary>Every path the update machinery uses, in one place so tests can move all of them at once.</summary>
public sealed record UpdatePaths(string InstallDirectory, string WorkDirectory)
{
    public const string HostExecutableName = "PcCore.Host.exe";
    public const string UpdaterExecutableName = "PcCore.Updater.exe";

    /// <summary>
    /// The named event a running PC Core waits on and stops when it is set. Session-local, so only
    /// something already running as this user in this session can set it — which could equally have
    /// just killed the process.
    /// </summary>
    public const string StopEventName = @"Local\Nexus.PcCore.Stop";

    /// <summary>Held while an update runs, so two cannot interleave.</summary>
    public const string UpdateMutexName = @"Local\Nexus.Updater";

    /// <summary>Nexus's per-user state, relative to <c>%LOCALAPPDATA%</c>. The update work directory and the release store are inside it.</summary>
    public const string StateFolder = "Nexus";

    /// <summary>The per-user install, relative to <c>%LOCALAPPDATA%</c>.</summary>
    public const string InstallFolder = @"Programs\Nexus";

    public string InstalledExecutable => Path.Combine(InstallDirectory, HostExecutableName);

    public string StagingRoot => Path.Combine(WorkDirectory, "staging");

    public string BackupDirectory => Path.Combine(WorkDirectory, "backup");

    public string JournalPath => Path.Combine(WorkDirectory, "journal.json");

    public string StatusPath => Path.Combine(WorkDirectory, "status.json");

    public string InstalledRecordPath => Path.Combine(WorkDirectory, "installed.json");

    /// <summary>Where PC Assistant is installed: a folder of PC Core's install directory that PC Core's own files never use.</summary>
    public string AssistantDirectory => Path.Combine(InstallDirectory, AssistantInstall.Folder);

    public string AssistantExecutable => Path.Combine(AssistantDirectory, AssistantInstall.ExecutableName);

    /// <summary>PC Assistant's file swap has its own journal and backup, so each component's undo stays its own.</summary>
    public string AssistantJournalPath => Path.Combine(WorkDirectory, "assistant-journal.json");

    public string AssistantBackupDirectory => Path.Combine(WorkDirectory, "assistant-backup");

    public string AssistantRecordPath => Path.Combine(WorkDirectory, "installed-assistant.json");

    public InstalledAssistantRecord? ReadAssistantRecord() => TryRead<InstalledAssistantRecord>(AssistantRecordPath);

    public string RunnerRoot => Path.Combine(WorkDirectory, "runner");

    public string LogPath => Path.Combine(WorkDirectory, "updater.log");

    /// <summary>
    /// The per-user install (<c>%LOCALAPPDATA%\Programs\Nexus</c>, where <c>--install-app</c> puts
    /// it) and the per-user work directory beside PC Core's own state.
    /// </summary>
    public static UpdatePaths ForCurrentUser() => ForLocalAppData(AppDataRedirection.LocalAppData);

    /// <summary>The same locations under another <c>%LOCALAPPDATA%</c>, the way a freshly started process resolves them.</summary>
    public static UpdatePaths ForLocalAppData(string localAppData) =>
        new(Path.Combine(localAppData, InstallFolder), Path.Combine(localAppData, StateFolder, "update"));

    public UpdateStatus? ReadStatus() => TryRead<UpdateStatus>(StatusPath);

    public InstalledRecord? ReadInstalledRecord() => TryRead<InstalledRecord>(InstalledRecordPath);

    public void WriteStatus(UpdateStatus status) => UpdateJson.WriteAtomic(StatusPath, status);

    private static T? TryRead<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? UpdateJson.Read<T>(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
