using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Identity;
using PcCore.Capabilities.Indicator;
using PcCore.Capabilities.Media;
using PcCore.Capabilities.Screen;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.Storage;
using PcCore.Capabilities.System;
using PcCore.Protocol;
using PcCore.Tests.Media;
using PcCore.Tests.TestSupport;
using PcCore.Transport.Remote;

using Xunit;

namespace PcCore.Tests.Remote;

/// <summary>
/// Live monitoring, authorised by the same session that authorises everything else.
/// <para>
/// <b>The bug this exists for.</b> Live camera and microphone were refused for every session that
/// ever asked, with "this session is not authorised for live monitoring", while the identical phone
/// on the identical socket drove remote control, real UAC prompts and security evidence without
/// complaint. The capability policy was fine. What was wrong was the wiring underneath it: the
/// composition root handed <c>LiveSensorService</c> a predicate that rebuilt a
/// <see cref="CallerDescriptor"/> out of a connection id, and a rebuilt caller has no device id —
/// which is the one field <c>AuthorityOf</c> keys on. It returned <c>None</c> unconditionally.
/// </para>
/// <para>
/// <b>Why every existing test passed.</b> They all substituted the predicate. Passing
/// <c>_ =&gt; true</c> tests that the service starts a camera when told it may, and passing
/// <c>_ =&gt; false</c> tests that it refuses when told it may not — and neither says anything about
/// the function that actually decides. These tests use <see cref="LiveSensorAuthority.For"/>, the
/// same object the host wires in, driven by a real owner session over the real transport.
/// </para>
/// </summary>
public sealed class LiveMonitoringAuthorityTests : IAsyncLifetime
{
    private TempStateDirectory _state = null!;
    private ServiceProvider _services = null!;
    private RemoteTransport _transport = null!;
    private PairingCompletion _phone = null!;
    private readonly OwnerPhoneKey _ownerKey = new();
    private Uri _uri = null!;
    private IPcIdentityProvider _identity = null!;
    private LiveSensorService _live = null!;
    private FakeLiveSource _camera = null!;
    private FakeLiveSource _microphone = null!;
    private OwnerSessionService _sessions = null!;
    private AdvanceableTimeProvider _clock = null!;

