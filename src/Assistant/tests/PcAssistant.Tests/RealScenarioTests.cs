using PcAssistant.ClaudeCode;
using PcAssistant.Core;
using PcAssistant.Core.Approvals;
using PcAssistant.Core.Bridge;
using PcAssistant.Core.Model;
using PcAssistant.Core.Persistence;
using PcAssistant.Core.Tasks;
using PcAssistant.Core.Workspaces;
using PcAssistant.Windows;
using Xunit;
using Xunit.Abstractions;

namespace PcAssistant.Tests;

/// <summary>
/// The things an owner actually says out loud, asked of the real assistant on the real PC.
///
/// These are not unit tests of the tools — those exist elsewhere. They check the answers are
/// about <em>this machine</em>: that "what is using the most RAM" names real processes, that
/// "set the volume to 30%" moves the real endpoint and puts it back, and that a window covered
/// by another window can still be described correctly. A mock cannot be wrong about any of
/// that, which is precisely why it would prove nothing.
///
/// Opt-in. Whatever the task does, the fixture restores the volume itself.
/// </summary>
[Trait("Category", "RealClaude")]
[Collection(DesktopCollection.Name)]
public sealed class RealScenarioTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly TaskStore _store;
    private readonly ApprovalBroker _approvals;
    private readonly AssistantService _service;
    private readonly List<ApprovalRequest> _approved = [];

    public RealScenarioTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "pcassistant-scenario", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _paths = new AppPaths(Path.Combine(_root, "app"));
        _settings = AppSettings.Load(_paths.SettingsFile);
        _settings.ApprovalTimeoutMinutes = 5;

        _store = new TaskStore(_paths.DatabaseFile);
        _approvals = new ApprovalBroker(TimeSpan.FromMinutes(5));
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
            _store, bridge, new WorkspaceService(() => new List<string>()), _paths, _settings, _approvals);
    }

    [SkippableFact]
    public async Task ItAnswersEverydayQuestionsAboutThisMachineFromRealState()
    {
        Skip.IfNot(RealClaudeIntegrationTests.Enabled, "Set PCASSISTANT_REAL_CLAUDE=1 to run the real scenarios.");

        const string prompt = """
            Answer these about this PC, using the PC Assistant tools rather than PowerShell:

            1. What windows are open right now? Name three of them.
            2. Which process is using the most RAM? Give its name and working set.
            3. Am I on battery or mains, and what is the battery percentage?
            4. Am I online, and on what kind of adapter?
            5. What is the master volume right now, and what is the default playback device?

            Be brief — a line each. End with the exact marker SCENARIOS-OK.
            """;

        var task = _service.CreateTask(new TaskCreationRequest
        {
            Kind = TaskKind.General, Mode = AssistantMode.Auto, FirstMessage = prompt,
        });
        var result = await _service.SendAsync(task.Id, prompt);
        Dump(task, "everyday questions");

        Assert.True(result.Success, result.Error);
        Assert.Contains("SCENARIOS-OK", result.FinalText ?? "");

        var tools = ToolNames(task);
        _output.WriteLine("tools: " + string.Join(", ", tools.Distinct()));
        foreach (var expected in new[] { "windows", "processes", "power_status", "network_status", "audio_status" })
            Assert.Contains("mcp__pc-assistant__" + expected, tools);

        // Observation costs the owner nothing.
        Assert.Empty(_approved);
    }

    [SkippableFact]
    public async Task ItSetsAndRestoresTheVolumeAndOpensARealSettingsPage()
    {
        Skip.IfNot(RealClaudeIntegrationTests.Enabled, "Set PCASSISTANT_REAL_CLAUDE=1 to run the real scenarios.");

        using var audio = new AudioService();
        var original = await audio.GetAsync();
        _output.WriteLine($"volume before: {original.VolumePercent}% on '{original.DeviceName}'");

        try
        {
            var prompt = $"""
                Two things on this PC, using the PC Assistant tools:

                1. Note the current master volume, set it to 30%, confirm what the device
                   reports back, then restore it to exactly {original.VolumePercent}% and
                   confirm that too.
                2. Open the Windows Bluetooth settings page, verify a window actually appeared,
                   report its title, then close it again.

                Report the volume readings and the window title. End with the exact marker
                VOLUME-AND-SETTINGS-OK.
                """;

            var task = _service.CreateTask(new TaskCreationRequest
            {
                Kind = TaskKind.General, Mode = AssistantMode.Auto, FirstMessage = prompt,
            });
            var result = await _service.SendAsync(task.Id, prompt);
            Dump(task, "volume and settings");

            Assert.True(result.Success, result.Error);
            Assert.Contains("VOLUME-AND-SETTINGS-OK", result.FinalText ?? "");

            var tools = ToolNames(task);
            _output.WriteLine("tools: " + string.Join(", ", tools.Distinct()));
            Assert.Contains("mcp__pc-assistant__audio_set", tools);
            Assert.Contains("mcp__pc-assistant__open_settings", tools);

            // The real endpoint, read independently of anything the task said about it.
            var after = await audio.GetAsync();
            _output.WriteLine($"volume after: {after.VolumePercent}%");
            Assert.InRange(after.VolumePercent, original.VolumePercent - 2, original.VolumePercent + 2);
        }
        finally
        {
            // The owner's volume is restored whatever the task did or failed to do.
            await audio.SetVolumeAsync(original.VolumePercent);
        }
    }

    [SkippableFact]
    public async Task ItDescribesAWindowThatAnotherWindowIsCovering()
    {
        Skip.IfNot(RealClaudeIntegrationTests.Enabled, "Set PCASSISTANT_REAL_CLAUDE=1 to run the real scenarios.");

        // Character Map is a real application, is not the owner's work, and closes cleanly.
        using var launcher = new AppLauncher();
        var opened = await launcher.OpenAsync("Character Map");
        Assert.True(opened.WindowAppeared, "Character Map should have opened: " + opened.Note);
        var target = opened.Hwnd!.Value;

        var desktop = new DesktopWindows();
        try
        {
            // Put it somewhere known, then genuinely cover it: maximize another window and give
            // it the foreground. Maximizing alone does not always take activation, and a fixture
            // that never covers the target would leave this test asserting nothing.
            await desktop.ChangeWindowAsync(target, WindowAction.MoveResize, new PixelRect(200, 200, 700, 520));
            var coverer = desktop.ListWindows(60)
                .FirstOrDefault(w => w.Hwnd != target && !w.IsMinimized && w.Bounds.Width > 900 &&
                                     !ProtectedWindows.IsProtectedWindow(w.Hwnd));
            Skip.If(coverer is null, "No window large enough to cover the target was open.");
            await desktop.ChangeWindowAsync(coverer!.Hwnd, WindowAction.Maximize);
            await desktop.ChangeWindowAsync(coverer.Hwnd, WindowAction.Focus);
            await Task.Delay(600);

            // Verified rather than assumed: if the target is not actually covered, this test
            // proves nothing about occlusion and should say so instead of passing.
            var registry = new PcAssistant.Windows.Capture.CaptureRegistry();
            var before = new PcAssistant.Windows.Capture.WindowCapture(desktop, registry).CaptureWindow(target);
            Assert.True(before.Occluded, "The fixture failed to cover the target window, so this run tests nothing.");

            // The claim is that observing changes nothing, so the desktop's own state before the
            // task is the baseline — not an assumption about which window happens to be in front.
            var foregroundBefore = desktop.ForegroundWindow()?.Hwnd;
            _output.WriteLine($"target={target} coverer={coverer.Hwnd} foreground={foregroundBefore}");

            var prompt = $"""
                Window {target} is a Character Map window that is currently completely covered by
                another window. Without focusing, raising, moving or restoring it, capture that
                window and tell me what is in it — the window title and two things you can see in
                its interface.

                Do not bring it to the front. If you genuinely cannot observe it, say so instead
                of guessing. End with the exact marker OCCLUDED-OK if you saw its contents, or
                OCCLUDED-FAILED with the reason if you did not.
                """;

            var task = _service.CreateTask(new TaskCreationRequest
            {
                Kind = TaskKind.General, Mode = AssistantMode.Auto, FirstMessage = prompt,
            });
            var result = await _service.SendAsync(task.Id, prompt);
            Dump(task, "occluded window");

            Assert.True(result.Success, result.Error);
            Assert.Contains("mcp__pc-assistant__capture_window", ToolNames(task));
            Assert.Contains("OCCLUDED-OK", result.FinalText ?? "");

            // Nothing was raised to achieve it: the desktop is where it was.
            var foregroundAfter = desktop.ForegroundWindow()?.Hwnd;
            Assert.Equal(foregroundBefore, foregroundAfter);
            Assert.NotEqual(target, foregroundAfter);

            var stillThere = desktop.ListWindows(80).FirstOrDefault(w => w.Hwnd == target);
            Assert.NotNull(stillThere);
            Assert.False(stillThere!.IsMinimized, "The target must not have been minimized or restored to be observed.");
        }
        finally
        {
            try { await desktop.ChangeWindowAsync(target, WindowAction.Close); }
            catch (Exception) { /* Already closed by the task. */ }
        }
    }

    // -------------------------------------------------------------- helpers

    private string[] ToolNames(AssistantTask task) => _service.EventsFor(task.Id)
        .Where(e => e.Kind == AssistantEventKind.ToolStarted && e.ToolName is not null)
        .Select(e => e.ToolName!)
        .ToArray();

    private void Dump(AssistantTask task, string label)
    {
        _output.WriteLine($"--- {label}: {task.State} ---");
        foreach (var m in _service.MessagesFor(task.Id))
            _output.WriteLine($"  <{m.Role}> {(m.Text.Length <= 1500 ? m.Text : m.Text[..1500] + "…")}");
        if (task.LastError is not null) _output.WriteLine("  ERROR: " + task.LastError);
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
        _service.Dispose();
        _approvals.Dispose();
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* Disposable temp folder. */ }
    }
}
