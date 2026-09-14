using System.Diagnostics;
using PcAssistant.ClaudeCode;
using PcAssistant.Core;
using PcAssistant.Core.Approvals;
using PcAssistant.Core.Bridge;
using PcAssistant.Core.Logging;
using PcAssistant.Core.Model;
using PcAssistant.Core.Persistence;
using PcAssistant.Core.Tasks;
using PcAssistant.Core.Workspaces;
using Xunit;
using Xunit.Abstractions;

namespace PcAssistant.Tests;

/// <summary>
/// Exercises the real installed Claude Code. These are opt-in: the normal test run uses
/// the fake bridge only. Enable with PCASSISTANT_REAL_CLAUDE=1.
///
///   scripts\verify-real-claude.ps1
///
/// Everything happens in a disposable temporary workspace outside the PC Assistant repo.
/// </summary>
[Trait("Category", "RealClaude")]
[Collection(DesktopCollection.Name)]
public sealed class RealClaudeIntegrationTests : IDisposable
{
    private const string EnableVariable = "PCASSISTANT_REAL_CLAUDE";

    private readonly ITestOutputHelper _output;
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly TaskStore _store;
    private readonly ApprovalBroker _approvals;
    private readonly AssistantService _service;
    private readonly string _projectsRoot;

    public static bool Enabled => Environment.GetEnvironmentVariable(EnableVariable) == "1";

