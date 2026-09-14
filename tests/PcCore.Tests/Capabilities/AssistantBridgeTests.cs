using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Assistant;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;

using Xunit;

namespace PcCore.Tests.Capabilities;

/// <summary>
/// PC Core's half of the PC Assistant bridge, against a scripted PC Assistant: the states a phone is
/// shown, what is refused before anything is relayed, how refusals map to PC Core's codes, and which
/// events reach the bus.
/// </summary>
public sealed class AssistantBridgeTests : IAsyncLifetime
{
    private readonly FakePcAssistant _pcAssistant = new();
    private readonly RecordingPublisher _events = new();
    private readonly AssistantBridgeOptions _options = new()
    {
        Enabled = true,
        ServerExecutable = @"C:\Programs\PcAssistant\PcAssistant.Desktop.exe",
        MessageIntervalMilliseconds = 200,
    };

    private AssistantBridge _bridge = null!;

    public ValueTask InitializeAsync()
    {
        _bridge = new AssistantBridge(_options, _pcAssistant, _events, NullLogger<AssistantBridge>.Instance);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _bridge.DisposeAsync();
        await _pcAssistant.DisposeAsync();
    }

    private static CapabilityContext Context(string connection = "c1") =>
        new("r1", new CallerDescriptor("remote", connection, ActorType.Device, "device-1"));

    private static Task<T> Run<T>(CapabilityAction<NoParameters, T> action, string connection = "c1")
        where T : class => Invoke<NoParameters, T>(action, new NoParameters(), connection);

    private static async Task<TResult> Invoke<TParams, TResult>(CapabilityAction<TParams, TResult> action, TParams parameters, string connection = "c1")
        where TParams : class
        where TResult : class =>
        (TResult)(await ((ICapabilityAction)action).InvokeAsync(parameters, Context(connection), TestContext.Current.CancellationToken))!;

    // --- States ------------------------------------------------------------------------------------

    [Fact]
    public async Task TurnedOffItSaysSoAndReachesNothing()
    {
        _options.Enabled = false;

        var status = await Run(new GetAssistantStatusAction(_bridge));
        Assert.Equal(AssistantAvailability.Disabled, status.State);

        var refused = await Assert.ThrowsAsync<CapabilityException>(() =>
            Invoke(new ListAssistantTasksAction(_bridge), new AssistantTaskListParameters()));
        Assert.Equal(ErrorCodes.AssistantUnavailable, refused.Code);
        Assert.Equal("DISABLED", refused.Details);
        Assert.Equal(0, _pcAssistant.Connections);
    }

    [Fact]
    public async Task EnabledWithoutATrustedExecutableIsNotConfigured()
    {
        _options.ServerExecutable = " ";

        Assert.Equal(AssistantAvailability.NotConfigured, (await Run(new GetAssistantStatusAction(_bridge))).State);
        Assert.Equal(0, _pcAssistant.Connections);
    }

    [Fact]
    public async Task NothingListeningIsNotRunning()
    {
        _pcAssistant.Running = false;

        Assert.Equal(AssistantAvailability.NotRunning, (await Run(new GetAssistantStatusAction(_bridge))).State);
    }

