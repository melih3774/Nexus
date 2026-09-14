using PcAssistant.Core.Model;
using PcAssistant.Core.Tasks;
using PcAssistant.Desktop.ViewModels;
using Xunit;

namespace PcAssistant.Tests;

/// <summary>
/// Searching the task list. The rule worth testing is not the filtering itself but what
/// happens to the conversation you are reading while you type: it stays.
/// </summary>
public sealed class TaskSearchTests
{
    private static MainViewModel Build(TestHarness h) => new(h.Service, h.Workspaces, h.Settings, h.Paths);

    private static AssistantTask Create(TestHarness h, string message, string? workspace = null) =>
        h.Service.CreateTask(new TaskCreationRequest
        {
            Kind = workspace is null ? TaskKind.General : TaskKind.Workspace,
            Mode = AssistantMode.Auto,
            WorkspacePath = workspace,
            FirstMessage = message,
        });

    [Fact]
    public void FilteringMatchesTitleAndWorkspaceAndReportsHowManyMatched()
    {
        using var harness = new TestHarness();
        var invoices = harness.CreateProject("invoices");
        Create(harness, "fix the login redirect on the website");
        Create(harness, "summarise last quarter", invoices);
        Create(harness, "plan a small app launcher");

        using var model = Build(harness);
        model.ReloadTasks();
        Assert.Equal(3, model.Tasks.Count);
        Assert.False(model.IsTaskFilterActive);
        Assert.Equal(string.Empty, model.TaskFilterSummary);

        model.TaskFilter = "login";
        Assert.True(model.IsTaskFilterActive);
        Assert.Equal(1, model.TaskMatchCount);
        Assert.Contains("1 task matches", model.TaskFilterSummary);
        Assert.Contains(model.Tasks, t => t.Title.Contains("login", StringComparison.OrdinalIgnoreCase));

        // Case-insensitive, and the workspace path counts as well as the title.
        model.TaskFilter = "INVOICES";
        Assert.Contains(model.Tasks, t => t.Task.WorkspacePath == invoices);

        model.TaskFilter = "nothing matches this";
        Assert.Equal(0, model.TaskMatchCount);
        Assert.Contains("No task matches", model.TaskFilterSummary);
        // The open conversation is still there, and the summary says so rather than pretending.
        Assert.Contains("Still showing the task you have open", model.TaskFilterSummary);

        model.TaskFilter = "";
        Assert.Equal(3, model.TaskMatchCount);
        Assert.Equal(3, model.Tasks.Count);
        Assert.False(model.IsTaskFilterActive);
    }

    [Fact]
    public void AFilterThatWouldHideTheOpenTaskKeepsItSelectedAndVisible()
    {
        using var harness = new TestHarness();
        var open = Create(harness, "the conversation I am reading right now");
        Create(harness, "an unrelated task about invoices");

        using var model = Build(harness);
        model.ReloadTasks();
        model.SelectedTask = model.Tasks.Single(t => t.Id == open.Id);

        // Typing a search that excludes the open task must not close it.
        model.TaskFilter = "invoices";

        Assert.Equal(open.Id, model.SelectedTask!.Id);
        Assert.Contains(model.Tasks, t => t.Id == open.Id);
    }

    [Fact]
    public async Task StartingANewTaskClearsAFilterThatWouldHaveHiddenIt()
    {
        using var harness = new TestHarness();
        Create(harness, "an old task about invoices");

        using var model = Build(harness);
        model.ReloadTasks();
        model.TaskFilter = "invoices";
        Assert.True(model.IsTaskFilterActive);

        var form = model.CreateNewTaskViewModel();
        form.FirstMessage = "something completely different";
        await model.StartTaskAsync(form);

        Assert.False(model.IsTaskFilterActive);
        Assert.Contains(model.Tasks, t => t.Title.Contains("different", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClearingIsOnlyOfferedWhileAFilterIsActive()
    {
        using var harness = new TestHarness();
        Create(harness, "a task");
        using var model = Build(harness);
        model.ReloadTasks();

        Assert.False(model.ClearTaskFilterCommand.CanExecute(null));
        model.TaskFilter = "task";
        Assert.True(model.ClearTaskFilterCommand.CanExecute(null));
        model.ClearTaskFilterCommand.Execute(null);
        Assert.Equal(string.Empty, model.TaskFilter);
    }
}
