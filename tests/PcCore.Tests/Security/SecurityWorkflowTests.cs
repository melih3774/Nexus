using Microsoft.Extensions.Logging.Abstractions;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.Security.Detection;
using PcCore.Capabilities.Security.Evidence;
using PcCore.Capabilities.Security.Sessions;
using PcCore.Capabilities.Storage;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using Xunit;

namespace PcCore.Tests.Security;

/// <summary>
/// The owner-controlled security workflow, end to end without a desktop.
/// <para>
/// The promise this milestone makes is narrow and absolute: <b>nothing is recorded unless the owner
/// armed the machine and a person was then detected using it.</b> Most of what follows exists to
/// hold that line — that OWNER_PRESENT records nothing, that Nexus's own remote input is never
/// mistaken for an intruder, and that an acknowledgement does not quietly destroy the evidence.
/// </para>
/// </summary>
public sealed class SecurityWorkflowTests : IDisposable
{
    private readonly TempStateDirectory _state = new("security-workflow");
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

    public SecurityWorkflowTests()
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

    private static async Task SettleAsync() => await Task.Delay(250);

    // --- The central promise ------------------------------------------------------------------------

    [Fact]
    public async Task NothingIsRecordedWhileTheOwnerIsPresent()
    {
        // The line the whole subsystem rests on. In OWNER_PRESENT the monitor discards every
        // observation before any evidence path is reachable, so this is a property of the code
        // rather than of the configuration.
        _detector.Local(ActivityKind.KeyboardActivity);
        _detector.LocalPointerMovement(20);
        _detector.Local(ActivityKind.SessionUnlocked);

        await SettleAsync();

        Assert.Equal(SecurityState.OwnerPresent, _stateService.Current.State);
        Assert.Empty(_store.All());
        Assert.Equal(0, _screen.Captures);
        Assert.Equal(0, _camera.Captures);
        Assert.Equal(0, _audio.Captures);
    }

    [Fact]
    public async Task LocalActivityWhileArmedOpensExactlyOneSession()
    {
        Arm();

        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        Assert.Equal(SecurityState.SecuritySession, _stateService.Current.State);

        var session = Assert.Single(_store.All());
        Assert.Equal(IncidentTrigger.KeyboardActivity, session.Trigger);
        Assert.Equal(SessionResolution.Active, session.Resolution);
    }

    [Fact]
    public async Task ContinuedActivityDoesNotOpenASecondSession()
    {
        // One event, one incident. A person using the machine for a minute must not produce sixty
        // of them, or the history becomes unreadable and the alert becomes noise.
        Arm();

        for (var i = 0; i < 30; i++)
        {
            _detector.Local(ActivityKind.KeyboardActivity);
        }

        await SettleAsync();

        Assert.Single(_store.All());
    }

    [Fact]
    public async Task NexusRemoteInputNeverOpensASession()
    {
        // The property the milestone turns on. Using the PC from the phone while it is armed must
        // not raise an alarm about the owner.
        Arm();

        for (var i = 0; i < 50; i++)
        {
            _detector.FromNexus(ActivityKind.PointerActivity);
            _detector.FromNexus(ActivityKind.KeyboardActivity);
        }

        await SettleAsync();

        Assert.Equal(SecurityState.Armed, _stateService.Current.State);
        Assert.Empty(_store.All());
        Assert.Equal(100, _monitor.IgnoredNexusInput);
    }

    [Fact]
    public async Task InputInjectedByOtherProgramsDoesNotOpenASession()
    {
        Arm();

        for (var i = 0; i < 20; i++)
        {
            _detector.Synthetic();
        }

        await SettleAsync();

        Assert.Equal(SecurityState.Armed, _stateService.Current.State);
        Assert.Empty(_store.All());
        Assert.Equal(20, _monitor.IgnoredSyntheticInput);
    }

    [Fact]
    public async Task AForegroundChangeAloneDoesNotOpenASession()
    {
        // An application taking focus with nobody touching anything is Windows Update or a scheduled
        // task, not a person arriving. It enriches a timeline; it does not start one.
        Arm();

        _detector.Local(ActivityKind.ForegroundWindowChanged, "notepad");
        await SettleAsync();

        Assert.Equal(SecurityState.Armed, _stateService.Current.State);
        Assert.Empty(_store.All());
    }

