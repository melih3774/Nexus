using System.Diagnostics;
using System.Text.Json;
using PcAssistant.ClaudeCode;
using PcAssistant.Core.Approvals;
using PcAssistant.Core.Policy;
using Xunit;

namespace PcAssistant.Tests;

/// <summary>
/// Runs the real PcAssistant.HookHost.exe — the binary Claude Code actually executes —
/// against a live approval pipe. This proves the whole enforcement path except Claude
/// itself, and needs no Claude authentication.
/// </summary>
public class HookHostProcessTests
{
    private static string HookHostPath()
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
        throw new InvalidOperationException("PcAssistant.HookHost.exe was not found; build the solution first.");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunHookAsync(
        string payload, string? pipeName, string eventName = "PreToolUse")
    {
        using var process = new Process();
        process.StartInfo.FileName = HookHostPath();
        process.StartInfo.ArgumentList.Add(eventName);
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        if (pipeName is not null)
            process.StartInfo.Environment[HookProtocol.PipeEnvironmentVariable] = pipeName;
        else
            process.StartInfo.Environment.Remove(HookProtocol.PipeEnvironmentVariable);

        process.Start();
        await process.StandardInput.WriteAsync(payload);
        process.StandardInput.Close();

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, stdout, stderr);
    }

    private static string Payload(string tool, string inputJson) => $$"""
        {"session_id":"s1","hook_event_name":"PreToolUse","cwd":"C:\\projects\\demo",
         "tool_name":"{{tool}}","tool_input":{{inputJson}}}
        """;

    private static string Decision(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout);
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString()!;
    }

    [Fact]
    public async Task AllowedOperationReturnsAllowAndExitsZero()
    {
        var pipeName = "pcassistant-hookhost-" + Guid.NewGuid().ToString("N");

        await using var server = new ApprovalPipeServer(pipeName,
            (_, _) => Task.FromResult(HookReply.Allow("AUTO mode allows Read.")));
        server.Start();

        var (exit, stdout, _) = await RunHookAsync(
            Payload("Read", """{"file_path":"C:\\projects\\demo\\notes.txt"}"""), pipeName);

        Assert.Equal(0, exit);
        Assert.Equal("allow", Decision(stdout));
    }

    [Fact]
    public async Task DeniedOperationSignalsBothWays()
    {
        var pipeName = "pcassistant-hookhost-" + Guid.NewGuid().ToString("N");

        await using var server = new ApprovalPipeServer(pipeName,
            (_, _) => Task.FromResult(HookReply.Deny("The owner denied this operation.")));
        server.Start();

        var (exit, stdout, stderr) = await RunHookAsync(
            Payload("Bash", """{"command":"git push origin main"}"""), pipeName);

        // Structured decision and exit code 2 both mean block, so whichever Claude Code
        // honours, the operation stops.
        Assert.Equal("deny", Decision(stdout));
        Assert.Equal(2, exit);
        Assert.Contains("denied", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealPolicyDecisionFlowsThroughTheHelper()
    {
        var pipeName = "pcassistant-hookhost-" + Guid.NewGuid().ToString("N");
        var taskId = Guid.NewGuid();

        using var broker = new ApprovalBroker(TimeSpan.FromSeconds(20));
        broker.ApprovalRequested += (_, r) => broker.Resolve(r.Id, allow: false);

        // The same classify -> policy -> broker chain the bridge uses.
        await using var server = new ApprovalPipeServer(pipeName, async (hook, ct) =>
        {
            var call = ToolInvocation.FromJson(hook.ToolName, hook.ToolInput, hook.Cwd!);
            var assessment = new RiskClassifier().Classify(call);
            var decision = ApprovalPolicy.Evaluate(Core.Model.AssistantMode.Auto, assessment.Category);

            if (decision == PolicyDecision.Allow) return HookReply.Allow();

            var outcome = await broker.RequestAsync(new ApprovalRequest
            {
                TaskId = taskId, ToolName = hook.ToolName, Action = "Claude wants to run",
                Target = assessment.Target, Reason = "test", Category = assessment.Category,
            }, ct);

            return outcome == ApprovalOutcome.Allowed ? HookReply.Allow() : HookReply.Deny("denied by owner");
        });
        server.Start();

        // AUTO allows an ordinary build without asking.
        var safe = await RunHookAsync(Payload("Bash", """{"command":"dotnet build"}"""), pipeName);
        Assert.Equal("allow", Decision(safe.StdOut));
        Assert.Equal(0, safe.ExitCode);

        // AUTO still gates a registry write, and the denial reaches Claude Code.
        var risky = await RunHookAsync(
            Payload("Bash", """{"command":"reg add HKCU\\Software\\Nope /v A /d 1 /f"}"""), pipeName);

        Assert.Equal("deny", Decision(risky.StdOut));
        Assert.Equal(2, risky.ExitCode);
    }

    [Fact]
    public async Task AnUnreachableBrokerFailsClosed()
    {
        // A pipe name nothing is listening on.
        var (exit, stdout, _) = await RunHookAsync(
            Payload("Write", """{"file_path":"C:\\projects\\demo\\x.txt"}"""),
            "pcassistant-nobody-" + Guid.NewGuid().ToString("N"));

        Assert.Equal("deny", Decision(stdout));
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task WithoutPcAssistantInTheEnvironmentTheHelperStaysOutOfTheWay()
    {
        // Someone ran claude themselves and the hook is still configured.
        var (exit, stdout, _) = await RunHookAsync(
            Payload("Read", """{"file_path":"notes.txt"}"""), pipeName: null);

        Assert.Equal(0, exit);
        Assert.Equal("allow", Decision(stdout));
    }

    [Fact]
    public async Task MalformedHookPayloadDoesNotCrashTheHelper()
    {
        var pipeName = "pcassistant-hookhost-" + Guid.NewGuid().ToString("N");

        await using var server = new ApprovalPipeServer(pipeName,
            (_, _) => Task.FromResult(HookReply.Allow("ok")));
        server.Start();

        var (exit, stdout, _) = await RunHookAsync("{ not valid json at all", pipeName);

        Assert.Equal(0, exit);
        Assert.Equal("allow", Decision(stdout));
    }

    [Fact]
    public async Task ObservationalEventsProduceNoDecision()
    {
        var pipeName = "pcassistant-hookhost-" + Guid.NewGuid().ToString("N");

        await using var server = new ApprovalPipeServer(pipeName,
            (_, _) => Task.FromResult(HookReply.Allow()));
        server.Start();

        var (exit, stdout, _) = await RunHookAsync(
            """{"hook_event_name":"PostToolUse","tool_name":"Read","tool_response":{"ok":true}}""",
            pipeName, eventName: "PostToolUse");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void TheSettingsFileWiresTheHookAtTheRightEvents()
    {
        var runtime = Path.Combine(Path.GetTempPath(), "pcassistant-runtime", Guid.NewGuid().ToString("N"));
        var files = new ClaudeRuntimeFiles(runtime, HookHostPath());

        var path = files.WriteSettings(Guid.NewGuid(), approvalTimeoutMinutes: 10);
        var json = File.ReadAllText(path);

        using var doc = JsonDocument.Parse(json);
        var hooks = doc.RootElement.GetProperty("hooks");

        foreach (var evt in new[] { "PreToolUse", "PostToolUse", "Stop" })
        {
            var entry = hooks.GetProperty(evt)[0];
            Assert.Equal("*", entry.GetProperty("matcher").GetString());

            var command = entry.GetProperty("hooks")[0].GetProperty("command").GetString()!;
            Assert.Contains("PcAssistant.HookHost.exe", command);
            Assert.EndsWith(evt, command);
        }

        // The helper must outlive the approval window, so the owner decides, not a timeout.
        var preTimeout = hooks.GetProperty("PreToolUse")[0].GetProperty("hooks")[0]
            .GetProperty("timeout").GetInt32();
        Assert.True(preTimeout >= 12 * 60, $"PreToolUse hook timeout was only {preTimeout}s");

        Directory.Delete(runtime, recursive: true);
    }

    [Fact]
    public void TheSkillBehaviourIsFoundAndCarriesItsRules()
    {
        var behaviour = ClaudeRuntimeFiles.LoadSkillBehaviour();

        Assert.False(string.IsNullOrWhiteSpace(behaviour));
        Assert.DoesNotContain("---", behaviour[..Math.Min(10, behaviour.Length)]); // frontmatter stripped
        Assert.Contains("PC Assistant", behaviour);
        Assert.Contains("AUTO", behaviour);
        Assert.Contains("ASK", behaviour);
        Assert.Contains("commit", behaviour, StringComparison.OrdinalIgnoreCase);
    }
}
