namespace PcCore.SecureCapture;

/// <summary>
/// The one request PC Core makes of the privileged helper: start a capture worker for the console
/// session, have it connect to this pipe, and have it prove it is the one we asked for.
/// </summary>
internal sealed record StartWorkerRequest
{
    /// <summary>The frame pipe PC Core is already serving. Must match the helper's fixed prefix.</summary>
    public string? Pipe { get; init; }

    /// <summary>Base64 of 32 random bytes, echoed by the worker so an impostor cannot answer.</summary>
    public string? Nonce { get; init; }
}

internal sealed record StartWorkerReply
{
    public required bool Ok { get; init; }

    /// <summary>Why not, in words safe to show the owner. Never contains anything the caller sent.</summary>
    public string? Reason { get; init; }

    public int WorkerProcessId { get; init; }
}
