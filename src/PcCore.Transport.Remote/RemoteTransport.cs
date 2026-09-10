using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Identity;
using PcCore.Capabilities.Security;

namespace PcCore.Transport.Remote;

/// <summary>
/// Tells the pairing service where a phone should connect back to.
/// <para>
/// It reports the address the transport is <em>actually</em> serving, not the one configuration
/// hopes for: <see cref="RemoteTransport.Endpoint"/> is null unless a listener is open on a vetted
/// Tailscale address. A pairing code therefore never promises an endpoint that does not exist.
/// </para>
/// </summary>
public sealed class RemoteTransportEndpointProvider : IDeviceEndpointProvider
{
    private readonly RemoteTransport _transport;

    public RemoteTransportEndpointProvider(RemoteTransport transport) => _transport = transport;

    public DeviceEndpoint? Current =>
        _transport.Endpoint is { } endpoint
            ? new DeviceEndpoint(endpoint.Address.ToString(), endpoint.Port)
            : null;
}

/// <summary>
/// The authenticated remote transport: a WebSocket listener bound to one Tailscale address.
/// <para>
/// <b>Why WebSocket.</b> The protocol already has three message types, and the third — events —
/// cannot be expressed over request/response HTTP. The phone needs a persistent bidirectional
/// connection anyway: it authenticates once per connection rather than per request, and it must
/// receive <c>security.state_changed</c> without polling. A later screen-streaming milestone wants
/// the same channel. No custom binary framing: the existing typed envelopes and codec are reused
/// verbatim, so there is exactly one serialization model shared by both transports.
/// </para>
/// <para>
/// <b>Why no TLS.</b> Every byte already travels inside a WireGuard tunnel that Tailscale
/// authenticates and encrypts end to end, and PC Core independently proves the peer holds the
/// Primary Phone secret before accepting a single command. Adding a certificate on top would mean
/// certificate generation, trust distribution, renewal, and a pinning story in the Android client
/// — real lifecycle complexity buying a second encryption layer inside the first. If this transport
/// is ever exposed outside a tailnet that calculation changes completely, which is why the listener
/// cannot bind anything but a Tailscale address.
/// </para>
/// </summary>
public sealed class RemoteTransport : IAsyncDisposable
{
    public const string TransportName = "tailscale-remote";
    public const string Path = "/nexus";

    private readonly IServiceProvider _services;
    private readonly RemoteTransportOptions _options;
    private readonly RemoteSessionRegistry _sessions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RemoteTransport> _logger;

    private WebApplication? _app;

    public RemoteTransport(
        IServiceProvider services,
        RemoteTransportOptions options,
        RemoteSessionRegistry sessions,
        ILoggerFactory loggerFactory)
    {
        _services = services;
        _options = options;
        _sessions = sessions;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RemoteTransport>();
    }

    public RemoteEndpoint? Endpoint { get; private set; }

    public bool IsRunning => _app is not null;

