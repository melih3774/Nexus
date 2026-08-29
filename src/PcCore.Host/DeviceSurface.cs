using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Identity;
using PcCore.Protocol;
using PcCore.Transport.Loopback;
using QRCoder;

namespace PcCore.Host;

/// <summary>
/// The device-facing HTTP surface: pairing completion and authentication.
/// <para>
/// These are deliberately <b>not</b> capabilities. A phone completing pairing, or proving it holds
/// the shared secret, has not authenticated yet — routing it through the same dispatcher that
/// serves the owner's trusted local calls would mean treating an unauthenticated party as an
/// ordinary caller. Owner actions (<c>pairing.begin</c>, <c>device.unpair_primary</c>) remain
/// capabilities; device actions live here.
/// </para>
/// <para>
/// <b>v0.3 scope.</b> These endpoints are served by the loopback transport and still require the
/// local session token, because the only client that exists is the development simulator on this
/// machine. When the remote transport arrives it will expose the same two operations without the
/// loopback token — a phone authenticates with its shared secret instead — and this file is the
/// contract it will implement.
/// </para>
/// </summary>
public static class DeviceSurface
{
    private sealed record PairRequest(string? SessionId, string? PairingSecret, string? DisplayName);

    private sealed record ChallengeVerifyRequest(string? ChallengeId, string? DeviceId, string? Proof);

    public static void MapDeviceSurface(this WebApplication app)
    {
        var guard = app.Services.GetRequiredService<LoopbackGuard>();
        var pairing = app.Services.GetRequiredService<IPairingService>();
        var authenticator = app.Services.GetRequiredService<IDeviceAuthenticator>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PcCore.Host.DeviceSurface");

        // --- Pairing QR (owner-facing, part of the local dev UI) -------------------------------
        app.MapGet("/pairing/qr.png", (HttpContext context) =>
        {
            var denied = guard.Validate(context, requireToken: true);
            if (denied is not null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var session = pairing.Active;
            if (session is null)
            {
                return Results.NotFound();
            }

            // The QR encodes the pairing payload and nothing else: no hostname, user name,
            // address, or hardware detail is in the image.
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(session.Payload, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(6);

            // Never cached: a pairing code is short-lived and single-use.
            context.Response.Headers.CacheControl = "no-store";
            return Results.File(png, "image/png");
        });

        // --- Pairing completion (device-facing) -------------------------------------------------
        app.MapPost("/device/pair", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            var denied = guard.Validate(context, requireToken: true);
            if (denied is not null)
            {
                return Results.Json(Failure(denied), ProtocolCodec.Options, statusCode: StatusCodes.Status403Forbidden);
            }

            PairRequest? request;
            try
            {
                request = await context.Request
                    .ReadFromJsonAsync<PairRequest>(ProtocolCodec.Options, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or BadHttpRequestException)
            {
                return Results.Json(
                    Failure(new ProtocolError(ErrorCodes.InvalidRequest, "Request body was not valid JSON.")),
                    ProtocolCodec.Options);
            }

            try
            {
                var completion = pairing.Complete(
                    request?.SessionId ?? string.Empty,
                    request?.PairingSecret ?? string.Empty,
                    request?.DisplayName);

                // This response carries the permanent shared secret. It is the one and only time
                // it is transmitted, and it is never logged here or anywhere else.
                context.Response.Headers.CacheControl = "no-store";
                return Results.Json(Success(completion), ProtocolCodec.Options);
            }
            catch (CapabilityException ex)
            {
                return Results.Json(Failure(new ProtocolError(ex.Code, ex.Message)), ProtocolCodec.Options);
            }
        });

        // --- Authentication challenge (device-facing) -------------------------------------------
        app.MapPost("/device/auth/challenge", (HttpContext context) =>
        {
            var denied = guard.Validate(context, requireToken: true);
            if (denied is not null)
            {
                return Results.Json(Failure(denied), ProtocolCodec.Options, statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Json(Success(authenticator.IssueChallenge()), ProtocolCodec.Options);
            }
            catch (CapabilityException ex)
            {
                return Results.Json(Failure(new ProtocolError(ex.Code, ex.Message)), ProtocolCodec.Options);
            }
        });

        // --- Authentication verification (device-facing) ----------------------------------------
        app.MapPost("/device/auth/verify", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            var denied = guard.Validate(context, requireToken: true);
            if (denied is not null)
            {
                return Results.Json(Failure(denied), ProtocolCodec.Options, statusCode: StatusCodes.Status403Forbidden);
            }

            ChallengeVerifyRequest? request;
            try
            {
                request = await context.Request
                    .ReadFromJsonAsync<ChallengeVerifyRequest>(ProtocolCodec.Options, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or BadHttpRequestException)
            {
                return Results.Json(
                    Failure(new ProtocolError(ErrorCodes.InvalidRequest, "Request body was not valid JSON.")),
                    ProtocolCodec.Options);
            }

            try
            {
                var device = authenticator.Verify(
                    request?.ChallengeId ?? string.Empty,
                    request?.DeviceId ?? string.Empty,
                    request?.Proof ?? string.Empty);

                // The caller identity is built here, from the verification result — never from
                // anything in the request. A body claiming actor_type DEVICE has no effect
                // whatsoever; only a valid HMAC over the issued challenge reaches this line.
                var caller = device.ToCaller("loopback-device", context.Connection.Id);

                logger.LogInformation("Device surface authenticated {Caller}.", caller);

                return Results.Json(
                    Success(new
                    {
                        Authenticated = true,
                        ActorType = caller.ActorType,
                        DeviceId = caller.DeviceId,
                        AuthenticatedAtUtc = device.AuthenticatedAtUtc,
                    }),
                    ProtocolCodec.Options);
            }
            catch (CapabilityException ex)
            {
                return Results.Json(Failure(new ProtocolError(ex.Code, ex.Message)), ProtocolCodec.Options);
            }
        });
    }

    private static object Success(object result) => new { Success = true, Result = result, Error = (ProtocolError?)null };

    private static object Failure(ProtocolError error) => new { Success = false, Result = (object?)null, Error = error };
}
