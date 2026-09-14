using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PcAssistant.Core.Approvals;
using PcAssistant.Core.Bridge;
using PcAssistant.Core.Model;
using PcAssistant.Core.NexusBridge;
using PcAssistant.Core.Policy;
using PcAssistant.Core.Tasks;
using PcAssistant.Desktop.ViewModels;
using Xunit;

namespace PcAssistant.Tests;

/// <summary>
/// The Nexus bridge, over a real named pipe, against the real task engine and approval broker.
/// Claude is the fake bridge: what is under test is that a remote client reaches the same tasks and
/// the same approval cards as the desktop, and nothing more than that.
/// </summary>
public sealed class NexusBridgeSessionTests
{
    [Fact]
    public async Task NothingIsServedBeforeHelloAndAWrongVersionIsRefusedByName()
    {
        using var h = new TestHarness();
        await using var link = await BridgeLink.OpenAsync(h);

        var early = await link.CallAsync("tasks.list");
        Assert.Equal("hello_required", Code(early));

        var wrong = await link.CallAsync("hello", new { protocol_version = 99 });
        Assert.Equal("unsupported_protocol_version", Code(wrong));

        var hello = Result(await link.CallAsync("hello", new { protocol_version = 1, client = "test" }));
        Assert.Equal(1, hello.GetProperty("protocol_version").GetInt32());
        Assert.Equal("pc-assistant", hello.GetProperty("server").GetString());
        Assert.Equal("ready", hello.GetProperty("claude").GetProperty("availability").GetString());
    }

