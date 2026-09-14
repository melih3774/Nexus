using PcAssistant.Core.Approvals;
using PcAssistant.Core.Model;
using PcAssistant.Core.Policy;
using PcAssistant.Desktop.Mvvm;

namespace PcAssistant.Desktop.ViewModels;

/// <summary>Anything that can appear in the task transcript.</summary>
public abstract class ConversationItem : ObservableObject
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string TimeText => Timestamp.ToString("HH:mm");
}

public sealed class MessageItem : ConversationItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required MessageRole Role { get; init; }
    private string _text = string.Empty;
    public required string Text
    {
        get => _text;
        set => SetField(ref _text, value);
    }

    public string Speaker => Role switch
    {
        MessageRole.Owner => "You",
        MessageRole.Assistant => "Claude",
        _ => "PC Assistant",
    };

    public bool IsOwner => Role == MessageRole.Owner;
    public bool IsAssistant => Role == MessageRole.Assistant;
    public bool IsSystem => Role == MessageRole.System;
}

/// <summary>A step in the structured progress list, not a terminal line.</summary>
public sealed class ProgressItem : ConversationItem
{
    public required AssistantEventKind Kind { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public string? ToolName { get; init; }

    /// <summary>
    /// What the owner sees in the progress list. PC Assistant's own tools are matched by their
    /// exact names rather than by substring: "capture_window" contains "window", and the older
    /// substring rules labelled an independent window capture as "Working with a window", which
    /// is both wrong and hides the one operation worth naming precisely.
    /// </summary>
    public string ActivityTitle
    {
        get
        {
            if (Kind is not (AssistantEventKind.ToolStarted or AssistantEventKind.ToolCompleted or AssistantEventKind.ToolFailed))
                return Title;

            var raw = ToolName ?? Title;
            var action = Native(raw) ?? Browser(raw) ?? Generic(raw.ToLowerInvariant()) ?? Title;

            return Kind switch
            {
                AssistantEventKind.ToolCompleted => action + " · complete",
                AssistantEventKind.ToolFailed => action + " · failed",
                _ => action,
            };
        }
    }

    private const string NativePrefix = "mcp__pc-assistant__";
    private const string BrowserPrefix = "mcp__claude-in-chrome__";

    private static string? Native(string tool) =>
        !tool.StartsWith(NativePrefix, StringComparison.Ordinal) ? null : tool[NativePrefix.Length..] switch
        {
            "capture_window" => "Looking at a window",
            "capture_foreground" => "Looking at the active window",
            "capture_monitor" or "screenshot" => "Observing the screen",
            "windows" => "Checking open windows",
            "monitors" => "Checking monitors",
            "window_action" => "Working with a window",
            "ui_snapshot" => "Reading application controls",
            "ui_find" => "Looking for a control",
            "ui_action" => "Operating a control",
            "input" => "Using mouse and keyboard",
            "clipboard_read" => "Reading clipboard",
            "clipboard_write" => "Updating clipboard",
            "processes" => "Inspecting processes",
            "system_info" => "Checking system state",
            "recent_files" => "Finding recent files",
            "installed_apps" => "Finding applications",
            "open_app" => "Opening an application",
            "open_settings" => "Opening Windows Settings",
            "open_folder" => "Opening a folder",
            "open_in_app" => "Opening a file in an application",
            "open_document" => "Opening a document",
            "open_url" => "Opening a link",
            "audio_status" => "Checking volume",
            "audio_set" => "Changing volume",
            "power_status" => "Checking power and battery",
            "network_status" => "Checking network",
            "recycle_bin" => "Checking the Recycle Bin",
            "elevated_status" => "Checking administrator access",
            "elevated_request" => "Administrator operation",
            _ => "Working on the PC",
        };

    private static string? Browser(string tool) =>
        !tool.StartsWith(BrowserPrefix, StringComparison.Ordinal) ? null : tool[BrowserPrefix.Length..] switch
        {
            "navigate" => "Browsing to a page",
            "read_page" or "get_page_text" => "Reading a page",
            "find" => "Looking for something on the page",
            "computer" => "Interacting with a page",
            "form_input" => "Filling in a field",
            "tabs_create_mcp" => "Opening a browser tab",
            "tabs_close_mcp" => "Closing a browser tab",
            "tabs_context_mcp" => "Checking browser tabs",
            "read_console_messages" => "Reading browser console",
            "read_network_requests" => "Reading network requests",
            "javascript_tool" => "Running script in the page",
            _ => "Working in the browser",
        };

    private string? Generic(string tool) => tool switch
    {
        "read" => "Reading file",
        "edit" or "write" or "multiedit" => "Updating file",
        "glob" or "grep" => "Finding files and text",
        "bash" or "powershell" when (Detail ?? "").Contains("build", StringComparison.OrdinalIgnoreCase) => "Running build",
        "bash" or "powershell" when (Detail ?? "").Contains("test", StringComparison.OrdinalIgnoreCase) => "Running tests",
        "bash" or "powershell" => "Running command",
        "websearch" => "Searching the web",
        "webfetch" => "Reading web page",
        "todowrite" => "Planning",
        "task" => "Working through a sub-task",
        _ => null,
    };

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    private bool _detailVisible;
    public bool DetailVisible
    {
        get => _detailVisible;
        set => SetField(ref _detailVisible, value);
    }

    public string Glyph => Kind switch
    {
        AssistantEventKind.TaskStarted => "▶",
        AssistantEventKind.ToolStarted => "→",
        AssistantEventKind.ToolCompleted => "✓",
        AssistantEventKind.ToolFailed => "✕",
        AssistantEventKind.ApprovalRequired => "!",
        AssistantEventKind.ApprovalResolved => "•",
        AssistantEventKind.TaskCompleted => "✓",
        AssistantEventKind.TaskFailed => "✕",
        AssistantEventKind.TaskCancelled => "■",
        _ => "•",
    };

    public bool IsFailure => Kind is AssistantEventKind.ToolFailed or AssistantEventKind.TaskFailed;
}

/// <summary>
/// An approval card. Deliberately distinct from a normal question: the owner should never
/// have to open the raw log to understand what is being approved.
/// </summary>
public sealed class ApprovalItem : ConversationItem
{
    public required ApprovalRequest Request { get; init; }
    public required Action<bool> Respond { get; init; }
    public Action? RespondForTask { get; init; }
    public bool CanAllowForTask => Request.ScopeKey is not null && RespondForTask is not null;
    public string? ScopeDescription => Request.ScopeDescription;

