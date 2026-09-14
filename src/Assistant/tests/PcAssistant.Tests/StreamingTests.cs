using System.Text.Json;
using PcAssistant.ClaudeCode;
using PcAssistant.Core.Bridge;
using PcAssistant.Core.Approvals;
using PcAssistant.Core.Model;
using PcAssistant.Core.Tasks;
using PcAssistant.Desktop.ViewModels;
using Xunit;

namespace PcAssistant.Tests;

public class StreamingTests
{
    private static string Stream(object evt) => JsonSerializer.Serialize(new { type = "stream_event", @event = evt });
    private static string Start(string id) => Stream(new { type = "message_start", message = new { id } });
    private static string Delta(string text, int index = 0) => Stream(new
    {
        type = "content_block_delta", index, delta = new { type = "text_delta", text },
    });

    [Fact]
    public void PartialsAndCompletedMessageShareAnIdentityWithoutDuplicatingText()
    {
        var normalizer = new StreamEventNormalizer(Guid.NewGuid());
        var events = new[]
        {
            Start("m1"),
            Stream(new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "Hello" } }),
            Delta(" "), Delta("world"),
            Stream(new { type = "content_block_stop", index = 0 }),
            """{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"Hello world"}]}}""",
        }.SelectMany(normalizer.Consume).ToList();

        var partials = events.Where(e => e.Kind == AssistantEventKind.AssistantMessageDelta).ToList();
        Assert.Equal("Hello world", string.Concat(partials.Select(e => e.Detail)));
        var complete = Assert.Single(events, e => e.Kind == AssistantEventKind.AssistantMessage);
        Assert.All(partials, e => Assert.Equal(complete.Id, e.Id));
        Assert.Equal("Hello world", Assert.Single(normalizer.AssistantTexts));
    }

    [Fact]
    public void MissingStartStillStreamsAndFinalMessageRepairsIncompleteFragments()
    {
        var normalizer = new StreamEventNormalizer(Guid.NewGuid());
        var delta = Assert.Single(normalizer.Consume(Delta("Hel")));
        var final = Assert.Single(normalizer.Consume(
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Hello"}]}}"""));
        Assert.Equal(delta.Id, final.Id);
        Assert.Equal("Hello", final.Detail);
    }

    [Fact]
    public void ConsecutiveProviderMessagesAndDifferentBlocksKeepDistinctIdentities()
    {
        var normalizer = new StreamEventNormalizer(Guid.NewGuid());
        var events = new[] { Start("m1"), Delta("one"), Delta("two", 2), Start("m2"), Delta("one") }
            .SelectMany(normalizer.Consume).ToList();
        Assert.Equal(3, events.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void RealCliOrderingMatchesTextAfterThinkingBeforeBlockStop()
    {
        // Claude Code 2.1.247 emits one assembled assistant envelope per content block.
        var normalizer = new StreamEventNormalizer(Guid.NewGuid());
        var events = new[]
        {
            Start("m1"),
            Stream(new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = "" } }),
            Stream(new { type = "content_block_stop", index = 0 }),
            Stream(new { type = "content_block_start", index = 1, content_block = new { type = "text", text = "" } }),
            Delta("Connected browsers: ", 1), Delta("0", 1),
            """{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"Connected browsers: 0"}]}}""",
            Stream(new { type = "content_block_stop", index = 1 }),
            """{"type":"result","result":"Connected browsers: 0","is_error":false}""",
        }.SelectMany(normalizer.Consume).ToList();
        var complete = Assert.Single(events, e => e.Kind == AssistantEventKind.AssistantMessage);
        Assert.All(events, e => Assert.Equal(complete.Id, e.Id));
        Assert.Equal("Connected browsers: 0", Assert.Single(normalizer.AssistantTexts));
    }

    [Fact]
    public void SeparateAssembledBlocksDoNotOverwriteEarlierTextWithTheSamePrefix()
    {
        var normalizer = new StreamEventNormalizer(Guid.NewGuid());
        var events = new[]
        {
            Start("m1"), Delta("Hello", 0),
            """{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"Hello"}]}}""",
            Stream(new { type = "content_block_stop", index = 0 }),
            Delta("Hello again", 1),
            """{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"Hello again"}]}}""",
            Stream(new { type = "content_block_stop", index = 1 }),
        }.SelectMany(normalizer.Consume).ToList();
        var complete = events.Where(e => e.Kind == AssistantEventKind.AssistantMessage).ToList();
        Assert.Equal(2, complete.Count);
        Assert.NotEqual(complete[0].Id, complete[1].Id);
        Assert.Equal(new[] { "Hello", "Hello again" }, normalizer.AssistantTexts);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":null}")]
    [InlineData("{\"type\":\"content_block_delta\",\"index\":999999999999999999,\"delta\":{}}")]
    [InlineData("{\"type\":\"content_block_start\",\"index\":0,\"content_block\":true}")]
    [InlineData("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"private\"}}")]
    public void MalformedAndNonTextFragmentsAreIgnored(string evt)
    {
        var normalizer = new StreamEventNormalizer(Guid.NewGuid());
        Assert.Empty(normalizer.Consume("{\"type\":\"stream_event\",\"event\":" + evt + "}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppingKeepsPartialTextInOneMessageAndReloadsIt(bool bridgeThrows)
    {
        using var harness = new TestHarness();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Bridge.Behaviour = async (request, emit, ct) =>
        {
            var id = Guid.NewGuid();
            emit(Event(request.TaskId, id, "A partial "));
            emit(Event(request.TaskId, id, "answer"));
            ready.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) when (!bridgeThrows) { }
            return new BridgeTurnResult(false, true, request.SessionId, null, null);
        };
        var task = harness.Service.CreateTask(General());
        using var vm = new MainViewModel(harness.Service, harness.Workspaces, harness.Settings, harness.Paths);
        var run = harness.Service.SendAsync(task.Id, "hello");
        await ready.Task;
        Assert.StartsWith("A partial ", Assert.Single(vm.Transcript.OfType<MessageItem>(), m => m.IsAssistant).Text);
        harness.Service.Stop(task.Id);
        Assert.True((await run).Cancelled);
        Assert.Equal("A partial answer", Assert.Single(vm.Transcript.OfType<MessageItem>(), m => m.IsAssistant).Text);
        Assert.Equal("A partial answer", Assert.Single(harness.Service.MessagesFor(task.Id), m => m.Role == MessageRole.Assistant).Text);
        var restarted = harness.Restart();
        Assert.Equal("A partial answer", Assert.Single(restarted.MessagesFor(task.Id), m => m.Role == MessageRole.Assistant).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalResultCompletesOrDeduplicatesStreamedReply(bool emitFullMessage)
    {
        using var harness = new TestHarness();
        harness.Bridge.Behaviour = (request, emit, _) =>
        {
            var id = Guid.NewGuid();
            emit(Event(request.TaskId, id, "Hel"));
            if (emitFullMessage) emit(Event(request.TaskId, id, "Hello", AssistantEventKind.AssistantMessage));
            return Task.FromResult(new BridgeTurnResult(true, false, request.SessionId, "Hello", null));
        };
        var task = harness.Service.CreateTask(General());
        using var vm = new MainViewModel(harness.Service, harness.Workspaces, harness.Settings, harness.Paths);
        await harness.Service.SendAsync(task.Id, "hello");
        Assert.Equal("Hello", Assert.Single(vm.Transcript.OfType<MessageItem>(), m => m.IsAssistant).Text);
        Assert.Equal("Hello", Assert.Single(harness.Service.MessagesFor(task.Id), m => m.Role == MessageRole.Assistant).Text);
    }

    [Fact]
    public async Task TokenBurstIsCoalescedAndDoesNotCreatePerTokenDatabaseRows()
    {
        using var harness = new TestHarness();
        var snapshots = 0;
        harness.Service.MessageAppended += (_, message) => { if (message.Role == MessageRole.Assistant) snapshots++; };
        harness.Bridge.Behaviour = (request, emit, _) =>
        {
            var id = Guid.NewGuid();
            for (var i = 0; i < 1000; i++) emit(Event(request.TaskId, id, "x"));
            return Task.FromResult(new BridgeTurnResult(true, false, request.SessionId, new string('x', 1000), null));
        };
        var task = harness.Service.CreateTask(General());
        await harness.Service.SendAsync(task.Id, "hello");
        Assert.InRange(snapshots, 1, 50);
        Assert.Single(harness.Service.MessagesFor(task.Id), m => m.Role == MessageRole.Assistant);
        Assert.DoesNotContain(harness.Service.EventsFor(task.Id), e => e.Kind == AssistantEventKind.AssistantMessageDelta);
    }

    [Fact]
    public async Task DiagnosticsUseProviderAndShowFailuresHonestly()
    {
        using var harness = new TestHarness();
        using var vm = new MainViewModel(harness.Service, harness.Workspaces, harness.Settings, harness.Paths)
        {
            CapabilityDiagnosticsProvider = _ => Task.FromResult("Screen: Ready\nBrowser: Extension required"),
        };
        await vm.RefreshCapabilitiesAsync();
        Assert.Contains("Extension required", vm.CapabilityDiagnosticsText);
        vm.CapabilityDiagnosticsProvider = _ => throw new InvalidOperationException("controlled failure");
        await vm.RefreshCapabilitiesAsync();
        Assert.Contains("controlled failure", vm.CapabilityDiagnosticsText);
        Assert.False(vm.IsRefreshingCapabilities);
    }

    [Fact]
    public async Task ScopeButtonOnlyAppearsForScopedRequestsAndCallsBroker()
    {
        using var harness = new TestHarness();
        harness.Service.CreateTask(General());
        using var vm = new MainViewModel(harness.Service, harness.Workspaces, harness.Settings, harness.Paths);
        var request = new ApprovalRequest
        {
            TaskId = vm.SelectedTask!.Id, ToolName = "Edit", Action = "Edit workspace file",
            Target = "notes.txt", Reason = "ASK", ScopeKey = "test-workspace-edit",
            ScopeDescription = "Allow edits in this workspace for this task",
        };
        var pending = harness.Approvals.RequestAsync(request, CancellationToken.None);
        var card = Assert.Single(vm.Transcript.OfType<ApprovalItem>());
        Assert.True(card.CanAllowForTask);
        Assert.Equal(request.ScopeDescription, card.ScopeDescription);
        card.AllowForTaskCommand.Execute(null);
        Assert.Equal(ApprovalOutcome.Allowed, await pending);
        Assert.True(card.IsResolved);
        Assert.False(card.AllowForTaskCommand.CanExecute(null));

        var unscoped = new ApprovalItem { Request = request with { ScopeKey = null }, Respond = _ => { }, RespondForTask = () => { } };
        Assert.False(unscoped.CanAllowForTask);
        Assert.False(unscoped.AllowForTaskCommand.CanExecute(null));
    }

    [Fact]
    public async Task EmergencyStopCancelsAllTasksIncludingUnselectedOnes()
    {
        using var harness = new TestHarness();
        harness.Bridge.Behaviour = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        };
        var first = harness.Service.CreateTask(General());
        var second = harness.Service.CreateTask(General());
        using var vm = new MainViewModel(harness.Service, harness.Workspaces, harness.Settings, harness.Paths);
        var runs = new[] { harness.Service.SendAsync(first.Id, "one"), harness.Service.SendAsync(second.Id, "two") };
        vm.StopAll();
        Assert.All(await Task.WhenAll(runs), result => Assert.True(result.Cancelled));
        Assert.False(harness.Service.IsRunning(first.Id));
        Assert.False(harness.Service.IsRunning(second.Id));
    }

    private static AssistantEvent Event(Guid task, Guid id, string text,
        AssistantEventKind kind = AssistantEventKind.AssistantMessageDelta)
        => new() { TaskId = task, Id = id, Kind = kind, Detail = text };

    private static TaskCreationRequest General() => new()
    {
        Kind = TaskKind.General, Mode = AssistantMode.Auto, FirstMessage = "hello",
    };
}