    /// <summary>
    /// Starts listening on exactly one address.
    /// <para>
    /// <see cref="RemoteEndpoint"/> can only be produced by <see cref="TailscaleAddressDetector"/>,
    /// so there is no code path — configuration, environment variable, or otherwise — that reaches
    /// <c>Listen</c> with a LAN address or a wildcard. <c>ASPNETCORE_URLS</c> is cleared for the
    /// same reason it is in the loopback transport.
    /// </para>
    /// </summary>
    public async Task StartAsync(RemoteEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // One address, one port. Never IPAddress.Any, never IPAddress.Loopback in production.
            kestrel.Listen(endpoint.Address, endpoint.Port);
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 64 * 1024;
        });

        var app = builder.Build();

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        var dispatcher = _services.GetRequiredService<ICapabilityDispatcher>();
        var authenticator = _services.GetRequiredService<IDeviceAuthenticator>();
        var enrollment = _services.GetRequiredService<IRemoteEnrollmentService>();
        var streams = _services.GetRequiredService<RemoteStreamCoordinator>();
        var input = _services.GetRequiredService<PcCore.Capabilities.Input.IInputService>();
        var phones = _services.GetRequiredService<IPrimaryPhoneRegistry>();
        var ownerSessions = _services.GetRequiredService<IOwnerSessionService>();
        var identity = _services.GetRequiredService<IPcIdentityProvider>();
        var events = _services.GetRequiredService<IEventStream>();
        var connectionLogger = _loggerFactory.CreateLogger("PcCore.Transport.Remote.Connection");

        // Resolved optionally: a host composed without the filesystem capabilities simply has no
        // roots to offer, and an authenticated session with an empty list is correct there.
        IReadOnlyList<string> allowedRoots = _services.GetService<AllowedRoots>()?.Roots ?? [];

        // Liveness only. Deliberately says nothing about pairing state or sessions: this port is
        // reachable by anything on the tailnet, and an unauthenticated peer learns only that
        // something is listening.
        app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));

        app.MapGet(Path, async (HttpContext context, CancellationToken requestAborted) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                return Results.BadRequest(new { error = "Expected a WebSocket upgrade request." });
            }

            var subprotocol = context.WebSockets.WebSocketRequestedProtocols.Contains(RemoteProtocol.Subprotocol)
                ? RemoteProtocol.Subprotocol
                : null;

            using var socket = await context.WebSockets
                .AcceptWebSocketAsync(new WebSocketAcceptContext { SubProtocol = subprotocol })
                .ConfigureAwait(false);

            var connectionId = context.Connection.Id;
            _logger.LogInformation("Remote connection {ConnectionId} opened.", connectionId);

            await using var connection = new RemoteConnection(
                socket,
                connectionId,
                dispatcher,
                authenticator,
                enrollment,
                phones,
                identity,
                allowedRoots,
                _sessions,
                streams,
                input,
                ownerSessions,
                _options,
                connectionLogger);

            await connection.RunAsync(events, requestAborted).ConfigureAwait(false);

            _logger.LogInformation("Remote connection {ConnectionId} closed.", connectionId);
            return Results.Empty;
        });

        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        _app = app;
        Endpoint = endpoint;

        _logger.LogInformation(
            "Remote transport listening on ws://{Endpoint}{Path} ({Source}). Reachable only over Tailscale.",
            endpoint,
            Path,
            endpoint.Source);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null)
        {
            return;
        }

        var app = _app;
        _app = null;
        Endpoint = null;

        try
        {
            await app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Shutting down anyway.
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }

        _logger.LogInformation("Remote transport stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

/// <summary>What the local diagnostics readout shows. Contains no secret and no challenge material.</summary>
public sealed record RemoteTransportStatus(
    bool Enabled,
    bool Available,
    string? Address,
    int Port,
    string Diagnostic,
    bool PrimaryPhonePaired,
    int AuthenticatedSessions);

/// <summary>
/// Keeps the remote transport in step with Tailscale.
/// <para>
/// Tailscale may not be up when PC Core starts — it is a separate service the owner controls, and
/// on a laptop it comes and goes with the network. Rather than requiring a restart, this re-checks
/// on a modest timer and brings the listener up or down accordingly. It is a poll for an interface
/// appearing, not a network monitoring subsystem: one timer, one detector call, no eventing.
/// </para>
/// </summary>
public sealed class RemoteTransportService : BackgroundService
{
    private readonly RemoteTransport _transport;
    private readonly TailscaleAddressDetector _detector;
    private readonly RemoteTransportOptions _options;
    private readonly RemoteSessionRegistry _sessions;
    private readonly IPrimaryPhoneRegistry _phones;
    private readonly ILogger<RemoteTransportService> _logger;

    private string _diagnostic = "not started";
    private string? _lastLoggedDiagnostic;

    public RemoteTransportService(
        RemoteTransport transport,
        TailscaleAddressDetector detector,
        RemoteTransportOptions options,
        RemoteSessionRegistry sessions,
        IPrimaryPhoneRegistry phones,
        ILogger<RemoteTransportService> logger)
    {
        _transport = transport;
        _detector = detector;
        _options = options;
        _sessions = sessions;
        _phones = phones;
        _logger = logger;
    }

    public RemoteTransportStatus GetStatus() => new(
        _options.Enabled,
        _transport.IsRunning,
        _transport.Endpoint?.Address.ToString(),
        _options.Port,
        _diagnostic,
        _phones.Current is not null,
        _sessions.Count);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _diagnostic = "disabled by configuration (PcCore:RemoteTransport:Enabled=false)";
            _logger.LogInformation("Remote transport is {Diagnostic}.", _diagnostic);
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.AvailabilityCheckSeconds));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A transient bind failure or an interface disappearing mid-start must not kill the
                // loop; the next tick simply tries again.
                _logger.LogWarning(ex, "Remote transport reconciliation failed; will retry.");
                await SafeStopAsync().ConfigureAwait(false);
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false));

        await SafeStopAsync().ConfigureAwait(false);
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var detection = _detector.Detect(_options.Port);
        _diagnostic = detection.Diagnostic;

        if (!detection.Found)
        {
            if (_transport.IsRunning)
            {
                // The Tailscale interface went away. Stop listening rather than keep a socket on an
                // address that may no longer mean what it did.
                _logger.LogWarning("Tailscale address lost ({Diagnostic}); stopping remote transport.", detection.Diagnostic);
                await _transport.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (_lastLoggedDiagnostic != detection.Diagnostic)
            {
                // Logged once per distinct reason, not once per tick.
                _logger.LogInformation("Remote transport unavailable: {Diagnostic}", detection.Diagnostic);
            }

            _lastLoggedDiagnostic = detection.Diagnostic;
            return;
        }

        var endpoint = detection.Endpoint!;

        if (_transport.IsRunning)
        {
            if (_transport.Endpoint is not null && _transport.Endpoint.Address.Equals(endpoint.Address))
            {
                return;
            }

            _logger.LogInformation("Tailscale address changed; rebinding remote transport.");
            await _transport.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        await _transport.StartAsync(endpoint, cancellationToken).ConfigureAwait(false);
        _lastLoggedDiagnostic = detection.Diagnostic;
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task SafeStopAsync()
    {
        try
        {
            await _transport.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Remote transport stop failed during shutdown.");
        }
    }
}
