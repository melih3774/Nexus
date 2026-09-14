namespace PcAssistant.Core.Model;

/// <summary>
/// One line of the owner-facing privileged history: what was asked for, what the owner
/// answered, and what actually happened.
///
/// Deliberately not stored: the command's output. Elevated commands print credentials,
/// tokens and licence keys often enough that keeping their output in an ordinary local
/// database is a worse trade than losing the detail. The exit code and the helper's own
/// message are enough to know how it ended.
/// </summary>
public sealed class PrivilegedOperationRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid? TaskId { get; init; }
    public string TaskTitle { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Terminal, Run, Launch, Service, Cancel.</summary>
    public string Operation { get; init; } = string.Empty;

    /// <summary>The literal command, service or shell the owner saw on the approval card.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>Allowed, Denied, No answer, Cancelled, or Not asked.</summary>
    public string Approval { get; init; } = string.Empty;

    /// <summary>The helper's outcome, or "Not run" when nothing was sent.</summary>
    public string Result { get; init; } = string.Empty;

    public int? ExitCode { get; init; }

    /// <summary>Short explanation. Never command output.</summary>
    public string? Detail { get; init; }

    public string Summary => $"{CreatedAt:yyyy-MM-dd HH:mm}  {Operation}  {Target}" +
                             $"  [{Approval} / {Result}{(ExitCode is { } code ? ", exit " + code : "")}]";
}
