using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PcAssistant.Elevation;

namespace PcAssistant.PcTools;

/// <summary>
/// How an administrator operation leaves this process: it does not.
///
/// The PC tools server never talks to the privileged helper. It hands the request to PC
/// Assistant over the same per-task approval pipe every other gated action uses, and PC
/// Assistant — which owns the owner's attention and the trusted privileged client — decides,
/// executes and reports back. That keeps exactly one privileged client on the machine, and
/// keeps this process, the one a model's tool call reaches first, on the unprivileged side
/// of the boundary.
/// </summary>
internal static class ElevationGate
{
    public static Task<string> RequestAsync(PrivilegedRequest request, CancellationToken cancellationToken)
        => ExchangeAsync(HookProtocolNames.ElevatedEvent, request, cancellationToken);

    public static Task<string> StatusAsync(CancellationToken cancellationToken)
        => ExchangeAsync(HookProtocolNames.ElevatedStatusEvent, null, cancellationToken);

    private static async Task<string> ExchangeAsync(
        string eventName, PrivilegedRequest? request, CancellationToken cancellationToken)
    {
        var pipeName = Environment.GetEnvironmentVariable("PCASSISTANT_HOOK_PIPE");
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new InvalidOperationException("Start PC tools from PC Assistant; no approval broker is attached.");

        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, cancellationToken);

        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);

        var envelope = JsonSerializer.Serialize(new
        {
            hookEventName = eventName,
            toolName = "mcp__pc-assistant__" + (request is null ? "elevated_status" : "elevated_request"),
            toolInput = new { },
            cwd = Environment.CurrentDirectory,
            processId = Environment.ProcessId,
            payload = request is null ? null : JsonSerializer.Serialize(request, PrivilegedProtocol.Json),
        });

        if (envelope.Length > 128_000) throw new ArgumentException("The administrator request exceeds the 128 KB limit.");

        await writer.WriteLineAsync(envelope.AsMemory(), cancellationToken);

        var reply = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(reply))
            throw new InvalidOperationException("PC Assistant closed the connection without answering the administrator request.");

        using var document = JsonDocument.Parse(reply);
        if (document.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
            return result.GetString()!;

        var reason = document.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
        throw new InvalidOperationException(reason ?? "PC Assistant refused the administrator request.");
    }
}

/// <summary>
/// The two hook event names, duplicated here rather than referenced, so the PC tools process
/// keeps its single dependency on the shared protocol library and not on the bridge.
/// </summary>
internal static class HookProtocolNames
{
    public const string ElevatedEvent = "PcElevatedOperation";
    public const string ElevatedStatusEvent = "PcElevatedStatus";
}
