using System.Text.RegularExpressions;
using PcAssistant.Core.Workspaces;

namespace PcAssistant.Core.Policy;

/// <summary>
/// Maps a tool call to a <see cref="RiskCategory"/>.
///
/// Design note: this is deliberately not a shell parser. It is a denylist scan over the
/// raw command text plus a containment check on path arguments. Anything that matches a
/// danger marker is escalated; only commands with no marker at all are treated as routine.
/// That errs toward asking, which is the safe direction.
/// </summary>
public sealed partial class RiskClassifier
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // --- Elevation -------------------------------------------------------
    [GeneratedRegex(@"(?:^|[\s;&|(])(?:sudo|gsudo|runas)(?:\s|$)|-Verb\s+RunAs", Opts)]
    private static partial Regex ElevationRx();

    // --- System change ---------------------------------------------------
    [GeneratedRegex(
        @"(?:^|[\s;&|(])reg(?:\.exe)?\s+(?:add|delete|import|copy)\b" +
        @"|HKLM:|HKCU:|HKCR:|HKEY_LOCAL_MACHINE|HKEY_CURRENT_USER" +
        @"|\b(?:New|Set|Remove)-ItemProperty\b" +
        @"|(?:^|[\s;&|(])(?:bcdedit|netsh|diskpart|wmic|setx|vssadmin|cipher|takeown|icacls)\b" +
        @"|(?:^|[\s;&|(])sc(?:\.exe)?\s+(?:config|create|delete|start|stop)\b" +
        @"|(?:^|[\s;&|(])schtasks\b|\bRegister-ScheduledTask\b|\bUnregister-ScheduledTask\b" +
        @"|\bSet-ExecutionPolicy\b|\b(?:New|Set|Remove|Stop|Start|Restart)-Service\b" +
        @"|\b(?:Add|Set|Remove)-MpPreference\b|\bSet-NetFirewall|\bNew-NetFirewallRule\b" +
        @"|shell:startup|\\CurrentVersion\\Run\b" +
        @"|(?:^|[\s;&|(])(?:winget|choco|scoop)\s+(?:install|uninstall|upgrade)\b" +
        @"|(?:^|[\s;&|(])npm\s+(?:install|i|uninstall|rm)\b[^\n|;&]*(?:\s-g\b|\s--global\b)" +
        @"|(?:^|[\s;&|(])dotnet\s+tool\s+(?:install|uninstall|update)\b[^\n|;&]*(?:\s-g\b|\s--global\b)" +
        @"|(?:^|[\s;&|(])pip3?\s+install\b" +
        @"|(?:^|[\s;&|(])(?:apt|apt-get|yum|dnf|brew)\s+install\b", Opts)]
    private static partial Regex SystemChangeRx();

    // --- Destructive / publishing git ------------------------------------
    [GeneratedRegex(
        @"(?:^|[\s;&|(])git\s+(?:-[^\s]+\s+)*" +
        @"(?:push|commit|reset|clean|rebase|revert|filter-branch|update-ref|cherry-pick|am|apply" +
        @"|merge|restore|checkout|switch|stash|remote|gc|prune|worktree|submodule|fetch|pull|init)\b" +
        @"|(?:^|[\s;&|(])git\s+tag\s+-d\b|(?:^|[\s;&|(])git\s+branch\s+-D?\b", Opts)]
    private static partial Regex GitMutatingRx();

    /// <summary>Read-only git subcommands stay routine in AUTO.</summary>
    [GeneratedRegex(
        @"(?:^|[\s;&|(])git\s+(?:-[^\s]+\s+)*" +
        @"(?:status|diff|log|show|blame|describe|rev-parse|ls-files|shortlog|whatchanged)\b" +
        @"|(?:^|[\s;&|(])git\s+branch\s*(?:$|-a|-r|-v|--list)" +
        @"|(?:^|[\s;&|(])git\s+remote\s+(?:-v|show)\b", Opts)]
    private static partial Regex GitReadOnlyRx();

    // --- Process control --------------------------------------------------
    [GeneratedRegex(
        @"(?:^|[\s;&|(])(?:taskkill|pkill|killall)\b|\bStop-Process\b" +
        @"|\bStop-Computer\b|\bRestart-Computer\b|(?:^|[\s;&|(])kill\s+-?\d", Opts)]
    private static partial Regex ProcessControlRx();

    // --- High-impact filesystem / disk ------------------------------------
    [GeneratedRegex(
        @"(?:^|[\s;&|(])rm\s+(?:-[a-z]*\s+)*-[a-z]*[rf]" +
        @"|\bRemove-Item\b[^\n]*(?:-Recurse|-Force)" +
        @"|\|\s*Remove-Item\b" +
        @"|(?:^|[\s;&|(])(?:rmdir|rd)\b[^\n]*/[sS]" +
        @"|(?:^|[\s;&|(])del\b[^\n]*/[sSqQ]" +
        @"|(?:^|[\s;&|(])(?:format|mkfs\S*|fdisk)\b" +
        @"|\bClear-Disk\b|\bFormat-Volume\b|\bInitialize-Disk\b" +
        @"|(?:^|[\s;&|(])dd\s+if=" +
        @"|\bInvoke-Expression\b|(?:^|[\s;&|(])iex\b", Opts)]
    private static partial Regex HighImpactRx();

    /// <summary>Absolute Windows paths and UNC shares appearing anywhere in a command.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/][^\s;&|<>]*|\\\\[^\s;&|<>]+)", Opts)]
    private static partial Regex AbsolutePathRx();

    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "Read", "Glob", "Grep", "LS", "WebFetch", "WebSearch", "TodoWrite", "Task",
        "NotebookRead", "ListMcpResources", "ReadMcpResource", "AskUserQuestion",
        "SlashCommand", "Skill", "ExitPlanMode", "BashOutput", "ToolSearch", "ListAgents",
        "TaskOutput", "Monitor",
    };

    /// <summary>
    /// Ending a background job this task started itself. Not process control in the sense the
    /// owner cares about: the only thing it can reach is a command the task already ran and
    /// the owner already saw. A real mixed-workflow run spent an approval card stopping its own
    /// background server, which is exactly the kind of noise that trains an owner to click
    /// Allow without reading.
    /// </summary>
    private static readonly HashSet<string> OwnBackgroundWorkTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "TaskStop", "KillShell", "KillBash",
    };

    private static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "Write", "Edit", "MultiEdit", "NotebookEdit", "StrReplace", "ApplyPatch",
    };

    private static readonly HashSet<string> ShellTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bash", "PowerShell", "Shell", "Terminal",
    };

    /// <summary>Extra directories that count as "inside" (e.g. the OS temp folder for build output).</summary>
    public IReadOnlyList<string> AdditionalAllowedRoots { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AdditionalReadRoots { get; init; } = Array.Empty<string>();

    /// <summary>
    /// What this task knows about the browser: which tabs it opened, and what the current
    /// page's elements are labelled. Absent means every ambiguous browser action asks, which
    /// is the old behaviour and the safe direction.
    /// </summary>
    public BrowserContext? Browser { get; init; }

    public RiskAssessment Classify(ToolInvocation call)
    {
        var tool = call.ToolName;

        if (PcToolPolicy.Classify(call, Browser) is { } pcAssessment) return pcAssessment;

        // MCP tools carry a server prefix; treat unknown ones as executing tools.
        if (tool.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase))
            return new RiskAssessment(RiskCategory.CommandHighImpact, "MCP tool " + tool, tool);

        if (OwnBackgroundWorkTools.Contains(tool))
            return new RiskAssessment(RiskCategory.CommandSafe, "Stop a background command this task started", tool);

        if (ShellTools.Contains(tool))
            return ClassifyCommand(call);

        if (WriteTools.Contains(tool))
        {
            var path = call.FilePath;
            if (path is null)
                return new RiskAssessment(RiskCategory.WorkspaceWrite, "File edit", tool);

            return IsInsideScope(call.WorkingDirectory, path)
                ? new RiskAssessment(RiskCategory.WorkspaceWrite, Shorten(path), tool)
                : new RiskAssessment(RiskCategory.OutsideWorkspace, Shorten(path), tool);
        }

        if (ReadOnlyTools.Contains(tool))
        {
            var path = call.FilePath;
            if (path is not null && Path.IsPathFullyQualified(path) &&
                !IsInsideScope(call.WorkingDirectory, path) &&
                !AdditionalReadRoots.Any(root => PathScope.IsInside(root, path)))
            {
                return new RiskAssessment(RiskCategory.OutsideWorkspace, Shorten(path), tool);
            }

            return new RiskAssessment(RiskCategory.Read, DescribeRead(call), tool);
        }

        // Unknown tool: conservative.
        return new RiskAssessment(RiskCategory.CommandHighImpact, "Unrecognized tool " + tool, tool);
    }

    private RiskAssessment ClassifyCommand(ToolInvocation call)
    {
        var cmd = call.Command;
        if (string.IsNullOrWhiteSpace(cmd))
            return new RiskAssessment(RiskCategory.CommandHighImpact, "Empty command", call.ToolName);

        var summary = Shorten(cmd, 160);

        if (ElevationRx().IsMatch(cmd))
            return new RiskAssessment(RiskCategory.Elevation, summary, call.ToolName);

        if (SystemChangeRx().IsMatch(cmd))
            return new RiskAssessment(RiskCategory.SystemChange, summary, call.ToolName);

        // Mutating git wins over the read-only pattern; a command containing both asks.
        if (GitMutatingRx().IsMatch(cmd))
            return new RiskAssessment(RiskCategory.DestructiveGit, summary, call.ToolName);

        if (ProcessControlRx().IsMatch(cmd))
            return new RiskAssessment(RiskCategory.ProcessControl, summary, call.ToolName);

        if (HighImpactRx().IsMatch(cmd))
            return new RiskAssessment(RiskCategory.CommandHighImpact, summary, call.ToolName);

        foreach (Match m in AbsolutePathRx().Matches(cmd))
        {
            var candidate = m.Value.Trim('"', '\'').TrimEnd('.', ',', ')');
            if (candidate.Length < 4) continue;
            if (!IsInsideScope(call.WorkingDirectory, candidate))
                return new RiskAssessment(RiskCategory.OutsideWorkspace, Shorten(candidate), call.ToolName);
        }

        return new RiskAssessment(RiskCategory.CommandSafe, summary, call.ToolName);
    }

    private bool IsInsideScope(string workingDirectory, string candidate)
    {
        if (PathScope.IsInside(workingDirectory, candidate)) return true;
        foreach (var root in AdditionalAllowedRoots)
        {
            if (PathScope.IsInside(root, candidate)) return true;
        }
        return false;
    }

    private static string DescribeRead(ToolInvocation call)
    {
        var p = call.FilePath;
        if (p is not null) return Shorten(p);
        var pattern = call.GetString("pattern", "query", "url");
        return pattern is not null ? Shorten(pattern) : call.ToolName;
    }

    internal static string Shorten(string s, int max = 90)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ");
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    /// <summary>Exposed so tests can assert the read-only git allowance explicitly.</summary>
    public static bool LooksLikeReadOnlyGit(string command) =>
        GitReadOnlyRx().IsMatch(command) && !GitMutatingRx().IsMatch(command);
}

public sealed record RiskAssessment(RiskCategory Category, string Target, string ToolName);
