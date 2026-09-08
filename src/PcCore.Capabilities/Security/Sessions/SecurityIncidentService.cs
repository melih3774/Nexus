using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Security.Detection;
using PcCore.Capabilities.Security.Evidence;
using PcCore.Protocol;

namespace PcCore.Capabilities.Security.Sessions;

public sealed class EvidenceOptions
{
    public const string SectionName = "PcCore:Security:Evidence";

    /// <summary>
    /// Shortest gap between screenshots while activity continues.
    /// <para>
    /// Eight seconds. A screenshot per mouse movement would be thousands of near-identical images
    /// and a visible load on the machine, while telling the owner nothing the first one did not.
    /// </para>
    /// </summary>
    public int ScreenshotIntervalSeconds { get; set; } = 8;

    /// <summary>Length of a camera clip. Short and bounded; never open-ended.</summary>
    public int CameraClipSeconds { get; set; } = 6;

    /// <summary>Length of an audio clip at the start of an incident.</summary>
    public int AudioClipSeconds { get; set; } = 20;

    /// <summary>Gap between camera or audio clips while an incident continues.</summary>
    public int ClipIntervalSeconds { get; set; } = 120;

    /// <summary>Segment length when the owner chose continuous recording.</summary>
    public int ContinuousSegmentSeconds { get; set; } = 60;

    /// <summary>Most evidence files one incident may produce, whatever else is configured.</summary>
    public int MaxEvidencePerSession { get; set; } = 400;
}

/// <summary>
/// Owns the life of a security incident.
/// <para>
/// It is the only thing that creates a session, the only thing that records evidence, and the only
/// thing that resolves one. Concentrating that here is what makes the central promise checkable in
/// one place: <b>nothing records unless a session is open, and a session cannot open unless the
/// owner armed the machine first.</b>
/// </para>
/// </summary>
public sealed class SecurityIncidentService : IAsyncDisposable
{
    private readonly ISecuritySessionStore _store;
    private readonly ISecurityStateService _state;
    private readonly SecurityActivityMonitor _monitor;
    private readonly SecurityConfigurationStore _configuration;
    private readonly IScreenEvidenceSource _screen;
    private readonly ICameraEvidenceSource _camera;
    private readonly IAudioEvidenceSource _audio;
    private readonly IEventPublisher _events;
    private readonly EvidenceOptions _options;
    private readonly ILogger<SecurityIncidentService> _logger;
    private readonly TimeProvider _time;

    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();

    private DateTimeOffset _lastScreenshot;
    private DateTimeOffset _lastClip;
    private Task? _continuous;

    /// <summary>
    /// Cancelled when the open incident ends, for any reason. Linked to <see cref="_stopping"/>, so
    /// shutting the service down stops evidence as surely as resolving the incident does.
    /// </summary>
    /// <remarks>
    /// This is what v0.7.1 was missing. Resolution wrote the record and moved the state, and then
    /// trusted every producer to notice on its own the next time it happened to look. Work that had
    /// already looked — a capture waiting on <see cref="_captureGate"/>, a continuous loop between
    /// segments, an ongoing-capture task that had been queued a moment earlier — never looked
    /// again, and went on writing evidence into an incident the owner had already answered.
    /// </remarks>
    private CancellationTokenSource? _incident;

    /// <summary>The incident whose producers are running. Null when nothing is capturing.</summary>
    private string? _capturingSessionId;

    public SecurityIncidentService(
        ISecuritySessionStore store,
        ISecurityStateService state,
        SecurityActivityMonitor monitor,
        SecurityConfigurationStore configuration,
        IScreenEvidenceSource screen,
        ICameraEvidenceSource camera,
        IAudioEvidenceSource audio,
        IEventPublisher events,
        EvidenceOptions options,
        ILogger<SecurityIncidentService> logger,
        TimeProvider? time = null)
    {
        _store = store;
        _state = state;
        _monitor = monitor;
        _configuration = configuration;
        _screen = screen;
        _camera = camera;
        _audio = audio;
        _events = events;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;

        _monitor.IncidentDetected += OnIncidentDetected;
        _monitor.ActivityDuringIncident += OnActivityDuringIncident;
    }

