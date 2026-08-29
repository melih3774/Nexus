using PcCore.Capabilities.Security;
using PcCore.Capabilities.Storage;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using Xunit;

namespace PcCore.Tests.Security;

/// <summary>
/// Restart behaviour. "Restart" here means constructing a second service over the same state
/// directory, which is exactly what a process restart does.
/// </summary>
public sealed class SecurityStatePersistenceTests : IDisposable
{
    private readonly TempStateDirectory _state = new("security-state");

    public void Dispose() => _state.Dispose();

    private SecurityStateService Start() =>
        new(new RecordingEventPublisher(), _state.Directory, TestLog.For<SecurityStateService>());

    [Fact]
    public void FirstRunStartsAtOwnerPresentAndWritesTheFile()
    {
        var service = Start();

        Assert.Equal(SecurityState.OwnerPresent, service.Current.State);
        Assert.True(_state.FileExists(SecurityStateService.FileName));
    }

    [Theory]
    [InlineData(SecurityState.OwnerPresent)]
    [InlineData(SecurityState.Armed)]
    [InlineData(SecurityState.SecuritySession)]
    public void EveryStateSurvivesARestart(SecurityState target)
    {
        var first = Start();

        // Reach the target through legal transitions only.
        if (target != SecurityState.OwnerPresent)
        {
            first.RequestTransition(SecurityState.Armed, "test");
        }

        if (target == SecurityState.SecuritySession)
        {
            first.RequestTransition(SecurityState.SecuritySession, "test");
        }

        var before = first.Current;

        var restarted = Start();

        Assert.Equal(target, restarted.Current.State);
        Assert.Equal(before.PreviousState, restarted.Current.PreviousState);

        // The clock is not reset by a restart: "since" means since the state began, not since boot.
        Assert.Equal(before.SinceUtc, restarted.Current.SinceUtc);
    }

    [Fact]
    public void ASecuritySessionIsNotDowngradedByARestart()
    {
        // The headline guarantee. If unauthorised activity opened a session, rebooting the machine
        // must not be a way to clear it — otherwise a reboot is the cheapest attack on the feature.
        var first = Start();
        first.RequestTransition(SecurityState.Armed, "test");
        first.RequestTransition(SecurityState.SecuritySession, "test");

        var restarted = Start();

        Assert.Equal(SecurityState.SecuritySession, restarted.Current.State);
        Assert.NotEqual(SecurityState.Armed, restarted.Current.State);
        Assert.NotEqual(SecurityState.OwnerPresent, restarted.Current.State);

        // It ends when the owner ends it, and only then.
        Assert.True(restarted.RequestTransition(SecurityState.OwnerPresent, "security.disarm").Changed);
    }

    [Fact]
    public void RestartingDoesNotRepublishAStateChangedEvent()
    {
        var first = Start();
        first.RequestTransition(SecurityState.Armed, "test");

        var events = new RecordingEventPublisher();
        var restarted = new SecurityStateService(events, _state.Directory, TestLog.For<SecurityStateService>());

        Assert.Equal(SecurityState.Armed, restarted.Current.State);

        // Restoring a state is not a transition. Announcing one would tell listeners the owner
        // just armed the machine when they did nothing.
        Assert.Empty(events.Published);
    }

    [Fact]
    public void TheRevisionAdvancesWithEachCommittedChange()
    {
        var service = Start();
        service.RequestTransition(SecurityState.Armed, "test");
        service.RequestTransition(SecurityState.OwnerPresent, "test");

        var raw = _state.ReadRaw(SecurityStateService.FileName);

        Assert.Contains("\"revision\": 2", raw);
        Assert.Contains("\"schema_version\": 1", raw);
    }

    [Fact]
    public void PersistedStateUsesTheProtocolWireNames()
    {
        var service = Start();
        service.RequestTransition(SecurityState.Armed, "test");

        // The file is diagnosable by eye and matches what the protocol puts on the wire.
        Assert.Contains("\"state\": \"ARMED\"", _state.ReadRaw(SecurityStateService.FileName));
        Assert.Contains("\"previous_state\": \"OWNER_PRESENT\"", _state.ReadRaw(SecurityStateService.FileName));
    }

    // --- Corruption ------------------------------------------------------------------------------

    [Fact]
    public void CorruptStateFallsBackToArmedRatherThanOwnerPresent()
    {
        // Documented recovery rule: a damaged file means the previous state is unknown, and the one
        // thing that must not be inferred from "unknown" is that the owner is present. ARMED
        // asserts only that presence is unconfirmed; it does not fabricate an incident.
        Start().RequestTransition(SecurityState.Armed, "test");
        _state.Corrupt(SecurityStateService.FileName);

        var recovered = Start();

        Assert.Equal(SecurityState.Armed, recovered.Current.State);
        Assert.Null(recovered.Current.PreviousState);
    }

    [Fact]
    public void CorruptStateIsQuarantinedRatherThanDeleted()
    {
        Start();
        _state.Corrupt(SecurityStateService.FileName, "definitely not json");

        Start();

        var quarantined = Directory.GetFiles(_state.Path, SecurityStateService.FileName + ".corrupt-*");
        Assert.Single(quarantined);
        Assert.Contains("definitely not json", File.ReadAllText(quarantined[0]));
    }

    [Fact]
    public void RecoveryFromCorruptionIsItselfPersisted()
    {
        Start();
        _state.Corrupt(SecurityStateService.FileName);

        Assert.Equal(SecurityState.Armed, Start().Current.State);

        // The fallback is written down, so a second restart does not re-run recovery.
        Assert.Equal(SecurityState.Armed, Start().Current.State);
        Assert.Empty(Directory.GetFiles(_state.Path, SecurityStateService.FileName + ".corrupt-*")
            .Skip(1));
    }