    [Fact]
    public async Task TinyPointerJitterDoesNotOpenASession()
    {
        // A mouse on a desk drifts. Two observations is far below the threshold.
        Arm();

        _detector.Local(ActivityKind.PointerActivity);
        _detector.Local(ActivityKind.PointerActivity);

        await SettleAsync();

        Assert.Equal(SecurityState.Armed, _stateService.Current.State);
        Assert.True(_monitor.SuppressedNoise > 0);
    }

    [Fact]
    public async Task SustainedPointerMovementDoesOpenASession()
    {
        Arm();

        _detector.LocalPointerMovement(20);
        await SettleAsync();

        Assert.Equal(SecurityState.SecuritySession, _stateService.Current.State);
        Assert.Equal(IncidentTrigger.PointerActivity, Assert.Single(_store.All()).Trigger);
    }

    [Fact]
    public async Task ArmingRestartsTheGracePeriodSoTheOwnersOwnHandDoesNotTripIt()
    {
        // Built in isolation rather than on the fixture: the fixture's monitor runs with no grace
        // period, and two monitors watching one detector would both see the event.
        using var state = new TempStateDirectory("security-grace");
        var events = new EventBus(NullLogger<EventBus>.Instance);
        var stateService = new SecurityStateService(events, state.Directory, TestLog.For<SecurityStateService>());

        using var detector = new ScriptedActivityDetector();
        using var monitor = new SecurityActivityMonitor(
            detector,
            stateService,
            events,
            new DetectionOptions { GraceSeconds = 30 },
            TestLog.For<SecurityActivityMonitor>());

        monitor.Start();

        // The owner arms by clicking something, and their hand is still on the mouse when they do.
        stateService.RequestTransition(SecurityState.Armed, "test").EnsureAccepted();
        await SettleAsync();

        detector.Local(ActivityKind.KeyboardActivity);
        detector.LocalPointerMovement(20);
        await SettleAsync();

        Assert.Equal(SecurityState.Armed, stateService.Current.State);
        Assert.True(monitor.SuppressedNoise > 0);
    }

    // --- Evidence ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AnIncidentRecordsEvidenceFromEverySource()
    {
        Arm();
        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        var session = _store.Active()!;

        Assert.True(_screen.Captures > 0, "no screen evidence");
        Assert.True(_camera.Captures > 0, "no camera evidence");
        Assert.True(_audio.Captures > 0, "no audio evidence");

        Assert.Contains(session.Evidence, e => e.Kind == EvidenceKind.Screenshot);
        Assert.Contains(session.Evidence, e => e.Kind == EvidenceKind.CameraImage);
        Assert.Contains(session.Evidence, e => e.Kind == EvidenceKind.Audio);
    }

    [Fact]
    public async Task EveryPieceOfEvidenceCarriesADigestThatMatchesItsFile()
    {
        Arm();
        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        var session = _store.Active()!;
        Assert.NotEmpty(session.Evidence);

        foreach (var evidence in session.Evidence)
        {
            var path = _store.ResolveEvidence(session.SessionId, evidence.FileName);

            Assert.NotNull(path);
            Assert.Equal(64, evidence.Sha256.Length);
            Assert.Equal(SecuritySessionStore.ComputeDigest(path!), evidence.Sha256);
            Assert.Equal(new FileInfo(path!).Length, evidence.SizeBytes);
        }
    }

    [Fact]
    public async Task AnUnavailableCameraDoesNotFailTheIncident()
    {
        // A camera another application owns, or one the owner has denied in Windows privacy
        // settings. The incident is the important thing; the camera is best effort.
        _camera.Available = false;

        Arm();
        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        var session = _store.Active()!;

        Assert.Equal(SecurityState.SecuritySession, _stateService.Current.State);
        Assert.DoesNotContain(session.Evidence, e => e.Kind == EvidenceKind.CameraImage);
        Assert.Contains(session.Timeline, t => t.Message.Contains("Camera evidence unavailable"));

        // And the sources that do work still recorded.
        Assert.Contains(session.Evidence, e => e.Kind == EvidenceKind.Screenshot);
    }

