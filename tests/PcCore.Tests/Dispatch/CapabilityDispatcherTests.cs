using System.Text.Json;
using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using Xunit;

namespace PcCore.Tests.Dispatch;

public sealed class CapabilityDispatcherTests
{
    private sealed record EchoParameters
    {
        public string? Message { get; init; }
        public int Count { get; init; }
    }

    private sealed record EchoResult(string Message, int Count);

    private sealed class EchoAction : CapabilityAction<EchoParameters, EchoResult>
    {
        public override string Name => "test.echo";
        public override string Description => "Echoes its parameters.";

        public CapabilityContext? LastContext { get; private set; }

        protected override ValueTask<EchoResult> ExecuteAsync(
            EchoParameters parameters,
            CapabilityContext context,
            CancellationToken cancellationToken)
        {
            LastContext = context;
            return ValueTask.FromResult(new EchoResult(parameters.Message ?? "(none)", parameters.Count));
        }
    }

    private sealed class ThrowingAction(Exception exception) : CapabilityAction<NoParameters, EchoResult>
    {
        public override string Name => "test.throw";
        public override string Description => "Always throws.";

        protected override ValueTask<EchoResult> ExecuteAsync(
            NoParameters parameters,
            CapabilityContext context,
            CancellationToken cancellationToken) => throw exception;
    }

    private static (ICapabilityDispatcher Dispatcher, CapabilityRegistry Registry) Build(params ICapabilityAction[] actions)
    {
        var registry = new CapabilityRegistry(actions);
        return (
            new CapabilityDispatcher(registry, new DefaultCapabilityPolicy(), TestLog.For<CapabilityDispatcher>()),
            registry);
    }

    private static RequestEnvelope Request(string action, string? paramsJson = null) => new()
    {
        Id = "req-1",
        Action = action,
        Params = paramsJson is null ? null : JsonDocument.Parse(paramsJson).RootElement,
    };

    // --- Success ------------------------------------------------------------------------------

