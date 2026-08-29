using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcCore.Protocol;

/// <summary>
/// A structured, machine-readable failure. Never carries stack traces, file contents,
/// or secrets — see SPEC.md 4.3.
/// </summary>
/// <param name="Code">One of <see cref="ErrorCodes"/>.</param>
/// <param name="Message">Human-readable explanation. Not stable; do not parse.</param>
/// <param name="Details">Optional safe extra context (for example, which parameter was invalid).</param>
public sealed record ProtocolError(
    string Code,
    string Message,
    string? Details = null);

/// <summary>
/// A caller-to-PC-Core request.
/// </summary>
public sealed record RequestEnvelope
{
    /// <summary>Protocol version. Absent on the wire means <see cref="ProtocolConstants.CurrentVersion"/>.</summary>
    public int ProtocolVersion { get; init; } = ProtocolConstants.CurrentVersion;

    /// <summary>Caller-chosen correlation id, echoed on the response.</summary>
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = MessageTypes.Request;

    /// <summary>Capability action name, for example <c>files.list</c>.</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>
    /// Action parameters, still in wire form.
    /// <para>
    /// This is the single deliberate place where JSON reaches beyond the codec: the envelope
    /// must be decoded before we know which action — and therefore which parameter type — it
    /// targets. <c>CapabilityDispatcher</c> binds this to the action's declared parameter record
    /// immediately, so no capability ever sees a <see cref="JsonElement"/>.
    /// </para>
    /// </summary>
    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }
}

/// <summary>
/// PC Core's reply to exactly one <see cref="RequestEnvelope"/>.
/// </summary>
public sealed record ResponseEnvelope
{
    public int ProtocolVersion { get; init; } = ProtocolConstants.CurrentVersion;

    /// <summary>The id of the request being answered.</summary>
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = MessageTypes.Response;

    /// <summary>The action that was requested, echoed for clients that log responses alone.</summary>
    public string? Action { get; init; }

    public bool Success { get; init; }

    /// <summary>Action-specific payload on success; <see langword="null"/> on failure.</summary>
    public object? Result { get; init; }

    /// <summary>Structured failure on error; <see langword="null"/> on success.</summary>
    public ProtocolError? Error { get; init; }

    /// <summary>Server-side handling time, useful for diagnosing slow capabilities.</summary>
    public long DurationMs { get; init; }

    public static ResponseEnvelope Ok(string id, string? action, object? result, long durationMs = 0) =>
        new() { Id = id, Action = action, Success = true, Result = result, DurationMs = durationMs };

    public static ResponseEnvelope Fail(string id, string? action, ProtocolError error, long durationMs = 0) =>
        new() { Id = id, Action = action, Success = false, Error = error, DurationMs = durationMs };

    public static ResponseEnvelope Fail(string id, string? action, string code, string message, string? details = null) =>
        Fail(id, action, new ProtocolError(code, message, details));
}

/// <summary>
/// An unsolicited server-to-caller message. v0.1 emits <c>core.connected</c> only; the
/// future <c>ai.*</c> progress events described in SPEC.md 11 use this same shape.
/// </summary>
public sealed record EventEnvelope
{
    public int ProtocolVersion { get; init; } = ProtocolConstants.CurrentVersion;

    /// <summary>Unique id for this event occurrence.</summary>
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = MessageTypes.Event;

    /// <summary>Dotted event name, for example <c>core.connected</c>.</summary>
    [JsonPropertyName("event")]
    public string EventName { get; init; } = string.Empty;

    public object? Payload { get; init; }

    public DateTimeOffset TimestampUtc { get; init; }
}
