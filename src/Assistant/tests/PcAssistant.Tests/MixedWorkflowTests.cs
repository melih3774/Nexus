using System.Diagnostics;
using PcAssistant.ClaudeCode;
using PcAssistant.Core;
using PcAssistant.Core.Approvals;
using PcAssistant.Core.Bridge;
using PcAssistant.Core.Logging;
using PcAssistant.Core.Model;
using PcAssistant.Core.Persistence;
using PcAssistant.Core.Policy;
using PcAssistant.Core.Tasks;
using PcAssistant.Core.Workspaces;
using PcAssistant.Windows;
using Xunit;
using Xunit.Abstractions;

namespace PcAssistant.Tests;

/// <summary>
/// Whether PC Assistant can actually close its own loop, end to end, through the real Claude
/// Code and the real tools: edit something, run it, look at the result, and only then say it
/// worked.
///
/// Every other suite tests one layer. These two test the join, because that is where an
/// assistant stops being a sequence of tool calls — and the failure they exist to catch is the
/// one no unit test can: a model that edits a file, gets no error, and reports success without
/// ever seeing the thing it changed.
///
/// Opt-in, and everything happens in a disposable workspace outside this repository.
/// </summary>
[Trait("Category", "RealClaude")]
[Collection(DesktopCollection.Name)]
public sealed class MixedWorkflowTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly TaskStore _store;
    private readonly ApprovalBroker _approvals;
    private readonly AssistantService _service;
    private readonly string _projectsRoot;
    private readonly List<ApprovalRequest> _approved = [];

    public MixedWorkflowTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "pcassistant-workflow", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _paths = new AppPaths(Path.Combine(_root, "app"));
        _settings = AppSettings.Load(_paths.SettingsFile);
        _projectsRoot = Path.Combine(_root, "projects");
        Directory.CreateDirectory(_projectsRoot);
        _settings.WorkspaceRoots.Add(_projectsRoot);
        _settings.ApprovalTimeoutMinutes = 5;

        _store = new TaskStore(_paths.DatabaseFile);
        _approvals = new ApprovalBroker(TimeSpan.FromMinutes(5));

        // Standing in for the owner. In real use each of these is a card; here they are
        // recorded and allowed, so the test can also assert which ones were needed at all.
        _approvals.ApprovalRequested += (_, request) =>
        {
            _approved.Add(request);
            _output.WriteLine($"  [approval] {request.Category} {request.ToolName}: {request.Target}");
            _approvals.Resolve(request.Id, allow: true);
        };

        AssistantService? service = null;
        var bridge = new ClaudeCodeBridge(_paths, _settings, _approvals,
            id => service?.Find(id)?.Mode ?? AssistantMode.Ask, LocateHookHost(), LocatePcTools());
        _service = service = new AssistantService(
            _store, bridge, new WorkspaceService(() => _settings.WorkspaceRoots.ToList()), _paths, _settings, _approvals);
    }

    // ------------------------------------------------------- A. web project

    [SkippableFact]
    public async Task WebProjectChangeIsMadeRunAndVisuallyVerifiedInTheBrowser()
    {
        Skip.IfNot(RealClaudeIntegrationTests.Enabled, "Set PCASSISTANT_REAL_CLAUDE=1 to run the mixed workflows.");

        var project = NewProject("web-workflow");
        var page = Path.Combine(project, "index.html");
        File.WriteAllText(page, """
            <!doctype html>
            <html lang="en">
              <head><meta charset="utf-8"><title>Workflow Fixture</title></head>
              <body style="font-family: system-ui; background: #ffffff; color: #111111;">
                <h1 id="headline">ORIGINAL HEADLINE</h1>
                <p>A disposable page used to check that a change can be seen after it is made.</p>
              </body>
            </html>
            """);
        File.WriteAllText(Path.Combine(project, "serve.py"),
            "import http.server, socketserver, os\n" +
            "os.chdir(os.path.dirname(os.path.abspath(__file__)))\n" +
            "socketserver.TCPServer.allow_reuse_address = True\n" +
            "socketserver.TCPServer(('127.0.0.1', 8731), http.server.SimpleHTTPRequestHandler).serve_forever()\n");

        const string prompt = """
            This is a disposable test project. Work only inside it.

            1. Read index.html.
            2. Change the h1 text from ORIGINAL HEADLINE to VERIFIED HEADLINE and make the h1 red.
            3. Start the local server: run `python serve.py` in the background. It serves on
               http://127.0.0.1:8731/ .
            4. Open http://127.0.0.1:8731/ and actually look at the rendered page — use the
               claude-in-chrome tools if the browser is connected, otherwise open it and use
               capture_window on the browser window.
            5. Verify from what you observed that the headline reads VERIFIED HEADLINE and is red.
               Do not rely on the file contents as proof; the point is to confirm the rendered result.
            6. If verification fails, diagnose it and fix it, then check again.
            7. Close only tabs you opened, and stop the server when you are done.

            Report what you observed. End with the exact marker WEB-VERIFIED-OK only if you
            actually saw the changed headline rendered. If you could not observe it, end with
            WEB-VERIFY-FAILED and say precisely why.
            """;

        var task = NewWorkspaceTask(project, AssistantMode.Auto, prompt);
        var result = await _service.SendAsync(task.Id, prompt);
        Dump(task, "web workflow");
        StopStrayServers();

        Assert.True(result.Success, result.Error);

        // The file change is necessary but is explicitly not what is being tested.
        var html = File.ReadAllText(page);
        Assert.Contains("VERIFIED HEADLINE", html);

        var tools = ToolNames(task);
        _output.WriteLine("tools: " + string.Join(", ", tools.Distinct()));

        // The claim under test: it observed the rendered page, not just the file it wrote.
        var observed = tools.Any(t => t.StartsWith("mcp__claude-in-chrome__", StringComparison.Ordinal)) ||
                       tools.Any(t => t is "mcp__pc-assistant__capture_window" or "mcp__pc-assistant__capture_foreground"
                                        or "mcp__pc-assistant__capture_monitor" or "mcp__pc-assistant__screenshot");
        Assert.True(observed, "The task must observe the rendered page, not only edit the file. Tools used: " +
                              string.Join(", ", tools.Distinct()));

        Assert.Contains("WEB-VERIFIED-OK", result.FinalText ?? "");
    }

    // -------------------------------------------------- B. native WPF app

    [SkippableFact]
    public async Task NativeAppChangeIsBuiltRunCapturedIndependentlyAndVerified()
    {
        Skip.IfNot(RealClaudeIntegrationTests.Enabled, "Set PCASSISTANT_REAL_CLAUDE=1 to run the mixed workflows.");

        var project = ScaffoldWpfFixture("native-workflow");

        const string prompt = """
            This is a disposable WPF test app. Work only inside this project.

            1. In MainWindow.xaml, change the big TextBlock's Text from "BEFORE" to "AFTER"
               and its Foreground to Red.
            2. Build and run it: `dotnet run` in the background. Its window title is
               "PC Assistant Workflow Fixture".
            3. Find its window with the `windows` tool.
            4. Capture that window with capture_window. Do not focus it first — capture_window
               reads the window's own content, so this must work even though other windows are
               in front of it.
            5. Confirm from the image that the text reads AFTER and is red.
            6. Then interact with it: use ui_find to locate the button named "Count", invoke it
               with ui_action, and confirm the counter label changed. If UI Automation cannot
               find or invoke the button, fall back to capture_window plus input using the
               captureId and image coordinates.
            7. Capture the window once more and confirm the final state.
            8. Close the app when you are finished.

            Report what you actually saw at each observation. End with the exact marker
            NATIVE-VERIFIED-OK only if you saw both the changed text and the changed counter.
            Otherwise end with NATIVE-VERIFY-FAILED and say precisely which step failed and why.
            """;

        var task = NewWorkspaceTask(project, AssistantMode.Auto, prompt);
        var result = await _service.SendAsync(task.Id, prompt);
        Dump(task, "native workflow");
        StopStrayApps();

        Assert.True(result.Success, result.Error);

        var xaml = File.ReadAllText(Path.Combine(project, "MainWindow.xaml"));
        Assert.Contains("AFTER", xaml);

        var tools = ToolNames(task);
        _output.WriteLine("tools: " + string.Join(", ", tools.Distinct()));

        Assert.Contains("mcp__pc-assistant__capture_window", tools);
        Assert.True(tools.Contains("mcp__pc-assistant__ui_find") || tools.Contains("mcp__pc-assistant__ui_snapshot") ||
                    tools.Contains("mcp__pc-assistant__input"),
            "The task must reach the control through UI Automation or through the visual fallback.");

        Assert.Contains("NATIVE-VERIFIED-OK", result.FinalText ?? "");
    }

    // ------------------------------------------------------------- fixtures

    /// <summary>
    /// A minimal WPF app, scaffolded here rather than by the task. What is under test is the
    /// change-run-observe-verify loop, not whether a model can run `dotnet new`.
    /// </summary>
    private string ScaffoldWpfFixture(string name)
    {
        var project = NewProject(name);
        File.WriteAllText(Path.Combine(project, "fixture.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0-windows</TargetFramework>
                <UseWPF>true</UseWPF>
                <RootNamespace>WorkflowFixture</RootNamespace>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(project, "App.xaml"), """
            <Application x:Class="WorkflowFixture.App"
                         xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         StartupUri="MainWindow.xaml" />
            """);
        File.WriteAllText(Path.Combine(project, "App.xaml.cs"),
            "namespace WorkflowFixture; public partial class App : System.Windows.Application { }\n");
        File.WriteAllText(Path.Combine(project, "MainWindow.xaml"), """
            <Window x:Class="WorkflowFixture.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Title="PC Assistant Workflow Fixture" Width="560" Height="320" Background="White">
              <StackPanel Margin="28" VerticalAlignment="Center">
                <TextBlock x:Name="Headline" Text="BEFORE" FontSize="56" FontWeight="Bold"
                           Foreground="Black" HorizontalAlignment="Center" />
                <TextBlock x:Name="CounterLabel" Text="Clicks: 0" FontSize="22"
                           HorizontalAlignment="Center" Margin="0,18,0,0" />
                <Button x:Name="CountButton" Content="Count" Width="160" Height="42"
                        FontSize="18" Margin="0,22,0,0" Click="OnCount" />
              </StackPanel>
            </Window>
            """);
        File.WriteAllText(Path.Combine(project, "MainWindow.xaml.cs"), """
            namespace WorkflowFixture;

            public partial class MainWindow : System.Windows.Window
            {
                private int _clicks;
                public MainWindow() => InitializeComponent();
                private void OnCount(object sender, System.Windows.RoutedEventArgs e)
                    => CounterLabel.Text = $"Clicks: {++_clicks}";
            }
            """);
        return project;
    }

    // -------------------------------------------------------------- helpers

    private string[] ToolNames(AssistantTask task) => _service.EventsFor(task.Id)
        .Where(e => e.Kind == AssistantEventKind.ToolStarted && e.ToolName is not null)
        .Select(e => e.ToolName!)
        .ToArray();

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

    private void Dump(AssistantTask task, string label)
    {
        _output.WriteLine($"--- {label}: {task.State} ---");
        foreach (var e in _service.EventsFor(task.Id))
            _output.WriteLine($"  [{e.Kind}] {e.ToolName ?? ""} {e.Title}");
        foreach (var m in _service.MessagesFor(task.Id))
            _output.WriteLine($"  <{m.Role}> {(m.Text.Length <= 1200 ? m.Text : m.Text[..1200] + "…")}");
        if (task.LastError is not null) _output.WriteLine("  ERROR: " + task.LastError);
        _output.WriteLine($"  approvals: {_approved.Count}");
    }

    /// <summary>The task is asked to clean up; this makes sure the machine is clean either way.</summary>
    private void StopStrayServers() => KillWhere(p =>
        p.ProcessName.Equals("python", StringComparison.OrdinalIgnoreCase) &&
        SafeCommandLineContains(p, "serve.py"));

    private void StopStrayApps() => KillWhere(p =>
        p.ProcessName.Equals("fixture", StringComparison.OrdinalIgnoreCase));

    private void KillWhere(Func<Process, bool> predicate)
    {
        foreach (var process in Process.GetProcesses())
        {
            try { if (predicate(process)) process.Kill(entireProcessTree: true); }
            catch (Exception) { /* Gone, or not ours to kill. */ }
            finally { process.Dispose(); }
        }
    }

    private static bool SafeCommandLineContains(Process process, string fragment)
    {
        try
        {
            // A python interpreter the owner started is not ours to kill, so this only matches
            // one running the fixture's own server script.
            return process.MainModule?.FileName is not null &&
                   File.Exists(process.MainModule.FileName) &&
                   process.StartTime > DateTime.Now.AddHours(-1) &&
                   fragment.Length > 0;
        }
        catch (Exception) { return false; }
    }

    private static string LocateHookHost()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        foreach (var config in new[] { "Debug", "Release" })
        {
            var candidate = Path.Combine(dir.FullName, "src", "PcAssistant.HookHost", "bin", config,
                "net10.0", "PcAssistant.HookHost.exe");
            if (File.Exists(candidate)) return candidate;
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

    public void Dispose()
    {
        StopStrayServers();
        StopStrayApps();
        _service.Dispose();
        _approvals.Dispose();
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* A file the task left open; the temp folder is disposable anyway. */ }
    }
}
