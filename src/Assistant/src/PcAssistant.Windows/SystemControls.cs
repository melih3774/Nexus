using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace PcAssistant.Windows;

public sealed record PowerState(string PowerSource, string BatteryState, int? BatteryPercent,
    TimeSpan? EstimatedRemaining, bool BatterySaver, string Note);

public sealed record NetworkAdapter(string Name, string Description, string Type, string Status,
    long? SpeedBitsPerSecond, IReadOnlyList<string> Addresses, string? Ssid);

public sealed record NetworkState(string HostName, bool HasUsableConnection, string? ActiveInterface,
    IReadOnlyList<NetworkAdapter> Adapters, IReadOnlyList<string> VirtualNetworks, string Note);

public sealed record RecycleBinState(long ItemCount, long TotalBytes, string Note);

/// <summary>
/// The everyday read-only questions an assistant on a laptop is actually asked: is it on
/// battery, is it online, what is in the recycle bin.
///
/// Nothing here changes power policy, adapter configuration or deletes anything. Shutting
/// down, sleeping and emptying the recycle bin are deliberately absent: they are one-way and
/// belong behind an explicit owner decision, not behind an observation tool.
/// </summary>
public static class SystemControls
{
    public static PowerState Power()
    {
        if (!Native.GetSystemPowerStatus(out var status))
            throw new InvalidOperationException("Windows did not report power status.");

        var source = status.AcLineStatus switch
        {
            0 => "Battery",
            1 => "AC power",
            _ => "Unknown",
        };

        // BatteryFlag 128 means "no system battery": a desktop, and the percentage is meaningless.
        var hasBattery = (status.BatteryFlag & 128) == 0 && status.BatteryLifePercent <= 100;
        int? percent = hasBattery ? status.BatteryLifePercent : null;

        var state = !hasBattery ? "No battery (desktop or always-plugged)"
            : (status.BatteryFlag & 8) != 0 ? "Charging"
            : status.AcLineStatus == 1 ? "Plugged in, not charging"
            : (status.BatteryFlag & 4) != 0 ? "Critical"
            : (status.BatteryFlag & 2) != 0 ? "Low"
            : "Discharging";

        // 0xFFFFFFFF is "Windows does not know yet", which is common for the first minute on battery.
        TimeSpan? remaining = status.BatteryLifeTime != uint.MaxValue && hasBattery && status.AcLineStatus != 1
            ? TimeSpan.FromSeconds(status.BatteryLifeTime)
            : null;

        var note = hasBattery
            ? "Reported by Windows. A remaining-time estimate is absent while charging and for a while after unplugging."
            : "This machine reports no system battery, so battery percentage does not apply.";

        return new PowerState(source, state, percent, remaining, (status.SystemStatusFlag & 1) != 0, note);
    }

    /// <summary>
    /// Adapters, addresses and whether anything is actually usable — which is a different
    /// question from whether an adapter is up, and the one the owner means by "am I online".
    /// </summary>
    public static NetworkState Network()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        var adapters = new List<NetworkAdapter>();
        var virtualNetworks = new List<string>();

        foreach (var adapter in interfaces.Take(64))
        {
            var type = adapter.NetworkInterfaceType;
            if (type == NetworkInterfaceType.Loopback) continue;

            IReadOnlyList<string> addresses;
            try
            {
                addresses = adapter.GetIPProperties().UnicastAddresses
                    .Where(a => !a.Address.IsIPv6LinkLocal)
                    .Take(8).Select(a => a.Address.ToString()).ToArray();
            }
            catch (NetworkInformationException) { addresses = []; }

            var up = adapter.OperationalStatus == OperationalStatus.Up;

            // A tunnel or virtual adapter that is up is worth naming separately: it explains a
            // second address range without pretending it is the machine's ordinary network.
            if (up && LooksVirtual(adapter) && addresses.Count > 0)
                virtualNetworks.Add($"{adapter.Name} ({string.Join(", ", addresses)})");

            adapters.Add(new NetworkAdapter(adapter.Name, Trim(adapter.Description), Describe(type),
                adapter.OperationalStatus.ToString(), SafeSpeed(adapter),
                addresses, up && type == NetworkInterfaceType.Wireless80211 ? WirelessNetworks.SsidFor(adapter.Id) : null));
        }

        var usable = interfaces.Any(a => a.OperationalStatus == OperationalStatus.Up &&
                                         a.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                         !LooksVirtual(a) &&
                                         a.GetIPProperties().GatewayAddresses.Count > 0);

        var active = adapters.FirstOrDefault(a => a.Status == "Up" && a.Addresses.Count > 0 &&
                                                  !virtualNetworks.Any(v => v.StartsWith(a.Name + " (", StringComparison.Ordinal)))?.Name;

        return new NetworkState(Environment.MachineName, usable, active, adapters, virtualNetworks,
            "Adapter state and addresses as Windows reports them. A default gateway on a physical adapter is treated as " +
            "'usable connection'; it is not a reachability test, so a captive portal or a DNS failure still looks connected.");
    }

    /// <summary>Recycle Bin contents across every drive, without touching anything in it.</summary>
    public static RecycleBinState RecycleBin()
    {
        var info = new Native.RecycleBinInfo { Size = Marshal.SizeOf<Native.RecycleBinInfo>() };
        var hr = Native.SHQueryRecycleBin(null, ref info);
        if (hr != 0) throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException("Recycle Bin state is unavailable.");
        return new RecycleBinState(info.ItemCount, info.ItemBytes,
            info.ItemCount == 0
                ? "The Recycle Bin is empty."
                : "Counted across all drives. PC Assistant never empties the Recycle Bin or restores from it; " +
                  "open it and let the owner decide.");
    }

    /// <summary>Opens the Recycle Bin in Explorer. Shows it to the owner; changes nothing.</summary>
    public static string OpenRecycleBin()
    {
        using var process = Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
        return "Recycle Bin opened in File Explorer for the owner. Nothing was deleted or restored.";
    }

    private static long? SafeSpeed(NetworkInterface adapter)
    {
        try { return adapter.Speed > 0 ? adapter.Speed : null; }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException) { return null; }
    }

    /// <summary>
    /// Recognises tunnels and host-only adapters generically, by adapter type and by the
    /// vendor words Windows puts in the description. No product is special-cased, and no
    /// specific VPN is required to be installed for any of this to work.
    /// </summary>
    private static bool LooksVirtual(NetworkInterface adapter)
    {
        if (adapter.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) return true;
        var text = adapter.Description + " " + adapter.Name;
        foreach (var marker in new[] { "virtual", "vethernet", "hyper-v", "vmware", "virtualbox", "tap-", "tun", "wintun", "loopback adapter" })
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string Describe(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
        NetworkInterfaceType.Tunnel => "Tunnel",
        NetworkInterfaceType.Ppp => "PPP",
        _ => type.ToString(),
    };

    private static string Trim(string value) => value.Length <= 120 ? value : value[..119] + "…";
}
