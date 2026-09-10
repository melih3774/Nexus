using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Files;
using PcCore.Capabilities.Identity;
using PcCore.Capabilities.Indicator;
using PcCore.Capabilities.Input;
using PcCore.Capabilities.Screen;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.Storage;
using PcCore.Capabilities.System;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using PcCore.Transport.Remote;
using Xunit;

namespace PcCore.Tests.Remote;

/// <summary>
/// Live streaming and remote input over a real WebSocket.
/// <para>
/// The pieces are unit-tested individually; this is about how they behave once a socket is involved.
/// Frames have to arrive as binary messages alongside JSON responses on the same connection, input
/// has to be gated on a session rather than on being connected, and — the part that only a real
/// connection can show — everything has to be released when the connection ends, however it ends.
/// </para>
/// </summary>
public sealed class RemoteControlTransportTests : IAsyncLifetime
{
    private TempStateDirectory _state = null!;
    private ServiceProvider _services = null!;
    private RemoteTransport _transport = null!;
    private PairingCompletion _phone = null!;
    private readonly OwnerPhoneKey _ownerKey = new();
    private Uri _uri = null!;
    private IPcIdentityProvider _identity = null!;
    private RecordingInputController _input = null!;
    private FakeDesktopStream _capture = null!;
    private NullRemoteIndicator _indicator = null!;

    public async ValueTask InitializeAsync()
    {
        _state = new TempStateDirectory("remote-control");

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton(_state.Directory);
        services.AddSingleton<ISecretProtector, PassThroughSecretProtector>();
        services.AddSingleton(new PairingOptions());
        services.AddSingleton(new ScreenOptions());
        services.AddSingleton<IPcIdentityProvider, PcIdentityProvider>();
        services.AddSingleton<IPrimaryPhoneRegistry, PrimaryPhoneRegistry>();
        services.AddSingleton<IDeviceEndpointProvider, NoDeviceEndpoint>();
        services.AddSingleton<IPairingService, PairingService>();
        services.AddSingleton<IDeviceAuthenticator, DeviceAuthenticator>();
        services.AddSingleton<IRemoteEnrollmentService, RemoteEnrollmentService>();

        services.AddSingleton<EventBus>();
        services.AddSingleton<IEventPublisher>(p => p.GetRequiredService<EventBus>());
        services.AddSingleton<IEventStream>(p => p.GetRequiredService<EventBus>());
        services.AddSingleton<ISecurityStateService, SecurityStateService>();
        services.AddSingleton<ISystemInfoProvider, FakeSystemInfoProvider>();

        var options = new RemoteTransportOptions { Port = FreePort() };
        services.AddSingleton(options);
        services.AddSingleton<RemoteSessionRegistry>();
        services.AddRemoteControlDoubles();
        services.AddSingleton<RemoteStreamCoordinator>();
        services.AddSingleton<IStreamCoordinator>(p => p.GetRequiredService<RemoteStreamCoordinator>());

        services.AddSingleton<ICapabilityAction, GetSystemInfoAction>();
        services.AddSingleton<ICapabilityAction, ListDisplaysAction>();
        services.AddSingleton<ICapabilityAction, StartStreamAction>();
        services.AddSingleton<ICapabilityAction, StopStreamAction>();
        services.AddSingleton<ICapabilityAction, BeginInputAction>();
        services.AddSingleton<ICapabilityAction, EndInputAction>();
        services.AddSingleton<ICapabilityAction, PointerAction>();
        services.AddSingleton<ICapabilityAction, ButtonInputAction>();
        services.AddSingleton<ICapabilityAction, ScrollAction>();
        services.AddSingleton<ICapabilityAction, KeyInputAction>();
        services.AddSingleton<ICapabilityAction, TextInputAction>();
        services.AddSingleton<ICapabilityAction, ReleaseAllInputAction>();

        services.AddSingleton<ICapabilityRegistry>(p => new CapabilityRegistry(p.GetServices<ICapabilityAction>()));
        services.AddSingleton<ICapabilityPolicy, DefaultCapabilityPolicy>();
        services.AddSingleton<ICapabilityDispatcher, CapabilityDispatcher>();

        _services = services.BuildServiceProvider();
        _identity = _services.GetRequiredService<IPcIdentityProvider>();
        _input = _services.GetRequiredService<RecordingInputController>();
        _capture = _services.GetRequiredService<FakeDesktopStream>();
        _indicator = _services.GetRequiredService<NullRemoteIndicator>();

        var pairing = _services.GetRequiredService<IPairingService>();
        var session = pairing.Begin(replaceExisting: true);
        Assert.True(PairingPayloadV2.TryDecode(session.Payload, out var payload));
        _phone = pairing.Complete(payload!.SessionId, payload.PairingSecret, "Test Phone");

        _transport = new RemoteTransport(
            _services,
            options,
            _services.GetRequiredService<RemoteSessionRegistry>(),
            _services.GetRequiredService<ILoggerFactory>());

        await _transport.StartAsync(new RemoteEndpoint(IPAddress.Loopback, options.Port, "test loopback endpoint"));
        _uri = new Uri($"ws://127.0.0.1:{options.Port}{RemoteTransport.Path}");
    }

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        await _services.DisposeAsync();
        _ownerKey.Dispose();
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

