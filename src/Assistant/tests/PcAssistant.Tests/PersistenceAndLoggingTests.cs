using PcAssistant.Core.Bridge;
using PcAssistant.Core.Logging;
using PcAssistant.Core.Model;
using PcAssistant.Core.Tasks;
using Xunit;

namespace PcAssistant.Tests;

public class PersistenceTests
{
    private static TaskCreationRequest General(string msg = "think about something") => new()
    {
        Kind = TaskKind.General, Mode = AssistantMode.Auto, FirstMessage = msg,
    };

    [Fact]
    public async Task TasksMessagesAndEventsSurviveARestart()
    {
        using var h = new TestHarness();
        var task = h.Service.CreateTask(General("plan a launcher"));
        await h.Service.SendAsync(task.Id, "hello");

        var service = h.Restart();
        var reloaded = service.Find(task.Id);

        Assert.NotNull(reloaded);
        Assert.Equal("Plan a launcher", reloaded!.Title);
        Assert.Equal(2, service.MessagesFor(task.Id).Count);
        Assert.NotEmpty(service.EventsFor(task.Id));
    }

    [Fact]
    public async Task CompletedTasksStayCompletedAcrossRestart()
    {
        using var h = new TestHarness();
        var task = h.Service.CreateTask(General());
        await h.Service.SendAsync(task.Id, "go");

        var service = h.Restart();

        Assert.Equal(TaskState.Completed, service.Find(task.Id)!.State);
    }