    /// <summary>The open incident, or null.</summary>
    public SecuritySession? Active => _store.Active();

    // --- Evidence session lifecycle ----------------------------------------------------------------

    /// <summary>
    /// The incident evidence is currently being captured for, or null.
    /// <para>
    /// Deliberately separate from <see cref="Active"/>. "There is an open incident" and "producers
    /// are running for it" are two facts, and treating them as one is what let capture outlive the
    /// answer to it. Exposed so a test can assert the producers actually stopped rather than
    /// inferring it from the absence of files.
    /// </para>
    /// </summary>
    public string? CapturingSessionId
    {
        get
        {
            lock (_gate)
            {
                return _capturingSessionId;
            }
        }
    }

    public bool IsCapturingEvidence => CapturingSessionId is not null;

    /// <summary>
    /// Opens the evidence session for an incident and returns the token every producer runs under.
    /// </summary>
    private CancellationToken StartEvidenceSession(string sessionId)
    {
        lock (_gate)
        {
            // Whatever was running belonged to a different incident and is over.
            _incident?.Cancel();
            _incident?.Dispose();

            _incident = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            _capturingSessionId = sessionId;

            // Per-incident, not per-process: these rate-limit an incident's own capture, and
            // carrying them across would let a previous incident suppress the first evidence of
            // the next one.
            _lastScreenshot = default;
            _lastClip = default;

            return _incident.Token;
        }
    }

    /// <summary>
    /// Stops every evidence producer for an incident.
    /// <para>
    /// <b>Idempotent, and safe from anywhere.</b> Calling it twice, for an incident that already
    /// stopped, for one that never started, during a failing capture, or while shutting down, all
    /// do the same thing: nothing beyond the first time. The one caller that matters is resolution,
    /// which must not be able to fail because a camera did.
    /// </para>
    /// <para>
    /// Cancels rather than disposes: a producer may still be holding the token, and disposing the
    /// source underneath it turns a clean stop into an <see cref="ObjectDisposedException"/> on a
    /// background thread. The source is disposed when the next incident starts, or with the service.
    /// </para>
    /// </summary>
    private void StopEvidenceSession(string sessionId, string reason)
    {
        CancellationTokenSource? stopping;

        lock (_gate)
        {
            if (_capturingSessionId != sessionId)
            {
                // Already stopped, or this is not the incident that is capturing. Either way there
                // is nothing to do and saying so twice would be noise in the timeline.
                return;
            }

            stopping = _incident;
            _capturingSessionId = null;
        }

        try
        {
            stopping?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Raced with disposal. The producers are stopping either way.
        }

        _logger.LogInformation("Evidence capture stopped for session {SessionId}: {Reason}", sessionId, reason);
        Record(sessionId, "evidence.stopped", reason);
    }

    /// <summary>
    /// The token producers run under. Already-cancelled when no incident is capturing, so work that
    /// arrives late has nothing to run against.
    /// </summary>
    private CancellationToken EvidenceToken
    {
        get
        {
            lock (_gate)
            {
                return _capturingSessionId is null ? new CancellationToken(canceled: true) : _incident!.Token;
            }
        }
    }

    /// <summary>
    /// Reconciles with whatever was left behind by the last run.
    /// <para>
    /// Two things can be true at startup: the state says an incident was in progress, and the store
    /// holds it. They must agree. A restart mid-incident continues the same session — creating a
    /// second one would split one event into two and make the history lie about what happened.
    /// </para>
    /// </summary>
    public void Recover()
    {
        _store.Prune();

        var current = _state.Current.State;
        var active = _store.Active();

        if (current == SecurityState.SecuritySession)
        {
            if (active is null)
            {
                // The state survived but the record did not. Rather than silently dropping the
                // incident, open one that says so: the owner needs to know the machine believes an
                // incident is running.
                _logger.LogWarning("State is SECURITY_SESSION but no session record exists; recreating one.");

                var recreated = NewSession(IncidentTrigger.PointerActivity, "Session record was missing at startup");
                _store.Save(recreated);
                StartEvidenceSession(recreated.SessionId);
                StartContinuousIfConfigured();
                return;
            }

            _store.Save(Append(active, "session.restarted", "Nexus restarted during an active security session"));
            _logger.LogWarning("Resumed active security session {SessionId} after restart.", active.SessionId);

            // The incident continues across the restart, so its evidence session does too — and it
            // gets a token that resolution can cancel, exactly as it would have had before.
            StartEvidenceSession(active.SessionId);
            StartContinuousIfConfigured();
            return;
        }

        if (active is not null)
        {
            // The record says open but the state does not. The state is authoritative about what
            // Nexus is doing, so the session is closed rather than left dangling — but it is closed
            // honestly, with a line saying why.
            _logger.LogWarning(
                "Session {SessionId} was left open but the state is {State}; closing it.", active.SessionId, current);

            _store.Save(Append(active, "session.closed", $"Closed at startup because the security state was {current}")
                with { EndedAtUtc = _time.GetUtcNow() });
        }
    }

