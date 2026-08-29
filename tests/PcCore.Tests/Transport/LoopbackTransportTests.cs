using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.Storage;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using PcCore.Transport.Loopback;
using Xunit;

namespace PcCore.Tests.Transport;

/// <summary>
/// End-to-end tests for the loopback transport, hosted in-process on a real socket.
/// <para>
/// These exist because the transport's wiring — guards, and above all the ordering between
/// subscribing a WebSocket client to the event stream and publishing its greeting — is not
/// reachable from unit tests of the capability layer, and a real ordering bug slipped through
/// exactly there.
/// </para>
/// </summary>
public sealed class LoopbackTransportTests : IAsyncLifetime
{
    private sealed record PingResult(string Reply);

    private sealed record WhoAmIResult(string Transport, ActorType ActorType, string? DeviceId);

    /// <summary>Reports the identity the dispatcher supplied, so tests can assert it is not caller-controlled.</summary>
    private sealed class WhoAmIAction : CapabilityAction<NoParameters, WhoAmIResult>
    {
        public override string Name => "test.whoami";
        public override string Description => "Echoes the caller identity the dispatcher supplied.";

        protected override ValueTask<WhoAmIResult> ExecuteAsync(
            NoParameters parameters,
            CapabilityContext context,
            CancellationToken cancellationToken) => ValueTask.FromResult(
                new WhoAmIResult(context.Caller.Transport, context.Caller.ActorType, context.Caller.DeviceId));
    }

    private sealed class PingAction : CapabilityAction<NoParameters, PingResult>
    {
        public override string Name => "test.ping";
        public override string Description => "Returns pong.";

        protected override ValueTask<PingResult> ExecuteAsync(
            NoParameters parameters,
            CapabilityContext context,
            CancellationToken cancellationToken) => ValueTask.FromResult(new PingResult("pong"));
    }

    private WebApplication _app = null!;
    private SessionToken _token = null!;
    private TempStateDirectory _state = null!;
    private int _port;

    private string BaseUrl => $"http://127.0.0.1:{_port}";

