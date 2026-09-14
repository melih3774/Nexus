using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PcAssistant.PcTools;

/// <summary>Native actions cannot run without a live owner approval broker, even in another MCP client.</summary>
public static class ToolGate
{
    public static async Task AuthorizeAsync(string toolName, object? arguments, CancellationToken cancellationToken)
    {
        var pipeName = Environment.GetEnvironmentVariable("PCASSISTANT_HOOK_PIPE");
        if (string.IsNullOrWhiteSpace(pipeName)) throw new InvalidOperationException("Start PC tools from PC Assistant; no approval broker is attached.");
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, cancellationToken);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        // processId lets PC Assistant hand this process the foreground privilege as it approves, which is the
        // only moment it can: the owner's click on the approval card is what makes PC Assistant the
        // foreground owner, and only the foreground owner may pass that right on.
        var json = JsonSerializer.Serialize(new { hookEventName = "PcToolUse", toolName = "mcp__pc-assistant__" + toolName,
            toolInput = arguments ?? new { }, cwd = Environment.CurrentDirectory, processId = Environment.ProcessId });
        if (json.Length > 128_000) throw new ArgumentException("Tool arguments exceed the 128 KB limit.");
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        var reply = await reader.ReadLineAsync(timeout.Token);
        using var doc = JsonDocument.Parse(reply ?? "{}");
        if (!doc.RootElement.TryGetProperty("decision", out var decision) || decision.GetString() != "allow")
            throw new InvalidOperationException("Action denied by PC Assistant. " +
                (doc.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : "No valid approval received."));
        cancellationToken.ThrowIfCancellationRequested();
    }
}
