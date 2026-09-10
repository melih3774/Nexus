using System.Text.Json.Serialization;

using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Security.Detection;
using PcCore.Capabilities.Security.Evidence;
using PcCore.Protocol;

namespace PcCore.Capabilities.Security.Sessions;

/// <summary>
/// An incident, as the phone sees it in a list.
/// <para>
/// No file paths and no bytes: enough to render a row and decide what to open. What the owner needs
/// to know at a glance is when, how long, how it ended, and whether it was flagged.
/// </para>
/// </summary>
public sealed record SecuritySessionSummary
{
    public required string SessionId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public required IncidentTrigger Trigger { get; init; }
    public required SessionResolution Resolution { get; init; }
    public required bool HighAlert { get; init; }
    public required long DurationSeconds { get; init; }
    public required int TimelineCount { get; init; }
    public required int ScreenshotCount { get; init; }
    public required int CameraCount { get; init; }
    public required int AudioCount { get; init; }

    public static SecuritySessionSummary From(SecuritySession session) => new()
    {
        SessionId = session.SessionId,
        StartedAtUtc = session.StartedAtUtc,
        EndedAtUtc = session.EndedAtUtc,
        Trigger = session.Trigger,
        Resolution = session.Resolution,
        HighAlert = session.HighAlert,
        DurationSeconds = (long)session.Duration.TotalSeconds,
        TimelineCount = session.Timeline.Count,
        ScreenshotCount = session.Evidence.Count(e =>
            e.Kind is EvidenceKind.Screenshot or EvidenceKind.ScreenRecording),
        CameraCount = session.Evidence.Count(e => e.Kind is EvidenceKind.CameraImage or EvidenceKind.CameraVideo),
        AudioCount = session.Evidence.Count(e => e.Kind == EvidenceKind.Audio),
    };
}

/// <summary>One piece of evidence, described. The bytes are fetched separately and deliberately.</summary>
public sealed record EvidenceSummary
{
    public required string EvidenceId { get; init; }
    public required EvidenceKind Kind { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public required long DurationMs { get; init; }
    public required string Sha256 { get; init; }
    public string? Note { get; init; }

    public static EvidenceSummary From(SecurityEvidence evidence) => new()
    {
        EvidenceId = evidence.EvidenceId,
        Kind = evidence.Kind,
        CreatedAtUtc = evidence.CreatedAtUtc,
        ContentType = evidence.ContentType,
        SizeBytes = evidence.SizeBytes,
        DurationMs = evidence.DurationMs,
        Sha256 = evidence.Sha256,
        Note = evidence.Note,
    };
}

public sealed record SessionDetail
{
    public required SecuritySessionSummary Summary { get; init; }
    public required IReadOnlyList<SecurityTimelineEntry> Timeline { get; init; }
    public required IReadOnlyList<EvidenceSummary> Evidence { get; init; }

    public static SessionDetail From(SecuritySession session) => new()
    {
        Summary = SecuritySessionSummary.From(session),
        Timeline = session.Timeline,
        Evidence = [.. session.Evidence.Select(EvidenceSummary.From)],
    };
}

public sealed record ListSessionsResult(int Count, IReadOnlyList<SecuritySessionSummary> Sessions);

public sealed record SessionIdParameters
{
    public string? SessionId { get; init; }
}

/// <summary>
/// The incident history.
/// <para>
/// Available to the phone: inspecting what happened is the entire point of the alert it received.
/// </para>
/// </summary>
public sealed class ListSecuritySessionsAction : CapabilityAction<NoParameters, ListSessionsResult>
{
    private readonly ISecuritySessionStore _store;

    public ListSecuritySessionsAction(ISecuritySessionStore store) => _store = store;

    public override string Name => "security.sessions.list";

    public override string Description => "Security incidents, newest first, without evidence contents.";

    protected override ValueTask<ListSessionsResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var sessions = _store.All();
        return ValueTask.FromResult(
            new ListSessionsResult(sessions.Count, [.. sessions.Select(SecuritySessionSummary.From)]));
    }
}

public sealed record GetSessionResult
{
    /// <summary>Null when there is no open incident and none was named.</summary>
    public SessionDetail? Session { get; init; }

    /// <summary>Whether an incident is open right now.</summary>
    public required bool HasActiveSession { get; init; }

    public required SecurityState State { get; init; }
}