    [Fact]
    public void AnUnrecognisedStateValueIsTreatedAsCorrupt()
    {
        Start();
        _state.Corrupt(
            SecurityStateService.FileName,
            """{ "schema_version": 1, "state": "NOT_A_REAL_STATE", "since_utc": "2026-01-01T00:00:00+00:00", "revision": 4 }""");

        // Well-formed JSON with a value this build does not know is still unusable state.
        Assert.Equal(SecurityState.Armed, Start().Current.State);
    }
}

/// <summary>
/// The v0.3 refactor: events are published in commit order but with the mutation lock released,
/// so subscriber code can never run underneath it.
/// </summary>
public sealed class SecurityStateEventOrderingTests : IDisposable
{
    private readonly TempStateDirectory _state = new("event-order");

    public void Dispose() => _state.Dispose();

    private sealed class ReentrantPublisher : IEventPublisherProbe
    {
        public Action<string, object?>? OnPublish { get; set; }

        private readonly List<(string Name, object? Payload)> _published = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<(string Name, object? Payload)> Published
        {
            get
            {
                lock (_gate)
                {
                    return [.. _published];
                }
            }
        }

        public void Publish(string eventName, object? payload = null)
        {
            lock (_gate)
            {
                _published.Add((eventName, payload));
            }

            OnPublish?.Invoke(eventName, payload);
        }
    }

    [Fact]
    public async Task ASubscriberCanRequestAnotherTransitionWithoutDeadlocking()
    {
        // The reason for the refactor. In v0.2 the publish happened while the mutation lock was
        // held, so any handler that touched the service again was one step from a stall. Now the
        // lock is free by the time subscribers run.
        var publisher = new ReentrantPublisher();
        var service = new SecurityStateService(publisher, _state.Directory, TestLog.For<SecurityStateService>());

        var reentered = false;
        publisher.OnPublish = (_, payload) =>
        {
            if (payload is SecurityStateChangedPayload { CurrentState: SecurityState.Armed } && !reentered)
            {
                reentered = true;
                service.RequestTransition(SecurityState.SecuritySession, "subscriber");
            }
        };

        var completed = Task.Run(() => service.RequestTransition(SecurityState.Armed, "test"));

        // A bounded wait, because the failure this guards against is a hang rather than a wrong
        // value: if the subscriber deadlocks, `completed` simply never finishes.
        var finished = await Task.WhenAny(
            completed,
            Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Same(completed, finished);
        Assert.True(reentered);
        Assert.Equal(SecurityState.SecuritySession, service.Current.State);
    }

    [Fact]
    public void SubscribersDoNotRunWhileTheMutationLockIsHeld()
    {
        // Proven by observation rather than by inspection: while a subscriber is running, another
        // thread must still be able to read and mutate the state.
        var publisher = new ReentrantPublisher();
        var service = new SecurityStateService(publisher, _state.Directory, TestLog.For<SecurityStateService>());

        var otherThreadObservedState = SecurityState.OwnerPresent;
        var otherThreadFinished = false;

        publisher.OnPublish = (_, _) =>
        {
            var other = Task.Run(() =>
            {
                otherThreadObservedState = service.Current.State;
                otherThreadFinished = true;
            });

            other.Wait(TimeSpan.FromSeconds(5));
        };

        service.RequestTransition(SecurityState.Armed, "test");

        Assert.True(otherThreadFinished, "another thread could not read the state while a subscriber was running");
        Assert.Equal(SecurityState.Armed, otherThreadObservedState);
    }

    [Fact]
    public async Task EventOrderStillMatchesCommitOrderUnderContention()
    {
        var publisher = new ReentrantPublisher();
        var service = new SecurityStateService(publisher, _state.Directory, TestLog.For<SecurityStateService>());

        using var start = new Barrier(16);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            start.SignalAndWait();
            var target = i % 2 == 0 ? SecurityState.Armed : SecurityState.OwnerPresent;

            for (var n = 0; n < 30; n++)
            {
                service.RequestTransition(target, "test");
            }
        })));

        // The trail must be a valid walk of the transition table ending at the final state. That
        // is only possible if published order equals committed order.
        var walked = SecurityState.OwnerPresent;
        foreach (var (_, payload) in publisher.Published)
        {
            var step = (SecurityStateChangedPayload)payload!;
            Assert.Equal(walked, step.PreviousState);
            Assert.True(SecurityStateTransitions.IsAllowed(step.PreviousState, step.CurrentState));
            walked = step.CurrentState;
        }

        Assert.Equal(service.Current.State, walked);
    }

    [Fact]
    public async Task PersistedStateAgreesWithTheFinalStateAfterContention()
    {
        var service = new SecurityStateService(
            new RecordingEventPublisher(), _state.Directory, TestLog.For<SecurityStateService>());

        using var start = new Barrier(12);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
        {
            start.SignalAndWait();
            service.RequestTransition(i % 2 == 0 ? SecurityState.Armed : SecurityState.OwnerPresent, "test");
        })));

        var final = service.Current.State;
        var restarted = new SecurityStateService(
            new RecordingEventPublisher(), _state.Directory, TestLog.For<SecurityStateService>());

        Assert.Equal(final, restarted.Current.State);
    }
}

/// <summary>Lets a test observe publishes and act during them.</summary>
internal interface IEventPublisherProbe : PcCore.Capabilities.Abstractions.IEventPublisher
{
    Action<string, object?>? OnPublish { get; set; }

    IReadOnlyList<(string Name, object? Payload)> Published { get; }
}
