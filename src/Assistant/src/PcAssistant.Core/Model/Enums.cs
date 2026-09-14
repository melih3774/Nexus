namespace PcAssistant.Core.Model;

/// <summary>What a task is for. Decides whether a project directory is required.</summary>
public enum TaskKind
{
    /// <summary>Discussion, planning, research. Runs in PC Assistant's own sandbox directory.</summary>
    General = 0,
    /// <summary>Real work inside an owner-selected project directory.</summary>
    Workspace = 1,
}

/// <summary>The two owner-facing execution modes.</summary>
public enum AssistantMode
{
    /// <summary>Routine work proceeds; only genuinely high-impact operations ask.</summary>
    Auto = 0,
    /// <summary>Reading is free; anything that mutates or executes asks first.</summary>
    Ask = 1,
}

public enum TaskState
{
    Draft = 0,
    Running = 1,
    WaitingForApproval = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
    /// <summary>PC Assistant exited while this task was running. Honest state after restart.</summary>
    Interrupted = 6,
}

public enum MessageRole
{
    Owner = 0,
    Assistant = 1,
    System = 2,
}

/// <summary>Provider-neutral event kinds surfaced to the UI.</summary>
public enum AssistantEventKind
{
    TaskStarted = 0,
    AssistantMessage = 1,
    Progress = 2,
    ToolStarted = 3,
    ToolCompleted = 4,
    ToolFailed = 5,
    ApprovalRequired = 6,
    ApprovalResolved = 7,
    Question = 8,
    TaskCompleted = 9,
    TaskFailed = 10,
    TaskCancelled = 11,
    /// <summary>Text fragment; Id identifies its message until an AssistantMessage completes it.</summary>
    AssistantMessageDelta = 12,
}
