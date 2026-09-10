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
/// Getting at evidence: after the incident is resolved, in ranges, and when the file is gone.
/// <para>
/// The first of those is the one that matters most. Acknowledging an incident decides <em>what
/// happened</em>; it must not decide whether the owner may still look at what was recorded. An
/// answer the owner can only review before answering it is not a record, and "I pressed the wrong
/// button and lost the footage" is the failure this pins shut.
/// </para>
/// </summary>
public sealed class EvidenceAccessTests : IDisposable
{
    private readonly TempStateDirectory _state = new("evidence-access");
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

    public EvidenceAccessTests()
    {
        _events = new EventBus(NullLogger<EventBus>.Instance);
        _stateService = new SecurityStateService(_events, _state.Directory, TestLog.For<SecurityStateService>());
        _configuration = new SecurityConfigurationStore(_state.Directory, TestLog.For<SecurityConfigurationStore>());
        _store = new SecuritySessionStore(_state.Directory, new SecurityStorageOptions(), TestLog.For<SecuritySessionStore>());

        _monitor = new SecurityActivityMonitor(
            _detector,
            new NullNexusInputAttribution(),
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

    private static readonly CapabilityContext Context = new("test", CallerDescriptor.Internal);

    private async Task<SecuritySession> OpenIncidentAsync()
    {
        _stateService.RequestTransition(SecurityState.Armed, "test").EnsureAccepted();
        _detector.Local(ActivityKind.KeyboardActivity);
        await Task.Delay(250, TestContext.Current.CancellationToken);

        return Assert.Single(_store.All());
    }

    private async Task<GetEvidenceResult> FetchAsync(
        string sessionId,
        string evidenceId,
        long offset = 0,
        int? maxBytes = null)
    {
        var action = (ICapabilityAction)new GetSecurityEvidenceAction(_store);

        var result = await action.InvokeAsync(
            new GetEvidenceParameters
            {
                SessionId = sessionId,
                EvidenceId = evidenceId,
                Offset = offset,
                MaxBytes = maxBytes,
            },
            Context,
            TestContext.Current.CancellationToken);

        return (GetEvidenceResult)result!;
    }

    // --- Access after resolution -------------------------------------------------------------------

    [Fact]
    public async Task EvidenceIsStillReadableAfterTheOwnerAcknowledgesTheIncident()
    {
        var session = await OpenIncidentAsync();
        var evidence = Assert.Single(_store.Find(session.SessionId)!.Evidence.Take(1));

        _incidents.AcknowledgeAsOwner("TEST");

        var resolved = _store.Find(session.SessionId)!;
        Assert.Equal(SessionResolution.AcknowledgedOwner, resolved.Resolution);

        var fetched = await FetchAsync(session.SessionId, evidence.EvidenceId);

        Assert.Equal(EvidenceAvailability.Available, fetched.Availability);
        Assert.NotNull(fetched.Data);
        Assert.True(fetched.ChunkBytes > 0);
    }

    /// <summary>
    /// The same for <c>Not me</c>, which keeps the incident open but flags it. Both answers are
    /// resolutions, and neither is a reason to withdraw the record.
    /// </summary>
    [Fact]
    public async Task EvidenceIsStillReadableAfterTheOwnerFlagsTheIncident()
    {
        var session = await OpenIncidentAsync();
        var evidence = _store.Find(session.SessionId)!.Evidence[0];

        _incidents.ConfirmNotOwner("TEST");

        var fetched = await FetchAsync(session.SessionId, evidence.EvidenceId);

        Assert.Equal(EvidenceAvailability.Available, fetched.Availability);
        Assert.NotNull(fetched.Data);
    }

    /// <summary>A resolved incident is still listed, so the phone can reach it from history at all.</summary>
    [Fact]
    public async Task AResolvedIncidentStaysInTheHistoryWithItsEvidence()
    {
        var session = await OpenIncidentAsync();
        _incidents.AcknowledgeAsOwner("TEST");

        var listed = Assert.Single(_store.All());
        Assert.Equal(session.SessionId, listed.SessionId);

        var detail = (ICapabilityAction)new GetSecuritySessionAction(_store, _stateService);

        var result = (GetSessionResult)(await detail.InvokeAsync(
            new SessionIdParameters { SessionId = session.SessionId },
            Context,
            TestContext.Current.CancellationToken))!;

        Assert.NotNull(result.Session);
        Assert.NotEmpty(result.Session!.Evidence);

        // And no longer the active one, which is the state that used to be conflated with "gone".
        Assert.False(result.HasActiveSession);
    }

    // --- Ranged reads ------------------------------------------------------------------------------

    [Fact]
    public async Task AFileCanBeReassembledFromChunks()
    {
        var session = await OpenIncidentAsync();
        var evidence = _store.Find(session.SessionId)!.Evidence[0];

        var whole = await FetchAsync(session.SessionId, evidence.EvidenceId);
        var expected = Convert.FromBase64String(whole.Data!);

        var assembled = new List<byte>();
        long offset = 0;

        // Deliberately tiny chunks: the point is that the seams line up, not that it is fast.
        for (var guard = 0; guard < 500; guard++)
        {
            var chunk = await FetchAsync(session.SessionId, evidence.EvidenceId, offset, maxBytes: 8);

            Assert.Equal(offset, chunk.Offset);

            if (chunk.Data is not null)
            {
                assembled.AddRange(Convert.FromBase64String(chunk.Data));
            }

            offset += chunk.ChunkBytes;

            if (chunk.EndOfFile)
            {
                break;
            }
        }

        Assert.Equal(expected, assembled);
    }

    [Fact]
    public async Task ReadingPastTheEndReturnsNothingRatherThanFailing()
    {
        var session = await OpenIncidentAsync();
        var evidence = _store.Find(session.SessionId)!.Evidence[0];

        var chunk = await FetchAsync(session.SessionId, evidence.EvidenceId, offset: 10_000_000);

        Assert.Equal(0, chunk.ChunkBytes);
        Assert.True(chunk.EndOfFile);
        Assert.Null(chunk.Data);
    }

    // --- Evidence that is no longer there ----------------------------------------------------------

    /// <summary>
    /// A file removed from disk is reported as <c>MISSING</c> rather than thrown as an error, so the
    /// phone can say "deleted or expired" instead of "something went wrong".
    /// </summary>
    [Fact]
    public async Task DeletedEvidenceIsReportedAsMissingRatherThanFailing()
    {
        var session = await OpenIncidentAsync();
        var evidence = _store.Find(session.SessionId)!.Evidence[0];

        var path = _store.ResolveEvidence(session.SessionId, evidence.FileName);
        Assert.NotNull(path);
        File.Delete(path!);

        var fetched = await FetchAsync(session.SessionId, evidence.EvidenceId);

        Assert.Equal(EvidenceAvailability.Missing, fetched.Availability);
        Assert.Null(fetched.Data);
        Assert.NotNull(fetched.Unavailable);

        // The record still describes what was captured, so the row is not blank.
        Assert.Equal(evidence.Kind, fetched.Kind);
        Assert.Equal(evidence.Sha256, fetched.Sha256);
    }

    [Fact]
    public async Task AnUnknownEvidenceIdIsStillAProtocolError()
    {
        var session = await OpenIncidentAsync();

        var error = await Assert.ThrowsAsync<CapabilityException>(
            async () => await FetchAsync(session.SessionId, "0000000000000000"));

        Assert.Equal(ErrorCodes.NotFound, error.Code);
    }

    [Fact]
    public async Task AnUnknownSessionIsStillAProtocolError()
    {
        await OpenIncidentAsync();

        var error = await Assert.ThrowsAsync<CapabilityException>(
            async () => await FetchAsync("not-a-session", "0000000000000000"));

        Assert.Equal(ErrorCodes.NotFound, error.Code);
    }
}
