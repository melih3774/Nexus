using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PcCore.Update.Windows;

/// <summary>
/// Reads the installed SecureCapture and HID driver versions from the registry, as a standard user.
/// <para>
/// The service's image path is under <c>HKLM\SYSTEM\CurrentControlSet\Services</c> and the driver's
/// version is on its device class key; both are readable without elevation, which is what lets PC
/// Core say "no administrator approval will be needed" before anyone is asked for it.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsComponentProbe : IElevatedComponentProbe
{
    public const string SecureCaptureServiceName = "NexusSecureCapture";

    /// <summary>The root-enumerated devnode the driver install created (devgen, instance id NexusHid).</summary>
    public const string HidDeviceInstance = @"ROOT\DEVGEN\NEXUSHID";

    public InstalledComponent SecureCapture()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{SecureCaptureServiceName}");

            if (key?.GetValue("ImagePath") is not string imagePath)
            {
                return InstalledComponent.Absent;
            }

            var executable = ExecutableFrom(imagePath);

            if (executable is null)
            {
                return new InstalledComponent(true, null);
            }

            // The assembly carries the version; the apphost .exe beside it is the same generic stub
            // for every .NET application and says nothing about which build it launches.
            var assembly = Path.ChangeExtension(executable, ".dll");

            return new InstalledComponent(true, File.Exists(assembly) ? VersionOf(assembly) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new InstalledComponent(true, null);
        }
    }

    public InstalledComponent HidDriver()
    {
        try
        {
            using var device = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{HidDeviceInstance}");

            if (device?.GetValue("Driver") is not string driverKey)
            {
                return InstalledComponent.Absent;
            }

            using var driver = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driverKey}");

            return new InstalledComponent(true, driver?.GetValue("DriverVersion") as string)
            {
                RestartPending = HidDriverRestartPending(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new InstalledComponent(true, null);
        }
    }

    /// <summary>
    /// Whether Windows is waiting for a restart to load the driver it has configured for the Nexus
    /// device: <c>DN_NEED_RESTART</c>, or problem code 14. The class key above is configuration and
    /// already names the new version. Runtime state, so a restart clears it - which is how a recorded
    /// "restart required" is known to be over. Readable without elevation.
    /// </summary>
    public static bool HidDriverRestartPending()
    {
        if (CM_Locate_DevNodeW(out var devInst, HidDeviceInstance, 0) != 0
            || CM_Get_DevNode_Status(out var status, out var problem, devInst, 0) != 0)
        {
            return false;
        }

        return (status & DnNeedRestart) != 0 || problem == HidDriverLifecycle.ProblemNeedRestart;
    }

    private const uint DnNeedRestart = 0x00000100;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    /// <summary>The product version of an assembly, without build metadata.</summary>
    public static string? VersionOf(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+')[0];
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary><c>"C:\...\PcCore.SecureCapture.exe" --service</c> to <c>C:\...\PcCore.SecureCapture.exe</c>.</summary>
    public static string? ExecutableFrom(string imagePath)
    {
        var trimmed = imagePath.Trim();

        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed[1..end] : null;
        }

        var space = trimmed.IndexOf(" --", StringComparison.Ordinal);
        return space > 0 ? trimmed[..space] : trimmed;
    }
}
