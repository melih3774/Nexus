namespace PcAssistant.Core.Policy;

/// <summary>Exact known tool names only. New tools fail closed until categorized.</summary>
public static class PcToolPolicy
{
    public const string Prefix = "mcp__pc-assistant__";
    private static readonly HashSet<string> Observations = new(StringComparer.Ordinal)
    {
        "windows", "monitors", "screenshot", "clipboard_read", "processes", "system_info",
        "recent_files", "installed_apps", "ui_snapshot",
        "capture_window", "capture_foreground", "capture_monitor", "ui_find",
        "audio_status", "power_status", "network_status", "recycle_bin",
    };
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "window_action", "clipboard_write", "open_url", "open_document", "open_app",
        "audio_set", "open_settings", "open_folder", "open_in_app",
    };
    private static readonly HashSet<string> Inputs = new(StringComparer.Ordinal)
    {
        "ui_action", "input",
    };

    /// <summary>
    /// Administrator territory. Kept in its own set because elevation is a stronger boundary
    /// than any desktop action: it never becomes automatic in AUTO and never earns a task grant.
    /// </summary>
    private static readonly HashSet<string> Privileged = new(StringComparer.Ordinal)
    {
        "elevated_request", "elevated_status",
    };

    public static bool IsKnown(string tool) => tool.StartsWith(Prefix, StringComparison.Ordinal) &&
        (Observations.Contains(tool[Prefix.Length..]) || Actions.Contains(tool[Prefix.Length..]) ||
         Inputs.Contains(tool[Prefix.Length..]) || Privileged.Contains(tool[Prefix.Length..]));

    public static RiskAssessment? Classify(ToolInvocation call, BrowserContext? context = null)
    {
        if (call.ToolName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var name = call.ToolName[Prefix.Length..];
            var category =
                name == "elevated_status" ? RiskCategory.Read :
                Privileged.Contains(name) ? RiskCategory.Elevation :
                Observations.Contains(name) ? RiskCategory.Read :
                Actions.Contains(name) ? RiskCategory.DesktopAction :
                Inputs.Contains(name) ? RiskCategory.InteractiveInput : RiskCategory.CommandHighImpact;
            if (name == "window_action" && string.Equals(call.GetString("action"), "close", StringComparison.OrdinalIgnoreCase))
                category = RiskCategory.InteractiveInput;
            if (name == "ui_action" && call.GetString("action") is { } uiAction &&
                uiAction.Equals("setValue", StringComparison.OrdinalIgnoreCase))
                category = RiskCategory.DesktopAction;
            if (name == "input" && call.GetString("action") is { } inputAction &&
                inputAction.Equals("typeText", StringComparison.OrdinalIgnoreCase))
                category = RiskCategory.DesktopAction;
            var target = string.Join(", ", call.Input.Select(p => p.Key + "=" + p.Value.ToString()));
            return new(category, RiskClassifier.Shorten(target, 1500), call.ToolName);
        }
        var browser = BrowserActionPolicy.Classify(call, context);
        if (browser is null) return null;
        return new RiskAssessment(browser.Value,
            RiskClassifier.Shorten(string.Join(", ", call.Input.Select(p => p.Key + "=" + p.Value)), 1500), call.ToolName);
    }

    public static (string? Key, string? Description) Scope(ToolInvocation call, RiskCategory category)
    {
        if (category == RiskCategory.WorkspaceWrite)
            return ("workspace-edits:" + Path.GetFullPath(call.WorkingDirectory).ToUpperInvariant(),
                "Allow normal file edits under " + call.WorkingDirectory + " for this task (until Stop or app exit).");
        if (category == RiskCategory.DesktopAction && IsKnown(call.ToolName))
            return (call.ToolName, "Allow " + call.ToolName[Prefix.Length..].Replace('_', ' ') + " for this task (until Stop or app exit).");
        // Generic input can execute commands, send messages, or purchase. No persistent grant.
        return (null, null);
    }
}