    /// <summary>
    /// A phone that has authenticated as the Primary Phone and nothing more. Everything below
    /// viewing and control is reachable from here; nothing above it is.
    /// </summary>
    private async Task<RemotePhoneClient> ConnectUnauthorizedAsync(CancellationToken token)
    {
        var client = new RemotePhoneClient(_phone.DeviceId, _phone.SharedSecret);
        await client.ConnectAsync(_uri, token);

        var authenticated = await client.AuthenticateAsync(_identity.PcInstanceId, token);
        Assert.True(authenticated.Success);

        return client;
    }

    /// <summary>
    /// A Full Remote Session: authenticated as the phone, then authorised by a signature from the
    /// key Android only releases after the owner satisfies a system prompt.
    /// </summary>
    private async Task<RemotePhoneClient> ConnectAsync(CancellationToken token)
    {
        var client = await ConnectUnauthorizedAsync(token);
        await client.AuthorizeAsync(_ownerKey, _identity.PcInstanceId, _phone.DeviceId, token);
        return client;
    }

    /// <summary>
    /// Viewing and control need the owner, not just the phone. This is the whole difference between
    /// a paired device and a person holding it.
    /// </summary>
    [Theory]
    [InlineData("screen.list_displays")]
    [InlineData("screen.stream.start")]
    [InlineData("input.begin")]
    public async Task AnUnauthorizedSessionCannotViewOrControl(string action)
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectUnauthorizedAsync(token);

        var response = await client.SendRequestAsync(action, null, token);

