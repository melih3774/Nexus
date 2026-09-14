using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PcAssistant.HookHost;

/// <summary>
/// The executable Claude Code runs for each configured hook event.
///
/// It reads the hook payload from stdin, asks PC Assistant over a per-task named pipe,
/// and prints the permission decision on stdout. It holds no policy of its own: PC
/// Assistant decides, this process only carries the question and the answer.
///
/// Kept dependency-free so it starts fast — it runs once per tool call.
/// </summary>
internal static class Program
{
    private const string PipeEnvironmentVariable = "PCASSISTANT_HOOK_PIPE";
    private const int ConnectTimeoutMs = 10_000;
    private const int ReplyTimeoutMs = 30 * 60 * 1000; // an owner may take a while to answer

    private static async Task<int> Main(string[] args)
    {
        var eventName = args.Length > 0 ? args[0] : "PreToolUse";
        var isGate = string.Equals(eventName, "PreToolUse", StringComparison.OrdinalIgnoreCase);

        string payload;
        try
        {
            payload = await Console.In.ReadToEndAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Respond(isGate, "deny", "PC Assistant could not read the hook payload.");
        }

        var pipeName = Environment.GetEnvironmentVariable(PipeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            // Not launched by PC Assistant. Stay out of the way rather than blocking.
            return Respond(isGate, "allow", "");
        }

        JsonNode? hook = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(payload)) hook = JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
            hook = null; // A malformed payload still gets forwarded, just without fields.
        }

        var request = new JsonObject
        {
            ["hookEventName"] = Str(hook, "hook_event_name") ?? eventName,
            ["toolName"] = Str(hook, "tool_name") ?? "",
            ["cwd"] = Str(hook, "cwd"),
            ["sessionId"] = Str(hook, "session_id"),
            ["toolInput"] = Clone(hook, "tool_input"),
            ["toolResponse"] = Clone(hook, "tool_response"),
        };

        try
        {
            var reply = await AskPcAssistantAsync(pipeName, request.ToJsonString()).ConfigureAwait(false);
            var decision = reply?["decision"]?.GetValue<string>() ?? "deny";
            var reason = reply?["reason"]?.GetValue<string>() ?? "";
            return Respond(isGate, decision, reason);
        }
        catch (TimeoutException)
        {
            return Respond(isGate, "deny", "PC Assistant did not answer in time, so this was not run.");
        }
        catch (Exception ex)
        {
            // Fail closed for the gate: an unreachable approval broker must not become
            // an implicit allow. Non-gate events are observational, so they pass through.
            return Respond(isGate, isGate ? "deny" : "allow",
                "PC Assistant could not be reached (" + ex.GetType().Name + ").");
        }
    }

    private static async Task<JsonNode?> AskPcAssistantAsync(string pipeName, string requestJson)
    {
        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await client.ConnectAsync(ConnectTimeoutMs).ConfigureAwait(false);

        var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        await writer.WriteLineAsync(requestJson).ConfigureAwait(false);

        using var timeout = new CancellationTokenSource(ReplyTimeoutMs);
        string? line;
        try
        {
            line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("No reply from PC Assistant.");
        }

        return string.IsNullOrWhiteSpace(line) ? null : JsonNode.Parse(line);
    }

    /// <summary>
    /// Emits the PreToolUse decision.
    ///
    /// A denial is reported two ways on purpose: the structured
    /// <c>hookSpecificOutput.permissionDecision</c> and exit code 2, which Claude Code
    /// also treats as a block. Both say the same thing, so whichever the installed
    /// version honours, the operation is stopped.
    /// </summary>
    private static int Respond(bool isGate, string decision, string reason)
    {
        if (!isGate)
        {
            // Observation-only events produce no decision.
            return 0;
        }

        decision = decision.ToLowerInvariant() switch
        {
            "allow" => "allow",
            "deny" => "deny",
            _ => "ask",
        };

        var output = new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = decision,
                ["permissionDecisionReason"] = reason,
            },
        };

        Console.Out.Write(output.ToJsonString());
        Console.Out.Flush();

        if (decision == "deny")
        {
            Console.Error.Write(string.IsNullOrWhiteSpace(reason)
                ? "Denied by PC Assistant."
                : reason);
            Console.Error.Flush();
            return 2;
        }

        return 0;
    }

    private static string? Str(JsonNode? node, string name)
    {
        try { return node?[name]?.GetValue<string>(); }
        catch (Exception) { return null; }
    }

    private static JsonNode? Clone(JsonNode? node, string name)
    {
        try { return node?[name]?.DeepClone(); }
        catch (Exception) { return null; }
    }
}
