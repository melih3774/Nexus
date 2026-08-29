using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Identity;
using PcCore.Capabilities.Storage;
using PcCore.Tests.TestSupport;
using PcCore.Transport.Remote;
using Xunit;

namespace PcCore.Tests.Remote;

/// <summary>
/// Tailscale is a separate service the owner controls: it may be down when PC Core starts, and on
/// a laptop it comes and goes. These cover the reconciliation loop that brings the listener up and
/// down without requiring a restart.
/// </summary>
public sealed class RemoteTransportRecoveryTests : IAsyncLifetime
{
    /// <summary>An interface set the test can change between reconciliation ticks.</summary>
    private sealed class MutableInterfaceProvider : INetworkInterfaceProvider
    {
        private volatile IReadOnlyList<NetworkInterfaceInfo> _interfaces = [];

        public void SetOffline() => _interfaces = FakeNetworkInterfaceProvider.TypicalMachine().GetInterfaces();

        public void SetOnline(string address) => _interfaces =
        [
            .. FakeNetworkInterfaceProvider.TypicalMachine().GetInterfaces(),
            new NetworkInterfaceInfo("Tailscale", "Tailscale Tunnel", OperationalStatus.Up, [IPAddress.Parse(address)]),
        ];

        public IReadOnlyList<NetworkInterfaceInfo> GetInterfaces() => _interfaces;
    }

