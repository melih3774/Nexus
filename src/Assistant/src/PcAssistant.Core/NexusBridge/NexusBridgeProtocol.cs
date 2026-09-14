using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using PcAssistant.Core.Approvals;
using PcAssistant.Core.Bridge;
using PcAssistant.Core.Model;
using PcAssistant.Core.Policy;

namespace PcAssistant.Core.NexusBridge;

/// <summary>
/// The local API Nexus PC Core uses to reach PC Assistant. One JSON object per line, in both
/// directions, over an owner-only named pipe that exists only while the owner has turned the bridge
/// on.
///
///     client → server   {"type":"request","id":"7","method":"tasks.list","params":{}}
///     server → client   {"type":"response","id":"7","ok":true,"result":{...}}
///                       {"type":"response","id":"7","ok":false,"error":{"code":"not_found","message":"..."}}
///                       {"type":"event","event":"task.changed","data":{...}}
///
/// Deliberately small. Nexus gets tasks, their conversation, their progress and their approval
/// cards, and the four things an owner does with them. It does not get PC Assistant's settings,
/// workspaces, raw logs, policy, tools, or anything about how Claude Code is run — and PC Assistant
/// learns nothing about Nexus beyond the one executable it has been told to trust.
/// </summary>
public static class NexusBridgeProtocol
{
    /// <summary>The only version this build speaks. <c>hello</c> refuses any other.</summary>
    public const int Version = 1;

    /// <summary>
    /// Per user, so two people signed in to one PC never meet on the same name. The protocol version
    /// is negotiated in <c>hello</c> rather than baked in here, so a mismatched Nexus is told it is
    /// incompatible instead of concluding PC Assistant is not running.
    /// </summary>
    public static string PipeName(string ownerSid) => "PcAssistant.NexusBridge." + ownerSid;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string CurrentPipeName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return PipeName(identity.User?.Value ?? "unknown");
    }

    /// <summary>The executable name a Nexus client must have to be offered for trust at all.</summary>
    public const string NexusExecutableName = "PcCore.Host.exe";

    /// <summary>One request line. Larger than any prompt a phone sends; far smaller than a flood.</summary>
    public const int MaxRequestBytes = 256 * 1024;

    /// <summary>What a prompt may carry. The desktop composer has no limit, but a phone keyboard does.</summary>
    public const int MaxPromptChars = 32_000;

    /// <summary>Per message on the wire. Longer ones are cut and marked, never silently.</summary>
    public const int MaxMessageChars = 64_000;

    public const int MaxDetailChars = 4_000;

    /// <summary>Roughly what one <c>tasks.get</c> may cost. Older messages are omitted first.</summary>
    public const int TranscriptBudgetChars = 1_000_000;

    public static class Methods
    {
        public const string Hello = "hello";
        public const string Status = "status";
        public const string TasksList = "tasks.list";
        public const string TasksGet = "tasks.get";
        public const string TasksCreate = "tasks.create";
        public const string TasksSend = "tasks.send";
        public const string TasksStop = "tasks.stop";
        public const string TasksSetMode = "tasks.set_mode";
        public const string ApprovalsResolve = "approvals.resolve";
    }

    public static class Events
    {
        public const string TaskChanged = "task.changed";
        public const string MessageAppended = "message.appended";
        public const string TaskProgress = "task.progress";
        public const string ApprovalRequested = "approval.requested";
        public const string ApprovalResolved = "approval.resolved";
    }

    public static class Errors
    {
        public const string InvalidRequest = "invalid_request";
        public const string InvalidParams = "invalid_params";
        public const string UnknownMethod = "unknown_method";
        public const string HelloRequired = "hello_required";
        public const string UnsupportedProtocol = "unsupported_protocol_version";
        public const string ClientNotTrusted = "client_not_trusted";
        public const string NotFound = "not_found";
        public const string TaskBusy = "task_busy";
        public const string Unsupported = "unsupported";
        public const string RequiresLocalApproval = "requires_local_approval";
        public const string Internal = "internal_error";
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    // ------------------------------------------------------------ vocabulary

    public static string Kind(TaskKind kind) => kind == TaskKind.Workspace ? "workspace" : "general";

    public static string Mode(AssistantMode mode) => mode == AssistantMode.Ask ? "ask" : "auto";

    public static AssistantMode? ParseMode(string? mode) => mode?.ToLowerInvariant() switch
    {
        "auto" => AssistantMode.Auto,
        "ask" => AssistantMode.Ask,
        _ => null,
    };

    public static string State(TaskState state) => state switch
    {
        TaskState.Draft => "draft",
        TaskState.Running => "running",
        TaskState.WaitingForApproval => "waiting_for_approval",
        TaskState.Completed => "completed",
        TaskState.Failed => "failed",
        TaskState.Cancelled => "cancelled",
        TaskState.Interrupted => "interrupted",
        _ => "unknown",
    };

    public static string Role(MessageRole role) => role switch
    {
        MessageRole.Owner => "owner",
        MessageRole.Assistant => "assistant",
        _ => "system",
    };

    public static string EventKind(AssistantEventKind kind) => JsonNamingPolicy.SnakeCaseLower.ConvertName(kind.ToString());

    public static string Category(RiskCategory category) => JsonNamingPolicy.SnakeCaseLower.ConvertName(category.ToString());

    public static string Outcome(ApprovalOutcome outcome) => outcome switch
    {
        ApprovalOutcome.Allowed => "allowed",
        ApprovalOutcome.Denied => "denied",
        ApprovalOutcome.TimedOut => "timed_out",
        _ => "cancelled",
    };

    public static string Claude(ClaudeAvailability availability) => availability switch
    {
        ClaudeAvailability.Ready => "ready",
        ClaudeAvailability.NotInstalled => "not_installed",
        ClaudeAvailability.AuthenticationRequired => "signed_out",
        ClaudeAvailability.FailedToStart => "failed",
        _ => "unknown",
    };

    /// <summary>
    /// Whether the phone may answer this card. Administrator operations stay on the PC in v1: they
    /// already have their own flow, with the privilege, the consequence and Claude's justification
    /// laid out, and that decision is not one to make from a notification-sized card.
    /// </summary>
    public static bool IsRemotelyResolvable(ApprovalRequest request) =>
        request.Category != RiskCategory.Elevation && request.Privilege is null;

    /// <summary>Progress the phone shows. Assistant text arrives as messages, so it is not repeated here.</summary>
    public static bool IsProgress(AssistantEvent evt) =>
        evt.Kind is not (AssistantEventKind.AssistantMessage or AssistantEventKind.AssistantMessageDelta);

    public static (string Text, bool Truncated) Cap(string? text, int max)
    {
        text ??= string.Empty;
        return text.Length <= max ? (text, false) : (text[..max], true);
    }
}