    public RealClaudeIntegrationTests(ITestOutputHelper output)
    {
        _output = output;

        // Deliberately outside the PC Assistant source tree.
        _root = Path.Combine(Path.GetTempPath(), "pcassistant-real", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _paths = new AppPaths(Path.Combine(_root, "app"));
        _settings = AppSettings.Load(_paths.SettingsFile);

        _projectsRoot = Path.Combine(_root, "projects");
        Directory.CreateDirectory(_projectsRoot);
        _settings.WorkspaceRoots.Add(_projectsRoot);
        _settings.ApprovalTimeoutMinutes = 3;

        _store = new TaskStore(_paths.DatabaseFile);
        _approvals = new ApprovalBroker(TimeSpan.FromMinutes(3));

        var workspaces = new WorkspaceService(() => _settings.WorkspaceRoots.ToList());

        AssistantService? service = null;
        var bridge = new ClaudeCodeBridge(
            _paths, _settings, _approvals,
            id => service?.Find(id)?.Mode ?? AssistantMode.Ask,
            LocateHookHost(), LocatePcTools());

        _service = service = new AssistantService(_store, bridge, workspaces, _paths, _settings, _approvals);
    }

    /// <summary>
    /// The hook helper is copied next to the desktop exe by the build, not into the test
    /// output, so point at the built binary directly.
    /// </summary>
    private static string LocateHookHost()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(dir.FullName,
                    "src", "PcAssistant.HookHost", "bin", config, "net10.0", "PcAssistant.HookHost.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "PcAssistant.HookHost.exe");
    }

    private static string LocatePcTools()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        foreach (var config in new[] { "Debug", "Release" })
        {
            var candidate = Path.Combine(dir.FullName, "src", "PcAssistant.Desktop", "bin", config,
                "net10.0-windows", "PcTools", "PcAssistant.PcTools.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return Path.Combine(AppContext.BaseDirectory, "PcTools", "PcAssistant.PcTools.exe");
    }

    [SkippableFact]
    public async Task GeneralTaskCanObserveTheRealPcThroughNativeMcpTools()
    {
        Skip.IfNot(Enabled, "real-claude only");
        var approvals = new List<ApprovalRequest>();
        _approvals.ApprovalRequested += (_, request) =>
        {
            approvals.Add(request);
            _approvals.Resolve(request.Id, allow: false);
        };
        const string prompt = "Use the PC Assistant native tools, not PowerShell: call system_info, windows, and screenshot. " +
                              "Report the OS family, count of visible windows, foreground window title, and briefly describe the screenshot. " +
                              "End with the exact marker NATIVE-PC-TOOLS-OK.";
        var task = NewGeneralTask(AssistantMode.Auto, prompt);
        var result = await _service.SendAsync(task.Id, prompt);
        Dump(task, "native pc tools");
        Assert.True(result.Success, result.Error);
        Assert.Contains("NATIVE-PC-TOOLS-OK", result.FinalText ?? "");
        Assert.Empty(approvals);
        var toolEvents = _service.EventsFor(task.Id).Where(e => e.Kind == AssistantEventKind.ToolStarted).ToArray();
        Assert.Contains(toolEvents, e => e.ToolName == "mcp__pc-assistant__system_info");
        Assert.Contains(toolEvents, e => e.ToolName == "mcp__pc-assistant__windows");
        Assert.Contains(toolEvents, e => e.ToolName == "mcp__pc-assistant__screenshot");
    }

    private string NewProject(string name)
    {
        var dir = Path.Combine(_projectsRoot, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private AssistantTask NewWorkspaceTask(string project, AssistantMode mode, string firstMessage)
        => _service.CreateTask(new TaskCreationRequest
        {
            Kind = TaskKind.Workspace, Mode = mode, WorkspacePath = project, FirstMessage = firstMessage,
        });

    private AssistantTask NewGeneralTask(AssistantMode mode, string firstMessage)
        => _service.CreateTask(new TaskCreationRequest
        {
            Kind = TaskKind.General, Mode = mode, FirstMessage = firstMessage,
        });

    private string RawLog(AssistantTask task) => RawLogWriter.ReadTail(_paths.RawLogFor(task.Id));

    private void Dump(AssistantTask task, string label)
    {
        _output.WriteLine($"--- {label}: {task.State} ---");
        foreach (var e in _service.EventsFor(task.Id))
            _output.WriteLine($"  [{e.Kind}] {e.Title}");
        foreach (var m in _service.MessagesFor(task.Id))
            _output.WriteLine($"  <{m.Role}> {Truncate(m.Text, 500)}");
        if (task.LastError is not null) _output.WriteLine("  ERROR: " + task.LastError);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ---------------------------------------------------------------- 1. status

    [SkippableFact]
    public async Task ClaudeCodeIsInstalledAndUsable()
    {
        Skip.IfNot(Enabled, "Set PCASSISTANT_REAL_CLAUDE=1 to run the real integration suite.");

        var status = await _service.GetClaudeStatusAsync();
        _output.WriteLine($"availability={status.Availability} version={status.Version} exe={status.ExecutablePath}");
        _output.WriteLine($"detail={status.Detail}");

        Assert.Equal(ClaudeAvailability.Ready, status.Availability);
        Assert.False(string.IsNullOrWhiteSpace(status.Version));
    }

    // -------------------------------------------------------------- 2. general

    [SkippableFact]
    public async Task GeneralTaskDiscussesWithoutNeedingAWorkspace()
    {
        Skip.IfNot(Enabled, "real-claude only");

        const string prompt =
            "I am thinking about making a small app launcher for my own PC. " +
            "In two or three sentences, what approaches make sense? Just discuss - build nothing.";

        var task = NewGeneralTask(AssistantMode.Auto, prompt);
        var result = await _service.SendAsync(task.Id, prompt);
        Dump(task, "general");

        Assert.True(result.Success, result.Error);
        Assert.Equal(TaskState.Completed, task.State);
        Assert.False(string.IsNullOrWhiteSpace(result.FinalText));

        // Discussion must not have created a project.
        Assert.Empty(Directory.GetDirectories(_projectsRoot));
        Assert.False(string.IsNullOrWhiteSpace(RawLog(task)));
    }

    // ------------------------------------------------- 3. workspace + follow-up

    [SkippableFact]
    public async Task WorkspaceTaskInspectsEditsAndKeepsContextAcrossTurns()
    {
        Skip.IfNot(Enabled, "real-claude only");

        var project = NewProject("edit-demo");
        var file = Path.Combine(project, "notes.txt");
        await File.WriteAllTextAsync(file, "alpha\nbeta\ngamma\n");

        const string prompt = "Read notes.txt and tell me its second line. Do not change anything yet.";
        var task = NewWorkspaceTask(project, AssistantMode.Auto, prompt);

        var first = await _service.SendAsync(task.Id, prompt);
        Dump(task, "workspace turn 1");

        Assert.True(first.Success, first.Error);
        Assert.Contains("beta", first.FinalText ?? "", StringComparison.OrdinalIgnoreCase);

        var events = _service.EventsFor(task.Id);
        Assert.Contains(events, e => e.Kind == AssistantEventKind.ToolStarted);
        Assert.Contains(events, e => e.Kind == AssistantEventKind.ToolCompleted);

        // Follow-up in the same task must remember what "that file" refers to.
        var sessionBefore = task.ClaudeSessionId;
        var second = await _service.SendAsync(task.Id, "Now append a line saying 'delta' to that same file.");
        Dump(task, "workspace turn 2");

        Assert.True(second.Success, second.Error);
        Assert.Equal(sessionBefore, task.ClaudeSessionId);

        var content = await File.ReadAllTextAsync(file);
        _output.WriteLine("file now: " + content.Replace("\n", "\\n"));
        Assert.Contains("delta", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alpha", content, StringComparison.OrdinalIgnoreCase);

        Assert.True(File.Exists(_paths.RawLogFor(task.Id)));
    }

    // ------------------------------------------------------------- 4. AUTO mode

    [SkippableFact]
    public async Task AutoModeDoesRoutineWorkWithoutAskingButStopsHighRiskWork()
    {
        Skip.IfNot(Enabled, "real-claude only");

        var project = NewProject("auto-demo");
        await File.WriteAllTextAsync(Path.Combine(project, "readme.md"), "# Demo\n");

        var approvals = new List<ApprovalRequest>();
        _approvals.ApprovalRequested += (_, r) =>
        {
            approvals.Add(r);
            _approvals.Resolve(r.Id, allow: false);   // deny everything AUTO chooses to ask about
        };

        // (a) routine work: read + create a file. No approval expected.
        const string routinePrompt =
            "Read readme.md, then create hello.txt containing exactly the word Hello. Keep it brief.";

        var routine = NewWorkspaceTask(project, AssistantMode.Auto, routinePrompt);
        var result = await _service.SendAsync(routine.Id, routinePrompt);
        Dump(routine, "auto routine");

        foreach (var a in approvals) _output.WriteLine($"  unexpected approval: {a.Category} {a.Target}");

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Path.Combine(project, "hello.txt")), "AUTO should have created the file");
        Assert.Empty(approvals);

        // (b) high-risk: a real registry write. It must be gated, and we deny it, so
        //     nothing is actually changed on the machine.
        const string keyPath = @"HKCU\Software\PcAssistantVerifyOnly";
        approvals.Clear();

        var riskyPrompt =
            $"Run this exact command and report what happened: reg add {keyPath} /v Probe /t REG_SZ /d 1 /f";

        var risky = NewWorkspaceTask(project, AssistantMode.Auto, riskyPrompt);
        await _service.SendAsync(risky.Id, riskyPrompt);
        Dump(risky, "auto high-risk");

        foreach (var a in approvals) _output.WriteLine($"  gated: {a.Category} {a.Target}");

        Assert.NotEmpty(approvals);
        Assert.Contains(approvals, a =>
            a.Category is Core.Policy.RiskCategory.SystemChange
                       or Core.Policy.RiskCategory.CommandHighImpact
                       or Core.Policy.RiskCategory.OutsideWorkspace);

        Assert.False(RegistryKeyExists(@"Software\PcAssistantVerifyOnly"),
            "the denied registry write must not have happened");
    }

    private static bool RegistryKeyExists(string subKey)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey);
        return key is not null;
    }

    // -------------------------------------------------------------- 5. ASK mode

    [SkippableFact]
    public async Task AskModeGatesEditsAndHonoursDenyThenAllow()
    {
        Skip.IfNot(Enabled, "real-claude only");

        var project = NewProject("ask-demo");
        var file = Path.Combine(project, "config.txt");
        await File.WriteAllTextAsync(file, "mode=old\n");
        var original = await File.ReadAllTextAsync(file);

        // (a) reading is free in ASK mode.
        const string readPrompt = "Read config.txt and tell me the value of mode. Change nothing.";
        var reading = NewWorkspaceTask(project, AssistantMode.Ask, readPrompt);

        var readApprovals = new List<ApprovalRequest>();
        void DenyRead(object? _, ApprovalRequest r) { readApprovals.Add(r); _approvals.Resolve(r.Id, false); }

        _approvals.ApprovalRequested += DenyRead;
        var readResult = await _service.SendAsync(reading.Id, readPrompt);
        _approvals.ApprovalRequested -= DenyRead;

        Dump(reading, "ask read");
        foreach (var a in readApprovals) _output.WriteLine($"  asked during read: {a.Category} {a.Target}");

        Assert.True(readResult.Success, readResult.Error);
        Assert.Contains("old", readResult.FinalText ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(readApprovals, a => a.Category == Core.Policy.RiskCategory.Read);

        // (b) a write is gated; denying it must leave the file untouched.
        const string editPrompt = "Change the mode value in config.txt from old to new.";
        var editTask = NewWorkspaceTask(project, AssistantMode.Ask, editPrompt);

        var denied = new List<ApprovalRequest>();
        void Deny(object? _, ApprovalRequest r) { denied.Add(r); _approvals.Resolve(r.Id, false); }

        _approvals.ApprovalRequested += Deny;
        await _service.SendAsync(editTask.Id, editPrompt);
        _approvals.ApprovalRequested -= Deny;

        Dump(editTask, "ask deny");
        foreach (var a in denied) _output.WriteLine($"  denied: {a.Category} {a.Target}");

        Assert.NotEmpty(denied);
        Assert.Equal(original, await File.ReadAllTextAsync(file));

        // (c) allowing the same operation lets it through.
        var allowed = new List<ApprovalRequest>();
        void Allow(object? _, ApprovalRequest r) { allowed.Add(r); _approvals.Resolve(r.Id, true); }

        _approvals.ApprovalRequested += Allow;
        var allowResult = await _service.SendAsync(editTask.Id,
            "Please try that change again - I will approve it this time.");
        _approvals.ApprovalRequested -= Allow;

        Dump(editTask, "ask allow");
        foreach (var a in allowed) _output.WriteLine($"  allowed: {a.Category} {a.Target}");

        Assert.NotEmpty(allowed);
        Assert.True(allowResult.Success, allowResult.Error);
        Assert.Contains("new", await File.ReadAllTextAsync(file), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------- 6. cancel + cleanup

    [SkippableFact]
    public async Task StopKillsOnlyTheOwnedClaudeProcessTree()
    {
        Skip.IfNot(Enabled, "real-claude only");

        var project = NewProject("cancel-demo");

        var unrelated = StartUnrelatedProcess();
        var before = ClaudeProcessIds();

        const string prompt =
            "Count slowly from 1 to 400, writing each number on its own line into count.txt, " +
            "one Write call per number. Take your time.";

        var task = NewWorkspaceTask(project, AssistantMode.Auto, prompt);
        var run = _service.SendAsync(task.Id, prompt);

        // Let it genuinely start before stopping it.
        await WaitUntilAsync(() => _service.EventsFor(task.Id)
                .Any(e => e.Kind is AssistantEventKind.ToolStarted or AssistantEventKind.Progress),
            TimeSpan.FromSeconds(120));

        _service.Stop(task.Id);
        var result = await run;

        Dump(task, "cancelled");
        Assert.True(result.Cancelled);
        Assert.Equal(TaskState.Cancelled, task.State);

        // The owned tree is gone.
        await WaitUntilAsync(() => !ClaudeProcessIds().Except(before).Any(), TimeSpan.FromSeconds(30));
        var survivors = ClaudeProcessIds().Except(before).ToList();
        foreach (var id in survivors) _output.WriteLine("  surviving claude pid: " + id);
        Assert.Empty(survivors);

        // Work produced before the stop is preserved.
        Assert.NotEmpty(_service.EventsFor(task.Id));
        Assert.False(string.IsNullOrWhiteSpace(RawLog(task)));

        // An unrelated process was not touched.
        Assert.False(unrelated.HasExited, "PC Assistant must not kill processes it did not launch");
        try { unrelated.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        unrelated.Dispose();
    }

    private static Process StartUnrelatedProcess()
    {
        var p = new Process
        {
            StartInfo =
            {
                FileName = "cmd.exe",
                Arguments = "/c ping -n 180 127.0.0.1 > nul",
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        p.Start();
        return p;
    }

    private static HashSet<int> ClaudeProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("claude"))
        {
            ids.Add(p.Id);
            p.Dispose();
        }
        return ids;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(250);
        }
    }

    // ------------------------------------------------------ 7. skill and hooks

    [SkippableFact]
    public async Task SkillBehaviourIsLoadedAndGitIsNotTouchedAutomatically()
    {
        Skip.IfNot(Enabled, "real-claude only");

        var project = NewProject("skill-demo");
        RunGit(project, "init");
        RunGit(project, "config user.email pcassistant@localhost");
        RunGit(project, "config user.name PcAssistant");
        await File.WriteAllTextAsync(Path.Combine(project, "a.txt"), "one\n");

        var approvals = new List<ApprovalRequest>();
        _approvals.ApprovalRequested += (_, r) => { approvals.Add(r); _approvals.Resolve(r.Id, false); };

        const string prompt =
            "Add a file b.txt containing the word two, then tell me the repository status. " +
            "Keep your answer to a couple of sentences.";

        var task = NewWorkspaceTask(project, AssistantMode.Auto, prompt);
        var result = await _service.SendAsync(task.Id, prompt);
        Dump(task, "skill");

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Path.Combine(project, "b.txt")));

        // The skill forbids automatic commits, and the policy gates them anyway.
        _output.WriteLine("git log: " + RunGit(project, "log --oneline"));
        Assert.DoesNotContain("b.txt", RunGit(project, "log --stat"));
        Assert.Contains("b.txt", RunGit(project, "status --porcelain"));

        // Hook lifecycle actually reached PC Assistant.
        var raw = RawLog(task);
        Assert.Contains("[hook] PreToolUse", raw);
        Assert.Contains("policy:", raw);
    }

    private static string RunGit(string workingDirectory, string args)
    {
        using var p = new Process
        {
            StartInfo =
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        p.Start();
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return output;
    }

    // ------------------------------------------------------- 8. restart honesty

    [SkippableFact]
    public async Task ResumeAfterRestartContinuesTheSameClaudeSession()
    {
        Skip.IfNot(Enabled, "real-claude only");

        var project = NewProject("resume-demo");

        const string prompt = "Remember this number: 8123. Just acknowledge it in a few words.";
        var task = NewWorkspaceTask(project, AssistantMode.Auto, prompt);

        var firstTurn = await _service.SendAsync(task.Id, prompt);
        Assert.True(firstTurn.Success, firstTurn.Error);

        // Simulate PC Assistant being killed mid-task, then restarted.
        task.State = TaskState.Running;
        _store.UpsertTask(task);

        var store2 = new TaskStore(_paths.DatabaseFile);
        var workspaces = new WorkspaceService(() => _settings.WorkspaceRoots.ToList());

        AssistantService? svc = null;
        var bridge = new ClaudeCodeBridge(_paths, _settings, _approvals,
            id => svc?.Find(id)?.Mode ?? AssistantMode.Ask, LocateHookHost(), LocatePcTools());

        using var service2 = svc = new AssistantService(store2, bridge, workspaces, _paths, _settings, _approvals);

        var reloaded = service2.Find(task.Id)!;
        Assert.Equal(TaskState.Interrupted, reloaded.State);

        var followUp = await service2.SendAsync(task.Id, "What number did I ask you to remember?");
        _output.WriteLine("resumed answer: " + followUp.FinalText);

        Assert.True(followUp.Success, followUp.Error);
        Assert.Contains("8123", followUp.FinalText ?? "");

        store2.Dispose();
    }

    public void Dispose()
    {
        _service.Dispose();
        _approvals.Dispose();
        _store.Dispose();

        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
