using PcAssistant.Core.Approvals;
using PcAssistant.Core.Bridge;
using PcAssistant.Core.Model;
using PcAssistant.Core.Policy;
using PcAssistant.Core.Tasks;
using PcAssistant.Desktop.ViewModels;
using Xunit;

namespace PcAssistant.Tests;

public class MainViewModelTests
{
    private static MainViewModel Build(TestHarness h)
        => new(h.Service, h.Workspaces, h.Settings, h.Paths);

    private static TaskCreationRequest General(string msg = "think about a launcher") => new()
    {
        Kind = TaskKind.General, Mode = AssistantMode.Auto, FirstMessage = msg,
    };

    [Fact]
    public async Task StartingATaskAddsSelectsAndRunsIt()
    {
        using var h = new TestHarness();
        using var vm = Build(h);

        var form = vm.CreateNewTaskViewModel();
        form.FirstMessage = "Plan a launcher";

        await vm.StartTaskAsync(form);

        var item = Assert.Single(vm.Tasks);
        Assert.Equal("Plan a launcher", item.Title);
        Assert.Same(item, vm.SelectedTask);
        Assert.Contains(vm.Transcript.OfType<MessageItem>(), m => m.IsOwner);
    }

    [Fact]
    public void NewTaskFormRequiresAWorkspaceForWorkspaceTasks()
    {
        using var h = new TestHarness();
        using var vm = Build(h);

        var form = vm.CreateNewTaskViewModel();
        form.FirstMessage = "fix a bug";
        form.Kind = TaskKind.Workspace;

        Assert.False(form.CanCreate);

        form.WorkspacePath = h.CreateProject("demo");
        Assert.True(form.CanCreate);
        Assert.False(form.HasError);
    }

    [Fact]
    public void NewTaskFormRejectsAWorkspaceOutsideEveryRoot()
    {
        using var h = new TestHarness();
        using var vm = Build(h);

        var form = vm.CreateNewTaskViewModel();
        form.Kind = TaskKind.Workspace;
        form.WorkspacePath = Path.GetTempPath();

        Assert.True(form.HasError);
    }

    [Fact]
    public void AddingAProjectsFolderShowsItWithoutChangingTheSelectedWorkspace()
    {
        using var h = new TestHarness();
        using var vm = Build(h);
        var form = vm.CreateNewTaskViewModel();
        var selected = h.CreateProject("selected");
        form.Kind = TaskKind.Workspace;
        form.SelectWorkspace(selected);

        var addedRoot = Path.Combine(h.RootDirectory, "more-projects");
        Directory.CreateDirectory(addedRoot);
        var error = form.AddRoot(addedRoot);

        Assert.Null(error);
        Assert.Contains(addedRoot, form.Roots);
        Assert.Equal(selected, form.WorkspacePath);
        Assert.Equal($"Projects folder added: {addedRoot}", form.WorkspaceFeedback);
        Assert.False(form.WorkspaceFeedbackIsError);
    }

    [Fact]
    public void SelectingAnExistingWorkspaceUpdatesTheVisibleSelectionAndTaskRequest()
    {
        using var h = new TestHarness();
        using var vm = Build(h);
        var form = vm.CreateNewTaskViewModel();
        var project = h.CreateProject("existing");
        form.Kind = TaskKind.Workspace;

        var error = form.SelectWorkspace(project);

        Assert.Null(error);
        Assert.Equal(project, form.WorkspacePath);
        Assert.Contains(project, form.AvailableWorkspaces);
        Assert.Equal($"Workspace selected: {project}", form.WorkspaceFeedback);
        Assert.False(form.WorkspaceFeedbackIsError);
        Assert.Equal(project, form.ToRequest().WorkspacePath);
    }

    [Fact]
    public void CreatingAProjectSelectsItAndReportsTheFullPath()
    {
        using var h = new TestHarness();
        using var vm = Build(h);
        var form = vm.CreateNewTaskViewModel();
        form.Kind = TaskKind.Workspace;

        var error = form.CreateWorkspaceFolder(h.ProjectsRoot, "created-here");
        var expected = Path.Combine(h.ProjectsRoot, "created-here");

        Assert.Null(error);
        Assert.True(Directory.Exists(expected));
        Assert.Equal(expected, form.WorkspacePath);
        Assert.Contains(expected, form.AvailableWorkspaces);
        Assert.Equal($"Project created: {expected}", form.WorkspaceFeedback);
        Assert.False(form.WorkspaceFeedbackIsError);
    }