/// <summary>
/// One incident in full, or the open one when none is named.
/// <para>
/// The phone calls this with no parameters when it opens the Security screen, which answers "is
/// something happening right now" and "what is it" in a single round trip.
/// </para>
/// </summary>
public sealed class GetSecuritySessionAction : CapabilityAction<SessionIdParameters, GetSessionResult>
{
    private readonly ISecuritySessionStore _store;
    private readonly ISecurityStateService _state;

    public GetSecuritySessionAction(ISecuritySessionStore store, ISecurityStateService state)
    {
        _store = store;
        _state = state;
    }

    public override string Name => "security.sessions.get";

    public override string Description => "One security incident with its timeline and evidence list.";

    protected override ValueTask<GetSessionResult> ExecuteAsync(
        SessionIdParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var active = _store.Active();

        var session = string.IsNullOrWhiteSpace(parameters.SessionId)
            ? active
            : _store.Find(parameters.SessionId);

        if (session is null && !string.IsNullOrWhiteSpace(parameters.SessionId))
        {
            throw new CapabilityException(ErrorCodes.NotFound, "No such security session.");
        }

        return ValueTask.FromResult(new GetSessionResult
        {
            Session = session is null ? null : SessionDetail.From(session),
            HasActiveSession = active is not null,
            State = _state.Current.State,
        });
    }
}

public sealed record GetEvidenceParameters
{
    public string? SessionId { get; init; }
    public string? EvidenceId { get; init; }

    /// <summary>
    /// Where in the file to start. Zero for the whole thing, which is what a still image asks for.
    /// <para>
    /// Ranged rather than all-or-nothing because a camera clip is megabytes and a phone holding a
    /// whole one as a base64 string, a byte array and a decoded buffer at the same time is three
    /// copies of the owner's living room on a heap that has other work to do.
    /// </para>
    /// </summary>
    public long Offset { get; init; }

    /// <summary>Most bytes to return in this chunk. Clamped; null takes the default.</summary>
    public int? MaxBytes { get; init; }
}

/// <summary>Whether the bytes are actually there.</summary>
public enum EvidenceAvailability
{
    /// <summary>The file is present and the requested range was returned.</summary>
    [JsonStringEnumMemberName("AVAILABLE")]
    Available,

    /// <summary>
    /// The session still lists it, but the file is gone — deleted, or past its retention period.
    /// <para>
    /// A distinct state rather than an error, because it is not a failure and the owner should be
    /// told which of "something went wrong" and "this no longer exists" is true.
    /// </para>
    /// </summary>
    [JsonStringEnumMemberName("MISSING")]
    Missing,

    /// <summary>The file is there and could not be read.</summary>
    [JsonStringEnumMemberName("UNREADABLE")]
    Unreadable,
}

public sealed record GetEvidenceResult
{
    public required string EvidenceId { get; init; }
    public required EvidenceKind Kind { get; init; }
    public required string ContentType { get; init; }
    public required string Encoding { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }

    /// <summary>Whether the bytes exist at all.</summary>
    public EvidenceAvailability Availability { get; init; } = EvidenceAvailability.Available;

    /// <summary>Where this chunk starts in the file.</summary>
    public long Offset { get; init; }

    /// <summary>How many bytes this chunk carries, before base64.</summary>
    public int ChunkBytes { get; init; }

    /// <summary>True when this chunk reaches the end. A single-chunk fetch sets it immediately.</summary>
    public bool EndOfFile { get; init; } = true;

    /// <summary>Base64 of this chunk. Absent when there is nothing to send.</summary>
    public string? Data { get; init; }

    /// <summary>Why there are no bytes, in words the owner can read.</summary>
    public string? Unavailable { get; init; }
}

/// <summary>
/// The bytes of one piece of evidence.
/// <para>
/// A dedicated action rather than exposure through <c>files.list</c>. The evidence directory holds
/// camera and microphone recordings of the owner's home; reaching it through the general filesystem
/// capability would mean any future widening of that capability silently widened access to this.
/// The only way in is by naming a session and an evidence id that the session actually contains.
/// </para>
/// </summary>
public sealed class GetSecurityEvidenceAction : CapabilityAction<GetEvidenceParameters, GetEvidenceResult>
{
    /// <summary>
    /// Largest chunk returned in one response, whatever the caller asks for.
    /// <para>
    /// A ceiling rather than a wall: a file bigger than this used to be refused outright, which is
    /// why camera clips were unreachable from the phone. Now it bounds one message and the caller
    /// asks again from the next offset.
    /// </para>
    /// </summary>
    private const int MaxChunkBytes = 4 * 1024 * 1024;

