using System.Net;
using System.Net.NetworkInformation;
using PcCore.Tests.TestSupport;
using PcCore.Transport.Remote;
using Xunit;

namespace PcCore.Tests.Remote;

internal sealed class FakeNetworkInterfaceProvider : INetworkInterfaceProvider
{
    private readonly List<NetworkInterfaceInfo> _interfaces = [];

    public FakeNetworkInterfaceProvider Add(
        string name,
        string description,
        OperationalStatus status,
        params string[] addresses)
    {
        _interfaces.Add(new NetworkInterfaceInfo(
            name, description, status, [.. addresses.Select(IPAddress.Parse)]));
        return this;
    }

    /// <summary>The interfaces on a typical Windows laptop with no Tailscale installed.</summary>
    public static FakeNetworkInterfaceProvider TypicalMachine() => new FakeNetworkInterfaceProvider()
        .Add("Wi-Fi", "Intel(R) Wi-Fi 6E AX211 160MHz", OperationalStatus.Up, "192.168.1.102")
        .Add("Ethernet", "Realtek Gaming GbE Family Controller", OperationalStatus.Down, "169.254.77.104")
        .Add("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", OperationalStatus.Up, "172.23.96.1")
        .Add("Loopback Pseudo-Interface 1", "Software Loopback Interface 1", OperationalStatus.Up, "127.0.0.1");

    public IReadOnlyList<NetworkInterfaceInfo> GetInterfaces() => _interfaces;
}

/// <summary>
/// The binding-safety suite. The invariant under test is the one that matters most in v0.4:
/// PC Core must never open the remote port on an ordinary LAN or WAN interface, and must refuse
/// to listen at all rather than fall back to something broader.
/// </summary>
public sealed class TailscaleDetectionTests
{
    private const int Port = 47812;

    private static TailscaleAddressDetector Detector(INetworkInterfaceProvider interfaces) =>
        new(interfaces, TestLog.For<TailscaleAddressDetector>());

    // --- Accepts a genuine Tailscale interface ---------------------------------------------------