    [Fact]
    public async Task Dispatch_InvokesTheActionWithBoundParameters()
    {
        var echo = new EchoAction();
        var (dispatcher, _) = Build(echo);

        var response = await dispatcher.DispatchAsync(
            Request("test.echo", """{ "message": "hi", "count": 3 }"""),
            CallerDescriptor.Internal,
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Null(response.Error);
        Assert.Equal("req-1", response.Id);
        Assert.Equal("test.echo", response.Action);

        var result = Assert.IsType<EchoResult>(response.Result);
        Assert.Equal("hi", result.Message);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task Dispatch_PassesTheRequestIdAndCallerInTheContext()
    {
        var echo = new EchoAction();
        var (dispatcher, _) = Build(echo);
        var caller = new CallerDescriptor("loopback-http", "conn-9", ActorType.Local);

        await dispatcher.DispatchAsync(Request("test.echo"), caller, TestContext.Current.CancellationToken);

        Assert.Equal("req-1", echo.LastContext!.RequestId);
        Assert.Equal(caller, echo.LastContext.Caller);

        // Identity metadata reaches the capability intact, which is the whole point of carrying it.
        Assert.Equal(ActorType.Local, echo.LastContext.Caller.ActorType);
        Assert.Null(echo.LastContext.Caller.DeviceId);
    }

    // --- Failure mapping ----------------------------------------------------------------------

    [Fact]
    public async Task Dispatch_ReturnsUnknownActionForAnUnregisteredName()
    {
        var (dispatcher, _) = Build(new EchoAction());

        var response = await dispatcher.DispatchAsync(Request("does.not.exist"), CallerDescriptor.Internal,
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Null(response.Result);
        Assert.Equal(ErrorCodes.UnknownAction, response.Error!.Code);
        // The id is echoed so the caller can correlate the failure.
        Assert.Equal("req-1", response.Id);
    }

    [Fact]
    public async Task Dispatch_ReturnsInvalidParametersForABadPayload()
    {
        var (dispatcher, _) = Build(new EchoAction());

        var response = await dispatcher.DispatchAsync(
            Request("test.echo", """{ "count": "not a number" }"""),
            CallerDescriptor.Internal,
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.InvalidParameters, response.Error!.Code);
    }

    [Fact]
    public async Task Dispatch_ReturnsInvalidParametersWhenParamsIsNotAnObject()
    {
        var (dispatcher, _) = Build(new EchoAction());

        var response = await dispatcher.DispatchAsync(Request("test.echo", "[1,2,3]"), CallerDescriptor.Internal,
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.InvalidParameters, response.Error!.Code);
    }

    [Theory]
    [InlineData(ErrorCodes.NotFound)]
    [InlineData(ErrorCodes.PathNotAllowed)]
    [InlineData(ErrorCodes.AccessDenied)]
    [InlineData(ErrorCodes.Unsupported)]
    public async Task Dispatch_PropagatesACapabilityExceptionCode(string code)
    {
        var (dispatcher, _) = Build(new ThrowingAction(new CapabilityException(code, "expected failure", "extra")));

        var response = await dispatcher.DispatchAsync(Request("test.throw"), CallerDescriptor.Internal,
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(code, response.Error!.Code);
        Assert.Equal("expected failure", response.Error.Message);
        Assert.Equal("extra", response.Error.Details);
    }

    [Fact]
    public async Task Dispatch_ConvertsAnUnexpectedExceptionToInternalErrorWithoutLeakingDetails()
    {
        // An exception message can carry paths, user names, or internals a caller must not see.
        var secret = new InvalidOperationException(@"C:\Users\Someone\secret-plans.txt is locked");
        var (dispatcher, _) = Build(new ThrowingAction(secret));

        var response = await dispatcher.DispatchAsync(Request("test.throw"), CallerDescriptor.Internal,
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.InternalError, response.Error!.Code);
        Assert.Null(response.Error.Details);
        Assert.DoesNotContain("secret-plans", response.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\Users", response.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dispatch_ReportsCancellation()
    {
        var (dispatcher, _) = Build(new ThrowingAction(new OperationCanceledException()));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var response = await dispatcher.DispatchAsync(Request("test.throw"), CallerDescriptor.Internal, cts.Token);

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.InternalError, response.Error!.Code);
    }

    // --- Registry -----------------------------------------------------------------------------

    [Fact]
    public void Registry_RejectsDuplicateActionNames()
    {
        // A duplicate would silently shadow a capability. Fail at startup, not at request time.
        var exception = Assert.Throws<InvalidOperationException>(
            () => new CapabilityRegistry([new EchoAction(), new EchoAction()]));

        Assert.Contains("test.echo", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_ExposesActionsSorted()
    {
        var (_, registry) = Build(new ThrowingAction(new Exception()), new EchoAction());

        Assert.Equal(["test.echo", "test.throw"], registry.Actions.Select(a => a.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nope")]
    public void Registry_LookupMisses(string name)
    {
        var (_, registry) = Build(new EchoAction());

        Assert.False(registry.TryGet(name, out var action));
        Assert.Null(action);
    }
}

public sealed class EventBusTests
{
    [Fact]
    public void PublishReachesEverySubscriber()
    {
        var bus = new EventBus(TestLog.For<EventBus>());
        var first = new List<EventEnvelope>();
        var second = new List<EventEnvelope>();

        using (bus.Subscribe(first.Add))
        using (bus.Subscribe(second.Add))
        {
            bus.Publish("core.connected", new { detail = 1 });
        }

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal("core.connected", first[0].EventName);
        Assert.Equal(MessageTypes.Event, first[0].Type);
        Assert.NotEqual(default, first[0].TimestampUtc);
        Assert.False(string.IsNullOrWhiteSpace(first[0].Id));
    }

    [Fact]
    public void DisposingASubscriptionStopsDelivery()
    {
        var bus = new EventBus(TestLog.For<EventBus>());
        var received = new List<EventEnvelope>();

        var subscription = bus.Subscribe(received.Add);
        bus.Publish("first");
        subscription.Dispose();
        bus.Publish("second");

        Assert.Single(received);
        Assert.Equal("first", received[0].EventName);
    }

    [Fact]
    public void AThrowingSubscriberDoesNotStopTheOthers()
    {
        // A dead WebSocket must not break event delivery for every other client.
        var bus = new EventBus(TestLog.For<EventBus>());
        var received = new List<EventEnvelope>();

        using (bus.Subscribe(_ => throw new InvalidOperationException("socket closed")))
        using (bus.Subscribe(received.Add))
        {
            bus.Publish("core.connected");
        }

        Assert.Single(received);
    }
}