    [Fact]
    public async Task AMicrophoneThatThrowsDoesNotFailTheIncident()
    {
        _audio.Throws = true;

        Arm();
        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        var session = _store.Active()!;

        Assert.Equal(SecurityState.SecuritySession, _stateService.Current.State);
        Assert.Contains(session.Timeline, t => t.Message.Contains("Microphone evidence unavailable"));
    }

    [Fact]
    public async Task AnEmptyCaptureFileIsNotRecordedAsEvidence()
    {
        // A failed encode can leave a zero-byte file behind. Recording it would put a broken entry
        // in the history that looks like real evidence.
        _camera.ProducesEmptyFile = true;

        Arm();
        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        Assert.DoesNotContain(_store.Active()!.Evidence, e => e.Kind == EvidenceKind.CameraImage);
    }

    [Fact]
    public async Task DisablingASourceStopsItRecording()
    {
        _configuration.Update(_configuration.Current with
        {
            Camera = CameraEvidenceMode.Disabled,
            Audio = AudioEvidenceMode.Disabled,
        });

        Arm();
        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        Assert.Equal(0, _camera.Captures);
        Assert.Equal(0, _audio.Captures);
        Assert.True(_screen.Captures > 0);
    }

    // --- Timeline --------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTimelineSaysThatKeyboardActivityHappenedAndNeverWhatWasTyped()
    {
        // The no-keylogging promise, at the layer that would betray it. LocalActivity has nowhere to
        // put a keystroke, so the timeline has nothing to say beyond that one occurred.
        Arm();
        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        var session = _store.Active()!;

        Assert.Contains(session.Timeline, t => t.Message == "Local keyboard activity detected");

        foreach (var entry in session.Timeline)
        {
            Assert.DoesNotContain("typed", entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("key:", entry.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task AForegroundChangeDuringAnIncidentNamesTheApplicationOnly()
    {
        Arm();
        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        _detector.Local(ActivityKind.ForegroundWindowChanged, "notepad");
        await SettleAsync();

        Assert.Contains(
            _store.Active()!.Timeline,
            t => t.Message.Contains("notepad") && t.Message.Contains("Foreground application changed"));
    }

    // --- Resolution ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ThisWasMeReturnsToOwnerPresentAndKeepsTheEvidence()
    {
        Arm();
        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        var before = _store.Active()!.Evidence.Count;
        Assert.True(before > 0);

        var resolved = _incidents.AcknowledgeAsOwner("PHONE");

        Assert.NotNull(resolved);
        Assert.Equal(SessionResolution.AcknowledgedOwner, resolved!.Resolution);
        Assert.NotNull(resolved.EndedAtUtc);
        Assert.Equal(SecurityState.OwnerPresent, _stateService.Current.State);

        // Acknowledging is the owner saying "I know what that was", not "erase it".
        Assert.Equal(before, resolved.Evidence.Count);

        foreach (var evidence in resolved.Evidence)
        {
            Assert.NotNull(_store.ResolveEvidence(resolved.SessionId, evidence.FileName));
        }
    }

    [Fact]
    public async Task NotMeRaisesHighAlertAndKeepsTheSessionOpen()
    {
        Arm();
        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        var resolved = _incidents.ConfirmNotOwner("PHONE");

        Assert.NotNull(resolved);
        Assert.True(resolved!.HighAlert);
        Assert.Equal(SessionResolution.ConfirmedNotOwner, resolved.Resolution);
        Assert.Null(resolved.EndedAtUtc);

        // Deliberately still an incident. Marking it as an intruder is not a reason to stand down.
        Assert.Equal(SecurityState.SecuritySession, _stateService.Current.State);
    }

    [Fact]
    public async Task EvidenceKeepsBeingRecordedAfterNotMe()
    {
        Arm();
        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        _incidents.ConfirmNotOwner("PHONE");

        var before = _screen.Captures;
        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        Assert.True(_screen.Captures > before, "recording stopped after the incident was flagged");
    }

    [Fact]
    public void ResolvingWhenNothingIsHappeningDoesNothing()
    {
        Assert.Null(_incidents.AcknowledgeAsOwner("PHONE"));
        Assert.Null(_incidents.ConfirmNotOwner("PHONE"));
        Assert.Equal(SecurityState.OwnerPresent, _stateService.Current.State);
    }

    // --- Notification -----------------------------------------------------------------------------------

    [Fact]
    public async Task OneIncidentPublishesExactlyOneAlert()
    {
        // An alert that fires repeatedly is an alert that gets silenced. Progress updates use a
        // different event, which the phone does not turn into a notification.
        var alerts = 0;
        using var subscription = _events.Subscribe(e =>
        {
            if (e.EventName == SecurityEvents.SessionStarted)
            {
                Interlocked.Increment(ref alerts);
            }
        });

        Arm();

        for (var i = 0; i < 20; i++)
        {
            _detector.Local(ActivityKind.KeyboardActivity);
        }

        await SettleAsync();

        Assert.Equal(1, alerts);
    }

    [Fact]
    public async Task ResolvingPublishesAResolutionEvent()
    {
        SecuritySessionSummary? resolved = null;
        using var subscription = _events.Subscribe(e =>
        {
            if (e.EventName == SecurityEvents.SessionResolved)
            {
                resolved = e.Payload as SecuritySessionSummary;
            }
        });

        Arm();
        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        _incidents.ConfirmNotOwner("PHONE");

        Assert.NotNull(resolved);
        Assert.True(resolved!.HighAlert);
    }

    // --- Restart ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnActiveIncidentSurvivesARestartWithoutBecomingTwo()
    {
        Arm();
        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        _incidents.ConfirmNotOwner("PHONE");

        var original = _store.Active()!;
        var timelineBefore = original.Timeline.Count;

        // A second process reading the same state directory is what a restart looks like.
        var events = new EventBus(NullLogger<EventBus>.Instance);
        var stateService = new SecurityStateService(events, _state.Directory, TestLog.For<SecurityStateService>());
        var store = new SecuritySessionStore(
            _state.Directory, new SecurityStorageOptions(), TestLog.For<SecuritySessionStore>());

        using var detector = new ScriptedActivityDetector();
        using var monitor = new SecurityActivityMonitor(
            detector, stateService, events, new DetectionOptions(), TestLog.For<SecurityActivityMonitor>());

        await using var incidents = new SecurityIncidentService(
            store,
            stateService,
            monitor,
            new SecurityConfigurationStore(_state.Directory, TestLog.For<SecurityConfigurationStore>()),
            _screen,
            _camera,
            _audio,
            events,
            new EvidenceOptions(),
            TestLog.For<SecurityIncidentService>());

        incidents.Recover();

        // The same incident, not a new one: a restart must not split one event into two.
        Assert.Single(store.All());

        var recovered = store.Active()!;
        Assert.Equal(original.SessionId, recovered.SessionId);
        Assert.True(recovered.HighAlert);
        Assert.Equal(SessionResolution.ConfirmedNotOwner, recovered.Resolution);
        Assert.Equal(SecurityState.SecuritySession, stateService.Current.State);
        Assert.True(recovered.Timeline.Count > timelineBefore);
        Assert.Contains(recovered.Timeline, t => t.Message.Contains("restarted during an active security session"));

        // And the evidence recorded before the restart is still there.
        Assert.NotEmpty(recovered.Evidence);
    }

    [Fact]
    public async Task HistorySurvivesARestart()
    {
        Arm();
        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        var acknowledged = _incidents.AcknowledgeAsOwner("LOCAL")!;

        var reopened = new SecuritySessionStore(
            _state.Directory, new SecurityStorageOptions(), TestLog.For<SecuritySessionStore>());

        var recovered = Assert.Single(reopened.All());

        Assert.Equal(acknowledged.SessionId, recovered.SessionId);
        Assert.Equal(SessionResolution.AcknowledgedOwner, recovered.Resolution);
        Assert.Equal(acknowledged.Evidence.Count, recovered.Evidence.Count);
        Assert.Equal(acknowledged.Timeline.Count, recovered.Timeline.Count);
    }

    // --- Retention -------------------------------------------------------------------------------------------

    [Fact]
    public void RetentionRemovesOldResolvedIncidentsAndTheirFiles()
    {
        var store = new SecuritySessionStore(
            _state.Directory,
            new SecurityStorageOptions { RetentionDays = 30 },
            TestLog.For<SecuritySessionStore>());

        var old = Store(store, "old-session-0001", DateTimeOffset.UtcNow.AddDays(-40), SessionResolution.AcknowledgedOwner);
        var recent = Store(store, "new-session-0001", DateTimeOffset.UtcNow.AddDays(-2), SessionResolution.AcknowledgedOwner);

        Assert.Equal(1, store.Prune());

        Assert.Null(store.Find(old));
        Assert.NotNull(store.Find(recent));
        Assert.False(Directory.Exists(Path.Combine(_state.Directory.Root, "Security", "evidence", old)));
    }

    [Fact]
    public void RetentionNeverRemovesAnOpenOrFlaggedIncident()
    {
        // The one the owner is most likely to want is the one an age rule would delete first.
        var store = new SecuritySessionStore(
            _state.Directory,
            new SecurityStorageOptions { RetentionDays = 1 },
            TestLog.For<SecuritySessionStore>());

        var open = Store(store, "open-session-0001", DateTimeOffset.UtcNow.AddDays(-400), SessionResolution.Active);
        var flagged = Store(
            store,
            "alert-session-001",
            DateTimeOffset.UtcNow.AddDays(-400),
            SessionResolution.ConfirmedNotOwner,
            highAlert: true);

        store.Prune();

        Assert.NotNull(store.Find(open));
        Assert.NotNull(store.Find(flagged));
    }

    [Fact]
    public void DeletingAnIncidentRemovesItsEvidenceToo()
    {
        var store = new SecuritySessionStore(
            _state.Directory, new SecurityStorageOptions(), TestLog.For<SecuritySessionStore>());

        var id = Store(store, "doomed-session-01", DateTimeOffset.UtcNow, SessionResolution.AcknowledgedOwner);
        var folder = Path.Combine(_state.Directory.Root, "Security", "evidence", id);

        Assert.True(Directory.Exists(folder));
        Assert.True(store.Delete(id));

        Assert.Null(store.Find(id));
        Assert.False(Directory.Exists(folder));
    }

    private static string Store(
        SecuritySessionStore store,
        string id,
        DateTimeOffset started,
        SessionResolution resolution,
        bool highAlert = false)
    {
        var folder = store.EvidenceFolder(id);
        File.WriteAllText(Path.Combine(folder, "evidence.png"), "sample");

        store.Save(new SecuritySession
        {
            SessionId = id,
            StartedAtUtc = started,
            EndedAtUtc = resolution == SessionResolution.AcknowledgedOwner ? started.AddMinutes(3) : null,
            Trigger = IncidentTrigger.PointerActivity,
            Resolution = resolution,
            HighAlert = highAlert,
        });

        return id;
    }

    // --- Storage safety ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("../../../secrets.json")]
    [InlineData("..\\..\\primary-phone.json")]
    [InlineData("C:\\Windows\\System32\\config\\SAM")]
    [InlineData("sub/dir/file.png")]
    [InlineData("")]
    public void EvidenceNamesThatEscapeTheFolderAreRefused(string fileName)
    {
        // The evidence directory holds camera and microphone recordings of the owner's home. The
        // only way in is a name the session itself recorded, and the containment is re-checked
        // anyway — the same rule the filesystem capability has enforced since v0.1.
        var store = new SecuritySessionStore(
            _state.Directory, new SecurityStorageOptions(), TestLog.For<SecuritySessionStore>());

        store.EvidenceFolder("safe-session-0001");

        Assert.Null(store.ResolveEvidence("safe-session-0001", fileName));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void SessionIdsThatAreNotSafePathComponentsAreRefused(string sessionId)
    {
        var store = new SecuritySessionStore(
            _state.Directory, new SecurityStorageOptions(), TestLog.For<SecuritySessionStore>());

        Assert.Null(store.ResolveEvidence(sessionId, "file.png"));
    }

    [Fact]
    public async Task EvidenceMetadataCarriesNoPathAndNoSecret()
    {
        Arm();
        _detector.Local(ActivityKind.KeyboardActivity);
        await SettleAsync();

        var session = _store.Active()!;

        foreach (var evidence in session.Evidence)
        {
            // A file name, never a location. The phone has no business learning the layout of the
            // owner's disk, and a path in metadata is a path that ends up in a log.
            Assert.DoesNotContain(":", evidence.FileName, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", evidence.FileName, StringComparison.Ordinal);
            Assert.DoesNotContain(_state.Directory.Root, evidence.FileName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
