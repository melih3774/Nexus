using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Identity;
using PcCore.Capabilities.Screen;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.System;
using PcCore.Capabilities.Update;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using PcCore.Transport.Remote;
using PcCore.Update;
using PcCore.Windows.Update;
using Xunit;

namespace PcCore.Tests.Update;

/// <summary>
/// The phone's update path over the real remote transport: what an old app is told when it
/// authenticates, what it may still reach, and what it may not.
/// </summary>
public sealed class UpdateTransportTests : IAsyncLifetime
{
    private readonly ReleaseFixture _release = new();
    private readonly byte[] _apk = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 7);

    private TempStateDirectory _state = null!;
    private ServiceProvider _services = null!;
    private RemoteTransport _transport = null!;
    private IPcIdentityProvider _identity = null!;
    private PairingCompletion _phone = null!;
    private Uri _uri = null!;

    public async ValueTask InitializeAsync()
    {
        _state = new TempStateDirectory("update-remote");
        _release.Install(ReleaseFixture.BaseFiles());
        _release.Publish(16, "1.3.1", ReleaseFixture.BaseFiles(), android: (16, _apk));

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
        services.AddSingleton<IScreenCapture, FakeScreenCapture>();

        services.AddSingleton<ICapabilityAction, GetSystemInfoAction>();
        services.AddSingleton<ICapabilityAction, CaptureScreenAction>();
        services.AddSingleton<ICapabilityAction, GetSecurityStateAction>();

        // The update surface, over a real release store.
        services.AddSingleton<IUpdateService>(new NexusUpdateService(_release.Store, _release.Paths, new FakeComponentProbe(), TestLog.For<NexusUpdateService>()));
        services.AddSingleton<ICapabilityAction, CheckForUpdatesAction>();
        services.AddSingleton<ICapabilityAction, GetAndroidPackageAction>();
        services.AddSingleton<ICapabilityAction, InstallUpdateAction>();

        services.AddSingleton<ICapabilityRegistry>(p => new CapabilityRegistry(p.GetServices<ICapabilityAction>()));
        services.AddSingleton<ICapabilityPolicy, DefaultCapabilityPolicy>();
        services.AddSingleton<ICapabilityDispatcher, CapabilityDispatcher>();

        var options = new RemoteTransportOptions { Port = FreePort(), MaxSessions = 4 };
        services.AddSingleton(options);
        services.AddSingleton<RemoteSessionRegistry>();
        services.AddRemoteControlDoubles();
        services.AddSingleton<RemoteStreamCoordinator>();

        _services = services.BuildServiceProvider();
        _identity = _services.GetRequiredService<IPcIdentityProvider>();

        var pairing = _services.GetRequiredService<IPairingService>();
        var session = pairing.Begin(replaceExisting: true);
        Assert.True(PairingPayloadV2.TryDecode(session.Payload, out var payload));
        _phone = pairing.Complete(payload!.SessionId, payload.PairingSecret, "Test Phone");

        _transport = new RemoteTransport(_services, options, _services.GetRequiredService<RemoteSessionRegistry>(), _services.GetRequiredService<ILoggerFactory>());
        await _transport.StartAsync(new RemoteEndpoint(IPAddress.Loopback, options.Port, "test loopback endpoint"));
        _uri = new Uri($"ws://127.0.0.1:{options.Port}{RemoteTransport.Path}");
    }

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        await _services.DisposeAsync();
        _state.Dispose();
        _release.Dispose();
    }

    private async Task<(RemotePhoneClient Client, JsonElement Session)> AuthenticateAsAppAsync(int? versionCode, CancellationToken token)
    {
        var client = new RemotePhoneClient(_phone.DeviceId, _phone.SharedSecret) { ClientVersionCode = versionCode };
        await client.ConnectAsync(_uri, token);

        var response = await client.AuthenticateAsync(_identity.PcInstanceId, token);
        Assert.True(response.Success);

        return (client, JsonSerializer.SerializeToElement(response.Result, ProtocolCodec.Options));
    }

    [Fact]
    public async Task AnAppTooOldForThisPcIsToldSoAtAuthenticationAndCanOnlyFetchTheUpdate()
    {
        var token = TestContext.Current.CancellationToken;
        var old = ClientCompatibility.MinimumAndroidVersionCode - 1;

        var (client, session) = await AuthenticateAsAppAsync(old, token);
        await using var _ = client;

        Assert.True(session.GetProperty("update_required").GetBoolean());
        Assert.Equal(ClientCompatibility.MinimumAndroidVersionCode, session.GetProperty("minimum_client_version_code").GetInt32());

        // Nothing but the update. Not even the two status reads a locked current app may make.
        foreach (var action in new[] { "system.get_info", "security.get_state", "screen.capture" })
        {
            var refused = await client.SendRequestAsync(action, null, token);
            Assert.False(refused.Success);
            Assert.Equal(ErrorCodes.UpdateRequired, refused.Error!.Code);
        }

        var check = await client.SendRequestAsync("update.check", new { client_version_code = old }, token);
        Assert.True(check.Success);

        var android = JsonSerializer.SerializeToElement(check.Result, ProtocolCodec.Options).GetProperty("android");
        Assert.True(android.GetProperty("required").GetBoolean());
        Assert.True(android.GetProperty("update_available").GetBoolean());
        Assert.Equal(16, android.GetProperty("version_code").GetInt32());

        // And the package itself, whole, over the same restricted session.
        using var apk = new MemoryStream();
        var offset = 0L;

        while (true)
        {
            var chunk = await client.SendRequestAsync("update.apk.get", new { version_code = 16, offset }, token);
            Assert.True(chunk.Success);

            var body = JsonSerializer.SerializeToElement(chunk.Result, ProtocolCodec.Options);
            var bytes = Convert.FromBase64String(body.GetProperty("data").GetString() ?? string.Empty);
            apk.Write(bytes);
            offset += bytes.Length;

            if (body.GetProperty("end_of_file").GetBoolean())
            {
                break;
            }
        }

        Assert.Equal(_apk, apk.ToArray());
    }

    [Fact]
    public async Task ACurrentAppIsNotFlaggedAndReachesTheUpdateBeforeItsOwnerAuthenticates()
    {
        var token = TestContext.Current.CancellationToken;

        var (client, session) = await AuthenticateAsAppAsync(ClientCompatibility.MinimumAndroidVersionCode + 1, token);
        await using var _ = client;

        Assert.False(session.GetProperty("update_required").GetBoolean());

        // Not owner-authenticated, and still able to check - the lock screen shows the update.
        Assert.True((await client.SendRequestAsync("update.check", null, token)).Success);

        // While the screen stays as out of reach as it always was.
        var screen = await client.SendRequestAsync("screen.capture", null, token);
        Assert.Equal(ErrorCodes.NotAuthorized, screen.Error!.Code);
    }

    [Fact]
    public async Task AnAppFromBeforeTheUpdatePathIsTreatedAsTheLastBuildBeforeIt()
    {
        var token = TestContext.Current.CancellationToken;

        var (client, session) = await AuthenticateAsAppAsync(null, token);
        await using var _ = client;

        Assert.Equal(
            ClientCompatibility.RequiresUpdate(null),
            session.GetProperty("update_required").GetBoolean());
    }

    [Fact]
    public async Task ThePhoneCannotStartAnUpdateOfThePc()
    {
        var token = TestContext.Current.CancellationToken;

        var (client, _) = await AuthenticateAsAppAsync(ClientCompatibility.MinimumAndroidVersionCode + 1, token);
        await using var __ = client;

        var install = await client.SendRequestAsync("update.install", null, token);

        Assert.False(install.Success);
        Assert.Equal(ErrorCodes.NotAuthorized, install.Error!.Code);
        Assert.Null(_release.Paths.ReadStatus());
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
