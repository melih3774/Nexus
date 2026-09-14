using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcAssistant.ClaudeCode;

/// <summary>
/// Wire contract between the hook helper that Claude Code executes and PC Assistant.
/// One JSON line in each direction over a per-task named pipe.
/// Kept intentionally tiny so the helper process starts fast.
/// </summary>
public sealed class HookRequest
{
    [JsonPropertyName("hookEventName")] public string HookEventName { get; set; } = "";
    [JsonPropertyName("toolName")] public string ToolName { get; set; } = "";
    [JsonPropertyName("toolInput")] public JsonElement ToolInput { get; set; }
    [JsonPropertyName("toolResponse")] public JsonElement ToolResponse { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("success")] public bool? Success { get; set; }

    /// <summary>PC tools server process, so an approval can hand it the foreground privilege. 0 when absent.</summary>
    [JsonPropertyName("processId")] public int ProcessId { get; set; }

    /// <summary>
    /// A privileged operation request, serialized as PcAssistant.Elevation's PrivilegedRequest.
    /// Only ever populated for the <see cref="HookProtocol.ElevatedEvent"/> event: an
    /// administrator operation is decided and performed by PC Assistant itself, so the PC
    /// tools process asks over this pipe rather than talking to the privileged helper.
    /// </summary>
    [JsonPropertyName("payload")] public string? Payload { get; set; }
}

public sealed class HookReply
{
    /// <summary>allow | deny | ask</summary>
    [JsonPropertyName("decision")] public string Decision { get; set; } = "allow";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";

    /// <summary>
    /// The outcome of a privileged operation PC Assistant performed on the caller's behalf.
    /// Present only for <see cref="HookProtocol.ElevatedEvent"/>; a decision alone would not
    /// tell the model what the approved administrator command actually did.
    /// </summary>
    [JsonPropertyName("result")] public string? Result { get; set; }

    public static HookReply Allow(string reason = "") => new() { Decision = "allow", Reason = reason };
    public static HookReply Deny(string reason) => new() { Decision = "deny", Reason = reason };
    public static HookReply Ask(string reason) => new() { Decision = "ask", Reason = reason };
}

public static class HookProtocol
{
    /// <summary>Environment variable naming the pipe for the current task.</summary>
    public const string PipeEnvironmentVariable = "PCASSISTANT_HOOK_PIPE";

    /// <summary>The PC tools process asking PC Assistant to perform a privileged operation.</summary>
    public const string ElevatedEvent = "PcElevatedOperation";

    /// <summary>The PC tools process asking whether the privileged helper is usable. No approval.</summary>
    public const string ElevatedStatusEvent = "PcElevatedStatus";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PipeNameFor(Guid taskId) => "pcassistant-hook-" + taskId.ToString("N");
}
