using System.Text.Json;
using PcCore.Protocol;
using Xunit;

namespace PcCore.Tests.Protocol;

public sealed class ProtocolCodecTests
{
    // --- Decoding -----------------------------------------------------------------------------

    [Fact]
    public void DecodeRequest_ReadsAWellFormedEnvelope()
    {
        const string json = """
            { "protocol_version": 1, "id": "abc", "type": "request", "action": "files.list",
              "params": { "path": "C:\\Temp", "include_hidden": true } }
            """;

        var result = ProtocolCodec.DecodeRequest(json);

        Assert.True(result.IsSuccess);
        Assert.Equal("abc", result.Request.Id);
        Assert.Equal("files.list", result.Request.Action);
        Assert.Equal(1, result.Request.ProtocolVersion);
        Assert.Equal(JsonValueKind.Object, result.Request.Params!.Value.ValueKind);
    }

    [Fact]
    public void DecodeRequest_DefaultsProtocolVersionWhenAbsent()
    {
        var result = ProtocolCodec.DecodeRequest("""{ "id": "a", "type": "request", "action": "system.get_info" }""");

        Assert.True(result.IsSuccess);
        Assert.Equal(ProtocolConstants.CurrentVersion, result.Request.ProtocolVersion);
    }

    [Fact]
    public void DecodeRequest_IgnoresUnknownFields()
    {
        // Forward compatibility: a newer client may send fields this build has never heard of.
        var result = ProtocolCodec.DecodeRequest(
            """{ "id": "a", "type": "request", "action": "system.get_info", "future_field": 42 }""");

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json ")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"just a string\"")]
    public void DecodeRequest_RejectsMalformedBodies(string body)
    {
        var result = ProtocolCodec.DecodeRequest(body);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error.Code);
    }

    [Fact]
    public void DecodeRequest_RejectsAMissingAction()
    {
        var result = ProtocolCodec.DecodeRequest("""{ "id": "a", "type": "request" }""");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error.Code);
        // The id survives so the failure can still be correlated by the caller.
        Assert.Equal("a", result.Id);
    }

    [Fact]
    public void DecodeRequest_RejectsAMissingId()
    {
        var result = ProtocolCodec.DecodeRequest("""{ "type": "request", "action": "system.get_info" }""");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error.Code);
    }

    [Fact]
    public void DecodeRequest_RejectsAWrongType()
    {
        var result = ProtocolCodec.DecodeRequest("""{ "id": "a", "type": "response", "action": "system.get_info" }""");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void DecodeRequest_RejectsAnUnsupportedProtocolVersion(int version)
    {
        var result = ProtocolCodec.DecodeRequest(
            $$"""{ "protocol_version": {{version}}, "id": "a", "type": "request", "action": "system.get_info" }""");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.UnsupportedProtocolVersion, result.Error.Code);
        Assert.Equal("a", result.Id);
    }

    // --- Parameter binding --------------------------------------------------------------------

    private sealed record SampleParameters
    {
        public string? Path { get; init; }
        public int? MaxWidth { get; init; }
    }

    [Fact]
    public void TryBindParameters_BindsSnakeCaseToProperties()
    {
        var element = JsonDocument.Parse("""{ "path": "C:\\Temp", "max_width": 640 }""").RootElement;

        Assert.True(ProtocolCodec.TryBindParameters(element, typeof(SampleParameters), out var bound, out _));

        var typed = Assert.IsType<SampleParameters>(bound);
        Assert.Equal(@"C:\Temp", typed.Path);
        Assert.Equal(640, typed.MaxWidth);
    }

    [Fact]
    public void TryBindParameters_TreatsAbsentParamsAsDefaults()
    {
        Assert.True(ProtocolCodec.TryBindParameters(null, typeof(SampleParameters), out var bound, out _));

        var typed = Assert.IsType<SampleParameters>(bound);
        Assert.Null(typed.Path);
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("true")]
    public void TryBindParameters_RejectsNonObjectParams(string json)
    {
        var element = JsonDocument.Parse(json).RootElement;

        Assert.False(ProtocolCodec.TryBindParameters(element, typeof(SampleParameters), out _, out var error));
        Assert.Equal(ErrorCodes.InvalidParameters, error.Code);
    }

    [Fact]
    public void TryBindParameters_RejectsAWronglyTypedField()
    {
        var element = JsonDocument.Parse("""{ "max_width": "not a number" }""").RootElement;

        Assert.False(ProtocolCodec.TryBindParameters(element, typeof(SampleParameters), out _, out var error));
        Assert.Equal(ErrorCodes.InvalidParameters, error.Code);
    }

    // --- Encoding -----------------------------------------------------------------------------

    [Fact]
    public void Encode_WritesSnakeCaseAndPreservesNulls()
    {
        var response = ResponseEnvelope.Fail("id-1", "files.list", ErrorCodes.PathNotAllowed, "denied");

        var json = ProtocolCodec.Encode(response);

        Assert.Contains("\"protocol_version\":1", json);
        Assert.Contains("\"duration_ms\":", json);
        Assert.Contains("\"path_not_allowed\"", json);
        Assert.Contains("\"result\":null", json);
        Assert.Contains("\"success\":false", json);
    }

    [Fact]
    public void Encode_SerialisesTheResultUsingItsRuntimeType()
    {
        var response = ResponseEnvelope.Ok("id-1", "system.get_info", new { MachineName = "BOX", ItemCount = 3 });

        var json = ProtocolCodec.Encode(response);

        Assert.Contains("\"machine_name\":\"BOX\"", json);
        Assert.Contains("\"item_count\":3", json);
    }

    [Fact]
    public void Encode_WritesTheEventShape()
    {
        var @event = new EventEnvelope
        {
            Id = "e1",
            EventName = "core.connected",
            Payload = new { Detail = "x" },
            TimestampUtc = new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero),
        };

        var json = ProtocolCodec.Encode(@event);

        Assert.Contains("\"type\":\"event\"", json);
        Assert.Contains("\"event\":\"core.connected\"", json);
        Assert.Contains("\"timestamp_utc\":\"2026-08-28T09:00:00+00:00\"", json);
    }

    [Fact]
    public void RequestRoundTrips()
    {
        var original = new RequestEnvelope { Id = "r1", Action = "files.stat" };

        var decoded = ProtocolCodec.DecodeRequest(ProtocolCodec.Encode(original));

        Assert.True(decoded.IsSuccess);
        Assert.Equal(original.Id, decoded.Request.Id);
        Assert.Equal(original.Action, decoded.Request.Action);
        Assert.Equal(MessageTypes.Request, decoded.Request.Type);
    }

    [Fact]
    public void DecodeRequest_RejectsDeeplyNestedPayloads()
    {
        // MaxDepth guards against a hostile payload exhausting the stack.
        var nested = string.Concat(Enumerable.Repeat("{\"a\":", 200)) + "1" + new string('}', 200);
        var json = $$"""{ "id": "a", "type": "request", "action": "x", "params": {{nested}} }""";

        var result = ProtocolCodec.DecodeRequest(json);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error.Code);
    }
}
