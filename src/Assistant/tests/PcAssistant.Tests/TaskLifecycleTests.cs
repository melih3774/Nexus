using PcAssistant.Core.Bridge;
using PcAssistant.Core.Model;
using PcAssistant.Core.Tasks;
using Xunit;

namespace PcAssistant.Tests;

public class TaskLifecycleTests
{
    private static TaskCreationRequest General(string message = "What do you think about a launcher?") => new()
    {
        Kind = TaskKind.General,
        Mode = AssistantMode.Auto,
        FirstMessage = message,
    };

    [Fact]
    public void GeneralTaskRunsInTheSandboxAndNeedsNoWorkspace()
    {
        using var h = new TestHarness();

        var task = h.Service.CreateTask(General());

        Assert.Equal(TaskKind.General, task.Kind);
        Assert.Null(task.WorkspacePath);
        Assert.Equal(h.Paths.GeneralSandbox, task.WorkingDirectory);
        Assert.True(Directory.Exists(task.WorkingDirectory));
    }

    [Fact]
    public void WorkspaceTaskUsesTheSelectedDirectory()
    {
        using var h = new TestHarness();
        var project = h.CreateProject("demo");

        var task = h.Service.CreateTask(new TaskCreationRequest
        {
            Kind = TaskKind.Workspace,
            Mode = AssistantMode.Ask,
            WorkspacePath = project,
            FirstMessage = "Fix the startup bug",
        });

        Assert.Equal(project, task.WorkspacePath);
        Assert.Equal(project, task.WorkingDirectory);
    }

    [Fact]
    public void WorkspaceTaskOutsideAnyRootIsRejected()
    {
        using var h = new TestHarness();
        var outside = Path.Combine(h.RootDirectory, "not-a-root");
        Directory.CreateDirectory(outside);

        Assert.Throws<InvalidOperationException>(() =>
        {
            h.Service.CreateTask(new TaskCreationRequest
            {
                Kind = TaskKind.Workspace,
                Mode = AssistantMode.Auto,
                WorkspacePath = outside,
                FirstMessage = "do something",
            });
        });
    }

    [Fact]
    public void TitleIsDerivedFromTheFirstMessage()
    {
        using var h = new TestHarness();

        var task = h.Service.CreateTask(General("Please fix the startup crash in Defter. It happens on launch."));

        Assert.Equal("Fix the startup crash in Defter", task.Title);
    }

    [Fact]
    public async Task SuccessfulTurnCompletesTheTaskAndRecordsTheReply()
    {
        using var h = new TestHarness();
        var task = h.Service.CreateTask(General());

        var result = await h.Service.SendAsync(task.Id, "hello");

        Assert.True(result.Success);
        Assert.Equal(TaskState.Completed, task.State);

        var messages = h.Service.MessagesFor(task.Id);
        Assert.Equal(MessageRole.Owner, messages[0].Role);
        Assert.Equal("hello", messages[0].Text);
        Assert.Equal(MessageRole.Assistant, messages[1].Role);
        Assert.Equal("done", messages[1].Text);
    }

    [Fact]
    public async Task FirstTurnCreatesASessionAndFollowUpsResumeIt()
    {
        using var h = new TestHarness();
        var task = h.Service.CreateTask(General());

        await h.Service.SendAsync(task.Id, "first");
        await h.Service.SendAsync(task.Id, "second");

        Assert.Equal(2, h.Bridge.Requests.Count);

        Assert.False(h.Bridge.Requests[0].Resume);
        Assert.True(h.Bridge.Requests[1].Resume);

        // Same task, same Claude session across turns.
        Assert.Equal(h.Bridge.Requests[0].SessionId, h.Bridge.Requests[1].SessionId);
    }

    [Fact]
    public async Task EachTaskGetsItsOwnSession()
    {
        using var h = new TestHarness();
        var a = h.Service.CreateTask(General("first idea"));
        var b = h.Service.CreateTask(General("second idea"));

        await h.Service.SendAsync(a.Id, "x");
        await h.Service.SendAsync(b.Id, "y");

        Assert.NotEqual(h.Bridge.Requests[0].SessionId, h.Bridge.Requests[1].SessionId);
    }