    /// <summary>
    /// What one request returns when the caller does not say.
    /// <para>
    /// Half a megabyte of file is about 700 KB of base64 on the wire. Small enough that a phone can
    /// hold one while writing it to disk, large enough that a six-second clip is a handful of round
    /// trips rather than a hundred.
    /// </para>
    /// </summary>
    private const int DefaultChunkBytes = 512 * 1024;

    private readonly ISecuritySessionStore _store;

    public GetSecurityEvidenceAction(ISecuritySessionStore store) => _store = store;

    public override string Name => "security.evidence.get";

    public override string Description => "The contents of one piece of security evidence.";

    protected override async ValueTask<GetEvidenceResult> ExecuteAsync(
        GetEvidenceParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        // A resolved incident answers exactly as an open one does. Find() searches every session the
        // store holds regardless of resolution, and that is deliberate: acknowledging an incident
        // decides what happened, not whether the owner may still look at what was recorded.
        var session = _store.Find(parameters.SessionId ?? string.Empty)
            ?? throw new CapabilityException(ErrorCodes.NotFound, "No such security session.");

        var evidence = session.Evidence.FirstOrDefault(e => e.EvidenceId == parameters.EvidenceId)
            ?? throw new CapabilityException(ErrorCodes.NotFound, "No such evidence.");

        var result = new GetEvidenceResult
        {
            EvidenceId = evidence.EvidenceId,
            Kind = evidence.Kind,
            ContentType = evidence.ContentType,
            Encoding = "base64",
            SizeBytes = evidence.SizeBytes,
            Sha256 = evidence.Sha256,
            Offset = parameters.Offset,
        };

        // The file name comes from the session record, not the request, and is re-checked for
        // containment on the way out anyway.
        var path = _store.ResolveEvidence(session.SessionId, evidence.FileName);

        if (path is null)
        {
            // Listed but gone: deleted by the owner, or removed by retention. Reported as a state
            // rather than thrown as an error, so the phone can say which and still show what the
            // record knows about it.
            return result with
            {
                Availability = EvidenceAvailability.Missing,
                EndOfFile = true,
                Unavailable = "This evidence is no longer on the PC. It was deleted or passed its "
                    + "retention period.",
            };
        }

        var offset = Math.Max(0, parameters.Offset);
        var wanted = Math.Clamp(parameters.MaxBytes ?? DefaultChunkBytes, 1, MaxChunkBytes);

        try
        {
            await using var file = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);

            var length = file.Length;

            if (offset >= length)
            {
                // A caller that has already read to the end asks once more and is told so, rather
                // than being handed an empty chunk it has to interpret.
                return result with
                {
                    SizeBytes = length,
                    Offset = offset,
                    ChunkBytes = 0,
                    EndOfFile = true,
                };
            }

            var count = (int)Math.Min(wanted, length - offset);
            var buffer = new byte[count];

            file.Seek(offset, SeekOrigin.Begin);
            await file.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);

            return result with
            {
                // The record's size is what was captured; the file's is what is there now. The
                // second is the one a caller reading ranges has to trust.
                SizeBytes = length,
                Offset = offset,
                ChunkBytes = count,
                EndOfFile = offset + count >= length,
                Data = Convert.ToBase64String(buffer),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result with
            {
                Availability = EvidenceAvailability.Unreadable,
                EndOfFile = true,
                Unavailable = "This evidence could not be read from disk.",
            };
        }
    }
}

public sealed record ResolveSessionResult
{
    public required bool Resolved { get; init; }
    public SecuritySessionSummary? Session { get; init; }
    public required SecurityState State { get; init; }
}

/// <summary>
/// <c>security.this_was_me</c> — the owner acknowledges the incident.
/// <para>
/// Available to the phone, which is the point: the alert arrives there and so should the answer. On
/// Android it sits behind the device's own biometric prompt, so possession of an unlocked phone is
/// not enough to stand a security incident down.
/// </para>
/// </summary>
public sealed class ThisWasMeAction : CapabilityAction<NoParameters, ResolveSessionResult>
{
    private readonly SecurityIncidentService _incidents;
    private readonly ISecurityStateService _state;