    [Fact]
    public void DuplicateProjectReportsAnInlineErrorAndKeepsThePreviousSelection()
    {
        using var h = new TestHarness();
        using var vm = Build(h);
        var form = vm.CreateNewTaskViewModel();
        var selected = h.CreateProject("selected");
        h.CreateProject("duplicate");
        form.Kind = TaskKind.Workspace;
        form.SelectWorkspace(selected);

        var error = form.CreateWorkspaceFolder(h.ProjectsRoot, "duplicate");

        Assert.Equal("A folder with that name already exists.", error);
        Assert.Equal(selected, form.WorkspacePath);
        Assert.Equal(error, form.WorkspaceFeedback);
        Assert.True(form.WorkspaceFeedbackIsError);
    }

    [Fact]
    public void NewTaskFormStartsFromTheLastUsedMode()
    {
        using var h = new TestHarness();
        h.Settings.LastMode = AssistantMode.Ask;

        using var vm = Build(h);
        Assert.Equal(AssistantMode.Ask, vm.CreateNewTaskViewModel().Mode);
    }

    [Fact]
    public void SelectingATaskSwitchesTheTranscript()
    {
        using var h = new TestHarness();
        var a = h.Service.CreateTask(General("first idea"));
        var b = h.Service.CreateTask(General("second idea"));
        h.Service.AppendMessage(a, MessageRole.Assistant, "reply about the first");
        h.Service.AppendMessage(b, MessageRole.Assistant, "reply about the second");

        using var vm = Build(h);

        vm.SelectedTask = vm.Tasks.Single(t => t.Id == a.Id);
        Assert.Contains(vm.Transcript.OfType<MessageItem>(), m => m.Text == "reply about the first");

        vm.SelectedTask = vm.Tasks.Single(t => t.Id == b.Id);
        Assert.Contains(vm.Transcript.OfType<MessageItem>(), m => m.Text == "reply about the second");
        Assert.DoesNotContain(vm.Transcript.OfType<MessageItem>(), m => m.Text == "reply about the first");
    }

    [Fact]
    public void ModeToggleUpdatesTheSelectedTask()
    {
        using var h = new TestHarness();
        h.Service.CreateTask(General());

        using var vm = Build(h);
        Assert.True(vm.IsAuto);

        vm.SetAskCommand.Execute(null);

        Assert.True(vm.IsAsk);
        Assert.False(vm.IsAuto);
        Assert.Equal(AssistantMode.Ask, vm.SelectedTask!.Task.Mode);
    }

    [Fact]
    public void SendIsBlockedUntilThereIsAMessageAndASelection()
    {
        using var h = new TestHarness();
        using var vm = Build(h);

        Assert.False(vm.CanSend);

        h.Service.CreateTask(General());
        vm.ReloadTasks();
        Assert.False(vm.CanSend);

        vm.Composer = "hello";
        Assert.True(vm.CanSend);
    }

    [Fact]
    public async Task FollowUpMessageAppearsInTheTranscript()
    {
        using var h = new TestHarness();
        using var vm = Build(h);

        var form = vm.CreateNewTaskViewModel();
        form.FirstMessage = "first";
        await vm.StartTaskAsync(form);

        vm.Composer = "and now fix only the first cause";
        await vm.SendAsync();

        Assert.Contains(vm.Transcript.OfType<MessageItem>(),
            m => m.Text == "and now fix only the first cause");
        Assert.Equal(string.Empty, vm.Composer);
    }

    [Fact]
    public async Task AnApprovalCardAppearsAndAllowingResolvesIt()
    {
        using var h = new TestHarness();
        using var vm = Build(h);

        ApprovalOutcome? outcome = null;
        var asked = new TaskCompletionSource();

        h.Bridge.Behaviour = async (r, _, ct) =>
        {
            var pending = h.Approvals.RequestAsync(new ApprovalRequest
            {
                TaskId = r.TaskId, ToolName = "Edit", Action = "Claude wants to modify",
                Target = "src/Main.cs", Reason = "ASK mode", Category = RiskCategory.WorkspaceWrite,
            }, ct);

            asked.SetResult();
            outcome = await pending;
            return new BridgeTurnResult(true, false, r.SessionId, "edited", null);
        };

        var form = vm.CreateNewTaskViewModel();
        form.FirstMessage = "edit main";
        var run = vm.StartTaskAsync(form);

        await asked.Task;

        var card = vm.Transcript.OfType<ApprovalItem>().Single();
        Assert.Equal("src/Main.cs", card.Target);
        Assert.Equal("Write in workspace", card.CategoryText);
        Assert.False(card.IsResolved);

        card.AllowCommand.Execute(null);
        await run;

        Assert.True(card.IsResolved);
        Assert.Equal("Allowed", card.OutcomeText);
        Assert.Equal(ApprovalOutcome.Allowed, outcome);
    }

