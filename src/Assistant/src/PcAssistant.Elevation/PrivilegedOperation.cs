namespace PcAssistant.Elevation;

/// <summary>
/// The complete privileged surface. Four operations, not dozens of one-off methods, and
/// deliberately not "run this string as SYSTEM".
/// </summary>
public enum PrivilegedOperationKind
{
    /// <summary>No privilege. Version negotiation and helper identity.</summary>
    Handshake = 0,
    /// <summary>No privilege. Reports session, owner and elevation availability.</summary>
    Status = 1,
    /// <summary>A visible interactive Administrator shell on the owner's own desktop.</summary>
    Terminal = 2,
    /// <summary>A program plus an argument vector, run elevated with output captured.</summary>
    Run = 3,
    /// <summary>A program plus an argument vector, launched elevated and visible; not captured.</summary>
    Launch = 4,
    /// <summary>Start, stop, restart or query one named Windows service.</summary>
    Service = 5,
    /// <summary>Cancel a still-running <see cref="Run"/> operation this client started.</summary>
    Cancel = 6,
}

public enum PrivilegedStatus
{
    /// <summary>The operation was performed. For <see cref="PrivilegedOperationKind.Run"/> this
    /// does not mean the command itself succeeded; see the exit code.</summary>
    Ok = 0,
    /// <summary>The helper's own validation rejected the request. Nothing ran.</summary>
    Invalid = 1,
    /// <summary>The caller is not the owner this helper was installed for, or not trusted.</summary>
    Denied = 2,
    /// <summary>Client and helper speak different protocol versions. Nothing ran.</summary>
    VersionMismatch = 3,
    /// <summary>The account has no administrator token, so no elevation is possible at all.</summary>
    NotElevatable = 4,
    /// <summary>Windows refused the launch or the operation errored. Details in Message.</summary>
    Failed = 5,
    /// <summary>The command exceeded its approved timeout and was terminated.</summary>
    TimedOut = 6,
    /// <summary>The owner or the task cancelled a running operation.</summary>
    Cancelled = 7,
}

/// <summary>
/// One request. Every field is bounded and re-validated by the helper; nothing here is
/// trusted because the client said so.
/// </summary>
public sealed record PrivilegedRequest
{
    public int Protocol { get; init; } = PrivilegedProtocol.Version;
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Client clock. Used only to reject stale/replayed requests, never for authorization.</summary>
    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow;

    public PrivilegedOperationKind Kind { get; init; }

    /// <summary>PC Assistant task this belongs to. Recorded in the audit trail.</summary>
    public string? TaskId { get; init; }
    public string? TaskTitle { get; init; }

    /// <summary>Claude's stated reason. Shown to the owner; never treated as authorization.</summary>
    public string? Reason { get; init; }

    // --- Terminal ---------------------------------------------------------
    /// <summary>powershell | pwsh | cmd</summary>
    public string? Shell { get; init; }
    public string? InitialCommand { get; init; }

    // --- Run / Launch -----------------------------------------------------
    public string? Executable { get; init; }
    public IReadOnlyList<string>? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public int TimeoutSeconds { get; init; }

    // --- Service ----------------------------------------------------------
    public string? ServiceName { get; init; }
    /// <summary>start | stop | restart | query</summary>
    public string? ServiceAction { get; init; }

    // --- Cancel -----------------------------------------------------------
    public string? OperationId { get; init; }

    /// <summary>Client protocol/app version, so a mismatch can name both sides.</summary>
    public string? ClientVersion { get; init; }
}

public sealed record PrivilegedResponse
{
    public int Protocol { get; init; } = PrivilegedProtocol.Version;
    public string RequestId { get; init; } = string.Empty;
    public PrivilegedStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;

    public string? HelperVersion { get; init; }

    /// <summary>Identifies a running operation for <see cref="PrivilegedOperationKind.Cancel"/>.</summary>
    public string? OperationId { get; init; }

    public int? ProcessId { get; init; }
    public int? ExitCode { get; init; }
    public string? StandardOutput { get; init; }
    public string? StandardError { get; init; }
    public long DurationMilliseconds { get; init; }

    /// <summary>True when the created process really carries an elevated token, read back from Windows.</summary>
    public bool? Elevated { get; init; }
    public string? TokenUser { get; init; }
    public int? SessionId { get; init; }

    /// <summary>Present on Status: whether this account has an administrator token at all.</summary>
    public bool? ElevationAvailable { get; init; }
    public string? OwnerAccount { get; init; }

    public static PrivilegedResponse For(PrivilegedRequest request, PrivilegedStatus status, string message)
        => new() { RequestId = request.RequestId, Status = status, Message = message };
}
