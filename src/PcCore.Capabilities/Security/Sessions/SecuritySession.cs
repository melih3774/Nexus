using System.Text.Json.Serialization;
using PcCore.Capabilities.Security.Detection;

namespace PcCore.Capabilities.Security.Sessions;

/// <summary>How an incident was resolved, if it has been.</summary>
public enum SessionResolution
{
    /// <summary>Open. Evidence collection continues according to policy.</summary>
    [JsonStringEnumMemberName("ACTIVE")]
    Active,

    /// <summary>The owner said it was them. The incident closes; its evidence is kept.</summary>
    [JsonStringEnumMemberName("ACKNOWLEDGED_OWNER")]
    AcknowledgedOwner,

    /// <summary>The owner said it was not them. The incident stays open and is flagged.</summary>
    [JsonStringEnumMemberName("CONFIRMED_NOT_OWNER")]
    ConfirmedNotOwner,
}

/// <summary>What kind of evidence a file holds.</summary>
public enum EvidenceKind
{
    [JsonStringEnumMemberName("SCREENSHOT")]
    Screenshot,

    [JsonStringEnumMemberName("SCREEN_RECORDING")]
    ScreenRecording,

    [JsonStringEnumMemberName("CAMERA_IMAGE")]
    CameraImage,

    [JsonStringEnumMemberName("CAMERA_VIDEO")]
    CameraVideo,

    [JsonStringEnumMemberName("AUDIO")]
    Audio,
}

/// <summary>
/// One recorded file, described.
/// <para>
/// The bytes live on disk and are referenced, never embedded: a JSON file that grows a base64 image
/// every few seconds stops being readable, stops being cheap to rewrite, and turns a metadata
/// corruption into the loss of the evidence as well.
/// </para>
/// </summary>
public sealed record SecurityEvidence
{
    public required string EvidenceId { get; init; }

    public required string SessionId { get; init; }

    public required EvidenceKind Kind { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>File name inside the session's evidence folder. Never a full path — see SPEC 76.</summary>
    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public required long SizeBytes { get; init; }

    /// <summary>Length of a recording, in milliseconds. Zero for stills.</summary>
    public long DurationMs { get; init; }

    /// <summary>
    /// SHA-256 of the file, lowercase hex.
    /// <para>
    /// A personal integrity check, not a forensic chain of custody: it catches a truncated write, a
    /// corrupted disk, or a file that changed since it was recorded. It proves nothing about who
    /// created it, and this project does not claim it does.
    /// </para>
    /// </summary>
    public required string Sha256 { get; init; }

    /// <summary>Safe, non-sensitive context — image dimensions, capture device name. Never content.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// One line of what happened, in words.
/// <para>
/// Everything here is written to be read by the owner months later. It says that keyboard activity
/// occurred, never what was typed; that the foreground application changed, never what was in the
/// window. A timeline that recorded content would be a log of the owner's own life, kept for the
/// benefit of nobody.
/// </para>
/// </summary>
public sealed record SecurityTimelineEntry
{
    public required DateTimeOffset AtUtc { get; init; }

    /// <summary>A short machine-readable tag, for example <c>evidence.screenshot</c>.</summary>
    public required string Code { get; init; }

    /// <summary>One human-readable sentence. Safe to display and to log.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// A security incident: what happened, when, what was recorded, and how it ended.
/// <para>
/// Deliberately separate from <c>SecurityState</c>. The state says what Nexus is doing right now and
/// is a single value; a session is a historical record and there are many. Folding history into the
/// state would have made "what is happening" and "what happened" the same field, and neither would
/// have survived it.
/// </para>
/// </summary>
public sealed record SecuritySession
{
    public required string SessionId { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Set when the incident closes. Null while it is open.</summary>
    public DateTimeOffset? EndedAtUtc { get; init; }

    public required IncidentTrigger Trigger { get; init; }

    public SessionResolution Resolution { get; init; } = SessionResolution.Active;

    /// <summary>Set when the owner says it was not them. Never cleared automatically.</summary>
    public bool HighAlert { get; init; }

    /// <summary>When the owner resolved it, either way.</summary>
    public DateTimeOffset? ResolvedAtUtc { get; init; }

    /// <summary>Which side resolved it: <c>PHONE</c> or <c>LOCAL</c>. Never a credential.</summary>
    public string? ResolvedBy { get; init; }

    public IReadOnlyList<SecurityTimelineEntry> Timeline { get; init; } = [];

    public IReadOnlyList<SecurityEvidence> Evidence { get; init; } = [];

    public bool IsOpen => Resolution != SessionResolution.AcknowledgedOwner;

    public TimeSpan Duration => (EndedAtUtc ?? DateTimeOffset.UtcNow) - StartedAtUtc;

    public SecuritySession With(SecurityTimelineEntry entry) =>
        this with { Timeline = [.. Timeline, entry] };

    public SecuritySession With(SecurityEvidence evidence) =>
        this with { Evidence = [.. Evidence, evidence] };
}

/// <summary>The on-disk shape. Versioned so a later format change is a migration, not a surprise.</summary>
internal sealed record SecuritySessionFile
{
    public int SchemaVersion { get; init; } = 1;

    public IReadOnlyList<SecuritySession> Sessions { get; init; } = [];
}
