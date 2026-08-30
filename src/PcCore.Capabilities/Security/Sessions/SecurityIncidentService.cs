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
                StartContinuousIfConfigured();
                return;
            }

            _store.Save(Append(active, "session.restarted", "Nexus restarted during an active security session"));
            _logger.LogWarning("Resumed active security session {SessionId} after restart.", active.SessionId);

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

        _ = Task.Run(() => CaptureInitialEvidenceAsync(session.SessionId));
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
    private async Task CaptureInitialEvidenceAsync(string sessionId)
    {
        var config = _configuration.Current;
        var token = _stopping.Token;

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

        _ = Task.Run(() => CaptureOngoingEvidenceAsync(session.SessionId, activity));
    }

    /// <summary>
    /// Evidence while an incident continues, rate-limited so it cannot run away.
    /// <para>
    /// The limits are what make continued activity affordable. Without them a person using the
    /// machine for an hour would produce a screenshot per movement and a camera clip per keystroke.
    /// </para>
    /// </summary>
    private async Task CaptureOngoingEvidenceAsync(string sessionId, LocalActivity activity)
    {
        var config = _configuration.Current;
        var now = _time.GetUtcNow();
        var token = _stopping.Token;

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

            _continuous = Task.Run(ContinuousLoopAsync);
        }
    }

    private async Task ContinuousLoopAsync()
    {
        var token = _stopping.Token;
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

    private async Task CaptureScreenshotAsync(string sessionId, string reason, CancellationToken token)
    {
        var captured = await CaptureAsync(
            sessionId,
            EvidenceKind.Screenshot,
            reason,
            "Screen evidence unavailable",
            folder => _screen.CaptureStillAsync(folder, token),
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
            folder => _camera.CaptureStillAsync(folder, token),
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
            folder => _camera.CaptureClipAsync(folder, TimeSpan.FromSeconds(_options.CameraClipSeconds), token),
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
            folder => _audio.CaptureClipAsync(folder, duration, token),
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

        await _captureGate.WaitAsync(token).ConfigureAwait(false);

        try
        {
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
    public SecuritySession? AcknowledgeAsOwner(string resolvedBy)
    {
        var session = _store.Active();

        if (session is null)
        {
            return null;
        }

        var now = _time.GetUtcNow();

        var resolved = Append(session, "session.acknowledged", $"Owner marked: THIS WAS ME ({resolvedBy})") with
        {
            Resolution = SessionResolution.AcknowledgedOwner,
            EndedAtUtc = now,
            ResolvedAtUtc = now,
            ResolvedBy = resolvedBy,
        };

        _store.Save(resolved);
        _state.RequestTransition(SecurityState.OwnerPresent, "security.this_was_me");

        _logger.LogInformation("Security session {SessionId} acknowledged by the owner.", resolved.SessionId);
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

        var resolved = Append(session, "session.not_me", $"Owner marked: NOT ME ({resolvedBy})") with
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
        _captureGate.Dispose();
    }
}
