using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.Storage;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using Xunit;

namespace PcCore.Tests.Security;

/// <summary>
/// Records every event a service publishes, so tests can assert both what was published and —
/// just as importantly — what was not.
/// </summary>
internal sealed class RecordingEventPublisher : IEventPublisher
{
    private readonly Lock _gate = new();
    private readonly List<(string Name, object? Payload)> _published = [];

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
    }
}

public sealed class SecurityStateServiceTests
{
    private static (SecurityStateService Service, RecordingEventPublisher Events) Build()
    {
        // Each service gets its own throwaway state directory, so persistence added in v0.3 never
        // leaks between tests or touches the owner's real state.
        var events = new RecordingEventPublisher();
        var state = new TempStateDirectory();
        return (new SecurityStateService(events, state.Directory, TestLog.For<SecurityStateService>()), events);
    }

    /// <summary>Drives the service into a state through legal transitions only.</summary>
    private static (SecurityStateService Service, RecordingEventPublisher Events) BuildIn(SecurityState state)
    {
        var (service, events) = Build();

        switch (state)
        {
            case SecurityState.Armed:
                service.RequestTransition(SecurityState.Armed, "test");
                break;

            case SecurityState.SecuritySession:
                service.RequestTransition(SecurityState.Armed, "test");
                service.RequestTransition(SecurityState.SecuritySession, "test");
                break;
        }

        return (service, events);
    }

    // --- Initial state --------------------------------------------------------------------------

    [Fact]
    public void StartsInOwnerPresentWithNoPreviousState()
    {
        var before = DateTimeOffset.UtcNow;
        var (service, events) = Build();
        var after = DateTimeOffset.UtcNow;

        var snapshot = service.Current;

        Assert.Equal(SecurityState.OwnerPresent, snapshot.State);
        Assert.Null(snapshot.PreviousState);
        Assert.InRange(snapshot.SinceUtc, before, after);

        // Startup is not a state change; announcing one would be a lie to every listener.
        Assert.Empty(events.Published);
    }

    // --- Valid transitions ----------------------------------------------------------------------

    [Theory]
    [InlineData(SecurityState.OwnerPresent, SecurityState.Armed)]
    [InlineData(SecurityState.Armed, SecurityState.OwnerPresent)]
    [InlineData(SecurityState.Armed, SecurityState.SecuritySession)]
    [InlineData(SecurityState.SecuritySession, SecurityState.OwnerPresent)]
    public void AllowsEveryTransitionInTheTable(SecurityState from, SecurityState to)
    {
        var (service, events) = BuildIn(from);
        var eventsBefore = events.Published.Count;

        var result = service.RequestTransition(to, "test");

        Assert.Equal(SecurityTransitionOutcome.Changed, result.Outcome);
        Assert.True(result.Changed);
        Assert.Equal(to, service.Current.State);
        Assert.Equal(from, service.Current.PreviousState);
        Assert.Equal(eventsBefore + 1, events.Published.Count);
    }

    [Fact]
    public void RecordsWhenTheCurrentStateBegan()
    {
        var (service, _) = Build();
        var startedAt = service.Current.SinceUtc;

        var before = DateTimeOffset.UtcNow;
        service.RequestTransition(SecurityState.Armed, "test");
        var after = DateTimeOffset.UtcNow;

        var snapshot = service.Current;

        Assert.InRange(snapshot.SinceUtc, before, after);
        Assert.True(snapshot.SinceUtc >= startedAt, "SinceUtc must move forward on a real change.");
    }

    [Fact]
    public void InternalCallersCanStartASecuritySessionWithoutAPublicCapability()
    {
        // There is no security.start_session action by design; the escalation path a future
        // activity detector needs is this service method.
        var (service, _) = BuildIn(SecurityState.Armed);

        var result = service.RequestTransition(SecurityState.SecuritySession, "internal:test-detector");

        Assert.True(result.Changed);
        Assert.Equal(SecurityState.SecuritySession, service.Current.State);
    }

    // --- Rejected transitions -------------------------------------------------------------------

    [Theory]
    [InlineData(SecurityState.OwnerPresent, SecurityState.SecuritySession)]
    [InlineData(SecurityState.SecuritySession, SecurityState.Armed)]
    public void RefusesTransitionsThatAreNotInTheTable(SecurityState from, SecurityState to)
    {
        var (service, events) = BuildIn(from);
        var eventsBefore = events.Published.Count;

        var result = service.RequestTransition(to, "test");

        Assert.Equal(SecurityTransitionOutcome.Rejected, result.Outcome);
        Assert.False(result.Accepted);
        Assert.Equal(to, result.Requested);

        // The refusal must leave the state exactly where it was — never quietly redirected.
        Assert.Equal(from, service.Current.State);
        Assert.Equal(eventsBefore, events.Published.Count);
    }

    [Fact]
    public void RejectionMapsToAStructuredProtocolError()
    {
        var (service, _) = Build();

        var result = service.RequestTransition(SecurityState.SecuritySession, "test");
        var exception = Assert.Throws<CapabilityException>(() => result.EnsureAccepted());

        Assert.Equal(ErrorCodes.InvalidStateTransition, exception.Code);
    }

    [Theory]
    [InlineData(SecurityState.OwnerPresent, SecurityState.Armed, true)]
    [InlineData(SecurityState.OwnerPresent, SecurityState.SecuritySession, false)]
    [InlineData(SecurityState.OwnerPresent, SecurityState.OwnerPresent, true)]
    [InlineData(SecurityState.SecuritySession, SecurityState.Armed, false)]
    public void CanTransitionToAgreesWithRequestTransition(SecurityState from, SecurityState to, bool expected)
    {
        var (service, _) = BuildIn(from);

        Assert.Equal(expected, service.CanTransitionTo(to));
        Assert.Equal(expected, service.RequestTransition(to, "test").Accepted);
    }

