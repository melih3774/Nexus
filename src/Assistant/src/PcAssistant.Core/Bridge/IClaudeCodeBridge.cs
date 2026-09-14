using PcAssistant.Core.Model;

namespace PcAssistant.Core.Bridge;

public enum ClaudeAvailability
{
    Unknown = 0,
    Ready = 1,
    NotInstalled = 2,
    AuthenticationRequired = 3,
    FailedToStart = 4,
}

public sealed record ClaudeStatus(
    ClaudeAvailability Availability,
    string? Version = null,
    string? ExecutablePath = null,
    string? Detail = null)
{
    public string DisplayText => Availability switch
    {
        ClaudeAvailability.Ready => "Ready",
        ClaudeAvailability.NotInstalled => "Claude Code not installed",
        ClaudeAvailability.AuthenticationRequired => "Claude Code authentication required",
        ClaudeAvailability.FailedToStart => "Claude Code failed to start",
        _ => "Checking…",
    };
}

/// <summary>One turn of a conversation: the owner said something, Claude works until it stops.</summary>
public sealed record BridgeTurnRequest
{
    public required Guid TaskId { get; init; }
    public required string Prompt { get; init; }
    public required string WorkingDirectory { get; init; }
    public required AssistantMode Mode { get; init; }

    /// <summary>Session id to create (first turn) or resume (later turns).</summary>
    public required string SessionId { get; init; }

    /// <summary>False on the first turn of a task, true for every follow-up.</summary>
    public bool Resume { get; init; }

    /// <summary>Shown on privileged approval cards so the owner knows which task is asking.</summary>
    public string TaskTitle { get; init; } = string.Empty;
}

public sealed record BridgeTurnResult(
    bool Success,
    bool Cancelled,
    string? SessionId,
    string? FinalText,
    string? Error);

/// <summary>
/// The only surface Core uses to reach Claude Code. Everything CLI-specific
/// (flags, stream-json shapes, hooks, process handling) lives behind this.
/// </summary>
public interface IClaudeCodeBridge
{
    Task<ClaudeStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<BridgeTurnResult> RunTurnAsync(
        BridgeTurnRequest request,
        Action<AssistantEvent> onEvent,
        CancellationToken cancellationToken);
}
