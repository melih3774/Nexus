using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Microsoft.Win32;

namespace PcCore.Update.Windows;

/// <summary>
/// <see cref="IHidDriverHost"/> on Windows. Runs elevated, from the updater's elevated half.
/// <para>
/// Every call is a documented path: pnputil for adding and deleting packages, the Configuration
/// Manager for the device's state and stack, <c>UpdateDriverForPlugAndPlayDevices</c> for forcing one
/// package onto the device, and a read of the UMDF service key. Nothing here writes that key - only
/// Windows' own install of the package does.
/// </para>
/// <para>
/// <b>What is running is asked of the driver.</b> Windows' properties for the device describe the bound
/// package even when a vetoed restart left the previous driver loaded, so the running version comes
/// from <c>IOCTL_NEXUS_HID_GET_VERSION</c> on the device's own control interface: the number compiled
/// into the DLL that answers. The handle is opened, asked once and closed at once - an open handle is
/// exactly what vetoes a restart - and the request is one the driver answers for any handle without
/// creating or touching a device.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsHidDriverHost : IHidDriverHost
{
    /// <summary>The Nexus HID control interface. Mirrors <c>NexusHidProtocol.ControlInterface</c>; a test holds the two together.</summary>
    internal static readonly Guid ControlInterface = new("6d1f4a2e-9c3b-4e7a-b5d8-1a2c3e4f5b60");

    /// <summary>
    /// <c>IOCTL_NEXUS_HID_GET_VERSION</c>: <c>CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED,
    /// FILE_WRITE_ACCESS)</c>. Mirrors <c>NexusHidProtocol.IoctlGetVersion</c>; a test holds the two together.
    /// </summary>
    internal const uint IoctlGetVersion = 0x0022A000;

    /// <summary>How long a device that has just started may take to offer its control interface.</summary>
    private static readonly TimeSpan InterfaceWait = TimeSpan.FromSeconds(5);

    private const string UmdfServiceKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\WUDF\Services\NexusHidDriver";

    private static string Pnputil => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");

    public (int Code, string Output) AddAndInstall(string infPath) =>
        Run(Pnputil, $"/add-driver \"{infPath}\" /install");

    public (bool Succeeded, string Detail) Reapply(string infPath)
    {
        if (UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, HidDriverLifecycle.HardwareId, infPath, InstallFlagForce | InstallFlagNonInteractive, out var reboot))
        {
            return (true, reboot ? "Windows wants a restart to finish." : string.Empty);
        }

        var error = Marshal.GetLastWin32Error();
        return (false, $"Win32 error 0x{error:X8}.");
    }

    public HidDeviceState ReadDevice()
    {
        if (CM_Locate_DevNodeW(out var devInst, HidDriverLifecycle.DeviceInstance, CmLocateDevnodeNormal) != CrSuccess)
        {
            return HidDeviceState.Absent;
        }

        var problem = ReadUInt32(devInst, ProblemCodeKey) ?? 0;
        var status = CM_Get_DevNode_Status(out var flags, out _, devInst, 0) == CrSuccess ? flags : 0;
        var restartPending = (status & DnNeedRestart) != 0 || problem == HidDriverLifecycle.ProblemNeedRestart;
        var stack = (ReadStrings(devInst, StackKey) ?? []).Select(ServiceName).ToList();

        return new HidDeviceState(
            Present: true,
            ProblemCode: problem,
            DriverInf: ReadStrings(devInst, DriverInfPathKey)?.FirstOrDefault(),
            DriverVersion: ReadStrings(devInst, DriverVersionKey)?.FirstOrDefault(),
            Stack: stack,
            RestartPending: restartPending,
            RunningVersion: RunningVersion(
                waitForInterface: (status & DnStarted) != 0 && !restartPending && stack.Contains("WUDFRd", StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>What the Nexus driver loaded in the device says it is; null when none answers.</summary>
    /// <param name="waitForInterface">
    /// The device is started under the reflector, so a driver should answer: give one that has only just
    /// started a moment to offer its interface.
    /// </param>
    internal static string? RunningVersion(bool waitForInterface)
    {
        var deadline = DateTime.UtcNow + (waitForInterface ? InterfaceWait : TimeSpan.Zero);

        while (true)
        {
            if (ControlInterfacePath() is { } path && AskVersion(path) is { } packed)
            {
                return FormatVersion(packed);
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            Thread.Sleep(250);
        }
    }

    /// <summary><c>0x00090203</c> to <c>0.9.2.3</c>: one byte per part, as <c>NEXUS_HID_DRIVER_VERSION</c> is stamped.</summary>
    internal static string FormatVersion(uint packed) =>
        $"{packed >> 24}.{(packed >> 16) & 0xFF}.{(packed >> 8) & 0xFF}.{packed & 0xFF}";

    /// <summary>The control interface of the Nexus devnode itself, never of some other device offering the same class.</summary>
    private static string? ControlInterfacePath()
    {
        var guid = ControlInterface;

        if (CM_Get_Device_Interface_List_SizeW(out var length, ref guid, HidDriverLifecycle.DeviceInstance, CmGetDeviceInterfaceListPresent) != CrSuccess || length <= 1)
        {
            return null;
        }

        var buffer = new char[length];

        if (CM_Get_Device_Interface_ListW(ref guid, HidDriverLifecycle.DeviceInstance, buffer, length, CmGetDeviceInterfaceListPresent) != CrSuccess)
        {
            return null;
        }

        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private static uint? AskVersion(string path)
    {
        using var handle = CreateFileW(path, GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return null;
        }

        // { UINT32 ProtocolVersion; UINT32 DriverVersion; } - the one reply every protocol version keeps.
        var reply = new byte[8];

        return DeviceIoControl(handle, IoctlGetVersion, IntPtr.Zero, 0, reply, reply.Length, out var returned, IntPtr.Zero) && returned == reply.Length
            ? BitConverter.ToUInt32(reply, 4)
            : null;
    }

    public UmdfRegistration? ReadUmdfRegistration()
    {
        using var key = Registry.LocalMachine.OpenSubKey(UmdfServiceKey);

        if (key is null)
        {
            return null;
        }

        var extensions = key.GetValue("WdfExtensions") switch
        {
            string[] many => many,
            string one => [one],
            _ => [],
        };

        return new UmdfRegistration(
            key.GetValue("ImagePath") as string,
            key.GetValue("WdfMajorVersion") as int?,
            key.GetValue("WdfMinorVersion") as int?,
            extensions);
    }

    public IReadOnlyList<DriverPackage>? MatchingPackages()
    {
        // XML to a file rather than parsed off stdout: it names its own encoding there, and the console
        // code page does not get a say.
        var output = Path.Combine(Path.GetTempPath(), $"nexus-hid-drivers-{Guid.NewGuid():N}.xml");

        try
        {
            var (code, text) = Run(Pnputil, $"/enum-devices /instanceid \"{HidDriverLifecycle.DeviceInstance}\" /drivers /format xml /output-file \"{output}\"");

            if (code != 0 || !File.Exists(output))
            {
                return null;
            }

            return PnputilDriverList.Parse(XDocument.Load(output));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
        finally
        {
            try
            {
                File.Delete(output);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public (bool Deleted, string Detail) DeletePackage(string publishedName)
    {
        // Only ever an oem#.inf name that came back from pnputil: nothing else reaches the command line.
        if (!PublishedInfName().IsMatch(publishedName))
        {
            return (false, "not a published driver package name.");
        }

        var (code, output) = Run(Pnputil, $"/delete-driver {publishedName}");
        return (code == 0, $"exit {code}. {output}");
    }

    public string? Sha256OfFile(string path)
    {
        try
        {
            return File.Exists(path) ? FileHash.Sha256OfFile(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><c>\Driver\WUDFRd</c> to <c>WUDFRd</c>.</summary>
    internal static string ServiceName(string driverObject)
    {
        var slash = driverObject.LastIndexOf('\\');
        return slash >= 0 ? driverObject[(slash + 1)..] : driverObject;
    }

    private static (int Code, string Output) Run(string executable, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException($"{Path.GetFileName(executable)} could not be started.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            process.Kill();
            return (-1, "timed out");
        }

        var text = (stdout.Result + " " + stderr.Result).ReplaceLineEndings(" ").Trim();
        return (process.ExitCode, text.Length > 600 ? text[..600] : text);
    }

    private static uint? ReadUInt32(uint devInst, DevPropKey key)
    {
        var buffer = ReadProperty(devInst, key, DevPropTypeUInt32);
        return buffer is { Length: >= 4 } ? BitConverter.ToUInt32(buffer, 0) : null;
    }

    /// <summary>A string or a string list, as its elements.</summary>
    private static string[]? ReadStrings(uint devInst, DevPropKey key)
    {
        var buffer = ReadProperty(devInst, key, DevPropTypeString, DevPropTypeStringList);

        return buffer is null
            ? null
            : Encoding.Unicode.GetString(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static byte[]? ReadProperty(uint devInst, DevPropKey key, params uint[] acceptedTypes)
    {
        uint size = 0;
        var result = CM_Get_DevNode_PropertyW(devInst, ref key, out _, null, ref size, 0);

        if (result != CrBufferSmall || size == 0)
        {
            return null;
        }

        var buffer = new byte[size];

        if (CM_Get_DevNode_PropertyW(devInst, ref key, out var type, buffer, ref size, 0) != CrSuccess
            || !acceptedTypes.Contains(type))
        {
            return null;
        }

        return buffer;
    }

    [GeneratedRegex(@"^oem\d{1,5}\.inf$", RegexOptions.IgnoreCase)]
    private static partial Regex PublishedInfName();

    // ------------------------------------------------------------------ interop

    private const uint InstallFlagForce = 0x00000001;
    private const uint InstallFlagNonInteractive = 0x00000004;
    private const uint CmLocateDevnodeNormal = 0x00000000;
    private const uint CmGetDeviceInterfaceListPresent = 0x00000000;
    private const uint DnStarted = 0x00000008;
    private const uint DnNeedRestart = 0x00000100;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint CrSuccess = 0x00000000;
    private const uint CrBufferSmall = 0x0000001A;
    private const uint DevPropTypeUInt32 = 0x00000007;
    private const uint DevPropTypeString = 0x00000012;
    private const uint DevPropTypeStringList = 0x00002012;

    private static readonly DevPropKey ProblemCodeKey = new(new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"), 3);
    private static readonly DevPropKey StackKey = new(new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 14);
    private static readonly DevPropKey DriverVersionKey = new(new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), 3);
    private static readonly DevPropKey DriverInfPathKey = new(new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), 5);

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey(Guid fmtid, uint pid)
    {
        public Guid FmtId = fmtid;
        public uint Pid = pid;
    }

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(
        IntPtr hwndParent,
        string hardwareId,
        string fullInfPath,
        uint installFlags,
        [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string deviceId, char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle device, uint controlCode,
        IntPtr inBuffer, int inBufferSize,
        [Out] byte[] outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(
        uint devInst,
        ref DevPropKey propertyKey,
        out uint propertyType,
        byte[]? propertyBuffer,
        ref uint propertyBufferSize,
        uint flags);
}

/// <summary>Reads <c>pnputil /enum-devices /drivers /format xml</c>.</summary>
public static class PnputilDriverList
{
    /// <summary>The matching drivers of the first device in the document.</summary>
    public static IReadOnlyList<DriverPackage> Parse(XDocument document)
    {
        var device = document.Root?.Element("Device");

        if (device is null)
        {
            return [];
        }

        return device.Element("MatchingDrivers")?.Elements("DriverName")
            .Select(d => new DriverPackage(
                PublishedName: (string?)d.Attribute("DriverName") ?? string.Empty,
                OriginalName: (string?)d.Element("OriginalName") ?? string.Empty,
                Provider: (string?)d.Element("ProviderName") ?? string.Empty,
                Version: VersionOf((string?)d.Element("DriverVersion")),
                Installed: ((string?)d.Element("Status") ?? string.Empty).Contains("Installed", StringComparison.OrdinalIgnoreCase)))
            .Where(p => p.PublishedName.Length > 0)
            .ToList() ?? [];
    }

    /// <summary><c>09/15/2026 0.9.2.2</c> to <c>0.9.2.2</c>.</summary>
    private static string? VersionOf(string? dateAndVersion) =>
        dateAndVersion?.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
}
