using PcAssistant.Core;
using PcAssistant.Core.Approvals;
using PcAssistant.Core.Bridge;
using PcAssistant.Core.Model;
using PcAssistant.Core.Persistence;
using PcAssistant.Core.Tasks;
using PcAssistant.Core.Workspaces;

namespace PcAssistant.Tests;

/// <summary>
/// A stand-in for the real bridge. Automated tests never launch Claude Code — the real
/// integration is exercised separately against the installed CLI.
/// </summary>
public sealed class FakeBridge : IClaudeCodeBridge
{
    public List<BridgeTurnRequest> Requests { get; } = new();

    public ClaudeStatus Status { get; set; } = new(ClaudeAvailability.Ready, "test", "claude.exe");

    /// <summary>Lets a test drive what happens during a turn (events, blocking, failure).</summary>
    public Func<BridgeTurnRequest, Action<AssistantEvent>, CancellationToken, Task<BridgeTurnResult>>? Behaviour { get; set; }

    public Task<ClaudeStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Status);

    public Task<BridgeTurnResult> RunTurnAsync(
        BridgeTurnRequest request, Action<AssistantEvent> onEvent, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (Behaviour is not null) return Behaviour(request, onEvent, cancellationToken);

        onEvent(new AssistantEvent
        {
            TaskId = request.TaskId,
            Kind = AssistantEventKind.ToolCompleted,
            Title = "Read notes.txt",
            ToolName = "Read",
        });

        return Task.FromResult(new BridgeTurnResult(true, false, request.SessionId, "done", null));
    }
}

/// <summary>Creates a fully wired assistant against a temporary directory.</summary>
public sealed class TestHarness : IDisposable
{
    public TestHarness()
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), "pcassistant-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootDirectory);

        Paths = new AppPaths(Path.Combine(RootDirectory, "app"));
        Settings = AppSettings.Load(Paths.SettingsFile);

        ProjectsRoot = Path.Combine(RootDirectory, "projects");
        Directory.CreateDirectory(ProjectsRoot);
        Settings.WorkspaceRoots.Add(ProjectsRoot);

        Store = new TaskStore(Paths.DatabaseFile);
        Approvals = new ApprovalBroker(TimeSpan.FromSeconds(30));
        Workspaces = new WorkspaceService(() => Settings.WorkspaceRoots.ToList());
        Bridge = new FakeBridge();
        Service = new AssistantService(Store, Bridge, Workspaces, Paths, Settings, Approvals);
    }

    public string RootDirectory { get; }
    public string ProjectsRoot { get; }
    public AppPaths Paths { get; }
    public AppSettings Settings { get; }
    public TaskStore Store { get; private set; }
    public ApprovalBroker Approvals { get; }
    public WorkspaceService Workspaces { get; }
    public FakeBridge Bridge { get; }
    public AssistantService Service { get; private set; }

    public string CreateProject(string name)
    {
        var dir = Path.Combine(ProjectsRoot, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Simulates closing and reopening PC Assistant against the same database.</summary>
    public AssistantService Restart()
    {
        Service.Dispose();
        Store.Dispose();

        Store = new TaskStore(Paths.DatabaseFile);
        Service = new AssistantService(Store, Bridge, Workspaces, Paths, Settings, Approvals);
        return Service;
    }

    public void Dispose()
    {
        Service.Dispose();
        Approvals.Dispose();
        Store.Dispose();

        try { Directory.Delete(RootDirectory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
