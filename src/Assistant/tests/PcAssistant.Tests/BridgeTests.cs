using System.Diagnostics;
using System.Text.Json;
using PcAssistant.ClaudeCode;
using PcAssistant.Core.Model;
using Xunit;

namespace PcAssistant.Tests;

public class StreamEventNormalizerTests
{
    private static List<AssistantEvent> Feed(StreamEventNormalizer n, params string[] lines)
        => lines.SelectMany(n.Consume).ToList();

    [Fact]
    public void InitLineStartsTheSessionAndCapturesTheId()
    {
        var n = new StreamEventNormalizer(Guid.NewGuid());

        var events = Feed(n,
            """{"type":"system","subtype":"init","session_id":"abc-123","model":"claude-sonnet-5"}""");

        Assert.Equal("abc-123", n.Turn.SessionId);
        Assert.Contains(events, e => e.Kind == AssistantEventKind.Progress);
    }

    [Fact]
    public void AssistantTextBecomesAnAssistantMessage()
    {
        var n = new StreamEventNormalizer(Guid.NewGuid());

        var events = Feed(n,
            """{"type":"assistant","message":{"content":[{"type":"text","text":"I found the cause."}]}}""");

        var message = Assert.Single(events);
        Assert.Equal(AssistantEventKind.AssistantMessage, message.Kind);
        Assert.Equal("I found the cause.", message.Detail);
        Assert.Equal("I found the cause.", n.AssistantTexts.Single());
    }

    [Fact]
    public void ToolUseAndResultPairIntoStartedAndCompleted()
    {
        var n = new StreamEventNormalizer(Guid.NewGuid());

        var events = Feed(n,
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"notes.txt"}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"alpha"}]}}""");

        Assert.Equal(AssistantEventKind.ToolStarted, events[0].Kind);
        Assert.Equal("Read", events[0].ToolName);
        Assert.Contains("notes.txt", events[0].Title);

        Assert.Equal(AssistantEventKind.ToolCompleted, events[1].Kind);
        Assert.Equal("Read", events[1].ToolName);
    }

    [Fact]
    public void FailedToolResultBecomesToolFailed()
    {
        var n = new StreamEventNormalizer(Guid.NewGuid());

        var events = Feed(n,
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t9","name":"Bash","input":{"command":"dotnet test"}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t9","is_error":true,"content":"1 test failed"}]}}""");

        var failure = events.Single(e => e.Kind == AssistantEventKind.ToolFailed);
        Assert.Equal("Bash", failure.ToolName);
        Assert.Contains("1 test failed", failure.Detail);
    }

    [Fact]
    public void ResultLineCarriesTheFinalText()
    {
        var n = new StreamEventNormalizer(Guid.NewGuid());

        Feed(n, """{"type":"result","subtype":"success","is_error":false,"result":"All done."}""");

        Assert.Equal("All done.", n.Turn.FinalText);
        Assert.NotEqual(true, n.Turn.IsError);
    }

    [Fact]
    public void ErrorResultIsRecognised()
    {
        var n = new StreamEventNormalizer(Guid.NewGuid());

        Feed(n, """{"type":"result","subtype":"success","is_error":true,"result":"Failed to authenticate"}""");

        Assert.True(n.Turn.IsError);
        Assert.Equal("Failed to authenticate", n.Turn.ErrorMessage);
    }

    [Fact]
    public void SyntheticAuthErrorOnAnAssistantMessageIsRecognised()
    {
        var n = new StreamEventNormalizer(Guid.NewGuid());

        Feed(n,
            """{"type":"assistant","error":"authentication_failed","message":{"content":[{"type":"text","text":"Failed to authenticate: OAuth session expired"}]}}""");

        Assert.True(n.Turn.IsError);
        Assert.Equal("authentication_failed", n.Turn.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ broken json")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("""{"type":"assistant"}""")]
    [InlineData("""{"type":"assistant","message":{"content":"not-an-array"}}""")]
    [InlineData("""{"type":"user","message":{"content":[{"type":"tool_result"}]}}""")]
    [InlineData("""{"type":"totally_unknown","payload":{}}""")]
    public void MalformedOutputNeverThrows(string line)
    {
        var n = new StreamEventNormalizer(Guid.NewGuid());
        var ex = Record.Exception(() => n.Consume(line).ToList());
        Assert.Null(ex);
    }

    [Fact]
    public void AuthFailureTextIsDetected()
    {
        Assert.True(ClaudeCodeBridge.LooksLikeAuthFailure("Failed to authenticate: OAuth session expired"));
        Assert.False(ClaudeCodeBridge.LooksLikeAuthFailure("Build failed: 3 errors"));
    }

    [Fact]
    public void SignedInButRefusedByTheAccountIsNotReadyAndSaysWhy()
    {
        // The exact text Claude Code 2.1.270 returned, with a 403, while `claude auth status` said logged in.
        const string refusal = "Your organization has disabled Claude subscription access for Claude Code · Use an Anthropic API key instead, or ask your admin to enable access";

        Assert.True(ClaudeCodeBridge.LooksLikeAuthFailure(refusal));
        Assert.Contains("subscription access", ClaudeCodeBridge.DescribeAuthFailure(refusal), StringComparison.Ordinal);
        Assert.DoesNotContain("claude auth login", ClaudeCodeBridge.DescribeAuthFailure(refusal), StringComparison.Ordinal);
        Assert.Contains("claude auth login", ClaudeCodeBridge.DescribeAuthFailure("OAuth token expired"), StringComparison.Ordinal);
    }
}

public class HookProtocolTests
{
    [Fact]
    public void RequestRoundTripsThroughJson()
    {
        const string wire = """
            {"hookEventName":"PreToolUse","toolName":"Bash",
             "toolInput":{"command":"dotnet build"},"cwd":"C:\\projects\\demo","sessionId":"s1"}
            """;

        var request = JsonSerializer.Deserialize<HookRequest>(wire, HookProtocol.Json)!;

        Assert.Equal("PreToolUse", request.HookEventName);
        Assert.Equal("Bash", request.ToolName);
        Assert.Equal("dotnet build", request.ToolInput.GetProperty("command").GetString());
        Assert.Equal(@"C:\projects\demo", request.Cwd);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"hookEventName":"PreToolUse"}""")]
    [InlineData("""{"toolName":null,"toolInput":null}""")]
    public void PartialPayloadsDeserializeWithoutThrowing(string wire)
    {
        var ex = Record.Exception(() => JsonSerializer.Deserialize<HookRequest>(wire, HookProtocol.Json));
        Assert.Null(ex);
    }

    [Fact]
    public void ReplySerializesTheDecision()
    {
        var json = JsonSerializer.Serialize(HookReply.Deny("nope"), HookProtocol.Json);

        Assert.Contains("\"decision\":\"deny\"", json);
        Assert.Contains("nope", json);
    }

    [Fact]
    public void PipeNamesAreStablePerTaskAndDistinctAcrossTasks()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Assert.Equal(HookProtocol.PipeNameFor(a), HookProtocol.PipeNameFor(a));
        Assert.NotEqual(HookProtocol.PipeNameFor(a), HookProtocol.PipeNameFor(b));
    }
}