    // --- Incident start ------------------------------------------------------------------------

    private void OnIncidentDetected(IncidentTrigger trigger, LocalActivity activity)
    {
        var transition = _state.RequestTransition(SecurityState.SecuritySession, "security.detection");

        if (!transition.Changed)
        {
            // Refused because the state moved underneath us — the owner disarmed, or an incident is
            // already open. Either way this is not a new incident.
            _logger.LogInformation(
                "Detection did not open a session; the state is {State}.", transition.Snapshot.State);
            return;
        }

        var session = NewSession(trigger, Describe(activity));
        _store.Save(session);

        _logger.LogWarning(
            "Security session {SessionId} started ({Trigger}).", session.SessionId, trigger);

        // The one alert per incident. Published before evidence so the phone is told immediately
        // rather than after several seconds of camera and audio capture.
        _events.Publish(SecurityEvents.SessionStarted, SecuritySessionSummary.From(session));

        // The producers are started through the lifecycle rather than just spawned, so there is
        // always something that can stop them.
        var token = StartEvidenceSession(session.SessionId);

        _ = Task.Run(() => CaptureInitialEvidenceAsync(session.SessionId, token));
    }

    private SecuritySession NewSession(IncidentTrigger trigger, string firstLine)
    {
        var now = _time.GetUtcNow();

        return new SecuritySession
        {
            // A generated identifier, safe as a path component and meaningless on its own.
            SessionId = Guid.NewGuid().ToString("n")[..16],
            StartedAtUtc = now,
            Trigger = trigger,
            Timeline =
            [
                new SecurityTimelineEntry
                {
                    AtUtc = now,
                    Code = "session.started",
                    Message = "Security session started",
                },
                new SecurityTimelineEntry { AtUtc = now, Code = "detection.activity", Message = firstLine },
            ],
        };
    }

    /// <summary>
    /// Everything captured at the moment an incident opens.
    /// <para>
    /// Ordered so the cheapest and most reliable comes first: a screenshot needs no device and
    /// cannot be held by another application, while the camera and microphone can both be busy,
    /// muted, or absent. If they are, their absence is a timeline entry and the incident continues.
    /// </para>
    /// </summary>
    private async Task CaptureInitialEvidenceAsync(string sessionId, CancellationToken token)
    {
        var config = _configuration.Current;

        try
        {
            if (config.Screen != ScreenEvidenceMode.Disabled)
            {
                await CaptureScreenshotAsync(sessionId, "Screenshot captured at session start", token)
                    .ConfigureAwait(false);
            }

            if (config.Camera != CameraEvidenceMode.Disabled)
            {
                await CaptureCameraAsync(sessionId, includeClip: true, token).ConfigureAwait(false);
            }

            if (config.Audio != AudioEvidenceMode.Disabled)
            {
                await CaptureAudioAsync(sessionId, TimeSpan.FromSeconds(_options.AudioClipSeconds), token)
                    .ConfigureAwait(false);
            }

            StartContinuousIfConfigured();
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-incident. The session and everything already written survive.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initial evidence capture failed for session {SessionId}.", sessionId);
        }
    }

    // --- Ongoing activity ----------------------------------------------------------------------