    [Fact]
    public async Task APcAssistantThatHasNotTrustedThisPcCoreIsReportedAsSuch()
    {
        _pcAssistant.HelloError = "client_not_trusted";

        var status = await Run(new GetAssistantStatusAction(_bridge));
        Assert.Equal(AssistantAvailability.NotTrusted, status.State);
        Assert.Contains("Capabilities panel", status.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData("unsupported_protocol_version", 1)]
    public async Task ADifferentBridgeVersionIsIncompatible(string? helloError, int serverVersion)
    {
        _pcAssistant.HelloError = helloError;
        _pcAssistant.ProtocolVersion = serverVersion;

        Assert.Equal(AssistantAvailability.Incompatible, (await Run(new GetAssistantStatusAction(_bridge))).State);
    }

    [Fact]
    public async Task ASignedOutClaudeBlocksNewWorkButNotReading()
    {
        _pcAssistant.Claude = "signed_out";

        var status = await Run(new GetAssistantStatusAction(_bridge));
        Assert.Equal(AssistantAvailability.ClaudeNotReady, status.State);
        Assert.Equal("signed_out", status.Claude);

        // Reading existing tasks does not need Claude.
        var list = await Invoke(new ListAssistantTasksAction(_bridge), new AssistantTaskListParameters());
        Assert.Single(list.Tasks);

        // Starting work does, and says which state stopped it.
        var refused = await Assert.ThrowsAsync<CapabilityException>(() =>
            Invoke(new CreateAssistantTaskAction(_bridge), new AssistantCreateTaskParameters { Mode = "auto", Prompt = "hello" }));
        Assert.Equal(ErrorCodes.AssistantUnavailable, refused.Code);
        Assert.Equal("CLAUDE_NOT_READY", refused.Details);
        Assert.DoesNotContain("tasks.create", _pcAssistant.WorkRequests);
    }

    [Fact]
    public async Task PcAssistantsReasonForClaudeNotBeingReadyReachesThePhone()
    {
        _pcAssistant.Claude = "signed_out";
        _pcAssistant.ClaudeDetail = "Claude Code is signed in but refused: Your organization has disabled Claude subscription access for Claude Code";

        var status = await Run(new GetAssistantStatusAction(_bridge));

        Assert.Equal(AssistantAvailability.ClaudeNotReady, status.State);
        Assert.Contains("subscription access", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadyReportsVersions()
    {
        var status = await Run(new GetAssistantStatusAction(_bridge));

        Assert.Equal(AssistantAvailability.Ready, status.State);
        Assert.True(status.CanWork);
        Assert.Equal("0.9.0-test", status.ServerVersion);
        Assert.Equal(1, status.ProtocolVersion);
        Assert.Equal("2.1-test", status.ClaudeVersion);
    }

    // --- What is relayed, and what never is ----------------------------------------------------------

    [Fact]
    public async Task ACreateRelaysOnlyAGeneralTaskWithItsModeAndPrompt()
    {
        var created = await Invoke(new CreateAssistantTaskAction(_bridge), new AssistantCreateTaskParameters { Mode = "ask", Prompt = "  plan my week  " });
        Assert.Equal("ask", created.Task.Mode);

        var request = Assert.Single(_pcAssistant.Requests, r => r.Method == "tasks.create");
        Assert.Equal("general", request.Params.GetProperty("kind").GetString());
        Assert.Equal("ask", request.Params.GetProperty("mode").GetString());
        Assert.Equal("plan my week", request.Params.GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task MalformedRequestsAreRefusedBeforeReachingPcAssistant()
    {
        var id = Guid.NewGuid().ToString();

        async Task Refused(Func<Task> call, string code)
        {
            var refusal = await Assert.ThrowsAsync<CapabilityException>(call);
            Assert.Equal(code, refusal.Code);
        }

        await Refused(() => Invoke(new CreateAssistantTaskAction(_bridge), new AssistantCreateTaskParameters { Kind = "workspace", Mode = "auto", Prompt = "x" }), ErrorCodes.Unsupported);
        await Refused(() => Invoke(new CreateAssistantTaskAction(_bridge), new AssistantCreateTaskParameters { Mode = "yolo", Prompt = "x" }), ErrorCodes.InvalidParameters);
        await Refused(() => Invoke(new CreateAssistantTaskAction(_bridge), new AssistantCreateTaskParameters { Mode = "auto", Prompt = "   " }), ErrorCodes.InvalidParameters);
        await Refused(() => Invoke(new CreateAssistantTaskAction(_bridge), new AssistantCreateTaskParameters { Mode = "auto", Prompt = new string('x', 32_001) }), ErrorCodes.InvalidParameters);
        await Refused(() => Invoke(new SendAssistantMessageAction(_bridge), new AssistantSendParameters { TaskId = "../../etc", Text = "x" }), ErrorCodes.InvalidParameters);
        await Refused(() => Invoke(new SetAssistantModeAction(_bridge), new AssistantSetModeParameters { TaskId = id, Mode = "AUTO!" }), ErrorCodes.InvalidParameters);
        await Refused(() => Invoke(new ResolveAssistantApprovalAction(_bridge), new AssistantResolveParameters { ApprovalId = id, Decision = "allow_for_task" }), ErrorCodes.InvalidParameters);
        await Refused(() => Invoke(new ResolveAssistantApprovalAction(_bridge), new AssistantResolveParameters { ApprovalId = "nope", Decision = "deny" }), ErrorCodes.InvalidParameters);

        Assert.Empty(_pcAssistant.WorkRequests);
    }

    [Theory]
    [InlineData("task_busy", ErrorCodes.AssistantTaskBusy)]
    [InlineData("requires_local_approval", ErrorCodes.AssistantRequiresLocalApproval)]
    [InlineData("not_found", ErrorCodes.NotFound)]
    [InlineData("something_new", ErrorCodes.InternalError)]
    public async Task PcAssistantRefusalsBecomePcCoreCodes(string pcAssistantCode, string expected)
    {
        _pcAssistant.Handler = (method, _) => method == "approvals.resolve" ? throw new FakePcAssistant.Refusal(pcAssistantCode, "no") : null;

        var refusal = await Assert.ThrowsAsync<CapabilityException>(() =>
            Invoke(new ResolveAssistantApprovalAction(_bridge), new AssistantResolveParameters { ApprovalId = Guid.NewGuid().ToString(), Decision = "allow_once" }));

        Assert.Equal(expected, refusal.Code);
    }

    [Fact]
    public async Task OneConnectionServesManyRequests()
    {
        for (var i = 0; i < 5; i++)
        {
            await Invoke(new ListAssistantTasksAction(_bridge), new AssistantTaskListParameters { Limit = 5 });
        }

        Assert.Equal(1, _pcAssistant.Connections);
        Assert.Equal(1, _pcAssistant.Requests.Count(r => r.Method == "hello"));
    }

    // --- Events --------------------------------------------------------------------------------------

    [Fact]
    public async Task WithNobodySubscribedNoEventReachesTheBus()
    {
        await Invoke(new ListAssistantTasksAction(_bridge), new AssistantTaskListParameters());

        await _pcAssistant.PushAsync("task.changed", new { task = FakePcAssistant.TaskShape(Guid.NewGuid(), "running") });
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(_events.Names, n => n.StartsWith("assistant.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubscribedEventsAreRelayedUnderTheirPhoneNames()
    {
        await Run(new SubscribeAssistantAction(_bridge));
        var task = Guid.NewGuid();
        var approval = Guid.NewGuid();

        await _pcAssistant.PushAsync("task.changed", new { task = FakePcAssistant.TaskShape(task, "running") });
        await _pcAssistant.PushAsync("task.progress", new { task_id = task, @event = new { id = Guid.NewGuid(), kind = "tool_started", title = "Read", created_at = DateTimeOffset.UtcNow } });
        await _pcAssistant.PushAsync("approval.requested", new
        {
            id = approval, task_id = task, tool_name = "Bash", action = "Claude wants to run", target = "git status",
            reason = "ASK", category = "command_safe", remote_resolvable = true, created_at = DateTimeOffset.UtcNow,
            some_future_field = "not for the phone",
        });
        await _pcAssistant.PushAsync("approval.resolved", new { approval_id = approval, task_id = task, outcome = "allowed" });
        await _pcAssistant.PushAsync("something.new", new { secret = "x" });

        await _events.WaitForAsync(AssistantEvents.ApprovalResolved);

        Assert.Contains(AssistantEvents.TaskChanged, _events.Names);
        Assert.Contains(AssistantEvents.Progress, _events.Names);

        var requested = _events.Payload<AssistantApprovalRequestedEvent>(AssistantEvents.ApprovalRequested);
        Assert.Equal("git status", requested.Approval.Target);
        Assert.True(requested.Approval.RemoteResolvable);
        Assert.DoesNotContain("some_future_field", JsonSerializer.Serialize(requested, ProtocolCodec.Options), StringComparison.Ordinal);

        Assert.DoesNotContain(_events.Names, n => n.Contains("something", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStreamingReplyIsThrottledAndItsLastTextArrivesBeforeTheTaskCompletes()
    {
        await Run(new SubscribeAssistantAction(_bridge));
        var task = Guid.NewGuid();
        var message = Guid.NewGuid();

        for (var i = 1; i <= 20; i++)
        {
            await _pcAssistant.PushAsync("message.appended", new
            {
                task_id = task,
                message = new { id = message, role = "assistant", text = new string('a', i), created_at = DateTimeOffset.UtcNow },
            });
        }

        await _pcAssistant.PushAsync("task.changed", new { task = FakePcAssistant.TaskShape(task, "completed") });
        await _events.WaitForAsync(AssistantEvents.TaskChanged);

        var sent = _events.All(AssistantEvents.Message).Select(e => ((AssistantMessageEvent)e).Message.Text.Length).ToList();

        Assert.True(sent.Count < 20, $"expected throttling, forwarded {sent.Count} snapshots");
        Assert.Equal(20, sent[^1]);

        var order = _events.Names.ToList();
        Assert.True(order.LastIndexOf(AssistantEvents.Message) < order.IndexOf(AssistantEvents.TaskChanged),
            "the final text must be forwarded before the completed state");
    }

    [Fact]
    public async Task PcAssistantClosingIsPushedAsNotRunning()
    {
        await Run(new SubscribeAssistantAction(_bridge));
        Assert.Equal(AssistantAvailability.Ready, _bridge.CurrentStatus.State);

        await _pcAssistant.CloseAsync();

        await _events.WaitForAsync(AssistantEvents.StatusChanged, e => ((AssistantStatus)e).State == AssistantAvailability.NotRunning);
    }

    [Fact]
    public async Task UnsubscribingStopsTheRelay()
    {
        await Run(new SubscribeAssistantAction(_bridge));
        await Run(new UnsubscribeAssistantAction(_bridge));

        await _pcAssistant.PushAsync("task.changed", new { task = FakePcAssistant.TaskShape(Guid.NewGuid(), "running") });
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(AssistantEvents.TaskChanged, _events.Names);
    }

    /// <summary>Records what reached the bus.</summary>
    private sealed class RecordingPublisher : IEventPublisher
    {
        private readonly Lock _gate = new();
        private readonly List<(string Name, object? Payload)> _events = [];

        public IReadOnlyList<string> Names
        {
            get
            {
                lock (_gate)
                {
                    return _events.Select(e => e.Name).ToList();
                }
            }
        }

        public void Publish(string eventName, object? payload = null)
        {
            lock (_gate)
            {
                _events.Add((eventName, payload));
            }
        }

        public IReadOnlyList<object> All(string name)
        {
            lock (_gate)
            {
                return _events.Where(e => e.Name == name).Select(e => e.Payload!).ToList();
            }
        }

        public T Payload<T>(string name) => (T)All(name).Single();

        public async Task WaitForAsync(string name, Func<object, bool>? predicate = null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            while (!All(name).Any(p => predicate?.Invoke(p) ?? true))
            {
                await Task.Delay(20, timeout.Token);
            }
        }
    }
}