    public async ValueTask InitializeAsync()
    {
        _port = FreePort();
        _token = SessionToken.Generate();

        var options = new LoopbackTransportOptions { Port = _port, RequireToken = true };

        // Built exactly the way PcCore.Host builds it, so the test covers the real composition.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.ConfigureLoopbackKestrel(options);
        builder.Services.AddLoopbackTransport(options, _token);

        builder.Services.AddSingleton<EventBus>();
        builder.Services.AddSingleton<IEventPublisher>(p => p.GetRequiredService<EventBus>());
        builder.Services.AddSingleton<IEventStream>(p => p.GetRequiredService<EventBus>());
        builder.Services.AddSingleton<ICapabilityAction, PingAction>();
        builder.Services.AddSingleton<ICapabilityAction, WhoAmIAction>();

        // The real security subsystem, so security.state_changed travels the production path:
        // service -> EventBus -> WebSocket connection -> client. Persistence is pointed at a
        // throwaway directory so the suite never touches the owner's real state.
        _state = new TempStateDirectory("transport");
        builder.Services.AddSingleton(_state.Directory);
        builder.Services.AddSingleton<ISecurityStateService, SecurityStateService>();
        builder.Services.AddSingleton<ICapabilityAction, GetSecurityStateAction>();
        builder.Services.AddSingleton<ICapabilityAction, ArmSecurityAction>();
        builder.Services.AddSingleton<ICapabilityAction, DisarmSecurityAction>();
        builder.Services.AddSingleton<ICapabilityRegistry>(p => new CapabilityRegistry(p.GetServices<ICapabilityAction>()));
        builder.Services.AddSingleton<ICapabilityDispatcher, CapabilityDispatcher>();

        _app = builder.Build();
        _app.MapLoopbackTransport("<html>test page</html>");

        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _state.Dispose();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private HttpClient Client(bool withToken = true)
    {
        var client = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        if (withToken)
        {
            client.DefaultRequestHeaders.Add(ProtocolConstants.TokenHeader, _token.Value);
        }

        return client;
    }

    private static StringContent Envelope(string action) => new(
        $$"""{ "protocol_version": 1, "id": "t1", "type": "request", "action": "{{action}}" }""",
        Encoding.UTF8,
        "application/json");

    // --- HTTP ---------------------------------------------------------------------------------

    [Fact]
    public async Task Rpc_DispatchesAndReturnsAResponseEnvelope()
    {
        using var client = Client();

        var response = await client.PostAsync("/rpc", Envelope("test.ping"), TestContext.Current.CancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal("t1", json.GetProperty("id").GetString());
        Assert.Equal("test.ping", json.GetProperty("action").GetString());
        Assert.Equal("pong", json.GetProperty("result").GetProperty("reply").GetString());
    }

    [Fact]
    public async Task Rpc_ReturnsTwoHundredForAProtocolLevelFailure()
    {
        // A well-formed exchange that ends in a capability error is still a successful HTTP call;
        // only transport-level refusals use an HTTP error status.
        using var client = Client();

        var response = await client.PostAsync("/rpc", Envelope("does.not.exist"), TestContext.Current.CancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal(ErrorCodes.UnknownAction, json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Rpc_RejectsAMissingToken()
    {
        using var client = Client(withToken: false);

        var response = await client.PostAsync("/rpc", Envelope("test.ping"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Rpc_RejectsAWrongToken()
    {
        using var client = Client(withToken: false);
        client.DefaultRequestHeaders.Add(ProtocolConstants.TokenHeader, new string('0', 64));

        var response = await client.PostAsync("/rpc", Envelope("test.ping"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Rpc_RejectsACrossOriginRequest()
    {
        // The DNS-rebinding defence: a page on another origin must not be able to drive PC Core
        // through the user's browser.
        using var client = Client();
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");

        var response = await client.PostAsync("/rpc", Envelope("test.ping"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Rpc_RejectsAnUnexpectedHostHeader()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Host = "attacker.example";

        var response = await client.PostAsync("/rpc", Envelope("test.ping"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Healthz_NeedsNoToken()
    {
        using var client = Client(withToken: false);

        var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TestPage_IsServedAtRoot()
    {
        using var client = Client(withToken: false);

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("test page", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // --- WebSocket ----------------------------------------------------------------------------

    private async Task<ClientWebSocket> ConnectAsync(bool withToken, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(LoopbackGuard.Subprotocol);

        if (withToken)
        {
            // Browsers cannot set headers on a handshake, so the token rides as a subprotocol.
            socket.Options.AddSubProtocol(LoopbackGuard.TokenSubprotocolPrefix + _token.Value);
        }

        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), cancellationToken);
        return socket;
    }

    /// <summary>
    /// Receives one frame, bounded by a short timeout.
    /// <para>
    /// The bound matters: the bug this suite guards against manifests as a frame that never
    /// arrives, and an unbounded receive would hang the whole run instead of failing.
    /// </para>
    /// </summary>
    private static async Task<JsonElement> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var buffer = new byte[64 * 1024];

        try
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Assert.Fail("Timed out waiting for a WebSocket frame.");
            throw;
        }
    }

    private static Task SendAsync(WebSocket socket, string json, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken);

    [Fact]
    public async Task WebSocket_DeliversTheConnectedEventToTheClientThatTriggeredIt()
    {
        // Regression guard. The greeting was previously published before the connection had
        // subscribed to the event stream, so it reached nobody and the whole event path was
        // silently dead despite looking wired up.
        var cancellationToken = TestContext.Current.CancellationToken;
        using var socket = await ConnectAsync(withToken: true, cancellationToken);

        var message = await ReceiveAsync(socket, cancellationToken);

        Assert.Equal(MessageTypes.Event, message.GetProperty("type").GetString());
        Assert.Equal("core.connected", message.GetProperty("event").GetString());
        Assert.False(string.IsNullOrWhiteSpace(message.GetProperty("id").GetString()));
        Assert.True(message.TryGetProperty("timestamp_utc", out _));
    }

    [Fact]
    public async Task WebSocket_NegotiatesTheProtocolSubprotocolAndNotTheTokenOne()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var socket = await ConnectAsync(withToken: true, cancellationToken);

        // The token must never be echoed back in the negotiated subprotocol.
        Assert.Equal(LoopbackGuard.Subprotocol, socket.SubProtocol);
        Assert.DoesNotContain(_token.Value, socket.SubProtocol ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebSocket_CarriesRequestsAndResponses()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var socket = await ConnectAsync(withToken: true, cancellationToken);

        await ReceiveAsync(socket, cancellationToken); // core.connected

        await SendAsync(
            socket,
            """{ "protocol_version": 1, "id": "ws-1", "type": "request", "action": "test.ping" }""",
            cancellationToken);

        var response = await ReceiveAsync(socket, cancellationToken);

        Assert.Equal(MessageTypes.Response, response.GetProperty("type").GetString());
        Assert.Equal("ws-1", response.GetProperty("id").GetString());
        Assert.True(response.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task WebSocket_ReturnsStructuredErrorsForBadMessages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var socket = await ConnectAsync(withToken: true, cancellationToken);

        await ReceiveAsync(socket, cancellationToken); // core.connected

        await SendAsync(socket, "{ not json", cancellationToken);
        var response = await ReceiveAsync(socket, cancellationToken);

        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Equal(ErrorCodes.InvalidRequest, response.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task LoopbackCallersRemainLocalAndCannotSelfAssertDeviceIdentity()
    {
        // v0.3 regression guard. Identity is stamped by the transport, never taken from the
        // request: a body claiming ActorType.DEVICE with a device id must have no effect at all.
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = Client();

        var hostile = new StringContent(
            """
            { "protocol_version": 1, "id": "t1", "type": "request", "action": "test.whoami",
              "params": { "actor_type": "DEVICE", "device_id": "attacker-device" } }
            """,
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/rpc", hostile, cancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        Assert.True(json.GetProperty("success").GetBoolean());

        var result = json.GetProperty("result");
        Assert.Equal("LOCAL", result.GetProperty("actor_type").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("device_id").ValueKind);
        Assert.StartsWith("loopback-", result.GetProperty("transport").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebSocket_RejectsAHandshakeWithoutAToken()
    {
        await Assert.ThrowsAsync<WebSocketException>(
            () => ConnectAsync(withToken: false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WebSocket_DeliversSecurityStateChangedFromAnotherCallersRequest()
    {
        // The whole point of the event channel: a state change made by one caller (here, an HTTP
        // request) is announced to every connected listener, not just the caller that caused it.
        var cancellationToken = TestContext.Current.CancellationToken;
        using var socket = await ConnectAsync(withToken: true, cancellationToken);

        await ReceiveAsync(socket, cancellationToken); // core.connected

        using var client = Client();
        var response = await client.PostAsync("/rpc", Envelope("security.arm"), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var message = await ReceiveAsync(socket, cancellationToken);

        Assert.Equal(MessageTypes.Event, message.GetProperty("type").GetString());
        Assert.Equal("security.state_changed", message.GetProperty("event").GetString());

        var payload = message.GetProperty("payload");
        Assert.Equal("OWNER_PRESENT", payload.GetProperty("previous_state").GetString());
        Assert.Equal("ARMED", payload.GetProperty("current_state").GetString());
    }

    [Fact]
    public async Task WebSocket_SendsNoEventForAnIdempotentSecurityCommand()
    {
        // Re-arming changes nothing, so no listener should be told anything happened.
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = Client();

        await client.PostAsync("/rpc", Envelope("security.arm"), cancellationToken);

        using var socket = await ConnectAsync(withToken: true, cancellationToken);
        await ReceiveAsync(socket, cancellationToken); // core.connected

        await client.PostAsync("/rpc", Envelope("security.arm"), cancellationToken);

        // Provoke a real event afterwards: if the no-op had published one, it would arrive first.
        await client.PostAsync("/rpc", Envelope("security.disarm"), cancellationToken);

        var message = await ReceiveAsync(socket, cancellationToken);

        Assert.Equal("security.state_changed", message.GetProperty("event").GetString());
        Assert.Equal("ARMED", message.GetProperty("payload").GetProperty("previous_state").GetString());
        Assert.Equal("OWNER_PRESENT", message.GetProperty("payload").GetProperty("current_state").GetString());
    }
}
