namespace PcAssistant.Core;

/// <summary>
/// Every file PC Assistant owns lives under one clearly-named application directory.
/// Nothing is written to the owner's project directories except by Claude, inside a workspace.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PcAssistant");

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(GeneralSandbox);
        Directory.CreateDirectory(RawLogs);
        Directory.CreateDirectory(Runtime);
    }

    public string Root { get; }

    /// <summary>Working directory for General tasks, so discussion never needs a project.</summary>
    public string GeneralSandbox => Path.Combine(Root, "general");

    public string RawLogs => Path.Combine(Root, "rawlogs");

    /// <summary>Generated Claude settings / hook config for a run. Recreated each launch.</summary>
    public string Runtime => Path.Combine(Root, "runtime");

    public string DatabaseFile => Path.Combine(Root, "pcassistant.db");

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string RawLogFor(Guid taskId) => Path.Combine(RawLogs, taskId.ToString("N") + ".log");
}
