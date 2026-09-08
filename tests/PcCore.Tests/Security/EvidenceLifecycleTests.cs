using Microsoft.Extensions.Logging.Abstractions;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.Security.Detection;
using PcCore.Capabilities.Security.Sessions;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using Xunit;

namespace PcCore.Tests.Security;

/// <summary>
/// When evidence capture stops, and what stops it.
/// <para>
/// <b>The bug this file exists for.</b> On a real phone the owner pressed NOT ME by accident, then
/// pressed THIS WAS ME and completed a real biometric prompt. The state went to OWNER_PRESENT, the
/// session was recorded as acknowledged, and the PC carried on recording evidence into it.
/// </para>
/// <para>
/// Nothing was wrong with the resolution: it wrote the right record and moved the right state. What
/// was missing was that resolution never <em>told</em> anything. Every producer decided for itself
/// whether to keep going, by looking at the store or the security state at its own entry point —
/// and work that had already passed that point never looked again. A capture waiting behind a
/// twenty-second audio clip on the shared gate, an ongoing-capture task queued a moment before the
/// acknowledgement, a continuous loop asleep between segments: all of them had already decided the
/// incident was open, and all of them went on to write evidence into one the owner had answered.
/// </para>
/// <para>
/// So resolution now stops the producers actively, and the last check before any device is touched
/// is made against the persisted record rather than a decision taken seconds earlier. These tests
/// hold a capture in flight on purpose, because the failure only exists in the window where one is.
/// </para>
/// </summary>
public sealed class EvidenceLifecycleTests : IDisposable
{
    private readonly TempStateDirectory _state = new("evidence-lifecycle");
    private readonly ScriptedActivityDetector _detector = new();
    private readonly EventBus _events;
    private readonly SecurityStateService _stateService;
    private readonly SecurityConfigurationStore _configuration;
    private readonly SecuritySessionStore _store;
    private readonly SecurityActivityMonitor _monitor;
    private readonly SecurityIncidentService _incidents;

    private readonly FakeEvidenceSource _screen = new("screen", ".png", "image/png");
    private readonly FakeEvidenceSource _camera = new("camera", ".jpg", "image/jpeg");
    private readonly FakeEvidenceSource _audio = new("audio", ".m4a", "audio/mp4");

    public EvidenceLifecycleTests()
    {
        _events = new EventBus(NullLogger<EventBus>.Instance);
        _stateService = new SecurityStateService(_events, _state.Directory, TestLog.For<SecurityStateService>());
        _configuration = new SecurityConfigurationStore(_state.Directory, TestLog.For<SecurityConfigurationStore>());
        _store = new SecuritySessionStore(_state.Directory, new SecurityStorageOptions(), TestLog.For<SecuritySessionStore>());

        _monitor = new SecurityActivityMonitor(
            _detector,
            _stateService,
            _events,
            new DetectionOptions { GraceSeconds = 0, PointerMovementThreshold = 40, CoalesceMilliseconds = 0 },
            TestLog.For<SecurityActivityMonitor>());

        _incidents = new SecurityIncidentService(
            _store,
            _stateService,
            _monitor,
            _configuration,
            _screen,
            _camera,
            _audio,
            _events,

            // Zero intervals: every activity is allowed to capture, so "no new evidence" can only
            // be the lifecycle working rather than a rate limit hiding it.
            new EvidenceOptions { ScreenshotIntervalSeconds = 0, ClipIntervalSeconds = 0 },
            TestLog.For<SecurityIncidentService>());

        _monitor.Start();
    }

    public void Dispose()
    {
        _incidents.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _monitor.Dispose();
        _state.Dispose();
    }

    private void Arm() => _stateService.RequestTransition(SecurityState.Armed, "test").EnsureAccepted();

    private static async Task SettleAsync() =>
        await Task.Delay(250, TestContext.Current.CancellationToken);

    private async Task<SecuritySession> OpenIncidentAsync()
    {
        Arm();
        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        var session = Assert.Single(_store.All());
        Assert.Equal(SecurityState.SecuritySession, _stateService.Current.State);

        return session;
    }

    /// <summary>Every evidence file recorded against a session, however it got there.</summary>
    private int EvidenceCount(string sessionId) => _store.Find(sessionId)!.Evidence.Count;

    // --- The physical-device sequence --------------------------------------------------------------