    [Fact]
    public void ATaskLeftRunningBecomesInterruptedNotRunning()
    {
        using var h = new TestHarness();
        var task = h.Service.CreateTask(General());

        // Simulate the process dying mid-turn: the row is left in Running.
        task.State = TaskState.Running;
        h.Store.UpsertTask(task);

        var service = h.Restart();
        var reloaded = service.Find(task.Id)!;

        Assert.Equal(TaskState.Interrupted, reloaded.State);
        Assert.False(service.IsRunning(task.Id));
        Assert.Contains("exited", reloaded.LastError!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ATaskWaitingForApprovalAlsoBecomesInterrupted()
    {
        using var h = new TestHarness();
        var task = h.Service.CreateTask(General());
        task.State = TaskState.WaitingForApproval;
        h.Store.UpsertTask(task);

        Assert.Equal(TaskState.Interrupted, h.Restart().Find(task.Id)!.State);
    }

    [Fact]
    public async Task InterruptedTaskResumesTheSameSessionWhenTheOwnerSendsAgain()
    {
        using var h = new TestHarness();
        var task = h.Service.CreateTask(General());
        await h.Service.SendAsync(task.Id, "first");
        var sessionId = task.ClaudeSessionId;

        task.State = TaskState.Running;
        h.Store.UpsertTask(task);

        var service = h.Restart();
        await service.SendAsync(task.Id, "carry on");

        var last = h.Bridge.Requests[^1];
        Assert.True(last.Resume);
        Assert.Equal(sessionId, last.SessionId);
    }

    [Fact]
    public void NoClaudeCredentialsAreEverStored()
    {
        using var h = new TestHarness();
        h.Service.CreateTask(General());

        // SQLite still holds the file open, so read it with sharing.
        using var fs = new FileStream(h.Paths.DatabaseFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        var raw = System.Text.Encoding.Latin1.GetString(ms.ToArray());
        foreach (var forbidden in new[] { "accessToken", "refreshToken", "ANTHROPIC_API_KEY", "sk-ant" })
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContinueAsNewTaskBranchesWithASummaryAndKeepsTheLink()
    {
        using var h = new TestHarness();
        var project = h.CreateProject("demo");

        var original = h.Service.CreateTask(new TaskCreationRequest
        {
            Kind = TaskKind.Workspace, Mode = AssistantMode.Ask,
            WorkspacePath = project, FirstMessage = "Investigate the crash",
        });
        h.Service.AppendMessage(original, MessageRole.Assistant, "I found two likely causes.");

        var next = h.Service.ContinueAsNewTask(original.Id);

        Assert.Equal(original.Id, next.ContinuedFromTaskId);
        Assert.Equal(original.Kind, next.Kind);
        Assert.Equal(original.Mode, next.Mode);
        Assert.Equal(original.WorkspacePath, next.WorkspacePath);
        Assert.NotEqual(original.ClaudeSessionId, next.ClaudeSessionId);
        Assert.Contains("continued", next.Title, StringComparison.OrdinalIgnoreCase);

        var summary = h.Service.MessagesFor(next.Id).Single();
        Assert.Equal(MessageRole.System, summary.Role);
        Assert.Contains(original.Id.ToString(), summary.Text);
        Assert.Contains("Investigate the crash", summary.Text);
        Assert.Contains("two likely causes", summary.Text);
    }

    [Fact]
    public void ContinuingTwiceDoesNotStackTheSuffix()
    {
        using var h = new TestHarness();
        var first = h.Service.CreateTask(General("do a thing"));
        var second = h.Service.ContinueAsNewTask(first.Id);
        var third = h.Service.ContinueAsNewTask(second.Id);

        Assert.Equal(second.Title, third.Title);
    }
}

public class RawLogTests
{
    [Fact]
    public void LogWritesAndReadsBack()
    {
        var path = Path.Combine(Path.GetTempPath(), "pcassistant-log", Guid.NewGuid().ToString("N") + ".log");
        using (var log = new RawLogWriter(path))
        {
            log.Write("stdout", "hello world");
        }

        Assert.Contains("hello world", RawLogWriter.ReadTail(path));
        File.Delete(path);
    }

    [Fact]
    public void LogStaysWithinItsCap()
    {
        var path = Path.Combine(Path.GetTempPath(), "pcassistant-log", Guid.NewGuid().ToString("N") + ".log");
        const long cap = 4096;

        using (var log = new RawLogWriter(path, cap))
        {
            for (var i = 0; i < 2000; i++) log.Write("stdout", new string('x', 200));
        }

        // The live file is bounded; one rotated generation may also exist.
        Assert.True(new FileInfo(path).Length <= cap * 2,
            $"live log was {new FileInfo(path).Length} bytes for a {cap} byte cap");

        var rotated = path + ".1";
        if (File.Exists(rotated))
        {
            Assert.True(new FileInfo(rotated).Length <= cap * 2);
            File.Delete(rotated);
        }

        File.Delete(path);
    }

    [Fact]
    public void ReadingAMissingLogIsEmptyNotAnError()
        => Assert.Equal(string.Empty, RawLogWriter.ReadTail(@"C:\nope\missing.log"));
}

public class TitleGeneratorTests
{
    [Theory]
    [InlineData("Fix the startup bug", "Fix the startup bug")]
    [InlineData("please fix the startup bug", "Fix the startup bug")]
    [InlineData("Can you look at the crash? It is urgent.", "Look at the crash")]
    [InlineData("I want to build a launcher\nwith tiles", "Build a launcher")]
    public void TitlesAreDerivedDeterministically(string prompt, string expected)
        => Assert.Equal(expected, TitleGenerator.FromPrompt(prompt));

    [Fact]
    public void EmptyPromptFallsBack()
        => Assert.Equal("New task", TitleGenerator.FromPrompt("   "));

    [Fact]
    public void LongPromptsAreTrimmedToATitle()
    {
        var title = TitleGenerator.FromPrompt(string.Join(' ', Enumerable.Repeat("word", 60)));

        Assert.True(title.Length <= 61, $"title was {title.Length} chars");
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void TitlesAreStable()
    {
        const string prompt = "Refactor the approval broker";
        Assert.Equal(TitleGenerator.FromPrompt(prompt), TitleGenerator.FromPrompt(prompt));
    }
}
