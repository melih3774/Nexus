using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcCore.NativeInput.Hid;

/// <summary>
/// Answers "did Nexus create this device?" from the Windows device tree.
/// <para>
/// <b>Ancestry rather than identifiers, and the difference is the whole security argument.</b> A
/// hardware id, a vendor and product id, a friendly name and a container id are all things a device
/// <em>claims</em>, and anything able to install a driver can claim any of them. Trusting a claim
/// means the security detector can be told that a physical keyboard is Nexus's own, which is the one
/// mistake this system must never make — it would let someone type at an ARMED machine without
/// raising an incident.
/// </para>
/// <para>
/// The device tree is not a claim. Windows records which devnode enumerated which, and every device
/// Nexus publishes is a descendant of the Nexus control device because VHF created it there. Nothing
/// else on the machine can appear under that node without already being the Nexus driver. So the
/// question becomes a walk: from the device behind a Raw Input path, up through
/// <c>CM_Get_Parent</c>, until either the Nexus control device is reached or the tree runs out.
/// </para>
/// <para>
/// <b>This is also the second design.</b> The first had the driver assign each device a hardware id
/// carrying Nexus's vendor and product ids, through <c>VHF_CONFIG.InstanceID</c> and
/// <c>HardwareIDs</c>, and matched Raw Input names against it. Those members are undocumented — the
/// VHF_CONFIG reference lists them with empty descriptions — and setting them made VHF accept the
/// create, accept the start, and enumerate nothing at all. Ancestry needs nothing from VHF, rests on
/// documented Configuration Manager APIs, and is stronger than the token match it replaced.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NexusHidPnpAncestry
{
    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;
    private const uint LocateDevnodeNormal = 0;

    /// <summary>How far up the tree to walk before giving up. A device tree is not deep.</summary>
    private const int MaxDepth = 32;

    /// <summary>DEVPKEY_Device_InstanceId — {78c34fc8-104a-4aca-9ea4-524d52996e57}, 256.</summary>
    private static readonly DevPropKey InstanceIdKey =
        new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);

    /// <summary>
    /// The device instance id behind a device interface path, for example turning
    /// <c>\\?\HID#...#{4d1e55b2-...}</c> into <c>HID\HID_DEVICE_SYSTEM_VHF\2&amp;37c680e2&amp;0&amp;0000</c>.
    /// </summary>
    internal static string? InstanceIdOfInterface(string interfacePath)
    {
        if (string.IsNullOrWhiteSpace(interfacePath))
        {
            return null;
        }

        var key = InstanceIdKey;
        uint size = 0;

        var result = CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out _, null, ref size, 0);

        if (result != CrBufferSmall || size == 0)
        {
            return null;
        }

        var buffer = new byte[size];
        result = CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out _, buffer, ref size, 0);

        return result == CrSuccess ? Decode(buffer, size) : null;
    }

    /// <summary>
    /// Whether <paramref name="instanceId"/> is <paramref name="ancestorInstanceId"/> or descends
    /// from it.
    /// <para>
    /// The device itself does not count as its own descendant for our purposes — the control device
    /// is not an input device — but the comparison is written to accept it so a caller asking about
    /// the control device gets a truthful answer rather than a surprising one.
    /// </para>
    /// </summary>
    internal static bool DescendsFrom(string? instanceId, string ancestorInstanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(ancestorInstanceId))
        {
            return false;
        }

        if (CM_Locate_DevNodeW(out var node, instanceId, LocateDevnodeNormal) != CrSuccess)
        {
            // A device that vanished between enumeration and this call. Unresolvable is untrusted.
            return false;
        }

        for (var depth = 0; depth < MaxDepth; depth++)
        {
            var id = DeviceIdOf(node);

            if (id is not null && string.Equals(id, ancestorInstanceId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (CM_Get_Parent(out var parent, node, 0) != CrSuccess)
            {
                return false;
            }

            node = parent;
        }

        // Deeper than any real device tree. Refusing is the safe direction: the alternative is
        // looping, and a loop in a security check is worse than a false negative.
        return false;
    }

    private static string? DeviceIdOf(uint node)
    {
        if (CM_Get_Device_ID_Size(out var chars, node, 0) != CrSuccess || chars == 0)
        {
            return null;
        }

        // The size excludes the terminator; the call wants room for it.
        var buffer = new char[chars + 1];

        return CM_Get_Device_IDW(node, buffer, (uint)buffer.Length, 0) == CrSuccess
            ? new string(buffer, 0, (int)chars)
            : null;
    }

    private static string? Decode(byte[] buffer, uint size)
    {
        var text = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size);
        var terminator = text.IndexOf('\0', StringComparison.Ordinal);

        if (terminator >= 0)
        {
            text = text[..terminator];
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        private Guid _formatId;
        private uint _propertyId;

        public DevPropKey(Guid formatId, uint propertyId)
        {
            _formatId = formatId;
            _propertyId = propertyId;
        }
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int CM_Get_Device_Interface_PropertyW(
        string deviceInterface,
        ref DevPropKey propertyKey,
        out uint propertyType,
        byte[]? propertyBuffer,
        ref uint propertyBufferSize,
        uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", SetLastError = false)]
    private static extern int CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", SetLastError = false)]
    private static extern int CM_Get_Device_ID_Size(out uint length, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int CM_Get_Device_IDW(uint devInst, char[] buffer, uint bufferLength, uint flags);
}
