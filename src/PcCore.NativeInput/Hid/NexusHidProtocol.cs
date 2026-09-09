using System.Runtime.InteropServices;

namespace PcCore.NativeInput.Hid;

/// <summary>
/// PC Core's half of the Nexus HID driver ABI. Mirrors <c>src/driver/NexusHid/NexusHidProtocol.h</c>
/// field for field.
/// <para>
/// <b>Two languages describing one memory layout is a bug waiting to be written</b>, so neither
/// side is trusted: the C header carries <c>static_assert</c>s on every struct size and this file is
/// checked by <c>NexusHidProtocolAbiTests</c> against the same numbers. If the two ever disagree,
/// the driver reads a report starting from the middle of a struct — which produces input, rather
/// than an error, and is therefore the worst failure this component has.
/// </para>
/// <para>
/// <b>Six operations and no seventh.</b> There is no passthrough opcode, no caller-supplied pointer,
/// no unbounded length, and no way to name a file, a process or an address. A driver reachable from
/// user mode is an escalation surface; this one stays small by not being able to do anything else.
/// </para>
/// </summary>
public static class NexusHidProtocol
{
    /// <summary>
    /// Bumped when any struct changes shape or any opcode changes meaning. PC Core refuses to talk
    /// to a driver reporting a different number, because version skew between a driver and its
    /// control client is a wrong-report bug rather than a degrade-gracefully opportunity.
    /// </summary>
    public const uint Version = 1;

    /// <summary>{6D1F4A2E-9C3B-4E7A-B5D8-1A2C3E4F5B60} — the control device interface.</summary>
    public static readonly Guid ControlInterface = new("6d1f4a2e-9c3b-4e7a-b5d8-1a2c3e4f5b60");

    /// <summary>
    /// Nexus's own vendor id. Not an allocated USB VID, and deliberately not one: these devices are
    /// root-enumerated and never appear on a USB bus, so no allocation is required and claiming
    /// another vendor's number would be both wrong and traceable to them. See
    /// <c>docs/virtual-hid.md</c> §5.
    /// </summary>
    public const ushort VendorId = 0xF055;

    public const ushort ProductIdMouse = 0x0001;
    public const ushort ProductIdKeyboard = 0x0002;
    public const ushort ProductIdTouch = 0x0003;
    public const ushort DeviceVersion = 0x0100;

    public const int MaxDevices = 3;
    public const int MaxReportLength = 64;
    public const int MaxDescriptorLength = 512;

    /// <summary>WCHARs in a device instance id field, excluding the terminator.</summary>
    public const int InstanceIdCapacity = 200;

    private const uint FileDeviceUnknown = 0x00000022;
    private const uint MethodBuffered = 0;
    private const uint FileWriteAccess = 0x0002;
    private const uint IoctlBase = 0x800;

    private static uint Code(uint index) =>
        (FileDeviceUnknown << 16) | (FileWriteAccess << 14) | ((IoctlBase + index) << 2) | MethodBuffered;

    public static uint IoctlGetVersion => Code(0);
    public static uint IoctlCreateDevice => Code(1);
    public static uint IoctlSubmitReport => Code(2);
    public static uint IoctlGetStatus => Code(3);
    public static uint IoctlNeutralize => Code(4);
    public static uint IoctlDeleteDevice => Code(5);

    /// <summary>Struct sizes the C header asserts. Checked from both sides; never edited to fit.</summary>
    public static class Sizes
    {
        public const int VersionReply = 8;
        public const int CreateRequest = 528;
        public const int CreateReply = 410;
        public const int SubmitRequest = 70;
        public const int DeviceStatus = 414;
        public const int StatusReply = 1254;
        public const int NeutralizeRequest = 4;
        public const int DeleteRequest = 4;
    }
}

/// <summary>Which of the three Nexus devices a request concerns. Values are ABI.</summary>
public enum NexusHidDeviceKind : uint
{
    Mouse = 0,
    Keyboard = 1,
    Touch = 2,
}

/// <summary>Lifecycle state of one published device, as the driver reports it.</summary>
public enum NexusHidDeviceState : uint
{
    /// <summary>Never created, or deleted.</summary>
    Absent = 0,

    /// <summary>Created and started. Reports are accepted.</summary>
    Ready = 1,

    /// <summary>Creation failed; the status field carries the NTSTATUS.</summary>
    Failed = 2,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NexusHidVersionReply
{
    public uint ProtocolVersion;
    public uint DriverVersion;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct NexusHidCreateRequest
{
    public uint ProtocolVersion;
    public uint DeviceKind;
    public ushort VendorId;
    public ushort ProductId;
    public ushort VersionNumber;
    public ushort DescriptorLength;
    public fixed byte Descriptor[NexusHidProtocol.MaxDescriptorLength];
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct NexusHidCreateReply
{
    public uint State;
    public int Status;
    public ushort DeviceInstanceIdLength;
    public fixed char DeviceInstanceId[NexusHidProtocol.InstanceIdCapacity];
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct NexusHidSubmitRequest
{
    public uint DeviceKind;
    public ushort ReportLength;
    public fixed byte Report[NexusHidProtocol.MaxReportLength];
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct NexusHidDeviceStatus
{
    public uint DeviceKind;
    public uint State;
    public int Status;
    public ushort DeviceInstanceIdLength;
    public fixed char DeviceInstanceId[NexusHidProtocol.InstanceIdCapacity];
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NexusHidStatusReply
{
    public uint ProtocolVersion;
    public uint DriverVersion;
    public uint DeviceCount;

    private NexusHidDeviceStatus _device0;
    private NexusHidDeviceStatus _device1;
    private NexusHidDeviceStatus _device2;

    /// <summary>
    /// Indexed access to the three device slots.
    /// <para>
    /// Three named fields rather than a fixed array because <c>NexusHidDeviceStatus</c> contains a
    /// fixed buffer and therefore cannot itself live in one. The indexer keeps that an
    /// implementation detail instead of leaking into every caller.
    /// </para>
    /// </summary>
    public NexusHidDeviceStatus this[int index] => index switch
    {
        0 => _device0,
        1 => _device1,
        2 => _device2,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NexusHidNeutralizeRequest
{
    /// <summary>Must be zero. Present so the request has a shape the driver can size-check.</summary>
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NexusHidDeleteRequest
{
    public uint DeviceKind;
}
