using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Files;
using PcCore.Capabilities.Identity;
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
/// End-to-end coverage of the remote transport: handshake, sessions, policy, and events, over a
/// real WebSocket against the real <see cref="RemoteTransport"/> and the real dispatcher.
/// <para>
/// The listener is bound to loopback here rather than a tailnet address, because CI and this
/// developer machine have no Tailscale interface. That substitution is confined to the test: the
/// <see cref="RemoteEndpoint"/> constructor is internal and the only production code that calls it
/// is <see cref="TailscaleAddressDetector"/>, whose refusal to yield anything but a
/// <c>100.64.0.0/10</c> address on a Tailscale interface is covered separately in
/// <see cref="TailscaleDetectionTests"/>. Everything above the socket — authentication, identity,
/// policy, sessions, events — is exercised exactly as it runs in production.
/// </para>
/// </summary>
public sealed class RemoteTransportTests : IAsyncLifetime
{
    private TempStateDirectory _state = null!;
    private ServiceProvider _services = null!;
    private RemoteTransport _transport = null!;
    private IPairingService _pairing = null!;
    private IPrimaryPhoneRegistry _phones = null!;
    private ISecurityStateService _security = null!;
    private IDeviceAuthenticator _authenticator = null!;
    private IPcIdentityProvider _identity = null!;

    private PairingCompletion _phone = null!;
    private readonly OwnerPhoneKey _ownerKey = new();
    private Uri _uri = null!;
    private TempDirectory _allowedRoot = null!;