    [Fact]
    public async Task ProviderFailureMarksTheTaskFailedAndKeepsTheReason()
    {
        using var h = new TestHarness();
        h.Bridge.Behaviour = (r, _, _) =>
            Task.FromResult(new BridgeTurnResult(false, false, r.SessionId, null, "claude exited with code 1"));

        var task = h.Service.CreateTask(General());
        var result = await h.Service.SendAsync(task.Id, "go");

        Assert.False(result.Success);
        Assert.Equal(TaskState.Failed, task.State);
        Assert.Equal("claude exited with code 1", task.LastError);
        Assert.Contains(h.Service.MessagesFor(task.Id), m => m.Role == MessageRole.System);
    }

    [Fact]
    public async Task BridgeExceptionIsReportedRatherThanCrashing()
    {
        using var h = new TestHarness();
        h.Bridge.Behaviour = (_, _, _) => throw new InvalidOperationException("boom");

        var task = h.Service.CreateTask(General());
        var result = await h.Service.SendAsync(task.Id, "go");

        Assert.False(result.Success);
        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("boom", task.LastError);
    }

    [Fact]
    public async Task StopCancelsTheRunningTurnAndMarksItCancelled()
    {
        using var h = new TestHarness();
        var started = new TaskCompletionSource();

        h.Bridge.Behaviour = async (r, _, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                return new BridgeTurnResult(false, true, r.SessionId, null, null);
            }
            return new BridgeTurnResult(true, false, r.SessionId, "never", null);
        };

        var task = h.Service.CreateTask(General());
        var run = h.Service.SendAsync(task.Id, "long job");

        await started.Task;
        Assert.True(h.Service.IsRunning(task.Id));

        h.Service.Stop(task.Id);
        var result = await run;

        Assert.True(result.Cancelled);
        Assert.Equal(TaskState.Cancelled, task.State);
        Assert.False(h.Service.IsRunning(task.Id));
    }

    [Fact]
    public async Task StopResolvesApprovalsThatWereWaiting()
    {
        using var h = new TestHarness();
        var asked = new TaskCompletionSource();

        h.Bridge.Behaviour = async (r, onEvent, ct) =>
        {
            onEvent(new AssistantEvent
            {
                TaskId = r.TaskId, Kind = AssistantEventKind.ApprovalRequired, Title = "needs approval",
            });

            var pending = h.Approvals.RequestAsync(new Core.Approvals.ApprovalRequest
            {
                TaskId = r.TaskId,
                ToolName = "Edit",
                Action = "Claude wants to modify",
                Target = "a.cs",
                Reason = "test",
                Category = Core.Policy.RiskCategory.WorkspaceWrite,
            }, ct);

            asked.SetResult();
            var outcome = await pending;

            return new BridgeTurnResult(false, outcome == Core.Approvals.ApprovalOutcome.Cancelled,
                r.SessionId, null, null);
        };

        var task = h.Service.CreateTask(General());
        var run = h.Service.SendAsync(task.Id, "edit something");

        await asked.Task;
        h.Service.Stop(task.Id);

        var result = await run;
        Assert.True(result.Cancelled);
        Assert.Empty(h.Approvals.Pending);
    }

    [Fact]
    public async Task ASecondSendWhileRunningIsRejected()
    {
        using var h = new TestHarness();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        h.Bridge.Behaviour = async (r, _, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new BridgeTurnResult(true, false, r.SessionId, "ok", null);
        };

        var task = h.Service.CreateTask(General());
        var run = h.Service.SendAsync(task.Id, "one");
        await started.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SendAsync(task.Id, "two"));

        release.SetResult();
        await run;
    }

    [Fact]
    public void ModeChangePersistsAndBecomesTheNewDefault()
    {
        using var h = new TestHarness();
        var task = h.Service.CreateTask(General());

        h.Service.SetMode(task.Id, AssistantMode.Ask);

        Assert.Equal(AssistantMode.Ask, task.Mode);
        Assert.Equal(AssistantMode.Ask, h.Settings.LastMode);
        Assert.Equal(AssistantMode.Ask, h.Store.LoadTasks().Single(t => t.Id == task.Id).Mode);
    }
}