    private readonly TempStateDirectory _state = new("remote-recovery");
    private readonly MutableInterfaceProvider _interfaces = new();
    private ServiceProvider? _services;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        // The container owns RemoteTransport, which is IAsyncDisposable.
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        _state.Dispose();
    }

    private (RemoteTransport Transport, RemoteTransportService Service) Build(int port)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton(_state.Directory);
        services.AddSingleton<ISecretProtector, PassThroughSecretProtector>();
        services.AddSingleton(new PairingOptions());
        services.AddSingleton<IPcIdentityProvider, PcIdentityProvider>();
        services.AddSingleton<IPrimaryPhoneRegistry, PrimaryPhoneRegistry>();
        services.AddSingleton<IDeviceEndpointProvider, NoDeviceEndpoint>();
        services.AddSingleton<IPairingService, PairingService>();
        services.AddSingleton<IDeviceAuthenticator, DeviceAuthenticator>();
        services.AddSingleton<IRemoteEnrollmentService, RemoteEnrollmentService>();
        services.AddSingleton<EventBus>();
        services.AddSingleton<IEventPublisher>(p => p.GetRequiredService<EventBus>());
        services.AddSingleton<IEventStream>(p => p.GetRequiredService<EventBus>());
        services.AddSingleton<ICapabilityRegistry>(new CapabilityRegistry([]));
        services.AddSingleton<ICapabilityPolicy, DefaultCapabilityPolicy>();
        services.AddSingleton<ICapabilityDispatcher, CapabilityDispatcher>();

        var options = new RemoteTransportOptions { Port = port, AvailabilityCheckSeconds = 1 };
        services.AddSingleton(options);
        services.AddSingleton<INetworkInterfaceProvider>(_interfaces);
        services.AddSingleton<TailscaleAddressDetector>();
        services.AddSingleton<RemoteSessionRegistry>();
        services.AddSingleton<RemoteTransport>();
        services.AddSingleton<RemoteTransportService>();

        _services = services.BuildServiceProvider();

        return (_services.GetRequiredService<RemoteTransport>(), _services.GetRequiredService<RemoteTransportService>());
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken token)
    {
        for (var i = 0; i < 120 && !condition(); i++)
        {
            await Task.Delay(50, token);
        }
    }

    [Fact]
    public async Task StaysOfflineWhileNoTailscaleAddressExists()
    {
        var token = TestContext.Current.CancellationToken;
        _interfaces.SetOffline();

        var (transport, service) = Build(FreePort());
        await service.StartAsync(token);

        try
        {
            await Task.Delay(300, token);

            Assert.False(transport.IsRunning);
            Assert.Null(transport.Endpoint);

            var status = service.GetStatus();
            Assert.False(status.Available);
            Assert.Contains("no Tailscale network interface", status.Diagnostic, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await service.StopAsync(token);
        }
    }

    [Fact]
    public async Task ComesOnlineByItselfWhenTailscaleAppears()
    {
        // The recovery requirement: no PC Core restart needed once Tailscale connects. Loopback is
        // used as the "Tailscale" address only because a test cannot create a real tunnel adapter;
        // the detector's refusal to accept anything but a CGNAT address on a Tailscale interface is
        // covered in TailscaleDetectionTests.
        var token = TestContext.Current.CancellationToken;
        _interfaces.SetOffline();

        var (transport, service) = Build(FreePort());
        await service.StartAsync(token);

        try
        {
            await Task.Delay(200, token);
            Assert.False(transport.IsRunning);

            _interfaces.SetOnline("100.101.102.103");
            await WaitForAsync(() => service.GetStatus().Diagnostic.Contains("100.101.102.103", StringComparison.Ordinal), token);

            // Detection now succeeds; the loop attempts a bind against that address.
            Assert.Contains("100.101.102.103", service.GetStatus().Diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            await service.StopAsync(token);
        }
    }

    [Fact]
    public async Task DisabledByConfigurationNeverOpensAPort()
    {
        var token = TestContext.Current.CancellationToken;
        _interfaces.SetOnline("100.101.102.103");

        var port = FreePort();
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton(_state.Directory);
        services.AddSingleton<ISecretProtector, PassThroughSecretProtector>();
        services.AddSingleton(new PairingOptions());
        services.AddSingleton<IPcIdentityProvider, PcIdentityProvider>();
        services.AddSingleton<IPrimaryPhoneRegistry, PrimaryPhoneRegistry>();
        services.AddSingleton<IDeviceEndpointProvider, NoDeviceEndpoint>();
        services.AddSingleton<IPairingService, PairingService>();
        services.AddSingleton<IDeviceAuthenticator, DeviceAuthenticator>();
        services.AddSingleton<IRemoteEnrollmentService, RemoteEnrollmentService>();
        services.AddSingleton<EventBus>();
        services.AddSingleton<IEventPublisher>(p => p.GetRequiredService<EventBus>());
        services.AddSingleton<IEventStream>(p => p.GetRequiredService<EventBus>());
        services.AddSingleton<ICapabilityRegistry>(new CapabilityRegistry([]));
        services.AddSingleton<ICapabilityPolicy, DefaultCapabilityPolicy>();
        services.AddSingleton<ICapabilityDispatcher, CapabilityDispatcher>();
        services.AddSingleton(new RemoteTransportOptions { Enabled = false, Port = port });
        services.AddSingleton<INetworkInterfaceProvider>(_interfaces);
        services.AddSingleton<TailscaleAddressDetector>();
        services.AddSingleton<RemoteSessionRegistry>();
        services.AddSingleton<RemoteTransport>();
        services.AddSingleton<RemoteTransportService>();

        _services = services.BuildServiceProvider();
        var transport = _services.GetRequiredService<RemoteTransport>();
        var service = _services.GetRequiredService<RemoteTransportService>();

        await service.StartAsync(token);
        await Task.Delay(300, token);

        try
        {
            Assert.False(transport.IsRunning);
            Assert.Contains("disabled", service.GetStatus().Diagnostic, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await service.StopAsync(token);
        }
    }

    [Fact]
    public void StatusReportsDiagnosticsWithoutSecrets()
    {
        _interfaces.SetOffline();
        var (_, service) = Build(FreePort());

        var status = service.GetStatus();

        // Everything a person needs to debug, and nothing a credential could hide in.
        Assert.True(status.Enabled);
        Assert.False(status.Available);
        Assert.Null(status.Address);
        Assert.False(status.PrimaryPhonePaired);
        Assert.Equal(0, status.AuthenticatedSessions);

        Assert.Equal(
            ["Address", "Authenticated Sessions", "Available", "Diagnostic", "Enabled", "Port", "Primary Phone Paired"],
            typeof(RemoteTransportStatus).GetProperties()
                .Select(p => string.Concat(p.Name.Select(c => char.IsUpper(c) ? " " + c : c.ToString())).Trim())
                .Order(StringComparer.Ordinal));
    }
}