    public async ValueTask InitializeAsync()
    {
        _state = new TempStateDirectory("live-authority");
        _clock = new AdvanceableTimeProvider();

        _camera = new FakeLiveSource("camera");
        _microphone = new FakeLiveSource("microphone");

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

        // The live sensor stack, composed the way the host composes it.
        services.AddSingleton<ILiveCameraSource>(_camera);
        services.AddSingleton<ILiveAudioSource>(_microphone);
        services.AddSingleton<LiveSensorService>();
        services.AddSingleton<ICapabilityAction, StartLiveSensorAction>();
        services.AddSingleton<ICapabilityAction, StopLiveSensorAction>();
        services.AddSingleton<ICapabilityAction, GetLiveSensorStatusAction>();

        // One ordinary Full Remote capability, so a single session can be shown doing both.
        services.AddSingleton<ICapabilityAction, GetSystemInfoAction>();

        services.AddSingleton<ICapabilityRegistry>(p => new CapabilityRegistry(p.GetServices<ICapabilityAction>()));
        services.AddSingleton<ICapabilityPolicy, DefaultCapabilityPolicy>();
        services.AddSingleton<ICapabilityDispatcher, CapabilityDispatcher>();

        _services = services.BuildServiceProvider();
        _identity = _services.GetRequiredService<IPcIdentityProvider>();
        _live = _services.GetRequiredService<LiveSensorService>();
        _sessions = _services.GetRequiredService<OwnerSessionService>();

        var coordinator = _services.GetRequiredService<RemoteStreamCoordinator>();
        coordinator.LiveSensors = _live;

        // The line under test. Not a stand-in for it — the production factory, with the production
        // owner-session service behind it.
        _live.Connect(
            sink: coordinator,
            isAuthorized: LiveSensorAuthority.For(_sessions),
            onChanged: _ => { });

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

    /// <summary>
    /// The reported failure, as one journey: the same authenticated, owner-authorised session that
    /// drives remote control starts the camera and the microphone.
    /// </summary>
    [Fact]
    public async Task TheSessionThatDrivesRemoteControlCanAlsoStartTheCameraAndMicrophone()
    {
        var token = TestContext.Current.CancellationToken;

        await using var phone = await ConnectAndAuthorizeAsync(token);

        // Ordinary Full Remote work, on this session, right now.
        var info = await phone.SendRequestAsync("system.get_info", null, token);
        Assert.True(info.Success, $"an ordinary Full Remote call failed: {info.Error?.Code}");

        // And live monitoring, on the very same session.
        var camera = await phone.SendRequestAsync("media.live.start", new { sensor = "CAMERA" }, token);

        Assert.True(camera.Success, $"media.live.start failed outright: {camera.Error?.Code}");
        Assert.Null(Refusal(camera));
        Assert.True(Flag(camera, "camera"), "the camera did not start for an authorised owner session");

        var microphone = await phone.SendRequestAsync("media.live.start", new { sensor = "MICROPHONE" }, token);

        Assert.True(microphone.Success, $"media.live.start failed outright: {microphone.Error?.Code}");
        Assert.Null(Refusal(microphone));
        Assert.True(Flag(microphone, "microphone"), "the microphone did not start for an authorised owner session");

        // The PC is saying so locally, which is not optional.
        Assert.Equal("Nexus · Camera + Microphone LIVE", Indicator(microphone));
        Assert.True(_live.TrayState.Camera);
        Assert.True(_live.TrayState.Microphone);
    }

    /// <summary>
    /// The other half of the rule, and the reason the predicate exists: a phone that has proved
    /// which device it is, but whose owner has not authorised this session, gets nothing.
    /// </summary>
    [Fact]
    public async Task AnAuthenticatedButUnauthorisedSessionIsRefused()
    {
        var token = TestContext.Current.CancellationToken;

        await using var phone = new RemotePhoneClient(_phone.DeviceId, _phone.SharedSecret);
        await phone.ConnectAsync(_uri, token);
        Assert.True((await phone.AuthenticateAsync(_identity.PcInstanceId, token)).Success);

        // Authenticated, not authorised: no biometric has been satisfied in this session.
        var camera = await phone.SendRequestAsync("media.live.start", new { sensor = "CAMERA" }, token);

        // Refused by the capability policy before it ever reaches the service.
        Assert.False(camera.Success);
        Assert.False(_live.TrayState.Any);
        Assert.Equal(0, _camera.Started);
    }

    /// <summary>
    /// Authority is not a fact checked once. When the owner session lapses, the camera goes off on
    /// its own rather than waiting for the phone to ask for something.
    /// </summary>
    [Fact]
    public async Task LosingOwnerAuthorityStopsAStreamAlreadyRunning()
    {
        var token = TestContext.Current.CancellationToken;

        await using var phone = await ConnectAndAuthorizeAsync(token);

        var started = await phone.SendRequestAsync("media.live.start", new { sensor = "CAMERA" }, token);
        Assert.True(Flag(started, "camera"));

        await Settle();
        Assert.True(_camera.Started > 0, "the capture loop never opened the device");

        // The owner locks the phone. Nothing else happens — no request, no disconnect.
        _sessions.Lock(_phone.DeviceId, "the owner locked the phone");

        // The watchdog runs on its own schedule; this is the behaviour being pinned, so wait for it.
        await WaitUntil(() => !_live.TrayState.Any, TimeSpan.FromSeconds(20));

        Assert.False(_live.TrayState.Any, "a live camera outlived the owner session that authorised it");
        Assert.True(_camera.Released, "the camera device was not released when authority lapsed");
    }

    /// <summary>The phone going away stops capture immediately, without waiting for the watchdog.</summary>
    [Fact]
    public async Task DisconnectingStopsEveryStreamThatConnectionStarted()
    {
        var token = TestContext.Current.CancellationToken;

        var phone = await ConnectAndAuthorizeAsync(token);

        Assert.True(Flag(await phone.SendRequestAsync("media.live.start", new { sensor = "CAMERA" }, token), "camera"));
        Assert.True(
            Flag(await phone.SendRequestAsync("media.live.start", new { sensor = "MICROPHONE" }, token), "microphone"));

        await Settle();

        await phone.DisposeAsync();

        await WaitUntil(() => !_live.TrayState.Any, TimeSpan.FromSeconds(10));

        Assert.False(_live.TrayState.Any, "a live stream outlived the connection that started it");
        Assert.True(_camera.Released, "the camera was not released when the phone disconnected");
        Assert.True(_microphone.Released, "the microphone was not released when the phone disconnected");
    }

    /// <summary>
    /// The predicate itself, at the seam where it went wrong: a caller carrying a device id is
    /// authorised, and one reconstructed without it — which is what the old wiring produced — is not.
    /// </summary>
    [Fact]
    public async Task TheAuthorityPredicateNeedsTheRealCallerNotAReconstructedOne()
    {
        var token = TestContext.Current.CancellationToken;

        await using var phone = await ConnectAndAuthorizeAsync(token);

        var authorized = LiveSensorAuthority.For(_sessions);
        var connectionId = Assert.Single(
            _services.GetRequiredService<RemoteSessionRegistry>().Snapshot()).Caller.ConnectionId;

        var real = new CallerDescriptor(
            RemoteTransport.TransportName, connectionId, ActorType.Device, _phone.DeviceId);

        Assert.True(authorized(real), "the real caller was refused");

        // Exactly what the composition root used to build. It is the same session, the same socket
        // and the same moment — and it is not authorised, because it is not a device.
        var reconstructed = new CallerDescriptor("remote", connectionId, ActorType.Device);

        Assert.Null(reconstructed.DeviceId);
        Assert.False(authorized(reconstructed));
    }

    // --- Helpers -----------------------------------------------------------------------------------

    /// <summary>Connect, authenticate as the paired device, and complete a real owner authorisation.</summary>
    private async Task<RemotePhoneClient> ConnectAndAuthorizeAsync(CancellationToken token)
    {
        var phone = new RemotePhoneClient(_phone.DeviceId, _phone.SharedSecret);
        await phone.ConnectAsync(_uri, token);

        Assert.True((await phone.AuthenticateAsync(_identity.PcInstanceId, token)).Success);

        await phone.AuthorizeAsync(_ownerKey, _identity.PcInstanceId, _phone.DeviceId, token);
        return phone;
    }

    private static async Task Settle() => await Task.Delay(200, TestContext.Current.CancellationToken);

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private static JsonElement Body(ResponseEnvelope response) =>
        JsonSerializer.SerializeToElement(response.Result, ProtocolCodec.Options);

    private static bool Flag(ResponseEnvelope response, string name) =>
        Body(response).GetProperty(name).GetBoolean();

    private static string? Refusal(ResponseEnvelope response) =>
        Body(response).TryGetProperty("refused", out var refused) && refused.ValueKind == JsonValueKind.String
            ? refused.GetString()
            : null;

    private static string Indicator(ResponseEnvelope response) =>
        Body(response).GetProperty("indicator").GetString() ?? string.Empty;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
