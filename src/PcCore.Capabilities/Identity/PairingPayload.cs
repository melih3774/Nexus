using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PcCore.Protocol;

namespace PcCore.Capabilities.Identity;

/// <summary>
/// Where a paired phone should connect back to.
/// <para>
/// Supplied by the remote transport, which is the only component that knows whether a safe address
/// is actually bound. It fails closed: when no Tailscale address exists this is
/// <see langword="null"/>, and the pairing code carries no endpoint rather than a guess.
/// </para>
/// </summary>
public sealed record DeviceEndpoint(string Host, int Port)
{
    public override string ToString() => $"{Host}:{Port}";
}

public interface IDeviceEndpointProvider
{
    /// <summary>The endpoint currently bound, or <see langword="null"/> when none is.</summary>
    DeviceEndpoint? Current { get; }
}

/// <summary>Used when no remote transport is composed in — pairing codes then carry no endpoint.</summary>
public sealed class NoDeviceEndpoint : IDeviceEndpointProvider
{
    public DeviceEndpoint? Current => null;
}

/// <summary>
/// The v1 pairing code: <c>NEXUS1.&lt;base64url(compact JSON)&gt;</c>.
/// <para>
/// <b>Superseded by <see cref="PairingPayloadV2"/>.</b> It is retained because it is still exactly
/// what the loopback development simulator needs: that client runs on this machine, already knows
/// where PC Core is, and pairs over the local device surface, so an endpoint in the payload would
/// be noise. A physical phone cannot use it — it has no way to learn the PC's address — which is
/// why v2 exists rather than v1 quietly gaining fields.
/// </para>
/// </summary>
public static class PairingPayload
{
    public const string Prefix = "NEXUS1.";
    public const int Version = 1;

    public static string Encode(int version, string pcInstanceId, string sessionId, string pairingSecret)
    {
        // Short keys: the payload is scanned, not read.
        var json = $$"""{"v":{{version}},"pc":"{{pcInstanceId}}","s":"{{sessionId}}","k":"{{pairingSecret}}"}""";
        return Prefix + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Decodes a payload. Used by tests and by the development phone simulator.</summary>
    public static bool TryDecode(
        string? payload,
        out int version,
        out string pcInstanceId,
        out string sessionId,
        out string pairingSecret)
    {
        version = 0;
        pcInstanceId = string.Empty;
        sessionId = string.Empty;
        pairingSecret = string.Empty;

        if (string.IsNullOrWhiteSpace(payload) || !payload.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(payload.AsSpan(Prefix.Length)));
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            version = root.GetProperty("v").GetInt32();
            pcInstanceId = root.GetProperty("pc").GetString() ?? string.Empty;
            sessionId = root.GetProperty("s").GetString() ?? string.Empty;
            pairingSecret = root.GetProperty("k").GetString() ?? string.Empty;

            return pcInstanceId.Length > 0 && sessionId.Length > 0 && pairingSecret.Length > 0;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>
/// The v2 pairing code: <c>NEXUS2.&lt;base64url(compact JSON)&gt;</c>.
/// <para>
/// <b>Why a new version rather than extra fields on v1.</b> A phone that scans a v1 code and a
/// phone that scans a v2 code are doing different things — v1 says "pair with this installation",
/// v2 says "pair with this installation, at this address, speaking this protocol version". Growing
/// v1 silently would leave a scanner unable to tell whether a missing endpoint means the PC has no
/// Tailscale address or that the code predates the field. The prefix answers that before a single
/// byte is parsed.
/// </para>
/// <para>
/// <b>What is deliberately absent.</b> No Windows user name, no host name, no MAC address, no
/// hardware serial, and above all no permanent Primary Phone secret. The only credential here is
/// <see cref="PairingSecret"/>, which is single-use, expires in minutes, and buys exactly one
/// thing: the right to complete this one enrollment. The permanent secret is issued only in the
/// enrollment response.
/// </para>
/// </summary>
public sealed record PairingPayloadV2
{
    public const string Prefix = "NEXUS2.";
    public const int Version = 2;

    public required string PcInstanceId { get; init; }

    public required string SessionId { get; init; }

    /// <summary>Short-lived, single-use, 256 bits. Not a permanent credential.</summary>
    public required string PairingSecret { get; init; }

    /// <summary>
    /// The PC's Tailscale address, or <see langword="null"/> when the remote transport has no safe
    /// address bound. A code without a host is still valid for the local development flow; a phone
    /// rejects it with a specific message rather than a generic failure.
    /// </summary>
    public string? Host { get; init; }

    public int Port { get; init; }

    /// <summary>The protocol version the phone must speak. A mismatch is a clear error, not a guess.</summary>
    public int ProtocolVersion { get; init; } = ProtocolConstants.CurrentVersion;

    public bool HasEndpoint => !string.IsNullOrWhiteSpace(Host) && Port > 0;

    public string Encode() =>
        Prefix + Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(
            new Wire
            {
                Version = Version,
                PcInstanceId = PcInstanceId,
                SessionId = SessionId,
                PairingSecret = PairingSecret,
                Host = Host,
                Port = Port,
                ProtocolVersion = ProtocolVersion,
            },
            WireOptions));

    /// <summary>
    /// Decodes a v2 code. Returns false for anything else — a v1 code, a truncated scan, or a
    /// payload whose version field names something this build does not implement.
    /// </summary>
    public static bool TryDecode(string? payload, out PairingPayloadV2? decoded)
    {
        decoded = null;

        if (string.IsNullOrWhiteSpace(payload) || !payload.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var wire = JsonSerializer.Deserialize<Wire>(
                Base64Url.DecodeFromChars(payload.AsSpan(Prefix.Length)),
                WireOptions);

            if (wire is null
                || wire.Version != Version
                || string.IsNullOrWhiteSpace(wire.PcInstanceId)
                || string.IsNullOrWhiteSpace(wire.SessionId)
                || string.IsNullOrWhiteSpace(wire.PairingSecret))
            {
                return false;
            }

            decoded = new PairingPayloadV2
            {
                PcInstanceId = wire.PcInstanceId,
                SessionId = wire.SessionId,
                PairingSecret = wire.PairingSecret,
                Host = string.IsNullOrWhiteSpace(wire.Host) ? null : wire.Host,
                Port = wire.Port,
                ProtocolVersion = wire.ProtocolVersion,
            };

            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Compact, stable field names. Fixed here rather than derived from a naming policy: this is a
    /// wire format two languages must agree on byte for byte, and it is covered by shared vectors.
    /// </summary>
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        MaxDepth = 8,
    };

    private sealed record Wire
    {
        [JsonPropertyName("v")]
        public int Version { get; init; }

        [JsonPropertyName("pc")]
        public string? PcInstanceId { get; init; }

        [JsonPropertyName("s")]
        public string? SessionId { get; init; }

        [JsonPropertyName("k")]
        public string? PairingSecret { get; init; }

        [JsonPropertyName("h")]
        public string? Host { get; init; }

        [JsonPropertyName("p")]
        public int Port { get; init; }

        [JsonPropertyName("pv")]
        public int ProtocolVersion { get; init; }
    }
}