public class ApprovalPipeServerTests
{
    [Fact]
    public async Task ServerAnswersAHookClient()
    {
        var pipeName = "pcassistant-test-" + Guid.NewGuid().ToString("N");

        await using var server = new ApprovalPipeServer(
            pipeName,
            (request, _) => Task.FromResult(
                request.ToolName == "Bash" ? HookReply.Deny("blocked") : HookReply.Allow()));

        server.Start();

        var reply = await HookClientHelper.AskAsync(pipeName,
            """{"hookEventName":"PreToolUse","toolName":"Bash","toolInput":{"command":"rm -rf /"}}""");

        Assert.Contains("deny", reply);
        Assert.Contains("blocked", reply);
    }

    [Fact]
    public async Task AHandlerThatThrowsDoesNotKillTheServer()
    {
        var pipeName = "pcassistant-test-" + Guid.NewGuid().ToString("N");
        var calls = 0;

        await using var server = new ApprovalPipeServer(pipeName, (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("boom");
            return Task.FromResult(HookReply.Allow("second call fine"));
        });

        server.Start();

        var first = await HookClientHelper.AskAsync(pipeName, """{"toolName":"Edit"}""");
        Assert.Contains("deny", first);  // fails closed, while the server remains healthy

        var second = await HookClientHelper.AskAsync(pipeName, """{"toolName":"Edit"}""");
        Assert.Contains("second call fine", second);
    }

    [Fact]
    public async Task MalformedHookPayloadIsHandled()
    {
        var pipeName = "pcassistant-test-" + Guid.NewGuid().ToString("N");

        await using var server = new ApprovalPipeServer(pipeName, (_, _) => Task.FromResult(HookReply.Allow("ok")));
        server.Start();

        var reply = await HookClientHelper.AskAsync(pipeName, "{ this is not json");
        Assert.Contains("deny", reply);
    }
}

internal static class HookClientHelper
{
    public static async Task<string> AskAsync(string pipeName, string payload)
    {
        await using var client = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);

        await client.ConnectAsync(5000);

        var writer = new StreamWriter(client) { AutoFlush = true };
        var reader = new StreamReader(client);

        await writer.WriteLineAsync(payload);
        return await reader.ReadLineAsync() ?? string.Empty;
    }
}

public class ProcessJobTests
{
    [Fact]
    public void TerminatingTheJobKillsOnlyItsOwnProcessTree()
    {
        using var outsider = StartSleeper();
        try
        {
            int insiderId;
            using (var job = new ProcessJob("test-job"))
            {
                using var insider = StartSleeper();
                Assert.True(job.Assign(insider));
                insiderId = insider.Id;

                job.TerminateAll();
                Assert.True(insider.WaitForExit(10_000), "the owned process should have been terminated");
            }

            Assert.False(outsider.HasExited, "a process outside the job must not be touched");
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(insiderId));
        }
        finally
        {
            try { outsider.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void DisposingTheJobCleansUpSurvivingProcesses()
    {
        Process insider;
        using (var job = new ProcessJob("test-job-dispose"))
        {
            insider = StartSleeper();
            Assert.True(job.Assign(insider));
        }

        // KILL_ON_JOB_CLOSE means no orphan is left behind.
        Assert.True(insider.WaitForExit(10_000), "closing the job should have cleaned up the process");
        insider.Dispose();
    }

    private static Process StartSleeper()
    {
        var p = new Process
        {
            StartInfo =
            {
                FileName = "cmd.exe",
                Arguments = "/c ping -n 60 127.0.0.1 > nul",
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        p.Start();
        return p;
    }
}

public class ClaudeCodeLocatorTests
{
    [Fact]
    public void AnExplicitOverrideThatExistsIsUsed()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(temp, "");

        try
        {
            var install = ClaudeCodeLocator.Locate(temp);
            Assert.NotNull(install);
            Assert.Equal(temp, install!.ExecutablePath);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void AnOverrideThatDoesNotExistFindsNothing()
        => Assert.Null(ClaudeCodeLocator.Locate(@"C:\definitely\not\here\claude.exe"));
}
