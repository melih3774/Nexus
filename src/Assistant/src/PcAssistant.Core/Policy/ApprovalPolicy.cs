using PcAssistant.Core.Model;

namespace PcAssistant.Core.Policy;

/// <summary>
/// The whole mode matrix, in one place.
///
/// AUTO  - routine project work proceeds; genuinely high-impact operations still ask.
/// ASK   - reading and inspecting is free; anything that mutates or executes asks first.
///
/// There is no third "allow everything" mode, and PC Assistant never passes
/// --dangerously-skip-permissions to Claude Code.
/// </summary>
public static class ApprovalPolicy
{
    public static PolicyDecision Evaluate(AssistantMode mode, RiskCategory category) => mode switch
    {
        AssistantMode.Auto => category switch
        {
            RiskCategory.Read => PolicyDecision.Allow,
            RiskCategory.WorkspaceWrite => PolicyDecision.Allow,
            RiskCategory.CommandSafe => PolicyDecision.Allow,
            RiskCategory.DesktopAction => PolicyDecision.Allow,
            _ => PolicyDecision.Ask,
        },

        AssistantMode.Ask => category switch
        {
            RiskCategory.Read => PolicyDecision.Allow,
            _ => PolicyDecision.Ask,
        },

        _ => PolicyDecision.Ask,
    };

    /// <summary>Owner-facing explanation of why an approval card appeared.</summary>
    public static string ExplainAsk(AssistantMode mode, RiskCategory category) => (mode, category) switch
    {
        (AssistantMode.Ask, RiskCategory.WorkspaceWrite) =>
            "ASK mode: changes to files need your approval.",
        (AssistantMode.Ask, RiskCategory.CommandSafe) =>
            "ASK mode: running commands needs your approval.",
        (_, RiskCategory.OutsideWorkspace) =>
            "This touches a path outside the task's workspace.",
        (_, RiskCategory.SystemChange) =>
            "This changes system-level configuration or installs software.",
        (_, RiskCategory.Elevation) =>
            "This requests administrator rights.",
        (_, RiskCategory.DestructiveGit) =>
            "Git operations that publish, rewrite history, or discard work are never automatic.",
        (_, RiskCategory.ProcessControl) =>
            "This stops or manipulates processes.",
        (_, RiskCategory.CommandHighImpact) =>
            "This command looks hard to undo.",
        (_, RiskCategory.InteractiveInput) =>
            "This interacts with an application or website. Review the target and input before allowing it.",
        (_, RiskCategory.DesktopAction) =>
            "ASK mode: this changes the desktop, opens an application, or writes the clipboard.",
        _ => "This operation needs your approval.",
    };
}