    // --- Idempotency ----------------------------------------------------------------------------

    [Theory]
    [InlineData(SecurityState.OwnerPresent)]
    [InlineData(SecurityState.Armed)]
    [InlineData(SecurityState.SecuritySession)]
    public void RequestingTheCurrentStateSucceedsAndChangesNothing(SecurityState state)
    {
        var (service, events) = BuildIn(state);
        var before = service.Current;
        var eventsBefore = events.Published.Count;

        var result = service.RequestTransition(state, "test");

        Assert.Equal(SecurityTransitionOutcome.Unchanged, result.Outcome);
        Assert.True(result.Accepted);
        Assert.False(result.Changed);

        // Same state, same start time: a no-op must not reset the clock either.
        Assert.Equal(before, service.Current);
        Assert.Equal(eventsBefore, events.Published.Count);
    }

    // --- Events ---------------------------------------------------------------------------------

    [Fact]
    public void PublishesStateChangedWithBothStates()
    {
        var (service, events) = Build();

        service.RequestTransition(SecurityState.Armed, "test");

        var (name, payload) = Assert.Single(events.Published);
        Assert.Equal(SecurityEvents.StateChanged, name);
        Assert.Equal("security.state_changed", name);

        var typed = Assert.IsType<SecurityStateChangedPayload>(payload);
        Assert.Equal(SecurityState.OwnerPresent, typed.PreviousState);
        Assert.Equal(SecurityState.Armed, typed.CurrentState);
    }

    [Fact]
    public void PublishesOneEventPerRealChangeAcrossAFullCycle()
    {
        var (service, events) = Build();

        service.RequestTransition(SecurityState.Armed, "test");
        service.RequestTransition(SecurityState.Armed, "test");            // no-op
        service.RequestTransition(SecurityState.SecuritySession, "test");
        service.RequestTransition(SecurityState.Armed, "test");            // rejected
        service.RequestTransition(SecurityState.OwnerPresent, "test");
        service.RequestTransition(SecurityState.OwnerPresent, "test");     // no-op

        var transitions = events.Published
            .Select(e => (SecurityStateChangedPayload)e.Payload!)
            .Select(p => (p.PreviousState, p.CurrentState))
            .ToArray();

        Assert.Equal(
            [
                (SecurityState.OwnerPresent, SecurityState.Armed),
                (SecurityState.Armed, SecurityState.SecuritySession),
                (SecurityState.SecuritySession, SecurityState.OwnerPresent),
            ],
            transitions);
    }

    // --- Concurrency ----------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentArmRequestsProduceExactlyOneChangeAndOneEvent()
    {
        // The race this guards: two callers both observe OWNER_PRESENT, both apply the change, and
        // two state_changed events go out for one real transition.
        var (service, events) = Build();

        using var start = new Barrier(32);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            return service.RequestTransition(SecurityState.Armed, "test");
        })));

        Assert.Equal(1, results.Count(r => r.Changed));
        Assert.Equal(31, results.Count(r => r.Outcome == SecurityTransitionOutcome.Unchanged));
        Assert.Single(events.Published);
        Assert.Equal(SecurityState.Armed, service.Current.State);
    }

    [Fact]
    public async Task ConcurrentOppositeTransitionsLeaveConsistentStateAndAMatchingEventTrail()
    {
        // Hammer arm/disarm from many threads. Whatever interleaving happens, the event trail must
        // be a valid walk through the transition table that ends at the final state — that is what
        // proves events cannot be reordered relative to the state itself.
        var (service, events) = Build();

        using var start = new Barrier(24);
        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
        {
            start.SignalAndWait();
            var target = i % 2 == 0 ? SecurityState.Armed : SecurityState.OwnerPresent;

            for (var n = 0; n < 25; n++)
            {
                service.RequestTransition(target, "test");
            }
        })));

        var trail = events.Published.Select(e => (SecurityStateChangedPayload)e.Payload!).ToArray();

        var walked = SecurityState.OwnerPresent;
        foreach (var step in trail)
        {
            Assert.Equal(walked, step.PreviousState);
            Assert.True(
                SecurityStateTransitions.IsAllowed(step.PreviousState, step.CurrentState),
                $"event trail contains an illegal transition {step.PreviousState} -> {step.CurrentState}");
            walked = step.CurrentState;
        }

        Assert.Equal(service.Current.State, walked);
    }

    // --- Transition table -----------------------------------------------------------------------

    [Fact]
    public void TheTransitionTableIsExactlyTheFourDocumentedEdges()
    {
        // Pinned deliberately: adding an edge is a security decision, so it should require
        // changing a test that says so out loud.
        Assert.Equal(
            [
                (SecurityState.OwnerPresent, SecurityState.Armed),
                (SecurityState.Armed, SecurityState.OwnerPresent),
                (SecurityState.Armed, SecurityState.SecuritySession),
                (SecurityState.SecuritySession, SecurityState.OwnerPresent),
            ],
            SecurityStateTransitions.Allowed);

        // SECURITY_SESSION -> ARMED stays out: nothing in v0.2 can legitimately end a session,
        // and letting one close itself would hide an incident from the owner.
        Assert.False(SecurityStateTransitions.IsAllowed(SecurityState.SecuritySession, SecurityState.Armed));
    }
}
