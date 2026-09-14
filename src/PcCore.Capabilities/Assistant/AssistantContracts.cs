using System.Text.Json.Serialization;

namespace PcCore.Capabilities.Assistant;

public sealed class AssistantBridgeOptions
{
    public const string SectionName = "PcCore:Assistant";

    /// <summary>Off unless the owner turns it on. While off, nothing tries to reach PC Assistant.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The PC Assistant executable this PC Core trusts, as a full path. The process serving PC
    /// Assistant's pipe must be running exactly this file, or nothing is sent to it: a program that
    /// took the pipe name first would otherwise receive the owner's prompts.
    /// </summary>
    public string? ServerExecutable { get; set; }

    /// <summary>How long to wait for PC Assistant's pipe before calling it not running.</summary>
    public int ConnectTimeoutMilliseconds { get; set; } = 1500;

    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// How often the streaming text of one message is forwarded to a phone. PC Assistant sends a
    /// fresh snapshot of a growing message up to sixteen times a second, which is right for a local
    /// window and wasteful over a phone link; the latest snapshot always arrives.
    /// </summary>
    public int MessageIntervalMilliseconds { get; set; } = 250;
}

/// <summary>Whether the phone can use PC Assistant, and if not, which of the reasons it cannot.</summary>
public enum AssistantAvailability
{
    /// <summary><c>PcCore:Assistant:Enabled</c> is false.</summary>
    [JsonStringEnumMemberName("DISABLED")]
    Disabled,

    /// <summary>Enabled, but no trusted PC Assistant executable is configured.</summary>
    [JsonStringEnumMemberName("NOT_CONFIGURED")]
    NotConfigured,

    /// <summary>No PC Assistant is listening: it is closed, or its Nexus bridge is turned off.</summary>
    [JsonStringEnumMemberName("NOT_RUNNING")]
    NotRunning,

    /// <summary>
    /// One side refused the other: PC Assistant has not trusted this PC Core yet, or the process on
    /// the pipe is not the PC Assistant this PC Core trusts.
    /// </summary>
    [JsonStringEnumMemberName("NOT_TRUSTED")]
    NotTrusted,

    /// <summary>PC Assistant speaks a different bridge protocol version.</summary>
    [JsonStringEnumMemberName("INCOMPATIBLE")]
    Incompatible,

    /// <summary>PC Assistant is reachable, but Claude Code is not installed, not signed in, or failing.</summary>
    [JsonStringEnumMemberName("CLAUDE_NOT_READY")]
    ClaudeNotReady,

    [JsonStringEnumMemberName("READY")]
    Ready,

    /// <summary>Something else went wrong talking to PC Assistant. The detail says what.</summary>
    [JsonStringEnumMemberName("UNAVAILABLE")]
    Unavailable,
}

public sealed record AssistantStatus
{
    public required AssistantAvailability State { get; init; }

    /// <summary>One sentence for the owner. Never a path, a prompt or a reply.</summary>
    public required string Detail { get; init; }

    public string? ServerVersion { get; init; }

    public int? ProtocolVersion { get; init; }

    /// <summary>PC Assistant's word for Claude Code: ready, signed_out, not_installed, failed, unknown.</summary>
    public string? Claude { get; init; }

    public string? ClaudeVersion { get; init; }

    /// <summary>Only in this state may tasks be created or messages sent.</summary>
    public bool CanWork => State == AssistantAvailability.Ready;
}

// --- The shapes Nexus relays --------------------------------------------------------------------
//
// Field for field what PC Assistant sends, re-serialised by Nexus rather than passed through: whatever
// PC Assistant adds in a later version does not reach the phone until someone names it here.

public sealed record AssistantTaskSummary
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;

    /// <summary>general or workspace.</summary>
    public string Kind { get; init; } = "general";

    /// <summary>auto or ask.</summary>
    public string Mode { get; init; } = "auto";

    /// <summary>draft, running, waiting_for_approval, completed, failed, cancelled, interrupted.</summary>
    public string State { get; init; } = "draft";
    public bool Running { get; init; }
    public int PendingApprovals { get; init; }

    /// <summary>A workspace task's folder name. Never its path.</summary>
    public string? WorkspaceName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? LastError { get; init; }
}

public sealed record AssistantMessage
{
    public Guid Id { get; init; }

    /// <summary>owner, assistant or system.</summary>
    public string Role { get; init; } = "system";
    public string Text { get; init; } = string.Empty;
    public bool Truncated { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record AssistantProgress
{
    public Guid Id { get; init; }

    /// <summary>task_started, tool_started, tool_completed, tool_failed, approval_required, … task_completed.</summary>
    public string Kind { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Detail { get; init; }
    public string? ToolName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record AssistantApproval
{
    public Guid Id { get; init; }
    public Guid TaskId { get; init; }
    public string? TaskTitle { get; init; }
    public string ToolName { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;

    /// <summary>False for administrator operations, which PC Assistant accepts only at the PC.</summary>
    public bool RemoteResolvable { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record AssistantTaskDetail
{
    public required AssistantTaskSummary Task { get; init; }
    public IReadOnlyList<AssistantMessage> Messages { get; init; } = [];
    public IReadOnlyList<AssistantProgress> Progress { get; init; } = [];
    public IReadOnlyList<AssistantApproval> PendingApprovals { get; init; } = [];
    public bool OlderOmitted { get; init; }
}

public sealed record AssistantTaskList(IReadOnlyList<AssistantTaskSummary> Tasks);

public sealed record AssistantTaskResult(AssistantTaskSummary Task);

public sealed record AssistantStopResult(bool WasRunning);

public sealed record AssistantResolveResult(bool Resolved, string Outcome);

public sealed record AssistantSubscription(bool Subscribed, AssistantStatus Status);

// --- Events the phone receives -------------------------------------------------------------------

public static class AssistantEvents
{
    public const string Prefix = "assistant.";
    public const string StatusChanged = "assistant.status_changed";
    public const string TaskChanged = "assistant.task_changed";
    public const string Message = "assistant.message";
    public const string Progress = "assistant.progress";
    public const string ApprovalRequested = "assistant.approval_requested";
    public const string ApprovalResolved = "assistant.approval_resolved";
}

public sealed record AssistantMessageEvent(Guid TaskId, AssistantMessage Message);

public sealed record AssistantProgressEvent(Guid TaskId, AssistantProgress Event);

public sealed record AssistantApprovalRequestedEvent(AssistantApproval Approval);

public sealed record AssistantApprovalResolvedEvent(Guid ApprovalId, Guid? TaskId, string Outcome);
