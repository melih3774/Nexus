using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcCore.Protocol;

/// <summary>
/// Outcome of decoding a wire message: either an envelope, or a structured reason it was rejected.
/// </summary>
public readonly record struct DecodeResult
{
    private DecodeResult(RequestEnvelope? request, ProtocolError? error, string? id)
    {
        Request = request;
        Error = error;
        Id = id;
    }

    public RequestEnvelope? Request { get; }

    public ProtocolError? Error { get; }

    /// <summary>
    /// The request id when it could be recovered, even from an otherwise-invalid message,
    /// so a failure response can still be correlated by the caller.
    /// </summary>
    public string? Id { get; }

    [MemberNotNullWhen(true, nameof(Request))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Request is not null;

    public static DecodeResult Success(RequestEnvelope request) => new(request, null, request.Id);

    public static DecodeResult Failure(string code, string message, string? id = null, string? details = null) =>
        new(null, new ProtocolError(code, message, details), id);
}

/// <summary>
/// The one and only place PC Core converts between wire text and protocol types.
/// Every transport uses it; nothing below the transport boundary does.
/// </summary>
public static class ProtocolCodec
{
    /// <summary>
    /// Shared serializer settings. <c>snake_case</c> on the wire, tolerant of unknown incoming
    /// fields (forward compatibility), and depth-limited so a hostile payload cannot exhaust
    /// the stack.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 32,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>
    /// Parse an incoming request. Never throws for malformed input — a bad message is a
    /// protocol error, not an exception.
    /// </summary>
    public static DecodeResult DecodeRequest(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return DecodeResult.Failure(ErrorCodes.InvalidRequest, "Request body was empty.");
        }

        RequestEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<RequestEnvelope>(json, Options);
        }
        catch (JsonException ex)
        {
            // The message names the offending position but never echoes the payload back.
            return DecodeResult.Failure(
                ErrorCodes.InvalidRequest,
                "Request body was not valid JSON.",
                details: $"line {ex.LineNumber}, position {ex.BytePositionInLine}");
        }

        if (envelope is null)
        {
            return DecodeResult.Failure(ErrorCodes.InvalidRequest, "Request body decoded to null.");
        }

        // Recoverable id first, so every subsequent rejection can still be correlated.
        var id = string.IsNullOrWhiteSpace(envelope.Id) ? null : envelope.Id;

        if (envelope.ProtocolVersion != ProtocolConstants.CurrentVersion)
        {
            return DecodeResult.Failure(
                ErrorCodes.UnsupportedProtocolVersion,
                $"Protocol version {envelope.ProtocolVersion} is not supported.",
                id,
                $"supported: {ProtocolConstants.CurrentVersion}");
        }

        if (string.IsNullOrWhiteSpace(envelope.Action))
        {
            return DecodeResult.Failure(ErrorCodes.InvalidRequest, "Field 'action' is required.", id);
        }

        if (!string.Equals(envelope.Type, MessageTypes.Request, StringComparison.Ordinal))
        {
            return DecodeResult.Failure(
                ErrorCodes.InvalidRequest,
                $"Field 'type' must be '{MessageTypes.Request}'.",
                id);
        }

        if (id is null)
        {
            return DecodeResult.Failure(ErrorCodes.InvalidRequest, "Field 'id' is required.");
        }

        return DecodeResult.Success(envelope);
    }

    /// <summary>
    /// Bind wire parameters to an action's declared parameter type. Returns <see langword="false"/>
    /// with a structured error rather than throwing, so a bad payload is an ordinary failure response.
    /// </summary>
    public static bool TryBindParameters(
        JsonElement? parameters,
        Type parameterType,
        out object? bound,
        [NotNullWhen(false)] out ProtocolError? error)
    {
        bound = null;
        error = null;

        // Actions that take no parameters use NoParameters; absent params are then valid.
        if (parameters is null || parameters.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            try
            {
                bound = Activator.CreateInstance(parameterType);
            }
            catch (Exception)
            {
                bound = null;
            }

            if (bound is null)
            {
                error = new ProtocolError(ErrorCodes.InvalidParameters, "Field 'params' is required for this action.");
                return false;
            }

            return true;
        }

        if (parameters.Value.ValueKind != JsonValueKind.Object)
        {
            error = new ProtocolError(
                ErrorCodes.InvalidParameters,
                "Field 'params' must be a JSON object.",
                $"received: {parameters.Value.ValueKind.ToString().ToLowerInvariant()}");
            return false;
        }

        try
        {
            bound = parameters.Value.Deserialize(parameterType, Options);
        }
        catch (JsonException ex)
        {
            error = new ProtocolError(ErrorCodes.InvalidParameters, "Field 'params' could not be bound.", ex.Message);
            return false;
        }
        catch (NotSupportedException ex)
        {
            error = new ProtocolError(ErrorCodes.InvalidParameters, "Field 'params' could not be bound.", ex.Message);
            return false;
        }

        if (bound is null)
        {
            error = new ProtocolError(ErrorCodes.InvalidParameters, "Field 'params' decoded to null.");
            return false;
        }

        return true;
    }

    public static string Encode(ResponseEnvelope response) => JsonSerializer.Serialize(response, Options);

    public static string Encode(EventEnvelope @event) => JsonSerializer.Serialize(@event, Options);

    /// <summary>Encode a request. Used by test clients and by future outbound bridges.</summary>
    public static string Encode(RequestEnvelope request) => JsonSerializer.Serialize(request, Options);
}