    /// <summary>
    /// The exact sequence a real phone performed: NOT ME by accident, then a biometric THIS WAS ME.
    /// </summary>
    [Fact]
    public async Task NotMeThenThisWasMeClosesTheIncidentAndStopsEvidence()
    {
        var session = await OpenIncidentAsync();

        Assert.True(_incidents.IsCapturingEvidence);

        // NOT ME. The incident gets worse, not over: it stays open and keeps recording.
        var flagged = _incidents.ConfirmNotOwner("PHONE")!;

        Assert.Equal(SessionResolution.ConfirmedNotOwner, flagged.Resolution);
        Assert.True(flagged.HighAlert);
        Assert.Equal(SecurityState.SecuritySession, _stateService.Current.State);
        Assert.True(_incidents.IsCapturingEvidence);

        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        var whileFlagged = EvidenceCount(session.SessionId);
        Assert.True(whileFlagged > 0, "NOT ME must not stop evidence");

        // THIS WAS ME, correcting the accident.
        var resolved = _incidents.AcknowledgeAsOwner("PHONE")!;

        Assert.Equal(SessionResolution.AcknowledgedOwner, resolved.Resolution);
        Assert.False(resolved.HighAlert, "an authenticated owner acknowledgement clears the high alert");
        Assert.NotNull(resolved.EndedAtUtc);
        Assert.Equal(SecurityState.OwnerPresent, _stateService.Current.State);

        // The producers are stopped, not merely expected to notice.
        Assert.False(_incidents.IsCapturingEvidence);

        // Everything that used to keep evidence flowing, tried again.
        _detector.Local(ActivityKind.KeyboardActivity);
        _detector.LocalPointerMovement(30);
        _detector.Local(ActivityKind.ForegroundWindowChanged, "explorer");
        await SettleAsync();

        Assert.Equal(whileFlagged, EvidenceCount(session.SessionId));
    }

    /// <summary>
    /// The race itself: a capture already at the device, and another queued behind it, when the
    /// owner acknowledges.
    /// <para>
    /// This is the shape of the real failure. Before the fix the queued capture went on to record,
    /// because it had already passed every check that could have stopped it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EvidenceQueuedWhenTheOwnerAcknowledgesIsNeverRecorded()
    {
        var session = await OpenIncidentAsync();
        var before = EvidenceCount(session.SessionId);

        // Hold the next screen capture at the device, the way a long clip would hold it.
        _screen.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _detector.Local(ActivityKind.ForegroundWindowChanged, "claude");
        await _screen.InFlight.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // A second cycle, which will queue behind the first on the shared capture gate.
        _detector.Local(ActivityKind.ForegroundWindowChanged, "explorer");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var startedBefore = _screen.Started;

        // The owner answers while one capture is at the device and another is waiting.
        _incidents.AcknowledgeAsOwner("PHONE");
        Assert.False(_incidents.IsCapturingEvidence);

        // Let the in-flight one finish. It is allowed to become a whole file.
        _screen.Hold.SetResult();
        await SettleAsync();

        Assert.Equal(SecurityState.OwnerPresent, _stateService.Current.State);

        // The queued one never reached the device.
        Assert.Equal(startedBefore, _screen.Started);

        // And at most the single in-flight capture was recorded — never the queued one.
        Assert.True(
            EvidenceCount(session.SessionId) <= before + 1,
            $"evidence grew from {before} to {EvidenceCount(session.SessionId)} after acknowledgement");
    }

    /// <summary>
    /// A recording already open when the owner acknowledges is finished, not truncated.
    /// <para>
    /// Cancelling the recorder mid-file would be destroying evidence in order to enforce a rule
    /// that exists to preserve it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ARecordingOpenAtAcknowledgementFinalisesCleanly()
    {
        var session = await OpenIncidentAsync();

        _screen.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _detector.Local(ActivityKind.ForegroundWindowChanged, "claude");
        await _screen.InFlight.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var capturesBefore = _screen.Captures;

        _incidents.AcknowledgeAsOwner("PHONE");

        // The device is released after the acknowledgement and must still complete.
        _screen.Hold.SetResult();
        await SettleAsync();

        Assert.Equal(capturesBefore + 1, _screen.Captures);

        // Whatever was already on disk is still there.
        Assert.True(EvidenceCount(session.SessionId) > 0);
        Assert.NotNull(_store.Find(session.SessionId));
    }

    // --- The other resolution paths ----------------------------------------------------------------

    [Fact]
    public async Task ThisWasMeStraightFromAnActiveIncidentStopsEvidence()
    {
        var session = await OpenIncidentAsync();

        var resolved = _incidents.AcknowledgeAsOwner("PHONE")!;

        Assert.Equal(SessionResolution.AcknowledgedOwner, resolved.Resolution);
        Assert.False(resolved.HighAlert);
        Assert.NotNull(resolved.EndedAtUtc);
        Assert.False(_incidents.IsCapturingEvidence);

        var after = EvidenceCount(session.SessionId);

        _detector.Local(ActivityKind.KeyboardActivity);
        _detector.LocalPointerMovement(30);
        await SettleAsync();

        Assert.Equal(after, EvidenceCount(session.SessionId));
    }

    [Fact]
    public async Task NotMeAloneLeavesEvidenceRunning()
    {
        var session = await OpenIncidentAsync();

        _incidents.ConfirmNotOwner("PHONE");

        Assert.True(_incidents.IsCapturingEvidence);
        Assert.Equal(SecurityState.SecuritySession, _stateService.Current.State);

        var before = EvidenceCount(session.SessionId);

        _detector.Local(ActivityKind.ForegroundWindowChanged, "explorer");
        await SettleAsync();

        Assert.True(
            EvidenceCount(session.SessionId) > before,
            "an incident the owner says was not them must keep recording");
    }