    [Fact]
    public void SelectsTheTailscaleInterface()
    {
        var interfaces = FakeNetworkInterfaceProvider.TypicalMachine()
            .Add("Tailscale", "Tailscale Tunnel", OperationalStatus.Up, "100.101.102.103");

        var result = Detector(interfaces).Detect(Port);

        Assert.True(result.Found);
        Assert.Equal(IPAddress.Parse("100.101.102.103"), result.Endpoint!.Address);
        Assert.Equal(Port, result.Endpoint.Port);
        Assert.Contains("Tailscale", result.Endpoint.Source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tailscale0", "Tailscale Tunnel")]
    [InlineData("Tailscale", "Tailscale Tunnel")]
    [InlineData("ts0", "TAILSCALE tunnel adapter")]
    public void RecognisesTheInterfaceByNameOrDescription(string name, string description)
    {
        var interfaces = new FakeNetworkInterfaceProvider()
            .Add(name, description, OperationalStatus.Up, "100.64.0.1");

        Assert.True(Detector(interfaces).Detect(Port).Found);
    }

    // --- Refuses everything else -----------------------------------------------------------------

    [Fact]
    public void RefusesOrdinaryLanInterfaces()
    {
        // The headline safety case: a normal machine with Wi-Fi, Ethernet and a Hyper-V switch
        // must produce no endpoint at all.
        var result = Detector(FakeNetworkInterfaceProvider.TypicalMachine()).Detect(Port);

        Assert.False(result.Found);
        Assert.Null(result.Endpoint);
        Assert.Contains("no Tailscale network interface", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("192.168.1.102")]
    [InlineData("10.0.0.5")]
    [InlineData("172.23.96.1")]
    [InlineData("169.254.77.104")]
    [InlineData("127.0.0.1")]
    [InlineData("8.8.8.8")]
    public void RefusesANonCgnatAddressEvenOnAnInterfaceNamedTailscale(string address)
    {
        // Interface identity alone is not enough. Renaming an adapter must not make a LAN address
        // bindable, which is why the CGNAT range is a second, independent requirement.
        var interfaces = new FakeNetworkInterfaceProvider()
            .Add("Tailscale", "Tailscale Tunnel", OperationalStatus.Up, address);

        var result = Detector(interfaces).Detect(Port);

        Assert.False(result.Found);
        Assert.Contains("100.64.0.0/10", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesACgnatAddressOnANonTailscaleInterface()
    {
        // The converse. Some ISPs hand out CGNAT addresses on ordinary WAN links; a 100.x address
        // is not by itself evidence of a tailnet.
        var interfaces = new FakeNetworkInterfaceProvider()
            .Add("Wi-Fi", "Intel(R) Wi-Fi 6E AX211", OperationalStatus.Up, "100.70.5.9");

        Assert.False(Detector(interfaces).Detect(Port).Found);
    }

    [Fact]
    public void RefusesADownTailscaleInterface()
    {
        var interfaces = new FakeNetworkInterfaceProvider()
            .Add("Tailscale", "Tailscale Tunnel", OperationalStatus.Down, "100.101.102.103");

        var result = Detector(interfaces).Detect(Port);

        Assert.False(result.Found);
        Assert.Contains("not up", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesATailscaleInterfaceWithNoAddressYet()
    {
        var interfaces = new FakeNetworkInterfaceProvider()
            .Add("Tailscale", "Tailscale Tunnel", OperationalStatus.Up);

        Assert.False(Detector(interfaces).Detect(Port).Found);
    }

    [Fact]
    public void FailsClosedWhenNoInterfacesCanBeEnumerated()
    {
        var result = Detector(new FakeNetworkInterfaceProvider()).Detect(Port);

        Assert.False(result.Found);
        Assert.Null(result.Endpoint);
    }

    // --- CGNAT boundary arithmetic ---------------------------------------------------------------

    [Theory]
    [InlineData("100.64.0.0", true)]      // first address in 100.64.0.0/10
    [InlineData("100.64.0.1", true)]
    [InlineData("100.101.102.103", true)]
    [InlineData("100.127.255.255", true)] // last address in range
    [InlineData("100.63.255.255", false)] // one below
    [InlineData("100.128.0.0", false)]    // one above
    [InlineData("100.0.0.1", false)]      // a "100." prefix that is NOT CGNAT
    [InlineData("100.255.255.1", false)]
    public void TheRangeCheckUsesAddressArithmeticNotAStringPrefix(string address, bool expected)
    {
        // A naive StartsWith("100.") would wrongly accept 100.0.0.1 and 100.255.255.1, both of
        // which are ordinary public addresses.
        Assert.Equal(expected, TailscaleAddressDetector.IsTailscaleAddressRange(IPAddress.Parse(address)));
    }

    [Fact]
    public void IPv6IsNotConsideredForBinding()
    {
        Assert.False(TailscaleAddressDetector.IsTailscaleAddressRange(IPAddress.Parse("fd7a:115c:a1e0::1")));
    }

    // --- Determinism ------------------------------------------------------------------------------

    [Fact]
    public void ChoosesTheLowestAddressWhenSeveralQualify()
    {
        // The bind address must not change between boots just because enumeration order did.
        var interfaces = new FakeNetworkInterfaceProvider()
            .Add("Tailscale", "Tailscale Tunnel", OperationalStatus.Up, "100.101.102.103", "100.64.0.7", "100.99.1.1");

        Assert.Equal(IPAddress.Parse("100.64.0.7"), Detector(interfaces).Detect(Port).Endpoint!.Address);
    }

    [Fact]
    public void TheChoiceIsStableAcrossEnumerationOrder()
    {
        var ascending = new FakeNetworkInterfaceProvider()
            .Add("Tailscale", "Tailscale Tunnel", OperationalStatus.Up, "100.64.0.7", "100.101.102.103");
        var descending = new FakeNetworkInterfaceProvider()
            .Add("Tailscale", "Tailscale Tunnel", OperationalStatus.Up, "100.101.102.103", "100.64.0.7");

        Assert.Equal(
            Detector(ascending).Detect(Port).Endpoint!.Address,
            Detector(descending).Detect(Port).Endpoint!.Address);
    }

    // --- No wildcard anywhere ---------------------------------------------------------------------

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    public void WildcardAndBroadcastAddressesAreNeverProduced(string address)
    {
        var interfaces = new FakeNetworkInterfaceProvider()
            .Add("Tailscale", "Tailscale Tunnel", OperationalStatus.Up, address);

        Assert.False(Detector(interfaces).Detect(Port).Found);
    }

    [Fact]
    public void OnlyTheDetectorCanProduceABindableEndpoint()
    {
        // Structural, not conventional: RemoteEndpoint's constructor is internal to the transport
        // assembly, so no configuration value or caller outside it can name a bind address.
        var constructors = typeof(RemoteEndpoint).GetConstructors(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        Assert.Empty(constructors);
    }

    // --- This machine, right now -------------------------------------------------------------------

    [Fact]
    public void RealInterfaceEnumerationNeverYieldsALanAddress()
    {
        // Runs against the actual machine. Whatever it finds, it must never be an address this
        // host holds on an ordinary interface.
        var result = new TailscaleAddressDetector(
            new SystemNetworkInterfaceProvider(), TestLog.For<TailscaleAddressDetector>()).Detect(Port);

        if (!result.Found)
        {
            Assert.Null(result.Endpoint);
            return;
        }

        Assert.True(
            TailscaleAddressDetector.IsTailscaleAddressRange(result.Endpoint!.Address),
            $"detector produced {result.Endpoint.Address}, which is outside 100.64.0.0/10");

        var lanAddresses = new SystemNetworkInterfaceProvider().GetInterfaces()
            .Where(nic => !TailscaleAddressDetector.LooksLikeTailscaleInterface(nic))
            .SelectMany(nic => nic.UnicastAddresses)
            .ToArray();

        Assert.DoesNotContain(result.Endpoint.Address, lanAddresses);
    }
}