        Assert.False(response.Success, $"{action} was allowed without owner authorisation");
        Assert.Equal(ErrorCodes.NotAuthorized, response.Error!.Code);
    }

    /// <summary>Locking the phone takes control away immediately, not in fifteen minutes.</summary>
    [Fact]
    public async Task LockingThePhoneWithdrawsControlAtOnce()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);

        Assert.True((await client.SendRequestAsync("input.begin", null, token)).Success);
        Assert.True((await client.SendRequestAsync("session.lock", null, token)).Success);

        var afterLock = await client.SendRequestAsync("input.pointer", new { mode = "RELATIVE", dx = 5, dy = 5 }, token);

        Assert.False(afterLock.Success);
        Assert.Equal(ErrorCodes.NotAuthorized, afterLock.Error!.Code);
    }

    /// <summary>
    /// A reconnect must not cost a fingerprint. The ticket is what carries the owner's authorisation
    /// across a socket that dropped in a lift.
    /// </summary>
    [Fact]
    public async Task AReconnectResumesTheSessionFromItsTicket()
    {
        var token = TestContext.Current.CancellationToken;

        string ticket;

        await using (var first = await ConnectUnauthorizedAsync(token))
        {
            ticket = await first.AuthorizeAsync(_ownerKey, _identity.PcInstanceId, _phone.DeviceId, token);
        }

        await using var second = await ConnectUnauthorizedAsync(token);

        // Not authorised yet: the grant belonged to a connection that no longer exists.
        Assert.False((await second.SendRequestAsync("input.begin", null, token)).Success);

        var resumed = await second.SendRequestAsync("session.resume", new { ticket }, token);
        Assert.True(resumed.Success, $"session.resume failed: {resumed.Error?.Code}");

        Assert.True((await second.SendRequestAsync("input.begin", null, token)).Success);
    }

    /// <summary>A ticket the owner has locked away is not a way back in.</summary>
    [Fact]
    public async Task ATicketIsUselessAfterThePhoneLocks()
    {
        var token = TestContext.Current.CancellationToken;

        string ticket;

        await using (var first = await ConnectUnauthorizedAsync(token))
        {
            ticket = await first.AuthorizeAsync(_ownerKey, _identity.PcInstanceId, _phone.DeviceId, token);
            Assert.True((await first.SendRequestAsync("session.lock", null, token)).Success);
        }

        await using var second = await ConnectUnauthorizedAsync(token);
        var resumed = await second.SendRequestAsync("session.resume", new { ticket }, token);

        Assert.False(resumed.Success);
        Assert.Equal(ErrorCodes.AuthenticationFailed, resumed.Error!.Code);
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, CancellationToken token, int timeoutMs = 5000)
    {
        for (var i = 0; i < timeoutMs / 20 && !condition(); i++)
        {
            await Task.Delay(20, token);
        }

        return condition();
    }

    // --- Streaming --------------------------------------------------------------------------------------

    [Fact]
    public async Task ADeviceCanListDisplays()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);

        var response = await client.SendRequestAsync("screen.list_displays", null, token);

        Assert.True(response.Success);

        var result = JsonSerializer.SerializeToElement(response.Result, ProtocolCodec.Options);
        Assert.Equal(1, result.GetProperty("count").GetInt32());
        Assert.Equal(1920, result.GetProperty("displays")[0].GetProperty("width").GetInt32());
    }

    [Fact]
    public async Task StartingAStreamDeliversBinaryFrames()
    {
        // The whole point of the binary path: frames arrive as their own message type on the same
        // authenticated connection, without being wrapped in JSON.
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);

        var started = await client.SendRequestAsync("screen.stream.start", null, token);
        Assert.True(started.Success);

        var result = JsonSerializer.SerializeToElement(started.Result, ProtocolCodec.Options);
        var streamId = result.GetProperty("stream_id").GetUInt32();

        Assert.Equal(StreamFrameHeader.CurrentVersion, result.GetProperty("frame_protocol_version").GetInt32());

        var frame = await client.WaitForFrameAsync(TimeSpan.FromSeconds(10), token);

        Assert.NotNull(frame);
        Assert.True(StreamFrameHeader.TryParse(frame, out var header));
        Assert.Equal(streamId, header.StreamId);
        Assert.Equal(FrameEncoding.Jpeg, header.Encoding);
        Assert.Equal(frame!.Length - StreamFrameHeader.Size, header.PayloadLength);

        var stopped = await client.SendRequestAsync("screen.stream.stop", null, token);
        Assert.True(stopped.Success);
    }

    [Fact]
    public async Task ControlTrafficKeepsWorkingWhileFramesFlow()
    {
        // Media must not starve control. A response that arrives only after the stream stops would
        // make the whole interface feel broken while streaming.
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);

        await client.SendRequestAsync("screen.stream.start", null, token);
        await client.WaitForFrameAsync(TimeSpan.FromSeconds(10), token);

        for (var i = 0; i < 5; i++)
        {
            var info = await client.SendRequestAsync("system.get_info", null, token);
            Assert.True(info.Success);
        }

        await client.SendRequestAsync("screen.stream.stop", null, token);
    }

    [Fact]
    public async Task StoppingAStreamReleasesTheCapture()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);

        await client.SendRequestAsync("screen.stream.start", null, token);
        await client.WaitForFrameAsync(TimeSpan.FromSeconds(10), token);

        var stopped = await client.SendRequestAsync("screen.stream.stop", null, token);
        var result = JsonSerializer.SerializeToElement(stopped.Result, ProtocolCodec.Options);

        Assert.True(result.GetProperty("stopped").GetBoolean());
        Assert.True(result.GetProperty("frames_sent").GetInt64() > 0);

        Assert.True(await WaitAsync(() => _capture.Opened.All(s => s.Disposed), token));
    }

    [Fact]
    public async Task DisconnectingStopsTheStream()
    {
        // Nobody asked for this cleanup; it has to happen anyway, or a dropped phone leaves a
        // display duplicated and a GPU busy indefinitely.
        var token = TestContext.Current.CancellationToken;

        var client = await ConnectAsync(token);
        await client.SendRequestAsync("screen.stream.start", null, token);
        await client.WaitForFrameAsync(TimeSpan.FromSeconds(10), token);

        await client.CloseAsync(token);
        await client.DisposeAsync();

        Assert.True(
            await WaitAsync(() => _capture.Opened.Count > 0 && _capture.Opened.All(s => s.Disposed), token),
            "the capture outlived the connection");
    }

    [Fact]
    public async Task ViewingAloneDoesNotRaiseTheOverlayInStandardMode()
    {
        // Watching is not controlling, and the default indicator mode draws that line.
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);

        await client.SendRequestAsync("screen.stream.start", null, token);
        await client.WaitForFrameAsync(TimeSpan.FromSeconds(10), token);

        Assert.True(await WaitAsync(() => _indicator.Last.Tray, token));
        Assert.False(_indicator.Last.Overlay);

        await client.SendRequestAsync("screen.stream.stop", null, token);
    }

    // --- Input ------------------------------------------------------------------------------------------

    [Fact]
    public async Task InputIsRefusedUntilTheControlSurfaceIsOpened()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);

        var refused = await client.SendRequestAsync(
            "input.pointer", new { mode = "ABSOLUTE", x = 0.5, y = 0.5, display_index = 0 }, token);

        Assert.False(refused.Success);
        Assert.Equal(ErrorCodes.NotAuthorized, refused.Error!.Code);
        Assert.Empty(_input.Events);
    }

    [Fact]
    public async Task OpeningTheControlSurfaceEnablesInputAndRaisesTheOverlay()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);

        var begun = await client.SendRequestAsync("input.begin", null, token);
        Assert.True(begun.Success);

        var moved = await client.SendRequestAsync(
            "input.pointer", new { mode = "ABSOLUTE", x = 1.0, y = 1.0, display_index = 0 }, token);

        Assert.True(moved.Success);
        Assert.Equal("move-absolute 1919,1079", _input.Events[^1]);

        // The badge follows input, not the connection.
        Assert.True(await WaitAsync(() => _indicator.Last.Overlay, token));
    }

    [Fact]
    public async Task TypingAndKeysReachTheController()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);
        await client.SendRequestAsync("input.begin", null, token);

        await client.SendRequestAsync("input.text", new { text = "merhaba 🙂" }, token);
        await client.SendRequestAsync("input.key", new { key = "ARROW_LEFT", action = "PRESS" }, token);
        await client.SendRequestAsync("input.scroll", new { vertical = 3.0, horizontal = 0.0 }, token);
        await client.SendRequestAsync("input.button", new { button = "RIGHT", action = "CLICK" }, token);

        Assert.Contains("text merhaba 🙂", _input.Events);
        Assert.Contains("key ArrowLeft down", _input.Events);
        Assert.Contains("scroll 0,3", _input.Events);
        Assert.Contains("button Right down", _input.Events);
    }

    [Fact]
    public async Task AMalformedPointerRequestIsAStructuredErrorAndTheSessionSurvives()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);
        await client.SendRequestAsync("input.begin", null, token);

        var refused = await client.SendRequestAsync(
            "input.pointer", new { mode = "SIDEWAYS", x = 0.5, y = 0.5 }, token);

        Assert.False(refused.Success);
        Assert.Equal(ErrorCodes.InvalidParameters, refused.Error!.Code);

        // The connection is still usable afterwards.
        var info = await client.SendRequestAsync("system.get_info", null, token);
        Assert.True(info.Success);
    }

    [Fact]
    public async Task EndingTheControlSurfaceReleasesHeldInput()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);
        await client.SendRequestAsync("input.begin", null, token);

        await client.SendRequestAsync("input.button", new { button = "LEFT", action = "DOWN" }, token);
        await client.SendRequestAsync("input.key", new { key = "CONTROL", action = "DOWN" }, token);

        _input.Events.Clear();

        var ended = await client.SendRequestAsync("input.end", null, token);
        Assert.True(ended.Success);

        Assert.Contains("button Left up", _input.Events);
        Assert.Contains("key Control up", _input.Events);
    }

    [Fact]
    public async Task DisconnectingReleasesHeldButtonsAndModifiers()
    {
        // The failure this prevents: a connection lost mid-drag with Ctrl held, leaving Windows
        // believing both are still down and nobody at the machine to release them.
        var token = TestContext.Current.CancellationToken;

        var client = await ConnectAsync(token);
        await client.SendRequestAsync("input.begin", null, token);
        await client.SendRequestAsync("input.button", new { button = "LEFT", action = "DOWN" }, token);
        await client.SendRequestAsync("input.key", new { key = "CONTROL", action = "DOWN" }, token);

        _input.Events.Clear();

        // Dropped, not closed politely.
        await client.DisposeAsync();

        Assert.True(
            await WaitAsync(() => _input.Events.Contains("button Left up"), token),
            "a held mouse button survived the connection");

        Assert.Contains("key Control up", _input.Events);

        // And the indicator stops claiming a session that has ended.
        Assert.True(await WaitAsync(() => !_indicator.Last.Overlay, token));
    }

    [Fact]
    public async Task ReleaseAllClearsHeldInputWithoutEndingTheSession()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);
        await client.SendRequestAsync("input.begin", null, token);
        await client.SendRequestAsync("input.key", new { key = "ALT", action = "DOWN" }, token);

        _input.Events.Clear();

        var released = await client.SendRequestAsync("input.release_all", null, token);
        Assert.True(released.Success);
        Assert.Contains("key Alt up", _input.Events);

        // Still usable: no need to re-open the control surface after an interrupted gesture.
        var moved = await client.SendRequestAsync(
            "input.pointer", new { mode = "RELATIVE", dx = 5.0, dy = 5.0 }, token);

        Assert.True(moved.Success);
    }

    [Fact]
    public async Task WindowsRefusingInputIsReportedRatherThanFailing()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);
        await client.SendRequestAsync("input.begin", null, token);

        _input.BlockedByIntegrity = true;

        var response = await client.SendRequestAsync("input.button", new { button = "LEFT", action = "CLICK" }, token);

        // The request succeeded; the delivery did not. The phone can say why.
        Assert.True(response.Success);

        var result = JsonSerializer.SerializeToElement(response.Result, ProtocolCodec.Options);
        Assert.False(result.GetProperty("delivered").GetBoolean());
        Assert.Equal("BLOCKED_BY_INTEGRITY", result.GetProperty("delivery").GetString());
    }

    [Fact]
    public async Task StreamingAndInputAreIndependent()
    {
        // Deliberately decoupled: a future milestone should be able to view without input, or stop
        // the stream while keeping control, without either change touching the other.
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);

        await client.SendRequestAsync("input.begin", null, token);
        var moved = await client.SendRequestAsync(
            "input.pointer", new { mode = "RELATIVE", dx = 3.0, dy = 3.0 }, token);

        // Input works with no stream at all.
        Assert.True(moved.Success);

        await client.SendRequestAsync("screen.stream.start", null, token);
        await client.WaitForFrameAsync(TimeSpan.FromSeconds(10), token);
        await client.SendRequestAsync("screen.stream.stop", null, token);

        // And still works after the stream has gone.
        var again = await client.SendRequestAsync("input.pointer", new { mode = "RELATIVE", dx = 1.0, dy = 1.0 }, token);
        Assert.True(again.Success);
    }

    [Fact]
    public async Task RemoteControlDoesNotChangeTheSecurityState()
    {
        // SECURITY_SESSION plus an authenticated phone driving the machine is not OWNER_PRESENT.
        // Acknowledging an incident is an explicit act that v0.6 deliberately does not implement.
        var token = TestContext.Current.CancellationToken;
        var security = _services.GetRequiredService<ISecurityStateService>();

        security.RequestTransition(SecurityState.Armed, "test").EnsureAccepted();
        security.RequestTransition(SecurityState.SecuritySession, "test").EnsureAccepted();

        await using var client = await ConnectAsync(token);
        await client.SendRequestAsync("input.begin", null, token);
        await client.SendRequestAsync("input.pointer", new { mode = "RELATIVE", dx = 5.0, dy = 5.0 }, token);
        await client.SendRequestAsync("screen.stream.start", null, token);
        await client.WaitForFrameAsync(TimeSpan.FromSeconds(10), token);

        Assert.Equal(SecurityState.SecuritySession, security.Current.State);

        await client.SendRequestAsync("screen.stream.stop", null, token);
    }

    [Fact]
    public async Task AnUnauthenticatedConnectionCannotStreamOrType()
    {
        var token = TestContext.Current.CancellationToken;

        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(RemoteProtocol.Subprotocol);
        await socket.ConnectAsync(_uri, token);

        // Skip the challenge and go straight for the powerful things.
        var request = JsonSerializer.Serialize(
            new
            {
                protocol_version = ProtocolConstants.CurrentVersion,
                id = "x1",
                type = MessageTypes.Request,
                action = "input.begin",
            },
            ProtocolCodec.Options);

        await socket.SendAsync(
            Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, endOfMessage: true, token);

        Assert.Empty(_input.Events);
        Assert.Equal(0, _services.GetRequiredService<DesktopStreamService>().ActiveStreams);
    }
}