    [Fact]
    public async Task AcknowledgingTwiceIsIdempotent()
    {
        var session = await OpenIncidentAsync();

        var first = _incidents.AcknowledgeAsOwner("PHONE");
        Assert.NotNull(first);

        var evidence = EvidenceCount(session.SessionId);
        var timeline = _store.Find(session.SessionId)!.Timeline.Count;

        // The second press has nothing left to answer, and must not throw, duplicate, or reopen.
        var second = _incidents.AcknowledgeAsOwner("PHONE");

        Assert.Null(second);
        Assert.Equal(evidence, EvidenceCount(session.SessionId));
        Assert.Equal(timeline, _store.Find(session.SessionId)!.Timeline.Count);
        Assert.Equal(SecurityState.OwnerPresent, _stateService.Current.State);
        Assert.False(_incidents.IsCapturingEvidence);
    }

    /// <summary>A device that fails must not be able to keep an incident open.</summary>
    [Fact]
    public async Task AFailingEvidenceSourceDoesNotPreventResolution()
    {
        var session = await OpenIncidentAsync();

        _screen.Throws = true;
        _camera.Available = false;
        _audio.Throws = true;

        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        var resolved = _incidents.AcknowledgeAsOwner("PHONE")!;

        Assert.Equal(SessionResolution.AcknowledgedOwner, resolved.Resolution);
        Assert.Equal(SecurityState.OwnerPresent, _stateService.Current.State);
        Assert.False(_incidents.IsCapturingEvidence);
        Assert.NotNull(_store.Find(session.SessionId));
    }

    // --- What must survive -------------------------------------------------------------------------

    [Fact]
    public async Task TheSessionItsEvidenceAndItsHistorySurviveResolution()
    {
        var session = await OpenIncidentAsync();

        _detector.Local(ActivityKind.ForegroundWindowChanged, "claude");
        await SettleAsync();

        var files = Directory.GetFiles(_store.EvidenceFolder(session.SessionId));
        Assert.NotEmpty(files);

        var evidence = EvidenceCount(session.SessionId);

        _incidents.ConfirmNotOwner("PHONE");
        _incidents.AcknowledgeAsOwner("PHONE");

        var stored = _store.Find(session.SessionId)!;

        // Nothing is deleted, truncated, or forgotten by answering an incident.
        Assert.Equal(evidence, stored.Evidence.Count);
        Assert.Equal(files.Length, Directory.GetFiles(_store.EvidenceFolder(session.SessionId)).Length);
        Assert.Contains(_store.All(), s => s.SessionId == session.SessionId);

        // And it is still readable after a restart.
        var reloaded = new SecuritySessionStore(
            _state.Directory, new SecurityStorageOptions(), TestLog.For<SecuritySessionStore>());

        var persisted = reloaded.Find(session.SessionId)!;

        Assert.Equal(SessionResolution.AcknowledgedOwner, persisted.Resolution);
        Assert.False(persisted.HighAlert);
        Assert.NotNull(persisted.EndedAtUtc);
        Assert.Equal(evidence, persisted.Evidence.Count);
    }

    /// <summary>
    /// The timeline has to read as an account of what the owner did, including the mistake.
    /// </summary>
    [Fact]
    public async Task TheTimelineRecordsBothTheMistakeAndTheCorrection()
    {
        var session = await OpenIncidentAsync();

        _incidents.ConfirmNotOwner("PHONE");
        _incidents.AcknowledgeAsOwner("PHONE");

        var codes = _store.Find(session.SessionId)!.Timeline.Select(e => e.Code).ToList();

        Assert.Contains("session.not_me", codes);
        Assert.Contains("session.high_alert", codes);
        Assert.Contains("session.acknowledged", codes);
        Assert.Contains("session.high_alert_cleared", codes);
        Assert.Contains("evidence.stopped", codes);
        Assert.Contains("session.closed", codes);

        // The earlier answer is not erased by the later one.
        Assert.True(
            codes.IndexOf("session.not_me") < codes.IndexOf("session.acknowledged"),
            "the correction must come after the mistake, not replace it");

        var corrected = _store.Find(session.SessionId)!
            .Timeline.Single(e => e.Code == "session.acknowledged");

        Assert.Contains("corrected", corrected.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A restart after resolution must not resume capture for the answered incident.
    /// </summary>
    [Fact]
    public async Task RecoveryAfterResolutionDoesNotResumeCapture()
    {
        var session = await OpenIncidentAsync();

        _incidents.AcknowledgeAsOwner("PHONE");

        // What the host does on the next start.
        _incidents.Recover();

        Assert.False(_incidents.IsCapturingEvidence);
        Assert.Null(_store.Active());
        Assert.Equal(SecurityState.OwnerPresent, _stateService.Current.State);

        var evidence = EvidenceCount(session.SessionId);

        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        Assert.Equal(evidence, EvidenceCount(session.SessionId));
    }
}
