using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

namespace PcCore.NativeInput.Hid;

/// <summary>
/// Finding and talking to the Nexus HID driver's control device.
/// <para>
/// <b>Found by interface GUID, never by name.</b> Windows chooses the device path, and a hardcoded
/// one would be both fragile and a thing another program could squat. The GUID is Nexus's own and is
/// declared once, in the driver's header and its managed mirror.
/// </para>
/// <para>
/// Every entry point here takes a typed request and, where there is one, a typed reply. There is
/// deliberately no overload accepting a raw control code and a raw buffer: the point of a narrow
/// driver interface is lost if the client can express anything the driver could theoretically
/// accept.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NexusHidControlDevice
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;

    /// <summary>
    /// The control device's path, or null when the driver is not installed.
    /// <para>
    /// Returns the first matching interface. There is exactly one control device by construction —
    /// the INF enumerates a single <c>Root\NexusHid</c> — and if there were somehow two, taking the
    /// first is no worse than any other choice and better than failing.
    /// </para>
    /// </summary>
    internal static string? Find() => InterfacePaths(NexusHidProtocol.ControlInterface).FirstOrDefault();

    /// <summary>
    /// Every present device interface of one class, as full paths.
    /// <para>
    /// Used for the control device, and for the touch screen — which Raw Input reports under the
    /// shared synthetic name <c>\?\VIRTUAL_DIGITIZER</c> rather than a device path of its own, so
    /// its real interface has to be found here instead. See
    /// <see cref="NexusHidBackend.ResolveIdentities"/>.
    /// </para>
    /// </summary>
    internal static List<string> InterfacePaths(Guid interfaceClass)
    {
        var paths = new List<string>();
        var interfaceGuid = interfaceClass;

        var set = SetupDiGetClassDevsW(ref interfaceGuid, IntPtr.Zero, IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);

        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            return paths;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };

                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceGuid, index, ref data))
                {
                    // ERROR_NO_MORE_ITEMS, or anything else that means "stop asking".
                    return paths;
                }

                // The documented two-call idiom: ask for the size, then ask again with the buffer.
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);

                if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || required == 0)
                {
                    continue;
                }

                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    // cbSize is the size of the fixed part of the struct, not of the buffer. On x64
                    // that is 8: a DWORD followed by a single WCHAR, padded. Getting this wrong is
                    // the classic SetupAPI failure and it reports as ERROR_INVALID_PARAMETER.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);

                    if (SetupDiGetDeviceInterfaceDetailW(set, ref data, buffer, required, out _, IntPtr.Zero)
                        && Marshal.PtrToStringUni(buffer + 4) is { Length: > 0 } path)
                    {
                        paths.Add(path);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    internal static SafeFileHandle? Open(string path)
    {
        var handle = CreateFileW(path, GenericRead | GenericWrite, FileShareReadWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

        return handle.IsInvalid ? null : handle;
    }

    /// <summary>Sends a request that expects a typed reply.</summary>
    internal static unsafe bool Control<TRequest, TReply>(
        SafeFileHandle handle,
        uint code,
        ref TRequest request,
        ref TReply reply)
        where TRequest : unmanaged
        where TReply : unmanaged
    {
        fixed (TRequest* input = &request)
        fixed (TReply* output = &reply)
        {
            return DeviceIoControl(handle, code, (IntPtr)input, sizeof(TRequest),
                (IntPtr)output, sizeof(TReply), out _, IntPtr.Zero);
        }
    }

    /// <summary>Sends a request that takes no input and expects a typed reply.</summary>
    internal static unsafe bool Control<TReply>(SafeFileHandle handle, uint code, ref TReply reply)
        where TReply : unmanaged
    {
        fixed (TReply* output = &reply)
        {
            return DeviceIoControl(handle, code, IntPtr.Zero, 0,
                (IntPtr)output, sizeof(TReply), out _, IntPtr.Zero);
        }
    }

    /// <summary>Sends a request with no reply. The hot path for report submission.</summary>
    internal static unsafe bool ControlNoReply<TRequest>(SafeFileHandle handle, uint code, ref TRequest request)
        where TRequest : unmanaged
    {
        fixed (TRequest* input = &request)
        {
            return DeviceIoControl(handle, code, (IntPtr)input, sizeof(TRequest),
                IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
        out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint controlCode,
        IntPtr inBuffer, int inBufferSize,
        IntPtr outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);
}
