namespace PcAssistant.Core.Model;

/// <summary>
/// One unit of work with its own conversation and context. Tasks are never merged into
/// a single global conversation.
/// </summary>
public sealed class AssistantTask
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = "Untitled task";
    public TaskKind Kind { get; init; } = TaskKind.General;
    public AssistantMode Mode { get; set; } = AssistantMode.Auto;

    /// <summary>Canonical absolute directory the Claude process runs in.</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>Owner-selected workspace for <see cref="TaskKind.Workspace"/> tasks; null otherwise.</summary>
    public string? WorkspacePath { get; set; }

    public TaskState State { get; set; } = TaskState.Draft;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Claude Code session id used with --session-id / --resume. Owned by PC Assistant.</summary>
    public string? ClaudeSessionId { get; set; }

    /// <summary>True once at least one turn has run, so follow-ups must use --resume.</summary>
    public bool HasClaudeSession { get; set; }

    public string? RawLogPath { get; set; }

    /// <summary>Set when this task was branched off another via "Continue as New Task".</summary>
    public Guid? ContinuedFromTaskId { get; set; }

    public string? LastError { get; set; }
}

public sealed class TaskMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TaskId { get; init; }
    public MessageRole Role { get; init; }
    public string Text { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed class AssistantEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TaskId { get; init; }
    public AssistantEventKind Kind { get; init; }

    /// <summary>Short line shown in the progress list.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Optional longer text revealed under "Details".</summary>
    public string? Detail { get; init; }

    public string? ToolName { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}