// ----------------------------------------------------------------- wire shapes

public sealed record BridgeError(string Code, string Message);

public sealed record BridgeResponse
{
    public string Type => "response";
    public string? Id { get; init; }
    public bool Ok { get; init; }
    public object? Result { get; init; }
    public BridgeError? Error { get; init; }
}

public sealed record BridgeEvent(string Event, object Data)
{
    public string Type => "event";
}

public sealed record BridgeTask
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public required string Kind { get; init; }
    public required string Mode { get; init; }
    public required string State { get; init; }
    public bool Running { get; init; }
    public int PendingApprovals { get; init; }

    /// <summary>The folder's name for a workspace task. Never the full path.</summary>
    public string? WorkspaceName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? LastError { get; init; }
}

public sealed record BridgeMessage
{
    public required Guid Id { get; init; }
    public required string Role { get; init; }
    public required string Text { get; init; }
    public bool Truncated { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record BridgeProgress
{
    public required Guid Id { get; init; }
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public string? ToolName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record BridgeApproval
{
    public required Guid Id { get; init; }
    public required Guid TaskId { get; init; }
    public string? TaskTitle { get; init; }
    public required string ToolName { get; init; }
    public required string Action { get; init; }
    public required string Target { get; init; }
    public required string Reason { get; init; }
    public required string Category { get; init; }

    /// <summary>False for administrator operations, which are answered on the PC.</summary>
    public bool RemoteResolvable { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record BridgeClaudeStatus(string Availability, string? Version, string? Detail);

public sealed record HelloResult
{
    public required int ProtocolVersion { get; init; }
    public required string Server { get; init; }
    public required string ServerVersion { get; init; }
    public required BridgeClaudeStatus Claude { get; init; }
}

public sealed record StatusResult(BridgeClaudeStatus Claude, int RunningTasks, int PendingApprovals);

public sealed record TaskListResult(IReadOnlyList<BridgeTask> Tasks);

public sealed record TaskDetailResult
{
    public required BridgeTask Task { get; init; }
    public required IReadOnlyList<BridgeMessage> Messages { get; init; }
    public required IReadOnlyList<BridgeProgress> Progress { get; init; }
    public required IReadOnlyList<BridgeApproval> PendingApprovals { get; init; }

    /// <summary>True when older messages or progress were left out to keep the answer bounded.</summary>
    public bool OlderOmitted { get; init; }
}

public sealed record TaskResult(BridgeTask Task);

public sealed record StopResult(bool WasRunning);

public sealed record ResolveResult(bool Resolved, string Outcome);

public sealed record MessageAppendedEvent(Guid TaskId, BridgeMessage Message);

public sealed record ProgressEvent(Guid TaskId, BridgeProgress Event);

public sealed record ApprovalResolvedEvent(Guid ApprovalId, Guid? TaskId, string Outcome);