    public async ValueTask InitializeAsync()
    {
        _state = new TempStateDirectory("remote");
        _allowedRoot = new TempDirectory("remote-files");
        _allowedRoot.CreateFile("readme.txt", "hello");

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

        services.AddSingleton(AllowedRoots.FromRealPaths(_allowedRoot.Path));
        services.AddSingleton<IRealPathResolver, PcCore.Windows.Win32RealPathResolver>();
        services.AddSingleton<PathAuthorizer>();
        services.AddSingleton<ISystemInfoProvider, FakeSystemInfoProvider>();
        services.AddSingleton<IProcessLister, FakeProcessLister>();
        services.AddSingleton<IScreenCapture, FakeScreenCapture>();

        // The full production capability surface, so policy is tested against real registrations.
        services.AddSingleton<ICapabilityAction, GetSystemInfoAction>();
        services.AddSingleton<ICapabilityAction, ListProcessesAction>();
        services.AddSingleton<ICapabilityAction, ListFilesAction>();
        services.AddSingleton<ICapabilityAction, StatFileAction>();
        services.AddSingleton<ICapabilityAction, CaptureScreenAction>();
        services.AddSingleton<ICapabilityAction, GetSecurityStateAction>();
        services.AddSingleton<ICapabilityAction, ArmSecurityAction>();
        services.AddSingleton<ICapabilityAction, DisarmSecurityAction>();
        services.AddSingleton<ICapabilityAction, GetPrimaryDeviceAction>();
        services.AddSingleton<ICapabilityAction, BeginPairingAction>();
        services.AddSingleton<ICapabilityAction, CancelPairingAction>();
        services.AddSingleton<ICapabilityAction, UnpairPrimaryDeviceAction>();

        services.AddSingleton<ICapabilityRegistry>(p => new CapabilityRegistry(p.GetServices<ICapabilityAction>()));
        services.AddSingleton<ICapabilityPolicy, DefaultCapabilityPolicy>();
        services.AddSingleton<ICapabilityDispatcher, CapabilityDispatcher>();

        var options = new RemoteTransportOptions { Port = FreePort(), MaxSessions = 2 };
        services.AddSingleton(options);
        services.AddSingleton<RemoteSessionRegistry>();
        services.AddRemoteControlDoubles();
        services.AddSingleton<RemoteStreamCoordinator>();

        _services = services.BuildServiceProvider();

        _pairing = _services.GetRequiredService<IPairingService>();
        _phones = _services.GetRequiredService<IPrimaryPhoneRegistry>();
        _security = _services.GetRequiredService<ISecurityStateService>();
        _authenticator = _services.GetRequiredService<IDeviceAuthenticator>();
        _identity = _services.GetRequiredService<IPcIdentityProvider>();

        _phone = PairPhone();

        _transport = new RemoteTransport(
            _services,
            options,
            _services.GetRequiredService<RemoteSessionRegistry>(),
            _services.GetRequiredService<ILoggerFactory>());

        // Internal constructor: production code can only obtain this from the Tailscale detector.
        var endpoint = new RemoteEndpoint(IPAddress.Loopback, options.Port, "test loopback endpoint");
        await _transport.StartAsync(endpoint);

        _uri = new Uri($"ws://127.0.0.1:{options.Port}{RemoteTransport.Path}");
    }

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        await _services.DisposeAsync();
        _ownerKey.Dispose();
        _allowedRoot.Dispose();
        _state.Dispose();
    }

    private PairingCompletion PairPhone()
    {
        var session = _pairing.Begin(replaceExisting: true);
        Assert.True(PairingPayloadV2.TryDecode(session.Payload, out var payload));
        return _pairing.Complete(payload!.SessionId, payload.PairingSecret, "Test Phone");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task<RemotePhoneClient> ConnectAsync(
        CancellationToken cancellationToken,
        string? deviceId = null,
        string? secret = null)
    {
        var client = new RemotePhoneClient(deviceId ?? _phone.DeviceId, secret ?? _phone.SharedSecret);
        await client.ConnectAsync(_uri, cancellationToken);
        return client;
    }

    private async Task<RemotePhoneClient> ConnectAndAuthenticateAsync(CancellationToken cancellationToken)
    {
        var client = await ConnectAsync(cancellationToken);
        var response = await client.AuthenticateAsync(_identity.PcInstanceId, cancellationToken);
        Assert.True(response.Success);
        return client;
    }

    /// <summary>
    /// Authenticated as the phone <em>and</em> authorised by the owner. Most capabilities need both,
    /// and the difference is what <see cref="AuthenticationAloneUnlocksAlmostNothing"/> pins down.
    /// </summary>
    private async Task<RemotePhoneClient> ConnectAndAuthorizeAsync(CancellationToken cancellationToken)
    {
        var client = await ConnectAndAuthenticateAsync(cancellationToken);
        await client.AuthorizeAsync(_ownerKey, _identity.PcInstanceId, _phone.DeviceId, cancellationToken);
        return client;
    }

    // --- Authentication ---------------------------------------------------------------------------

    [Fact]
    public async Task ThePairedPhoneEstablishesADeviceSession()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);

        var response = await client.AuthenticateAsync(_identity.PcInstanceId, token);

        Assert.True(response.Success);

        var result = System.Text.Json.JsonSerializer.SerializeToElement(response.Result, ProtocolCodec.Options);
        Assert.True(result.GetProperty("authenticated").GetBoolean());
        Assert.Equal("DEVICE", result.GetProperty("actor_type").GetString());
        Assert.Equal(_phone.DeviceId, result.GetProperty("device_id").GetString());
    }

    [Fact]
    public async Task AWrongSecretFailsAndTheConnectionIsClosed()
    {
        var token = TestContext.Current.CancellationToken;
        var wrong = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        await using var client = await ConnectAsync(token, secret: wrong);
        var response = await client.AuthenticateAsync(_identity.PcInstanceId, token);

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.AuthenticationFailed, response.Error!.Code);
        Assert.False(client.IsAuthenticated);
    }

    [Fact]
    public async Task AnUnknownDeviceIdFails()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token, deviceId: "not-the-paired-device");

        var response = await client.AuthenticateAsync(_identity.PcInstanceId, token);

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.AuthenticationFailed, response.Error!.Code);
    }

    [Fact]
    public async Task AProofBoundToADifferentInstallationFails()
    {
        // Domain separation in practice: the signed message includes the PC instance id.
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);

        var response = await client.AuthenticateAsync(Guid.NewGuid().ToString(), token);

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.AuthenticationFailed, response.Error!.Code);
    }

    [Fact]
    public async Task AReplayedProofCannotEstablishASecondSession()
    {
        // Capture a valid proof from a real handshake, then try to reuse it on a new connection.
        var token = TestContext.Current.CancellationToken;

        await using var first = await ConnectAsync(token);
        var challenge = await CaptureChallengeAsync(first, token);
        var proof = first.Sign(_identity.PcInstanceId, challenge.Challenge);

        var accepted = await first.SendRequestAsync(
            RemoteProtocol.AuthenticateAction,
            new { challenge_id = challenge.ChallengeId, device_id = _phone.DeviceId, proof },
            token);
        Assert.True(accepted.Success);

        await using var replay = await ConnectAsync(token);
        await CaptureChallengeAsync(replay, token);

        var replayed = await replay.SendRequestAsync(
            RemoteProtocol.AuthenticateAction,
            new { challenge_id = challenge.ChallengeId, device_id = _phone.DeviceId, proof },
            token);

        Assert.False(replayed.Success);
        Assert.Equal(ErrorCodes.AuthenticationFailed, replayed.Error!.Code);
    }

    [Fact]
    public async Task AChallengeFromOneConnectionCannotBeAnsweredOnAnother()
    {
        var token = TestContext.Current.CancellationToken;

        await using var a = await ConnectAsync(token);
        var challengeA = await CaptureChallengeAsync(a, token);

        await using var b = await ConnectAsync(token);
        var challengeB = await CaptureChallengeAsync(b, token);

        // Sign B's challenge but present A's id: neither half matches the other.
        var response = await b.SendRequestAsync(
            RemoteProtocol.AuthenticateAction,
            new
            {
                challenge_id = challengeA.ChallengeId,
                device_id = _phone.DeviceId,
                proof = b.Sign(_identity.PcInstanceId, challengeB.Challenge),
            },
            token);

        Assert.False(response.Success);
    }

    [Fact]
    public async Task AnUnpairedSystemAcceptsNoSession()
    {
        var token = TestContext.Current.CancellationToken;
        var old = _phone;

        _phones.Unpair();

        await using var client = new RemotePhoneClient(old.DeviceId, old.SharedSecret);
        await client.ConnectAsync(_uri, token);

        // The handshake proceeds normally and fails at verification: an unpaired PC is
        // indistinguishable from a wrong secret, so the tailnet learns nothing about pairing state.
        var failure = await client.AuthenticateAsync(_identity.PcInstanceId, token);

        Assert.False(failure.Success);
        Assert.Equal(ErrorCodes.AuthenticationFailed, failure.Error!.Code);
        Assert.False(client.IsAuthenticated);
    }

    [Fact]
    public async Task ARequestCannotSelfAssertDeviceIdentityBeforeAuthenticating()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);
        await CaptureChallengeAsync(client, token);

        // A body claiming to be a device, without any proof.
        var response = await client.SendRequestAsync(
            "system.get_info",
            new { actor_type = "DEVICE", device_id = _phone.DeviceId },
            token);

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.AuthenticationFailed, response.Error!.Code);
    }

    // --- Policy -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("system.get_info")]
    [InlineData("system.list_processes")]
    [InlineData("security.get_state")]
    public async Task AllowedActionsSucceedForADevice(string action)
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAndAuthorizeAsync(token);

        var response = await client.SendRequestAsync(action, null, token);

        Assert.True(response.Success, $"{action} was refused: {response.Error?.Code}");
    }

    /// <summary>
    /// The distinction v1.0 introduced, stated as a test.
    /// <para>
    /// Authenticating proves which phone this is. It is a stored credential, so it is a fact about a
    /// device - and a device can be picked up by somebody else. Everything that lets the holder see
    /// or touch the PC therefore needs the second proof, and what is left without it is two status
    /// reads and the handshake that obtains the second proof.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("files.list")]
    [InlineData("system.list_processes")]
    [InlineData("screen.capture")]
    [InlineData("security.arm")]
    public async Task AuthenticationAloneUnlocksAlmostNothing(string action)
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAndAuthenticateAsync(token);

        var response = await client.SendRequestAsync(action, new { path = _allowedRoot.Path }, token);

        Assert.False(response.Success, $"{action} was allowed without owner authorisation");
        Assert.Equal(ErrorCodes.NotAuthorized, response.Error!.Code);
    }

    /// <summary>What a locked phone may still ask, so it can show whether the PC is even there.</summary>
    [Theory]
    [InlineData("system.get_info")]
    [InlineData("security.get_state")]
    [InlineData("session.status")]
    public async Task ALockedPhoneCanStillSeeThatThePcIsThere(string action)
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAndAuthenticateAsync(token);

        var response = await client.SendRequestAsync(action, null, token);

        Assert.True(response.Success, $"{action} was refused: {response.Error?.Code}");
    }

    /// <summary>
    /// A key registered once cannot be swapped by the connection that registered it. Otherwise a
    /// phone in the wrong hands - which is an authenticated phone - could simply enroll its own.
    /// </summary>
    [Fact]
    public async Task AnAlreadyRegisteredOwnerKeyCannotBeReplacedRemotely()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAndAuthorizeAsync(token);

        using var attacker = new OwnerPhoneKey();

        var response = await client.SendRequestAsync(
            "session.enroll_key", new { public_key = attacker.PublicKey }, token);

        Assert.True(response.Success);
        Assert.False(OwnerSessionTesting.Read(response.Result).GetProperty("registered").GetBoolean());
    }

    /// <summary>A signature made by another key is not a signature by the owner's.</summary>
    [Fact]
    public async Task AWrongSignatureDoesNotAuthorizeTheSession()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAndAuthenticateAsync(token);

        Assert.True((await client.SendRequestAsync(
            "session.enroll_key", new { public_key = _ownerKey.PublicKey }, token)).Success);

        var challenge = await client.SendRequestAsync("session.challenge", null, token);
        var payload = OwnerSessionTesting.Read(challenge.Result);

        using var impostor = new OwnerPhoneKey();

        var response = await client.SendRequestAsync(
            "session.authorize",
            new
            {
                challenge_id = payload.GetProperty("challenge_id").GetString(),
                signature = impostor.Sign(
                    _identity.PcInstanceId, _phone.DeviceId, payload.GetProperty("challenge").GetString()!),
            },
            token);

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.AuthenticationFailed, response.Error!.Code);
    }

    [Fact]
    public async Task ADeviceCanListFilesInsideTheAllowedRoots()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAndAuthorizeAsync(token);

        var response = await client.SendRequestAsync("files.list", new { path = _allowedRoot.Path }, token);

        Assert.True(response.Success);
    }

    [Fact]
    public async Task TheFilesystemBoundaryStillAppliesToRemoteCallers()
    {
        // Policy grants files.list; it does not widen the allowed roots.
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAndAuthorizeAsync(token);

        var response = await client.SendRequestAsync("files.list", new { path = @"C:\Windows" }, token);

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.PathNotAllowed, response.Error!.Code);
    }

    [Fact]
    public async Task ADeviceCanCaptureTheScreen()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAndAuthorizeAsync(token);

        var response = await client.SendRequestAsync("screen.capture", null, token);

        Assert.True(response.Success);
    }

    [Theory]
    [InlineData("pairing.begin")]
    [InlineData("pairing.cancel")]
    [InlineData("device.unpair_primary")]
    [InlineData("device.get_primary")]
    public async Task DeviceManagementIsDeniedToRemoteCallers(string action)
    {
        // The phone controls the PC; it is not the authority that decides which phone is trusted.
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAndAuthenticateAsync(token);

        var response = await client.SendRequestAsync(action, null, token);

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.NotAuthorized, response.Error!.Code);
    }

    [Fact]
    public async Task ADeniedRequestDoesNotCloseTheConnectionOrRunAnyPartOfTheAction()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAndAuthenticateAsync(token);

        var denied = await client.SendRequestAsync("device.unpair_primary", null, token);
        Assert.Equal(ErrorCodes.NotAuthorized, denied.Error!.Code);

        // The phone is still paired: no part of the denied action executed.
        Assert.NotNull(_phones.Current);

        // And the session survives one refusal.
        Assert.True((await client.SendRequestAsync("system.get_info", null, token)).Success);
    }

    [Fact]
    public async Task ADeniedRequestLeaksNoInternalDetail()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAndAuthenticateAsync(token);

        var response = await client.SendRequestAsync("device.unpair_primary", null, token);

        Assert.Null(response.Error!.Details);
        Assert.DoesNotContain("at PcCore", response.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePolicyAllowlistIsExactlyTheDocumentedActions()
    {
        // Stated in full rather than counted. The list is the security boundary between "the phone
        // may look" and "the phone may act", and a capability joining it should require editing this
        // test - which is a moment to think about whether it belongs there.
        Assert.Equal(
            [
                "files.list",
                "files.stat",
                "input.begin",
                "input.button",
                "input.end",
                "input.key",
                "input.pointer",
                "input.release_all",
                "input.scroll",
                "input.text",
                "screen.capture",
                "screen.list_displays",
                "screen.stream.start",
                "screen.stream.stop",
                "security.arm",
                "security.disarm",
                "security.evidence.get",
                "security.get_state",
                "security.not_me",
                "security.sessions.get",
                "security.sessions.list",
                "security.this_was_me",
                "session.authorize",
                "session.challenge",
                "session.enroll_key",
                "session.lock",
                "session.resume",
                "session.status",
                "system.get_info",
                "system.list_processes",
            ],
            DefaultCapabilityPolicy.RemoteAllowedActions.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// And the much shorter list a phone may reach before its owner has authenticated on it. Adding
    /// anything here is adding it to what a stolen, unlocked phone can do.
    /// </summary>
    [Fact]
    public void TheUnauthorizedSubsetIsExactlyTheBootstrapAndTwoStatusReads()
    {
        Assert.Equal(
            [
                "security.get_state",
                "session.authorize",
                "session.challenge",
                "session.enroll_key",
                "session.lock",
                "session.resume",
                "session.status",
                "system.get_info",
            ],
            DefaultCapabilityPolicy.ActionsWithoutOwnerAuthorization.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DeviceTrustAndIndicatorControlStayOutOfReachOfThePhone()
    {
        // The two things a remote caller must never gain: deciding which phone is trusted, and
        // quieting the sign that it is there. v0.6 adds a great deal of remote power and neither of
        // these came with it.
        var phone = new CallerDescriptor("test", "c1", ActorType.Device, "device-1");
        var policy = new DefaultCapabilityPolicy();

        foreach (var action in new[]
                 {
                     "pairing.begin",
                     "pairing.cancel",
                     "device.get_primary",
                     "device.unpair_primary",
                     "indicator.get",
                     "indicator.set",

                     // v0.7: a stolen phone must not be able to destroy the recording of the theft,
                     // or quietly turn the camera off before it happens.
                     "security.session.delete",
                     "security.config.get",
                     "security.config.set",
                 })
        {
            Assert.False(policy.Evaluate(action, phone).Allowed, action);
        }
    }

    // --- Security state and events -----------------------------------------------------------------

    [Fact]
    public async Task ADeviceCanArmAndDisarmAndReceivesTheStateChangedEvent()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAndAuthorizeAsync(token);

        var armed = await client.SendRequestAsync("security.arm", null, token);
        Assert.True(armed.Success);
        Assert.Equal(SecurityState.Armed, _security.Current.State);

        var stateChanged = await client.WaitForEventAsync(
            SecurityEvents.StateChanged, TimeSpan.FromSeconds(10), token);

        Assert.NotNull(stateChanged);
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(stateChanged!.Payload, ProtocolCodec.Options);
        Assert.Equal("OWNER_PRESENT", payload.GetProperty("previous_state").GetString());
        Assert.Equal("ARMED", payload.GetProperty("current_state").GetString());

        Assert.True((await client.SendRequestAsync("security.disarm", null, token)).Success);
        Assert.Equal(SecurityState.OwnerPresent, _security.Current.State);
    }

    [Fact]
    public async Task AnIdempotentTransitionProducesNoDuplicateEvent()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAndAuthorizeAsync(token);

        await client.SendRequestAsync("security.arm", null, token);
        await client.WaitForEventAsync(SecurityEvents.StateChanged, TimeSpan.FromSeconds(10), token);

        // Re-arming changes nothing, so it must announce nothing.
        var again = await client.SendRequestAsync("security.arm", null, token);
        Assert.True(again.Success);

        await client.SendRequestAsync("security.disarm", null, token);
        await client.WaitForEventAsync(SecurityEvents.StateChanged, TimeSpan.FromSeconds(10), token);

        var transitions = client.ReceivedEvents
            .Where(e => e.EventName == SecurityEvents.StateChanged)
            .Select(e => System.Text.Json.JsonSerializer.SerializeToElement(e.Payload, ProtocolCodec.Options))
            .Select(p => (p.GetProperty("previous_state").GetString(), p.GetProperty("current_state").GetString()))
            .ToArray();

        Assert.Equal([("OWNER_PRESENT", "ARMED"), ("ARMED", "OWNER_PRESENT")], transitions);
    }

    [Fact]
    public async Task AnUnauthenticatedConnectionReceivesNoEventStream()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);
        await CaptureChallengeAsync(client, token);

        // Drive a real state change from outside while the connection sits unauthenticated.
        _security.RequestTransition(SecurityState.Armed, "test");

        var leaked = await client.WaitForEventAsync(
            SecurityEvents.StateChanged, TimeSpan.FromSeconds(2), token);

        Assert.Null(leaked);
    }

    // --- Sessions -----------------------------------------------------------------------------------

    [Fact]
    public async Task DisconnectingEndsTheSessionAndReconnectingRequiresFreshAuthentication()
    {
        var token = TestContext.Current.CancellationToken;
        var sessions = _services.GetRequiredService<RemoteSessionRegistry>();

        var client = await ConnectAndAuthenticateAsync(token);
        Assert.Equal(1, sessions.Count);

        await client.CloseAsync(token);
        await client.DisposeAsync();

        await WaitForSessionCountAsync(sessions, 0, token);

        // A new connection starts unauthenticated: its first request is refused until it proves
        // itself again, with a brand-new challenge.
        await using var reconnected = await ConnectAsync(token);
        await CaptureChallengeAsync(reconnected, token);

        var beforeAuth = await reconnected.SendRequestAsync("system.get_info", null, token);
        Assert.False(beforeAuth.Success);

        await using var fresh = await ConnectAndAuthenticateAsync(token);
        Assert.True((await fresh.SendRequestAsync("system.get_info", null, token)).Success);
    }

    [Fact]
    public async Task TheAuthenticatedIdentityIsBoundToItsOwnConnection()
    {
        var token = TestContext.Current.CancellationToken;

        await using var authenticated = await ConnectAndAuthenticateAsync(token);
        await using var anonymous = await ConnectAsync(token);
        await CaptureChallengeAsync(anonymous, token);

        // One connection authenticating grants the other nothing.
        Assert.True((await authenticated.SendRequestAsync("system.get_info", null, token)).Success);
        Assert.False((await anonymous.SendRequestAsync("system.get_info", null, token)).Success);
    }

    [Fact]
    public async Task SimultaneousSessionsAreBounded()
    {
        // MaxSessions is 2 in this harness. A third authentication evicts the oldest rather than
        // refusing the newest, so a reconnect race never locks the owner out.
        var token = TestContext.Current.CancellationToken;
        var sessions = _services.GetRequiredService<RemoteSessionRegistry>();

        await using var first = await ConnectAndAuthenticateAsync(token);
        await using var second = await ConnectAndAuthenticateAsync(token);
        Assert.Equal(2, sessions.Count);

        await using var third = await ConnectAndAuthenticateAsync(token);
        await WaitForSessionCountAsync(sessions, 2, token);

        // The newest connection is the usable one.
        Assert.True((await third.SendRequestAsync("system.get_info", null, token)).Success);
    }

    [Fact]
    public async Task MalformedMessagesAreRejectedWithoutDroppingTheSession()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAndAuthenticateAsync(token);

        var badVersion = await client.SendRequestAsync("system.get_info", null, token);
        Assert.True(badVersion.Success);

        var unknown = await client.SendRequestAsync("no.such.action", null, token);
        Assert.False(unknown.Success);
        Assert.Equal(ErrorCodes.UnknownAction, unknown.Error!.Code);

        // Still usable afterwards.
        Assert.True((await client.SendRequestAsync("system.get_info", null, token)).Success);
    }

    [Fact]
    public async Task ReAuthenticatingOnALiveSessionIsRefused()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAndAuthenticateAsync(token);

        var response = await client.SendRequestAsync(
            RemoteProtocol.AuthenticateAction,
            new { challenge_id = "x", device_id = _phone.DeviceId, proof = "x" },
            token);

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, response.Error!.Code);
        Assert.True((await client.SendRequestAsync("system.get_info", null, token)).Success);
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private sealed record IssuedChallenge(string ChallengeId, string Challenge);

    private static async Task<IssuedChallenge> CaptureChallengeAsync(RemotePhoneClient client, CancellationToken token)
    {
        var envelope = await client.WaitForEventAsync(RemoteProtocol.ChallengeEvent, TimeSpan.FromSeconds(10), token);
        Assert.NotNull(envelope);

        var payload = System.Text.Json.JsonSerializer.SerializeToElement(envelope!.Payload, ProtocolCodec.Options);
        return new IssuedChallenge(
            payload.GetProperty("challenge_id").GetString()!,
            payload.GetProperty("challenge").GetString()!);
    }

    private static async Task WaitForSessionCountAsync(
        RemoteSessionRegistry sessions,
        int expected,
        CancellationToken token)
    {
        // Cleanup happens when the server-side connection unwinds, which is moments after the
        // client's close frame.
        for (var i = 0; i < 100 && sessions.Count != expected; i++)
        {
            await Task.Delay(50, token);
        }

        Assert.Equal(expected, sessions.Count);
    }
}
