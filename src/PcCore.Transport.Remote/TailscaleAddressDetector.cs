using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace PcCore.Transport.Remote;

/// <summary>
/// A vetted address the remote transport is allowed to bind.
/// <para>
/// This type exists so that "which address do we listen on" is not a string a caller can supply.
/// Its constructor is <c>internal</c>, and in production the only code that calls it is
/// <see cref="TailscaleAddressDetector"/> after the address has passed every check in
/// <see cref="TailscaleAddressDetector.Detect"/>. Configuration cannot name a bind address, and
/// there is no wildcard or "all interfaces" path anywhere in this assembly.
/// </para>
/// <para>
/// Same pattern as <c>AuthenticatedDevice</c> in v0.3: make the unsafe thing unconstructible
/// rather than merely unwritten.
/// </para>
/// </summary>
public sealed class RemoteEndpoint
{
    internal RemoteEndpoint(IPAddress address, int port, string source)
    {
        Address = address;
        Port = port;
        Source = source;
    }

    public IPAddress Address { get; }

    public int Port { get; }

    /// <summary>Human-readable explanation of where this address came from, for logs.</summary>
    public string Source { get; }

    public override string ToString() => $"{Address}:{Port}";
}

/// <summary>One network interface, reduced to what detection needs. Abstracted so it can be faked.</summary>
public sealed record NetworkInterfaceInfo(
    string Name,
    string Description,
    OperationalStatus Status,
    IReadOnlyList<IPAddress> UnicastAddresses);

public interface INetworkInterfaceProvider
{
    IReadOnlyList<NetworkInterfaceInfo> GetInterfaces();
}

/// <summary>Reads the machine's real interfaces. Requires no elevation.</summary>
public sealed class SystemNetworkInterfaceProvider : INetworkInterfaceProvider
{
    public IReadOnlyList<NetworkInterfaceInfo> GetInterfaces()
    {
        try
        {
            return
            [
                .. NetworkInterface.GetAllNetworkInterfaces().Select(nic => new NetworkInterfaceInfo(
                    nic.Name,
                    nic.Description,
                    nic.OperationalStatus,
                    [
                        .. nic.GetIPProperties().UnicastAddresses
                            .Select(a => a.Address)
                            .Where(a => a.AddressFamily == AddressFamily.InterNetwork),
                    ])),
            ];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}

/// <summary>Why no endpoint was produced, for logging and the status readout.</summary>
public sealed record TailscaleDetectionResult(RemoteEndpoint? Endpoint, string Diagnostic)
{
    public bool Found => Endpoint is not null;
}

/// <summary>
/// Finds the machine's Tailscale address, or refuses.
/// <para>
/// <b>The invariant this type exists to enforce:</b> PC Core must never listen on an ordinary LAN
/// or WAN interface. Detection therefore requires <em>two independent</em> facts to agree, and
/// yields nothing if either is missing:
/// </para>
/// <list type="number">
/// <item><b>Interface identity.</b> The adapter must identify itself as Tailscale by name or
/// description (on Windows: adapter <c>Tailscale</c>, description <c>Tailscale Tunnel</c>), and be
/// operationally up.</item>
/// <item><b>Address range.</b> The address must fall inside <c>100.64.0.0/10</c>, the CGNAT range
/// Tailscale assigns. This is evaluated with integer arithmetic on the address bytes, not a
/// <c>"100."</c> string prefix — <c>100.0.0.1</c> and <c>100.128.0.1</c> are ordinary public
/// addresses outside the range and are correctly rejected.</item>
/// </list>
/// <para>
/// Requiring both is what makes the check safe in either direction: a LAN adapter someone renamed
/// "Tailscale" still fails the range test, and a machine that genuinely holds a CGNAT address on a
/// non-Tailscale interface (some ISP setups do) still fails the identity test.
/// </para>
/// <para>
/// <b>Deterministic choice.</b> If several addresses qualify, the numerically lowest is chosen, so
/// the bind address does not change between boots.
/// </para>
/// <para>
/// <b>Fails closed.</b> No candidate means no endpoint and no listener. There is no fallback to
/// <c>0.0.0.0</c>, to a LAN address, or to a configured override.
/// </para>
/// <para>
/// The Tailscale CLI is deliberately not invoked: it is not always on PATH, shelling out to it on
/// a timer is fragile, and interface identity plus the CGNAT range already answers the question
/// from information the OS gives us directly.
/// </para>
/// </summary>
public sealed class TailscaleAddressDetector
{
    /// <summary>100.64.0.0 as a big-endian 32-bit value.</summary>
    private const uint CgnatBase = 0x64_40_00_00;

    /// <summary>/10 — the top ten bits must match.</summary>
    private const uint CgnatMask = 0xFF_C0_00_00;

    private static readonly string[] InterfaceMarkers = ["tailscale"];

    private readonly INetworkInterfaceProvider _interfaces;
    private readonly ILogger<TailscaleAddressDetector> _logger;

    public TailscaleAddressDetector(INetworkInterfaceProvider interfaces, ILogger<TailscaleAddressDetector> logger)
    {
        _interfaces = interfaces;
        _logger = logger;
    }

    /// <summary>True when <paramref name="address"/> is inside 100.64.0.0/10.</summary>
    public static bool IsTailscaleAddressRange(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out _))
        {
            return false;
        }

        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        return (value & CgnatMask) == CgnatBase;
    }

