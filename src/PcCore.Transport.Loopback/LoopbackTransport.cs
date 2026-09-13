using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Transport.Loopback;

/// <summary>
/// The v0.1 development transport: Kestrel, bound to loopback only.
/// <para>
/// <b>Why HTTP and WebSocket together.</b> <c>POST /rpc</c> gives a request/response shape that is
/// trivial to drive from <c>curl</c> or any client. But the protocol has three message types, and
/// HTTP cannot express the third â€” so <c>/ws</c> carries the same envelopes plus server-pushed
/// events. That is the channel a future Android UI and the future <c>ai.*</c> progress events use.
/// </para>
/// <para>
/// A remote transport is a <em>different project</em> implementing the same handoff to
/// <see cref="ICapabilityDispatcher"/>. Nothing below this layer changes when it arrives.
/// </para>
/// </summary>
public static class LoopbackTransport
{
    public const string TransportName = "loopback";

    /// <summary>
    /// Binds Kestrel to the loopback interface and nowhere else.
    /// <para>
    /// The address is hard-coded rather than configured on purpose: an "allowed bind address"
    /// setting is exactly how a localhost-only service ends up on the LAN. <c>ASPNETCORE_URLS</c>
    /// is cleared for the same reason â€” an environment variable must not be able to widen the
    /// binding.
    /// </para>
    /// </summary>
    public static void ConfigureLoopbackKestrel(this WebApplicationBuilder builder, LoopbackTransportOptions options)
    {
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // ListenLocalhost covers 127.0.0.1 and [::1] and refuses everything else.
            kestrel.ListenLocalhost(options.Port);

            kestrel.Limits.MaxRequestBodySize = options.MaxRequestBytes;
            kestrel.AddServerHeader = false;
        });
    }

    public static IServiceCollection AddLoopbackTransport(
        this IServiceCollection services,
        LoopbackTransportOptions options,
        SessionToken token)
    {
        services.AddSingleton(options);
        services.AddSingleton(token);
        services.AddSingleton<LoopbackGuard>();
        return services;
    }

    /// <summary>Maps the transport endpoints. <paramref name="testPageHtml"/> may be null.</summary>
    public static void MapLoopbackTransport(this WebApplication app, string? testPageHtml)
    {
        var options = app.Services.GetRequiredService<LoopbackTransportOptions>();
        var guard = app.Services.GetRequiredService<LoopbackGuard>();
        var dispatcher = app.Services.GetRequiredService<ICapabilityDispatcher>();
        var events = app.Services.GetRequiredService<IEventStream>();
        var publisher = app.Services.GetRequiredService<IEventPublisher>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PcCore.Transport.Loopback");

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        // Liveness. No auth, and deliberately discloses nothing beyond "a PC Core is here".
        app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));

        // The developer test page. Not token-protected, because the page has to bootstrap before
        // it can know the token â€” the Host/Origin checks are what keep a hostile site out, and
        // the browser's same-origin policy stops it reading this response. See SPEC.md 7.3.
        app.MapGet("/", (HttpContext context) =>
        {
            var denied = guard.Validate(context, requireToken: false);
            if (denied is not null)
            {
                return Results.Json(new { error = denied }, statusCode: StatusCodes.Status403Forbidden);
            }

            return testPageHtml is null
                ? Results.NotFound()
                : Results.Content(testPageHtml, "text/html; charset=utf-8");
        });

        app.MapPost("/rpc", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            var denied = guard.Validate(context, requireToken: true);
            if (denied is not null)
            {
                return Results.Json(
                    ResponseEnvelope.Fail(string.Empty, null, denied),
                    ProtocolCodec.Options,
                    statusCode: StatusCodes.Status403Forbidden);
            }

            string body;
            using (var reader = new StreamReader(context.Request.Body))
            {
                body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            var decoded = ProtocolCodec.DecodeRequest(body);

            var response = decoded.IsSuccess
                ? await dispatcher
                    .DispatchAsync(decoded.Request, CallerFor(context, "http"), cancellationToken)
                    .ConfigureAwait(false)
                : ResponseEnvelope.Fail(decoded.Id ?? string.Empty, null, decoded.Error);

            // The transport always answers 200: a well-formed protocol failure is a successful
            // exchange. Only transport-level refusals (403 above) use an HTTP error status.
            return Results.Json(response, ProtocolCodec.Options);
        });

        app.MapGet("/ws", async (HttpContext context, CancellationToken requestAborted) =>
        {
            // Ends when PC Core stops, not only when the browser closes the page. A graceful shutdown
            // waits for every open request, so an open control panel used to hold PC Core up until the
            // host's thirty-second shutdown timeout cut it off.
            using var connectionEnds = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, app.Lifetime.ApplicationStopping);
            var cancellationToken = connectionEnds.Token;

            if (!context.WebSockets.IsWebSocketRequest)
            {
                return Results.BadRequest(new { error = "Expected a WebSocket upgrade request." });
            }

            var denied = guard.Validate(context, requireToken: true);
            if (denied is not null)
            {
                return Results.Json(new { error = denied }, statusCode: StatusCodes.Status403Forbidden);
            }

            var caller = CallerFor(context, "ws");

            // Echo back only the protocol subprotocol, never the token-bearing one.
            var acceptSubprotocol = context.WebSockets.WebSocketRequestedProtocols
                .Contains(LoopbackGuard.Subprotocol)
                ? LoopbackGuard.Subprotocol
                : null;

            using var socket = await context.WebSockets
                .AcceptWebSocketAsync(new WebSocketAcceptContext { SubProtocol = acceptSubprotocol })
                .ConfigureAwait(false);

            logger.LogInformation("WebSocket client connected: {Caller}", caller);

            await using var connection = new WebSocketConnection(
                socket,
                dispatcher,
                caller,
                options.MaxRequestBytes,
                logger);

            // Subscribe first, then publish: the greeting must reach the client that triggered it.
            using var subscription = connection.SubscribeTo(events, cancellationToken);

            publisher.Publish("core.connected", new { transport = TransportName, connection_id = caller.ConnectionId });

            await connection.RunAsync(cancellationToken).ConfigureAwait(false);

            logger.LogInformation("WebSocket client disconnected: {Caller}", caller);
            return Results.Empty;
        });
    }

    /// <summary>
    /// Builds the descriptor the capability layer sees. It carries a transport <em>name</em> and an
    /// opaque connection id â€” never an address, socket, or header â€” so no capability can behave
    /// differently based on how the caller reached us.
    /// </summary>
    private static CallerDescriptor CallerFor(HttpContext context, string kind) =>
        // ActorType.Local, and DeviceId deliberately null: this transport authenticates a session
        // token, not a device, so there is no device identity to claim.
        new($"{TransportName}-{kind}", context.Connection.Id, ActorType.Local);

    /// <summary>Loopback endpoint description, for startup logging.</summary>
    public static string DescribeEndpoint(LoopbackTransportOptions options) =>
        $"http://{IPAddress.Loopback}:{options.Port}";
}
