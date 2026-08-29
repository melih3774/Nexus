using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PcCore.Protocol;

namespace PcCore.Transport.Loopback;

/// <summary>
/// The transport's only security policy: prove the request really came from this machine's
/// loopback interface, and that the caller knows the session token.
/// <para>
/// The <c>Host</c> and <c>Origin</c> checks exist to stop <b>DNS rebinding</b>. Without them, a
/// hostile site can point a hostname it controls at <c>127.0.0.1</c> and have the victim's browser
/// send same-origin requests to PC Core — the attack that turns "it only listens on localhost"
/// into "any website can drive the user's PC".
/// </para>
/// </summary>
public sealed class LoopbackGuard
{
    private readonly SessionToken _token;
    private readonly LoopbackTransportOptions _options;
    private readonly ILogger<LoopbackGuard> _logger;
    private readonly HashSet<string> _allowedHosts;
    private readonly HashSet<string> _allowedOrigins;

    public LoopbackGuard(SessionToken token, LoopbackTransportOptions options, ILogger<LoopbackGuard> logger)
    {
        _token = token;
        _options = options;
        _logger = logger;

        // Only literal loopback authorities. A hostname that merely resolves to 127.0.0.1 is
        // exactly what a rebinding attack supplies, so names other than "localhost" are refused.
        _allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"127.0.0.1:{options.Port}",
            $"localhost:{options.Port}",
            $"[::1]:{options.Port}",
        };

        _allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"http://127.0.0.1:{options.Port}",
            $"http://localhost:{options.Port}",
            $"http://[::1]:{options.Port}",
        };
    }

    /// <summary>Validates transport-level access. Returns null when the request may proceed.</summary>
    public ProtocolError? Validate(HttpContext context, bool requireToken)
    {
        // Belt and braces: Kestrel is bound to loopback, but if that ever changes by accident this
        // check still refuses a remote peer rather than serving it.
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && !System.Net.IPAddress.IsLoopback(remote))
        {
            _logger.LogWarning("Rejected a non-loopback connection.");
            return new ProtocolError(ErrorCodes.AccessDenied, "Only loopback connections are accepted.");
        }

        var host = context.Request.Headers.Host.ToString();
        if (!_allowedHosts.Contains(host))
        {
            _logger.LogWarning("Rejected request with unexpected Host header.");
            return new ProtocolError(ErrorCodes.AccessDenied, "Unexpected Host header.");
        }

        // An Origin is only present on browser-issued requests. When present it must be ours.
        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && !_allowedOrigins.Contains(origin))
        {
            _logger.LogWarning("Rejected request with cross-origin Origin header.");
            return new ProtocolError(ErrorCodes.AccessDenied, "Cross-origin requests are not accepted.");
        }

        if (requireToken && _options.RequireToken && !_token.Matches(ExtractToken(context)))
        {
            _logger.LogWarning("Rejected request with a missing or invalid session token.");
            return new ProtocolError(ErrorCodes.AccessDenied, "Missing or invalid session token.");
        }

        return null;
    }

    /// <summary>
    /// Browsers cannot set custom headers on a WebSocket handshake, so the token may also arrive
    /// as a <c>Sec-WebSocket-Protocol</c> entry — the standard workaround, and better than putting
    /// a secret in a URL query string where it lands in logs and history.
    /// </summary>
    public const string TokenSubprotocolPrefix = "pccore.token.";

    public const string Subprotocol = "pccore.v1";

    private static string? ExtractToken(HttpContext context)
    {
        var header = context.Request.Headers[ProtocolConstants.TokenHeader].ToString();
        if (!string.IsNullOrEmpty(header))
        {
            return header;
        }

        foreach (var requested in context.WebSockets.WebSocketRequestedProtocols)
        {
            if (requested.StartsWith(TokenSubprotocolPrefix, StringComparison.Ordinal))
            {
                return requested[TokenSubprotocolPrefix.Length..];
            }
        }

        return null;
    }
}