    public string Action => Request.Action;
    public string Target => Request.Target;
    public string Reason => Request.Reason;
    public string CategoryText => RiskCategoryText.Describe(Request.Category);
    public string ToolName => Request.ToolName;

    // Consequence-aware detail. Present on privileged cards; absent on ordinary ones, which
    // keeps a routine approval as short as it was before.
    public string? Privilege => Request.Privilege;
    public string? Consequence => Request.Consequence;
    public string? Justification => Request.Justification;
    public string? TaskTitle => Request.TaskTitle;

    public bool HasPrivilege => !string.IsNullOrWhiteSpace(Request.Privilege);
    public bool HasConsequence => !string.IsNullOrWhiteSpace(Request.Consequence);
    public bool HasJustification => !string.IsNullOrWhiteSpace(Request.Justification);
    public bool HasTaskTitle => !string.IsNullOrWhiteSpace(Request.TaskTitle);

    /// <summary>Administrator cards are visually distinct because the decision is different in kind.</summary>
    public bool IsElevation => Request.Category == RiskCategory.Elevation;

    private bool _resolved;
    public bool IsResolved
    {
        get => _resolved;
        private set => SetField(ref _resolved, value);
    }

    private string? _outcomeText;
    public string? OutcomeText
    {
        get => _outcomeText;
        private set => SetField(ref _outcomeText, value);
    }

    public RelayCommand AllowCommand => _allow ??= new RelayCommand(() => Answer(true), () => !IsResolved);
    public RelayCommand DenyCommand => _deny ??= new RelayCommand(() => Answer(false), () => !IsResolved);
    public RelayCommand AllowForTaskCommand => _allowForTask ??= new RelayCommand(() =>
    {
        if (IsResolved || !CanAllowForTask) return;
        RespondForTask!();
    }, () => !IsResolved && CanAllowForTask);

    private RelayCommand? _allow;
    private RelayCommand? _deny;
    private RelayCommand? _allowForTask;

    private void Answer(bool allow)
    {
        if (IsResolved) return;
        MarkResolved(allow ? "Allowed" : "Denied");
        Respond(allow);
    }

    /// <summary>Called when the broker resolved this some other way (timeout, cancel).</summary>
    public void MarkResolved(string outcome)
    {
        IsResolved = true;
        OutcomeText = outcome;
        _allow?.RaiseCanExecuteChanged();
        _deny?.RaiseCanExecuteChanged();
        _allowForTask?.RaiseCanExecuteChanged();
    }
}