    public static bool LooksLikeTailscaleInterface(NetworkInterfaceInfo nic) =>
        InterfaceMarkers.Any(marker =>
            nic.Name.Contains(marker, StringComparison.OrdinalIgnoreCase)
            || nic.Description.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public TailscaleDetectionResult Detect(int port)
    {
        var interfaces = _interfaces.GetInterfaces();

        if (interfaces.Count == 0)
        {
            return Unavailable("no network interfaces could be enumerated");
        }

        var tailscaleInterfaces = interfaces.Where(LooksLikeTailscaleInterface).ToArray();

        if (tailscaleInterfaces.Length == 0)
        {
            return Unavailable(
                "no Tailscale network interface is present. Install Tailscale and sign in; "
                + "PC Core does not manage Tailscale itself.");
        }

        var up = tailscaleInterfaces.Where(n => n.Status == OperationalStatus.Up).ToArray();

        if (up.Length == 0)
        {
            return Unavailable("a Tailscale interface exists but is not up (Tailscale may be disconnected)");
        }

        var candidates = up
            .SelectMany(nic => nic.UnicastAddresses.Select(address => (Nic: nic, Address: address)))
            .Where(c => IsTailscaleAddressRange(c.Address))
            .OrderBy(c => c.Address.GetAddressBytes(), ByteArrayComparer.Instance)
            .ToArray();

        if (candidates.Length == 0)
        {
            return Unavailable(
                "a Tailscale interface is up but holds no address in 100.64.0.0/10 "
                + "(Tailscale may still be connecting)");
        }

        var chosen = candidates[0];

        if (candidates.Length > 1)
        {
            _logger.LogInformation(
                "{Count} Tailscale addresses available; selecting the lowest ({Address}) for a stable bind address.",
                candidates.Length,
                chosen.Address);
        }

        var endpoint = new RemoteEndpoint(chosen.Address, port, $"Tailscale interface '{chosen.Nic.Name}'");
        return new TailscaleDetectionResult(endpoint, $"bound to {endpoint} via {endpoint.Source}");
    }

    private TailscaleDetectionResult Unavailable(string diagnostic)
    {
        _logger.LogDebug("Remote transport unavailable: {Diagnostic}", diagnostic);
        return new TailscaleDetectionResult(null, diagnostic);
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) =>
            x is null || y is null ? 0 : ((ReadOnlySpan<byte>)x).SequenceCompareTo(y);
    }
}
