using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PcAssistant.Windows;

/// <summary>
/// The SSID of the network a Wi-Fi adapter is currently on, through the documented WLAN API.
///
/// Not through <c>netsh wlan show interfaces</c>: that prints localized labels, and this
/// machine's Windows is Turkish, so parsing it would break on exactly the PC it has to work on.
///
/// Every failure here is answered with null. A missing network name is a cosmetic gap in a
/// status report, never a reason to fail the report.
/// </summary>
internal static class WirelessNetworks
{
    /// <summary>
    /// Byte offset of <c>dot11Ssid</c> inside WLAN_CONNECTION_ATTRIBUTES:
    /// <c>isState</c> (4) + <c>wlanConnectionMode</c> (4) + <c>strProfileName[256]</c> (512).
    /// Read by offset rather than by declaring the whole nested structure, because everything
    /// past the SSID is signal and rate detail this does not use.
    /// </summary>
    private const int SsidOffset = 520;
    private const int MaxSsidBytes = 32;

    public static string? SsidFor(string adapterId)
    {
        if (!Guid.TryParse(adapterId, out var wanted)) return null;

        nint client = 0;
        try
        {
            if (WlanOpenHandle(2, 0, out _, out client) != 0 || client == 0) return null;
            if (WlanEnumInterfaces(client, 0, out var list) != 0 || list == 0) return null;

            try
            {
                var count = Marshal.ReadInt32(list);
                // WLAN_INTERFACE_INFO_LIST: dwNumberOfItems (4) + dwIndex (4), then the array.
                for (var i = 0; i < Math.Min(count, 32); i++)
                {
                    var entry = list + 8 + i * InterfaceInfoSize;
                    var guid = Marshal.PtrToStructure<Guid>(entry);
                    if (guid != wanted) continue;
                    return CurrentSsid(client, guid);
                }
            }
            finally { WlanFreeMemory(list); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            // No WLAN service on this machine (a desktop with no wireless stack).
        }
        finally { if (client != 0) WlanCloseHandle(client, 0); }

        return null;
    }

    private static string? CurrentSsid(nint client, Guid adapter)
    {
        if (WlanQueryInterface(client, ref adapter, CurrentConnectionOpcode, 0, out var size, out var data, 0) != 0 ||
            data == 0) return null;
        try
        {
            if (size < SsidOffset + 4 + MaxSsidBytes) return null;
            var length = Marshal.ReadInt32(data + SsidOffset);
            if (length is < 1 or > MaxSsidBytes) return null;
            var bytes = new byte[length];
            Marshal.Copy(data + SsidOffset + 4, bytes, 0, length);
            // SSIDs are octet strings; UTF-8 is what modern access points use and what Windows shows.
            var ssid = Encoding.UTF8.GetString(bytes).Trim();
            return string.IsNullOrEmpty(ssid) ? null : ssid;
        }
        finally { WlanFreeMemory(data); }
    }

    /// <summary>GUID (16) + strInterfaceDescription[256] (512) + isState (4).</summary>
    private const int InterfaceInfoSize = 16 + 512 + 4;

    /// <summary>wlan_intf_opcode_current_connection.</summary>
    private const int CurrentConnectionOpcode = 7;

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiatedVersion, out nint client);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(nint client, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(nint client, nint reserved, out nint interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(nint client, ref Guid adapter, int opCode, nint reserved,
        out int dataSize, out nint data, nint valueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(nint memory);
}
