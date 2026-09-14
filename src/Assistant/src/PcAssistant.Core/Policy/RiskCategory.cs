namespace PcAssistant.Core.Policy;

/// <summary>
/// Deliberately small set of buckets. Each real Claude Code tool call is mapped into
/// exactly one of these; the mode matrix then decides allow vs ask.
/// </summary>
public enum RiskCategory
{
    /// <summary>Reading, searching, inspecting. Never mutates anything.</summary>
    Read = 0,
    /// <summary>Creating or editing a file inside the task's working directory.</summary>
    WorkspaceWrite = 1,
    /// <summary>Ordinary development command (build, test, lint, read-only git).</summary>
    CommandSafe = 2,
    /// <summary>Bulk deletion, disk operations, or other hard-to-undo shell work.</summary>
    CommandHighImpact = 3,
    /// <summary>Touches a path outside the task's working directory.</summary>
    OutsideWorkspace = 4,
    /// <summary>Registry, services, scheduled tasks, startup entries, package installs.</summary>
    SystemChange = 5,
    /// <summary>Requests administrator rights.</summary>
    Elevation = 6,
    /// <summary>Git operations that publish, rewrite history, or discard work.</summary>
    DestructiveGit = 7,
    /// <summary>Killing or manipulating processes.</summary>
    ProcessControl = 8,
    DesktopAction = 9,
    InteractiveInput = 10,
}

public enum PolicyDecision
{
    Allow = 0,
    Ask = 1,
}

public static class RiskCategoryText
{
    public static string Describe(RiskCategory c) => c switch
    {
        RiskCategory.Read => "Read / inspect",
        RiskCategory.WorkspaceWrite => "Write in workspace",
        RiskCategory.CommandSafe => "Development command",
        RiskCategory.CommandHighImpact => "High-impact command",
        RiskCategory.OutsideWorkspace => "Outside workspace",
        RiskCategory.SystemChange => "System change",
        RiskCategory.Elevation => "Administrator elevation",
        RiskCategory.DestructiveGit => "Destructive Git operation",
        RiskCategory.ProcessControl => "Process control",
        RiskCategory.DesktopAction => "Desktop action",
        RiskCategory.InteractiveInput => "Interactive application action",
        _ => c.ToString(),
    };
}