    public ThisWasMeAction(SecurityIncidentService incidents, ISecurityStateService state)
    {
        _incidents = incidents;
        _state = state;
    }

    public override string Name => "security.this_was_me";

    public override string Description => "Acknowledge the active security session and return to OWNER_PRESENT.";

    protected override ValueTask<ResolveSessionResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var session = _incidents.AcknowledgeAsOwner(
            context.Caller.ActorType == ActorType.Device ? "PHONE" : "LOCAL");

        return ValueTask.FromResult(new ResolveSessionResult
        {
            Resolved = session is not null,
            Session = session is null ? null : SecuritySessionSummary.From(session),
            State = _state.Current.State,
        });
    }
}

/// <summary>
/// <c>security.not_me</c> — the owner says someone else was there.
/// <para>
/// Flags the incident and keeps it open. Deliberately does nothing else: no shutdown, no lock, no
/// wipe. An automated destructive response to a judgement this uncertain is a way to lose data over
/// a misread, and the owner is already holding the device that can act.
/// </para>
/// </summary>
public sealed class NotMeAction : CapabilityAction<NoParameters, ResolveSessionResult>
{
    private readonly SecurityIncidentService _incidents;
    private readonly ISecurityStateService _state;

    public NotMeAction(SecurityIncidentService incidents, ISecurityStateService state)
    {
        _incidents = incidents;
        _state = state;
    }

    public override string Name => "security.not_me";

    public override string Description => "Mark the active security session as not the owner. Stays open, high alert.";

    protected override ValueTask<ResolveSessionResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var session = _incidents.ConfirmNotOwner(
            context.Caller.ActorType == ActorType.Device ? "PHONE" : "LOCAL");

        return ValueTask.FromResult(new ResolveSessionResult
        {
            Resolved = session is not null,
            Session = session is null ? null : SecuritySessionSummary.From(session),
            State = _state.Current.State,
        });
    }
}

public sealed record DeleteSessionResult
{
    public required bool Deleted { get; init; }
    public required string Verification { get; init; }
}

/// <summary>
/// <c>security.session.delete</c> — remove an incident and its recordings.
/// <para>
/// Local-only and gated on Windows Hello. Deleting evidence is the one action in this subsystem that
/// destroys something irreplaceable, and the person doing it should have to prove they are the owner
/// at the machine rather than merely holding an authenticated phone.
/// </para>
/// </summary>
public sealed class DeleteSecuritySessionAction : CapabilityAction<SessionIdParameters, DeleteSessionResult>
{
    private readonly SecurityIncidentService _incidents;
    private readonly ISecuritySessionStore _store;
    private readonly IOwnerVerifier _verifier;

    public DeleteSecuritySessionAction(
        SecurityIncidentService incidents,
        ISecuritySessionStore store,
        IOwnerVerifier verifier)
    {
        _incidents = incidents;
        _store = store;
        _verifier = verifier;
    }

    public override string Name => "security.session.delete";

    public override string Description => "Delete a security incident and its evidence. Requires Windows Hello.";

    protected override async ValueTask<DeleteSessionResult> ExecuteAsync(
        SessionIdParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(parameters.SessionId))
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, "A session id is required.");
        }

        if (_store.Find(parameters.SessionId) is null)
        {
            throw new CapabilityException(ErrorCodes.NotFound, "No such security session.");
        }

        var verification = await _verifier
            .VerifyAsync("Delete this security incident and its evidence", cancellationToken)
            .ConfigureAwait(false);

        if (verification != OwnerVerificationResult.Verified)
        {
            // Unavailable is a refusal, not a bypass. A check that passes when it cannot run is not
            // a check.
            throw new CapabilityException(
                ErrorCodes.NotAuthorized,
                verification == OwnerVerificationResult.Unavailable
                    ? "Windows Hello is not set up on this PC, so security evidence cannot be deleted."
                    : "Owner verification was not completed.");
        }

        _incidents.RecordAudit(
            "audit.evidence_deleted",
            $"Security session {parameters.SessionId} deleted. Owner verified: Windows Hello");

        return new DeleteSessionResult
        {
            Deleted = _incidents.Delete(parameters.SessionId),
            Verification = "WINDOWS_HELLO",
        };
    }
}