    [Fact]
    public async Task ClaudeSignedOutIsReportedAsItsOwnState()
    {
        using var h = new TestHarness();
        h.Bridge.Status = new ClaudeStatus(ClaudeAvailability.AuthenticationRequired, "2.1", "claude.exe");
        await using var link = await BridgeLink.OpenAsync(h);

        var hello = Result(await link.CallAsync("hello", new { protocol_version = 1 }));
        Assert.Equal("signed_out", hello.GetProperty("claude").GetProperty("availability").GetString());
        Assert.False(string.IsNullOrWhiteSpace(hello.GetProperty("claude").GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task APhoneStartedGeneralTaskIsAnOrdinaryTaskAndStreamsItsTurn()
    {
        using var h = new TestHarness();
        h.Bridge.Behaviour = (request, onEvent, _) =>
        {
            onEvent(new AssistantEvent { TaskId = request.TaskId, Kind = AssistantEventKind.ToolStarted, Title = "Read", ToolName = "Read" });
            var id = Guid.NewGuid();
            onEvent(new AssistantEvent { Id = id, TaskId = request.TaskId, Kind = AssistantEventKind.AssistantMessageDelta, Detail = "Hello " });
            onEvent(new AssistantEvent { Id = id, TaskId = request.TaskId, Kind = AssistantEventKind.AssistantMessageDelta, Detail = "from Claude" });
            return Task.FromResult(new BridgeTurnResult(true, false, request.SessionId, "Hello from Claude", null));
        };

        await using var link = await BridgeLink.OpenHelloAsync(h);

        var created = Result(await link.CallAsync("tasks.create", new { kind = "general", mode = "ask", prompt = "Say hello" }));
        var taskId = Guid.Parse(created.GetProperty("task").GetProperty("id").GetString()!);

        // The same engine: the desktop's service knows the task, in the mode the phone chose.
        var task = h.Service.Find(taskId);
        Assert.NotNull(task);
        Assert.Equal(TaskKind.General, task!.Kind);
        Assert.Equal(AssistantMode.Ask, task.Mode);

        await link.WaitForEventAsync("task.changed", d => d.GetProperty("task").GetProperty("state").GetString() == "completed");

        Assert.Contains(link.Events, e => e.Name == "message.appended" &&
            e.Data.GetProperty("message").GetProperty("role").GetString() == "owner" &&
            e.Data.GetProperty("message").GetProperty("text").GetString() == "Say hello");
        Assert.Contains(link.Events, e => e.Name == "message.appended" &&
            e.Data.GetProperty("message").GetProperty("text").GetString() == "Hello from Claude");
        Assert.Contains(link.Events, e => e.Name == "task.progress" &&
            e.Data.GetProperty("event").GetProperty("kind").GetString() == "tool_started");

        // Assistant text is a message, never repeated as progress.
        Assert.DoesNotContain(link.Events, e => e.Name == "task.progress" &&
            e.Data.GetProperty("event").GetProperty("kind").GetString() is "assistant_message" or "assistant_message_delta");

        var detail = Result(await link.CallAsync("tasks.get", new { task_id = taskId.ToString() }));
        Assert.Equal(new[] { "owner", "assistant" }, detail.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()).ToArray());
        Assert.Contains(detail.GetProperty("progress").EnumerateArray(), p => p.GetProperty("kind").GetString() == "task_completed");
    }

    [Fact]
    public async Task AFollowUpContinuesTheSameClaudeSession()
    {
        using var h = new TestHarness();
        await using var link = await BridgeLink.OpenHelloAsync(h);

        var created = Result(await link.CallAsync("tasks.create", new { mode = "auto", prompt = "first" }));
        var taskId = created.GetProperty("task").GetProperty("id").GetString()!;
        await link.WaitForEventAsync("task.changed", d => d.GetProperty("task").GetProperty("state").GetString() == "completed");
        link.ClearEvents();

        Result(await link.CallAsync("tasks.send", new { task_id = taskId, text = "second" }));
        await link.WaitForEventAsync("task.changed", d => d.GetProperty("task").GetProperty("state").GetString() == "completed");

        Assert.Equal(2, h.Bridge.Requests.Count);
        Assert.False(h.Bridge.Requests[0].Resume);
        Assert.True(h.Bridge.Requests[1].Resume);
        Assert.Equal(h.Bridge.Requests[0].SessionId, h.Bridge.Requests[1].SessionId);
    }

    [Fact]
    public async Task WorkspaceTasksCannotBeCreatedRemotelyButCanBeContinued()
    {
        using var h = new TestHarness();
        var workspace = h.Service.CreateTask(new TaskCreationRequest
        {
            Kind = TaskKind.Workspace, Mode = AssistantMode.Auto, FirstMessage = "fix it", WorkspacePath = h.CreateProject("demo"),
        });

        await using var link = await BridgeLink.OpenHelloAsync(h);

        Assert.Equal("unsupported", Code(await link.CallAsync("tasks.create", new { kind = "workspace", mode = "auto", prompt = "x" })));

        var listed = Result(await link.CallAsync("tasks.list")).GetProperty("tasks").EnumerateArray().Single();
        Assert.Equal("workspace", listed.GetProperty("kind").GetString());
        Assert.Equal("demo", listed.GetProperty("workspace_name").GetString());
        Assert.DoesNotContain(h.ProjectsRoot, listed.GetRawText(), StringComparison.OrdinalIgnoreCase);

        Result(await link.CallAsync("tasks.send", new { task_id = workspace.Id.ToString(), text = "carry on" }));
        await link.WaitForEventAsync("task.changed", d => d.GetProperty("task").GetProperty("state").GetString() == "completed");
        Assert.Equal(workspace.WorkingDirectory, h.Bridge.Requests.Single().WorkingDirectory);
    }

    [Fact]
    public async Task StopEndsARunningTurnAndABusyTaskRefusesAnotherMessage()
    {
        using var h = new TestHarness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Bridge.Behaviour = async (request, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new BridgeTurnResult(true, false, request.SessionId, null, null);
        };

        await using var link = await BridgeLink.OpenHelloAsync(h);
        var taskId = Result(await link.CallAsync("tasks.create", new { mode = "auto", prompt = "wait" }))
            .GetProperty("task").GetProperty("id").GetString()!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("task_busy", Code(await link.CallAsync("tasks.send", new { task_id = taskId, text = "again" })));

        var stop = Result(await link.CallAsync("tasks.stop", new { task_id = taskId }));
        Assert.True(stop.GetProperty("was_running").GetBoolean());
        await link.WaitForEventAsync("task.changed", d => d.GetProperty("task").GetProperty("state").GetString() == "cancelled");
    }

    [Fact]
    public async Task ModeCanBeSwitchedForAnExistingTask()
    {
        using var h = new TestHarness();
        var task = h.Service.CreateTask(new TaskCreationRequest { Kind = TaskKind.General, Mode = AssistantMode.Auto, FirstMessage = "x" });
        await using var link = await BridgeLink.OpenHelloAsync(h);

        var result = Result(await link.CallAsync("tasks.set_mode", new { task_id = task.Id.ToString(), mode = "ask" }));
        Assert.Equal("ask", result.GetProperty("task").GetProperty("mode").GetString());
        Assert.Equal(AssistantMode.Ask, h.Service.Find(task.Id)!.Mode);

        Assert.Equal("invalid_params", Code(await link.CallAsync("tasks.set_mode", new { task_id = task.Id.ToString(), mode = "yolo" })));
        Assert.Equal("not_found", Code(await link.CallAsync("tasks.get", new { task_id = Guid.NewGuid().ToString() })));
    }

    [Fact]
    public async Task AnOrdinaryApprovalIsAnsweredOnceFromTheBridge()
    {
        using var h = new TestHarness();
        var task = h.Service.CreateTask(new TaskCreationRequest { Kind = TaskKind.General, Mode = AssistantMode.Ask, FirstMessage = "x" });
        await using var link = await BridgeLink.OpenHelloAsync(h);

        var allowRequest = Card(task.Id, RiskCategory.CommandSafe, "git status");
        var allowed = h.Approvals.RequestAsync(allowRequest, CancellationToken.None);

        var requested = await link.WaitForEventAsync("approval.requested", d => d.GetProperty("id").GetString() == allowRequest.Id.ToString());
        Assert.True(requested.GetProperty("remote_resolvable").GetBoolean());
        Assert.Equal("git status", requested.GetProperty("target").GetString());
        Assert.Equal("command_safe", requested.GetProperty("category").GetString());

        // What an open task shows: the card is pending on it.
        var detail = Result(await link.CallAsync("tasks.get", new { task_id = task.Id.ToString() }));
        Assert.Equal(allowRequest.Id.ToString(), detail.GetProperty("pending_approvals")[0].GetProperty("id").GetString());

        // "Allow for this task" is a desktop decision and does not exist here.
        Assert.Equal("invalid_params", Code(await link.CallAsync("approvals.resolve", new { approval_id = allowRequest.Id.ToString(), decision = "allow_for_task" })));

        var resolve = Result(await link.CallAsync("approvals.resolve", new { approval_id = allowRequest.Id.ToString(), decision = "allow_once" }));
        Assert.True(resolve.GetProperty("resolved").GetBoolean());
        Assert.Equal(ApprovalOutcome.Allowed, await allowed.WaitAsync(TimeSpan.FromSeconds(5)));

        var resolved = await link.WaitForEventAsync("approval.resolved", d => d.GetProperty("approval_id").GetString() == allowRequest.Id.ToString());
        Assert.Equal("allowed", resolved.GetProperty("outcome").GetString());
        Assert.Equal(task.Id.ToString(), resolved.GetProperty("task_id").GetString());

        // Answered is answered: a second attempt, from anywhere, finds nothing to answer.
        Assert.Equal("not_found", Code(await link.CallAsync("approvals.resolve", new { approval_id = allowRequest.Id.ToString(), decision = "deny" })));

        var denyRequest = Card(task.Id, RiskCategory.WorkspaceWrite, "notes.txt");
        var denied = h.Approvals.RequestAsync(denyRequest, CancellationToken.None);
        await link.WaitForEventAsync("approval.requested", d => d.GetProperty("id").GetString() == denyRequest.Id.ToString());
        Result(await link.CallAsync("approvals.resolve", new { approval_id = denyRequest.Id.ToString(), decision = "deny" }));
        Assert.Equal(ApprovalOutcome.Denied, await denied.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AnAdministratorApprovalCannotBeAnsweredFromTheBridge()
    {
        using var h = new TestHarness();
        var task = h.Service.CreateTask(new TaskCreationRequest { Kind = TaskKind.General, Mode = AssistantMode.Auto, FirstMessage = "x" });
        await using var link = await BridgeLink.OpenHelloAsync(h);

        var elevated = Card(task.Id, RiskCategory.Elevation, "services.msc") with { Privilege = "Administrator" };
        var pending = h.Approvals.RequestAsync(elevated, CancellationToken.None);

        var requested = await link.WaitForEventAsync("approval.requested", d => d.GetProperty("id").GetString() == elevated.Id.ToString());
        Assert.False(requested.GetProperty("remote_resolvable").GetBoolean());

        Assert.Equal("requires_local_approval",
            Code(await link.CallAsync("approvals.resolve", new { approval_id = elevated.Id.ToString(), decision = "allow_once" })));

        // Still waiting for the PC.
        Assert.False(pending.IsCompleted);
        Assert.Contains(h.Approvals.Pending, p => p.Id == elevated.Id);
        h.Approvals.Resolve(elevated.Id, allow: false);
    }

    [Fact]
    public async Task ARemoteAnswerResolvesTheCardTheDesktopIsShowing()
    {
        using var h = new TestHarness();
        using var vm = new MainViewModel(h.Service, h.Workspaces, h.Settings, h.Paths);
        var task = h.Service.CreateTask(new TaskCreationRequest { Kind = TaskKind.General, Mode = AssistantMode.Ask, FirstMessage = "x" });
        vm.SelectedTask = vm.Tasks.Single(t => t.Id == task.Id);

        await using var link = await BridgeLink.OpenHelloAsync(h);
        var request = Card(task.Id, RiskCategory.CommandSafe, "dir");
        var pending = h.Approvals.RequestAsync(request, CancellationToken.None);

        var card = Assert.Single(vm.Transcript.OfType<ApprovalItem>());
        Assert.False(card.IsResolved);

        Result(await link.CallAsync("approvals.resolve", new { approval_id = request.Id.ToString(), decision = "allow_once" }));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (!card.IsResolved) await Task.Delay(20, timeout.Token);
        Assert.Equal("Allowed", card.OutcomeText);
    }

    [Fact]
    public async Task EventsStopWhenTheClientLeaves()
    {
        using var h = new TestHarness();
        var link = await BridgeLink.OpenHelloAsync(h);
        await link.DisposeAsync();
        await link.Served.WaitAsync(TimeSpan.FromSeconds(5));

        // Nothing is subscribed any more, so a task change has nowhere to go and costs nothing.
        h.Service.CreateTask(new TaskCreationRequest { Kind = TaskKind.General, Mode = AssistantMode.Auto, FirstMessage = "after" });
    }

    private static ApprovalRequest Card(Guid taskId, RiskCategory category, string target) => new()
    {
        TaskId = taskId,
        ToolName = "Bash",
        Action = "Claude wants to run",
        Target = target,
        Reason = "ASK mode asks before commands.",
        Category = category,
    };

    internal static JsonElement Result(JsonElement response)
    {
        Assert.True(response.GetProperty("ok").GetBoolean(), "expected ok: " + response.GetRawText());
        return response.GetProperty("result");
    }

    internal static string? Code(JsonElement response)
    {
        Assert.False(response.GetProperty("ok").GetBoolean(), "expected a refusal: " + response.GetRawText());
        return response.GetProperty("error").GetProperty("code").GetString();
    }
}

/// <summary>The listener's own trust decisions, over the real owner-only pipe.</summary>
[Collection(DesktopCollection.Name)]
public sealed class NexusBridgeServerTests
{
    [Fact]
    public async Task AClientThePcHasNotTrustedIsRefusedBeforeItSeesAnything()
    {
        using var h = new TestHarness();
        var pipeName = "pcassistant-nexus-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NexusBridgeServer(h.Service, () => null, h.Service.GetClaudeStatusAsync, pipeName: pipeName);
        server.Start();

        await using var client = await BridgeClient.ConnectAsync(pipeName);
        var hello = await client.CallAsync("hello", new { protocol_version = 1 });

        Assert.Equal("client_not_trusted", NexusBridgeSessionTests.Code(hello));
        Assert.True(await client.ClosedAsync(), "an untrusted client must be disconnected");
        Assert.Empty(client.Events);
    }

    [Fact]
    public async Task TheTrustedExecutableIsServed()
    {
        using var h = new TestHarness();
        var pipeName = "pcassistant-nexus-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NexusBridgeServer(h.Service, () => Environment.ProcessPath, h.Service.GetClaudeStatusAsync, pipeName: pipeName);
        server.Start();

        await using var client = await BridgeClient.ConnectAsync(pipeName);
        NexusBridgeSessionTests.Result(await client.CallAsync("hello", new { protocol_version = 1 }));

        await WaitAsync(() => server.Status.State == NexusBridgeListenState.Connected);
        Assert.True(NexusBridgeServer.SamePath(Environment.ProcessPath, server.Status.ConnectedClient));
    }

    [Fact]
    public async Task AnUntrustedNexusIsOfferedToTheOwnerAndAnythingElseIsNot()
    {
        using var h = new TestHarness();
        var pipeName = "pcassistant-nexus-test-" + Guid.NewGuid().ToString("N");
        var pretend = @"C:\Programs\Nexus\PcCore.Host.exe";
        var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();

        await using var server = new NexusBridgeServer(h.Service, () => @"C:\Elsewhere\PcCore.Host.exe", h.Service.GetClaudeStatusAsync, pipeName: pipeName)
        {
            IdentifyOverride = _ => new NexusClientIdentity(4242, sessionId, pretend, me.User!.Value),
        };
        server.Start();

        await using (var client = await BridgeClient.ConnectAsync(pipeName))
        {
            Assert.Equal("client_not_trusted", NexusBridgeSessionTests.Code(await client.CallAsync("hello", new { protocol_version = 1 })));
        }

        await WaitAsync(() => server.Status.CandidateClient is not null);
        Assert.Equal(pretend, server.Status.CandidateClient);
    }

    [Fact]
    public void TheTrustRuleRefusesOtherSessionsOtherUsersAndUnknownImages()
    {
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        const string nexus = @"C:\Users\Owner\AppData\Local\Programs\Nexus\PcCore.Host.exe";

        Assert.Null(NexusBridgeServer.Judge(new(1, session, nexus.ToUpperInvariant(), me.User!.Value), nexus));
        Assert.NotNull(NexusBridgeServer.Judge(new(1, session + 1, nexus, me.User!.Value), nexus));
        Assert.NotNull(NexusBridgeServer.Judge(new(1, session, nexus, "S-1-5-18"), nexus));
        Assert.NotNull(NexusBridgeServer.Judge(new(1, session, null, me.User!.Value), nexus));
        Assert.NotNull(NexusBridgeServer.Judge(new(1, session, @"C:\Temp\PcCore.Host.exe", me.User!.Value), nexus));
        Assert.NotNull(NexusBridgeServer.Judge(new(1, session, nexus, me.User!.Value), null));
    }

    [Fact]
    public async Task AProcessHoldingTheNameFirstIsReportedNotShared()
    {
        using var h = new TestHarness();
        var pipeName = "pcassistant-nexus-test-" + Guid.NewGuid().ToString("N");

        await using var squatter = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var server = new NexusBridgeServer(h.Service, () => Environment.ProcessPath, h.Service.GetClaudeStatusAsync, pipeName: pipeName);
        server.Start();

        await WaitAsync(() => server.Status.State == NexusBridgeListenState.NameInUse);
    }

    [Fact]
    public async Task OnlyOneClientIsServedAtATime()
    {
        using var h = new TestHarness();
        var pipeName = "pcassistant-nexus-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NexusBridgeServer(h.Service, () => Environment.ProcessPath, h.Service.GetClaudeStatusAsync, pipeName: pipeName);
        server.Start();

        await using var first = await BridgeClient.ConnectAsync(pipeName);
        NexusBridgeSessionTests.Result(await first.CallAsync("hello", new { protocol_version = 1 }));

        await using var second = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Assert.ThrowsAnyAsync<Exception>(() => second.ConnectAsync(300));
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}

public sealed class PhoneStartedTaskDesktopTests
{
    [Fact]
    public void ATaskStartedElsewhereAppearsInTheListWithoutTakingTheSelection()
    {
        using var h = new TestHarness();
        using var vm = new MainViewModel(h.Service, h.Workspaces, h.Settings, h.Paths);

        var mine = h.Service.CreateTask(new TaskCreationRequest { Kind = TaskKind.General, Mode = AssistantMode.Auto, FirstMessage = "mine" });
        vm.SelectedTask = vm.Tasks.Single(t => t.Id == mine.Id);

        var phone = h.Service.CreateTask(new TaskCreationRequest { Kind = TaskKind.General, Mode = AssistantMode.Ask, FirstMessage = "from the phone" });

        Assert.Equal(2, vm.Tasks.Count);
        Assert.Equal(phone.Id, vm.Tasks[0].Id);
        Assert.Equal(mine.Id, vm.SelectedTask!.Id);
    }

    [Fact]
    public async Task StartingATaskHereStillAddsExactlyOneRow()
    {
        using var h = new TestHarness();
        using var vm = new MainViewModel(h.Service, h.Workspaces, h.Settings, h.Paths);

        var form = vm.CreateNewTaskViewModel();
        form.FirstMessage = "one row";
        await vm.StartTaskAsync(form);

        Assert.Single(vm.Tasks);
    }
}

/// <summary>A session served over a private pipe pair, with a client on the other end.</summary>
internal sealed class BridgeLink : IAsyncDisposable
{
    private readonly NamedPipeServerStream _server;
    private readonly BridgeClient _client;
    private readonly NexusBridgeSession _session;

    private BridgeLink(NamedPipeServerStream server, BridgeClient client, NexusBridgeSession session, Task served)
    {
        _server = server;
        _client = client;
        _session = session;
        Served = served;
    }

    public Task Served { get; }
    public List<(string Name, JsonElement Data)> Events => _client.Events;

    public static async Task<BridgeLink> OpenAsync(TestHarness h)
    {
        var name = "pcassistant-session-test-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var waiting = server.WaitForConnectionAsync();
        var client = await BridgeClient.ConnectAsync(name);
        await waiting;

        var session = new NexusBridgeSession(server, h.Service, h.Service.GetClaudeStatusAsync);
        var served = Task.Run(() => session.RunAsync(CancellationToken.None));
        return new BridgeLink(server, client, session, served);
    }

    public static async Task<BridgeLink> OpenHelloAsync(TestHarness h)
    {
        var link = await OpenAsync(h);
        NexusBridgeSessionTests.Result(await link.CallAsync("hello", new { protocol_version = 1 }));
        return link;
    }

    public Task<JsonElement> CallAsync(string method, object? parameters = null) => _client.CallAsync(method, parameters);

    public Task<JsonElement> WaitForEventAsync(string name, Func<JsonElement, bool> predicate) =>
        _client.WaitForEventAsync(name, predicate);

    public void ClearEvents() => _client.ClearEvents();

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        try { await Served.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        await _session.DisposeAsync();
        await _server.DisposeAsync();
    }
}

/// <summary>Speaks the bridge protocol the way Nexus does: requests out, responses and events in.</summary>
internal sealed class BridgeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _waiting = new();
    private readonly object _gate = new();
    private readonly Task _reader;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<(string, JsonElement)> _eventSignal = Channel.CreateUnbounded<(string, JsonElement)>();
    private int _id;

    private BridgeClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = Task.Run(ReadAsync);
    }

    public List<(string Name, JsonElement Data)> Events { get; } = new();

    public static async Task<BridgeClient> ConnectAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        return new BridgeClient(pipe);
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters = null)
    {
        var id = Interlocked.Increment(ref _id).ToString();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _waiting[id] = completion;

        var line = JsonSerializer.Serialize(new { type = "request", id, method, @params = parameters ?? new { } }) + "\n";
        await _pipe.WriteAsync(Encoding.UTF8.GetBytes(line));
        await _pipe.FlushAsync();

        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public async Task<JsonElement> WaitForEventAsync(string name, Func<JsonElement, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        lock (_gate)
        {
            foreach (var (n, d) in Events)
                if (n == name && predicate(d)) return d;
        }

        while (true)
        {
            var (n, d) = await _eventSignal.Reader.ReadAsync(timeout.Token);
            if (n == name && predicate(d)) return d;
        }
    }

    public void ClearEvents()
    {
        lock (_gate)
        {
            Events.Clear();
            while (_eventSignal.Reader.TryRead(out _)) { }
        }
    }

    public async Task<bool> ClosedAsync()
    {
        try { await _closed.Task.WaitAsync(TimeSpan.FromSeconds(10)); return true; }
        catch (TimeoutException) { return false; }
    }

    private async Task ReadAsync()
    {
        try
        {
            using var reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            while (await reader.ReadLineAsync() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement.Clone();

                if (root.GetProperty("type").GetString() == "event")
                {
                    var item = (root.GetProperty("event").GetString()!, root.GetProperty("data"));
                    lock (_gate) Events.Add(item);
                    _eventSignal.Writer.TryWrite(item);
                    continue;
                }

                var id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
                TaskCompletionSource<JsonElement>? waiter = null;
                lock (_gate)
                {
                    if (id is not null && _waiting.Remove(id, out var found)) waiter = found;
                    else if (id is null && _waiting.Count > 0)
                    {
                        // A refusal sent before the server read an id answers the oldest request.
                        var first = _waiting.Keys.OrderBy(int.Parse).First();
                        waiter = _waiting[first];
                        _waiting.Remove(first);
                    }
                }

                waiter?.TrySetResult(root);
            }
        }
        catch (Exception)
        {
            // Closed.
        }
        finally
        {
            _closed.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _pipe.DisposeAsync();
        try { await _reader.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
    }
}