    private void OnActivityDuringIncident(LocalActivity activity)
    {
        var session = _store.Active();

        if (session is null)
        {
            return;
        }

        _store.Save(Append(session, "detection.activity", Describe(activity)));
        _events.Publish(SecurityEvents.SessionUpdated, SecuritySessionSummary.From(_store.Find(session.SessionId)!));

        _ = Task.Run(() => CaptureOngoingEvidenceAsync(session.SessionId, activity, EvidenceToken));
    }

    /// <summary>
    /// Evidence while an incident continues, rate-limited so it cannot run away.
    /// <para>
    /// The limits are what make continued activity affordable. Without them a person using the
    /// machine for an hour would produce a screenshot per movement and a camera clip per keystroke.
    /// </para>
    /// </summary>
    private async Task CaptureOngoingEvidenceAsync(
        string sessionId,
        LocalActivity activity,
        CancellationToken token)
    {
        var config = _configuration.Current;
        var now = _time.GetUtcNow();

        try
        {
            if (config.Screen == ScreenEvidenceMode.Screenshots
                && now - _lastScreenshot >= TimeSpan.FromSeconds(_options.ScreenshotIntervalSeconds))
            {
                var reason = activity.Kind == ActivityKind.ForegroundWindowChanged
                    ? $"Screenshot captured after the foreground application changed to {activity.Detail}"
                    : "Screenshot captured while activity continued";

                await CaptureScreenshotAsync(sessionId, reason, token).ConfigureAwait(false);
            }

            if (now - _lastClip >= TimeSpan.FromSeconds(_options.ClipIntervalSeconds))
            {
                if (config.Camera == CameraEvidenceMode.EventClips)
                {
                    await CaptureCameraAsync(sessionId, includeClip: true, token).ConfigureAwait(false);
                }

                if (config.Audio == AudioEvidenceMode.EventClips)
                {
                    await CaptureAudioAsync(sessionId, TimeSpan.FromSeconds(_options.AudioClipSeconds), token)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ongoing evidence capture failed for session {SessionId}.", sessionId);
        }
    }

    /// <summary>Continuous modes, as bounded segments rather than one growing file.</summary>
    private void StartContinuousIfConfigured()
    {
        var config = _configuration.Current;

        var wanted = config.Screen == ScreenEvidenceMode.Continuous
            || config.Camera == CameraEvidenceMode.Continuous
            || config.Audio == AudioEvidenceMode.Continuous;

        lock (_gate)
        {
            if (!wanted || _continuous is { IsCompleted: false })
            {
                return;
            }

            var token = EvidenceToken;
            _continuous = Task.Run(() => ContinuousLoopAsync(token));
        }
    }

    private async Task ContinuousLoopAsync(CancellationToken token)
    {
        var segment = TimeSpan.FromSeconds(Math.Clamp(_options.ContinuousSegmentSeconds, 10, 600));

        try
        {
            while (!token.IsCancellationRequested)
            {
                var session = _store.Active();

                if (session is null || _state.Current.State != SecurityState.SecuritySession)
                {
                    // The incident ended. Continuous capture ends with it — this is the line that
                    // stops recording outliving the reason for it.
                    return;
                }

                var config = _configuration.Current;

                if (config.Screen == ScreenEvidenceMode.Continuous)
                {
                    await CaptureScreenshotAsync(session.SessionId, "Continuous screen segment", token)
                        .ConfigureAwait(false);
                }

                if (config.Camera == CameraEvidenceMode.Continuous)
                {
                    await CaptureCameraAsync(session.SessionId, includeClip: true, token).ConfigureAwait(false);
                }

                if (config.Audio == AudioEvidenceMode.Continuous)
                {
                    await CaptureAudioAsync(session.SessionId, segment, token).ConfigureAwait(false);
                }

                await Task.Delay(segment, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Continuous evidence capture stopped.");
        }
    }

    // --- Capture helpers -------------------------------------------------------------------------

    /// <summary>
    /// The token a device recording runs under: service shutdown only, never incident resolution.
    /// <para>
    /// A camera or microphone file that has already started is finished rather than cut off. The
    /// alternative — cancelling the recorder mid-file — would leave a truncated clip, which is
    /// destroying evidence to enforce a rule that exists to preserve it. Scheduling is what
    /// resolution stops; a file already being written is allowed to become a whole file.
    /// </para>
    /// </summary>
    private CancellationToken MediaToken => _stopping.Token;

    private async Task CaptureScreenshotAsync(string sessionId, string reason, CancellationToken token)
    {
        var captured = await CaptureAsync(
            sessionId,
            EvidenceKind.Screenshot,
            reason,
            "Screen evidence unavailable",
            folder => _screen.CaptureStillAsync(folder, MediaToken),
            token).ConfigureAwait(false);

        if (captured)
        {
            _lastScreenshot = _time.GetUtcNow();
        }
    }

    private async Task CaptureCameraAsync(string sessionId, bool includeClip, CancellationToken token)
    {
        var still = await CaptureAsync(
            sessionId,
            EvidenceKind.CameraImage,
            "Camera image captured",
            "Camera evidence unavailable",
            folder => _camera.CaptureStillAsync(folder, MediaToken),
            token).ConfigureAwait(false);

        if (!still || !includeClip)
        {
            return;
        }

        await CaptureAsync(
            sessionId,
            EvidenceKind.CameraVideo,
            "Camera clip recorded",
            "Camera clip unavailable",
            folder => _camera.CaptureClipAsync(folder, TimeSpan.FromSeconds(_options.CameraClipSeconds), MediaToken),
            token).ConfigureAwait(false);

        _lastClip = _time.GetUtcNow();
    }

    private async Task CaptureAudioAsync(string sessionId, TimeSpan duration, CancellationToken token)
    {
        await CaptureAsync(
            sessionId,
            EvidenceKind.Audio,
            "Audio clip recorded",
            "Microphone evidence unavailable",
            folder => _audio.CaptureClipAsync(folder, duration, MediaToken),
            token).ConfigureAwait(false);

        _lastClip = _time.GetUtcNow();
    }

    /// <summary>
    /// The one path every capture takes: check the bounds, capture, hash, record, announce.
    /// <para>
    /// Serialised on a single gate. The camera and the microphone are exclusive devices, and two
    /// overlapping captures would mean one of them failing for a reason that looks like the hardware
    /// being unavailable.
    /// </para>
    /// </summary>
    private async Task<bool> CaptureAsync(
        string sessionId,
        EvidenceKind kind,
        string successMessage,
        string failureMessage,
        Func<string, ValueTask<CapturedEvidence?>> capture,
        CancellationToken token)
    {
        // The incident is over. Checked before anything else and again after the gate, because the
        // gap between those two lines is where the bug lived: a capture could pass every check,
        // wait behind a twenty-second audio clip, and reach the device long after the owner had
        // answered the incident it belonged to.
        if (token.IsCancellationRequested || IsResolved(sessionId))
        {
            return false;
        }

        var session = _store.Find(sessionId);

        if (session is null)
        {
            return false;
        }

        if (session.Evidence.Count >= _options.MaxEvidencePerSession)
        {
            return false;
        }

        if (!_store.HasRoomForEvidence())
        {
            Record(sessionId, "evidence.skipped", "Evidence capture paused: not enough free disk space");
            return false;
        }

        try
        {
            await _captureGate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Resolved while queued. Nothing was started, so there is nothing to finish.
            return false;
        }

        try
        {
            // The last word before a device is touched. Everything above may have been true
            // several seconds ago.
            if (token.IsCancellationRequested || IsResolved(sessionId))
            {
                return false;
            }

            var folder = _store.EvidenceFolder(sessionId);
            var captured = await capture(folder).ConfigureAwait(false);

            if (captured is null)
            {
                Record(sessionId, "evidence.unavailable", failureMessage);
                return false;
            }

            var path = Path.Combine(folder, captured.FileName);
            var info = new FileInfo(path);

            if (!info.Exists || info.Length == 0)
            {
                Record(sessionId, "evidence.unavailable", failureMessage);
                return false;
            }

            var evidence = new SecurityEvidence
            {
                EvidenceId = Guid.NewGuid().ToString("n")[..16],
                SessionId = sessionId,
                Kind = kind,
                CreatedAtUtc = _time.GetUtcNow(),
                FileName = captured.FileName,
                ContentType = captured.ContentType,
                SizeBytes = info.Length,
                DurationMs = captured.DurationMs,
                Sha256 = SecuritySessionStore.ComputeDigest(path),
                Note = captured.Note,
            };

            var updated = _store.Find(sessionId);

            if (updated is null)
            {
                return false;
            }

            var withEvidence = updated
                .With(evidence)
                .With(new SecurityTimelineEntry
                {
                    AtUtc = evidence.CreatedAtUtc,
                    Code = $"evidence.{kind.ToString().ToLowerInvariant()}",
                    Message = successMessage,
                });

            _store.Save(withEvidence);
            _events.Publish(SecurityEvents.SessionUpdated, SecuritySessionSummary.From(withEvidence));

            _logger.LogInformation(
                "Recorded {Kind} evidence for session {SessionId} ({Size} bytes).", kind, sessionId, info.Length);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failing device must never take the incident down with it.
            _logger.LogWarning(ex, "{Kind} evidence capture failed for session {SessionId}.", kind, sessionId);
            Record(sessionId, "evidence.unavailable", failureMessage);
            return false;
        }
        finally
        {
            _captureGate.Release();
        }
    }

    // --- Resolution ------------------------------------------------------------------------------

    /// <summary>
    /// The owner says it was them.
    /// <para>
    /// Closes the incident and returns the machine to normal. Evidence is deliberately kept: an
    /// acknowledgement is the owner saying "I know what that was", not "erase it" — and a system
    /// that deleted the record on acknowledgement would make the history a poor account of what
    /// actually happened.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Reachable from NOT ME, deliberately.</b> An incident marked NOT ME stays open, so it is
    /// still the active one and the owner can still answer it — which is the point. Pressing NOT ME
    /// by accident and then correcting it with a biometric-confirmed THIS WAS ME is a sequence a
    /// person will actually perform, and treating CONFIRMED_NOT_OWNER as terminal would leave them
    /// with an incident they could not close and a machine stuck in SECURITY_SESSION. The high
    /// alert is cleared with it: the owner has since authenticated and said it was them, and that
    /// is a better answer than the one it replaces. The timeline keeps both.
    /// </remarks>
    public SecuritySession? AcknowledgeAsOwner(string resolvedBy)
    {
        var session = _store.Active();

        if (session is null)
        {
            // Nothing open. Acknowledging twice is not an error — the second one has simply
            // already happened.
            return null;
        }

        var now = _time.GetUtcNow();
        var correcting = session.Resolution == SessionResolution.ConfirmedNotOwner;

        // Producers stop first, before the record is written and before the state moves. Doing it
        // in this order means there is no window in which the incident is answered on disk while
        // something is still capturing into it.
        StopEvidenceSession(session.SessionId, "The owner acknowledged the incident");

        var message = correcting
            ? $"Owner corrected incident: THIS WAS ME ({resolvedBy})"
            : $"Owner marked: THIS WAS ME ({resolvedBy})";

        var acknowledged = Append(_store.Find(session.SessionId) ?? session, "session.acknowledged", message);

        if (correcting)
        {
            acknowledged = Append(acknowledged, "session.high_alert_cleared", "High Alert cleared by the owner");
        }

        var resolved = Append(acknowledged, "session.closed", "Security session closed") with
        {
            Resolution = SessionResolution.AcknowledgedOwner,
            HighAlert = false,
            EndedAtUtc = now,
            ResolvedAtUtc = now,
            ResolvedBy = resolvedBy,
        };

        _store.Save(resolved);
        _state.RequestTransition(SecurityState.OwnerPresent, "security.this_was_me");

        _logger.LogInformation(
            "Security session {SessionId} acknowledged by the owner{Correction}.",
            resolved.SessionId,
            correcting ? " (correcting an earlier NOT ME)" : string.Empty);

        _events.Publish(SecurityEvents.SessionResolved, SecuritySessionSummary.From(resolved));

        return resolved;
    }

    /// <summary>
    /// The owner says it was not them.
    /// <para>
    /// The incident stays open and is flagged. Nothing destructive follows — no shutdown, no lock,
    /// no wipe. An automated response to a signal this uncertain would be a way to lose data over a
    /// misread, and the owner is already holding the device that can act.
    /// </para>
    /// </summary>
    public SecuritySession? ConfirmNotOwner(string resolvedBy)
    {
        var session = _store.Active();

        if (session is null)
        {
            return null;
        }

        // Evidence deliberately keeps running: the incident is not over, it is worse. This is the
        // one resolution path that does not stop the producers.
        var flagged = Append(session, "session.not_me", $"Owner marked: NOT ME ({resolvedBy})");

        var resolved = Append(flagged, "session.high_alert", "High Alert enabled") with
        {
            Resolution = SessionResolution.ConfirmedNotOwner,
            HighAlert = true,
            ResolvedAtUtc = _time.GetUtcNow(),
            ResolvedBy = resolvedBy,
        };

        _store.Save(resolved);

        _logger.LogWarning(
            "Security session {SessionId} marked NOT ME; high alert. State remains SECURITY_SESSION.",
            resolved.SessionId);

        _events.Publish(SecurityEvents.SessionResolved, SecuritySessionSummary.From(resolved));

        return resolved;
    }

    public bool Delete(string sessionId) => _store.Delete(sessionId);

    /// <summary>Adds an audit line to the open incident, or logs it when there is none.</summary>
    public void RecordAudit(string code, string message)
    {
        var session = _store.Active();

        if (session is not null)
        {
            _store.Save(Append(session, code, message));
        }

        _logger.LogInformation("Security audit: {Message}", message);
    }

    /// <summary>
    /// Whether an incident has been answered, according to what is on disk.
    /// <para>
    /// The persisted record rather than the cancellation token, because the two answer slightly
    /// different questions and both matter. The token is the active signal — it stops work that is
    /// already in flight the instant the owner answers. This is the durable one: it is still true
    /// after a restart, when there is no token because there is no running incident, and it catches
    /// anything the signal could have missed.
    /// </para>
    /// </summary>
    private bool IsResolved(string sessionId)
    {
        var session = _store.Find(sessionId);

        return session is null
            || session.EndedAtUtc is not null
            || session.Resolution == SessionResolution.AcknowledgedOwner;
    }

    private void Record(string sessionId, string code, string message)
    {
        var session = _store.Find(sessionId);

        if (session is not null)
        {
            _store.Save(Append(session, code, message));
        }
    }

    private SecuritySession Append(SecuritySession session, string code, string message) =>
        session.With(new SecurityTimelineEntry { AtUtc = _time.GetUtcNow(), Code = code, Message = message });

    /// <summary>
    /// Turns an observation into one timeline sentence.
    /// <para>
    /// This is where "keyboard activity, never what was typed" becomes concrete: the only thing
    /// available to say about a key press is that one happened.
    /// </para>
    /// </summary>
    private static string Describe(LocalActivity activity) => activity.Kind switch
    {
        ActivityKind.PointerActivity => "Local pointer activity detected",
        ActivityKind.KeyboardActivity => "Local keyboard activity detected",
        ActivityKind.SessionUnlocked => "Workstation unlocked",
        ActivityKind.InteractiveLogon => "Interactive sign-in",
        ActivityKind.ForegroundWindowChanged => activity.Detail is { Length: > 0 } app
            ? $"Foreground application changed to {app}"
            : "Foreground application changed",
        _ => "Local activity detected",
    };

    public async ValueTask DisposeAsync()
    {
        _monitor.IncidentDetected -= OnIncidentDetected;
        _monitor.ActivityDuringIncident -= OnActivityDuringIncident;

        await _stopping.CancelAsync().ConfigureAwait(false);

        var continuous = _continuous;

        if (continuous is not null)
        {
            try
            {
                await continuous.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException)
            {
                // Expected.
            }
        }

        _stopping.Dispose();
        lock (_gate)
        {
            _incident?.Dispose();
            _incident = null;
            _capturingSessionId = null;
        }

        _captureGate.Dispose();
    }
}