    [Fact]
    public async Task DenyingAnApprovalIsReportedBackToTheBridge()
    {
        using var h = new TestHarness();
        using var vm = Build(h);

        ApprovalOutcome? outcome = null;
        var asked = new TaskCompletionSource();

        h.Bridge.Behaviour = async (r, _, ct) =>
        {
            var pending = h.Approvals.RequestAsync(new ApprovalRequest
            {
                TaskId = r.TaskId, ToolName = "Bash", Action = "Claude wants to run",
                Target = "git push", Reason = "never automatic", Category = RiskCategory.DestructiveGit,
            }, ct);

            asked.SetResult();
            outcome = await pending;
            return new BridgeTurnResult(true, false, r.SessionId, "did not push", null);
        };

        var form = vm.CreateNewTaskViewModel();
        form.FirstMessage = "push my work";
        var run = vm.StartTaskAsync(form);

        await asked.Task;
        vm.Transcript.OfType<ApprovalItem>().Single().DenyCommand.Execute(null);
        await run;

        Assert.Equal(ApprovalOutcome.Denied, outcome);
    }

    [Fact]
    public async Task StopCancelsTheRunningTask()
    {
        using var h = new TestHarness();
        using var vm = Build(h);

        var started = new TaskCompletionSource();
        h.Bridge.Behaviour = async (r, _, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { return new BridgeTurnResult(false, true, r.SessionId, null, null); }
            return new BridgeTurnResult(true, false, r.SessionId, "", null);
        };

        var form = vm.CreateNewTaskViewModel();
        form.FirstMessage = "long job";
        var run = vm.StartTaskAsync(form);

        await started.Task;
        Assert.True(vm.CanStop);

        vm.StopCommand.Execute(null);
        await run;

        Assert.Equal(TaskState.Cancelled, vm.SelectedTask!.Task.State);
    }

    [Fact]
    public async Task RawLogViewTogglesAndShowsContent()
    {
        using var h = new TestHarness();
        using var vm = Build(h);

        var task = h.Service.CreateTask(General());
        vm.ReloadTasks();

        await File.WriteAllTextAsync(h.Paths.RawLogFor(task.Id), "[stdout] hello from claude");

        Assert.False(vm.ShowRawLog);

        vm.ToggleRawLogCommand.Execute(null);
        Assert.True(vm.ShowRawLog);
        Assert.Contains("hello from claude", vm.RawLogText);

        vm.ToggleRawLogCommand.Execute(null);
        Assert.False(vm.ShowRawLog);
    }

    [Fact]
    public void RawLogViewIsHonestWhenThereIsNothingYet()
    {
        using var h = new TestHarness();
        using var vm = Build(h);

        h.Service.CreateTask(General());
        vm.ReloadTasks();

        vm.ToggleRawLogCommand.Execute(null);
        Assert.Contains("nothing logged", vm.RawLogText);
    }

    [Fact]
    public void AnInterruptedTaskIsSurfacedNotShownAsRunning()
    {
        using var h = new TestHarness();
        var task = h.Service.CreateTask(General());
        task.State = TaskState.Running;
        h.Store.UpsertTask(task);

        var service = h.Restart();
        using var vm = new MainViewModel(service, h.Workspaces, h.Settings, h.Paths);

        Assert.True(vm.IsInterrupted);
        Assert.Equal("Interrupted", vm.SelectedTask!.StateText);
        Assert.False(vm.CanStop);
    }

    [Fact]
    public void ContinueAsNewTaskAddsAndSelectsTheBranch()
    {
        using var h = new TestHarness();
        var original = h.Service.CreateTask(General("investigate the crash"));
        h.Service.AppendMessage(original, MessageRole.Assistant, "Two likely causes.");

        using var vm = Build(h);
        vm.ContinueAsNewTaskCommand.Execute(null);

        Assert.Equal(2, vm.Tasks.Count);
        Assert.Equal(original.Id, vm.SelectedTask!.Task.ContinuedFromTaskId);
        Assert.Contains(vm.Transcript.OfType<MessageItem>(),
            m => m.IsSystem && m.Text.Contains("Two likely causes"));
    }

    [Fact]
    public async Task ClaudeStatusIsSurfacedToTheHeader()
    {
        using var h = new TestHarness();
        using var vm = Build(h);

        h.Bridge.Status = new ClaudeStatus(ClaudeAvailability.AuthenticationRequired, "2.1.247",
            "claude.exe", "Run: claude auth login");

        await vm.RefreshClaudeStatusAsync();

        Assert.Equal("Claude Code authentication required", vm.ClaudeStatusText);
        Assert.Equal("Run: claude auth login", vm.ClaudeStatusDetail);
    }

    [Fact]
    public void ProgressItemsRenderWithGlyphsAndCollapsedDetail()
    {
        var item = new ProgressItem
        {
            Kind = AssistantEventKind.ToolFailed, Title = "Bash failed", Detail = "exit code 1",
        };

        Assert.Equal("✕", item.Glyph);
        Assert.True(item.IsFailure);
        Assert.True(item.HasDetail);
        Assert.False(item.DetailVisible);
    }
}
