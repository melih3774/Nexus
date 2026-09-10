using System.Runtime.InteropServices;
using System.Text;

namespace PcCore.SecureCapture;

internal sealed record ServiceStatusReport(bool Installed, bool Running, string? ImagePath);

internal sealed record ServiceCommandResult(bool Ok, string Message);

/// <summary>
/// Registering and removing the service, through the Service Control Manager rather than
/// <c>sc.exe</c>.
/// <para>
/// Directly, because the difference matters for the one thing that can go wrong: <c>sc.exe</c>
/// reports "access denied" identically whether the terminal is not elevated or the service name is
/// taken by something else, and the owner running an install command once deserves to be told which.
/// </para>
/// </summary>
internal static class ServiceControl
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceDelete = 0x00010000;

    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceErrorNormal = 0x00000001;

    private const int ServiceRunning = 0x00000004;
    private const int ServiceConfigDescription = 1;

    private const int ErrorServiceExists = 1073;
    private const int ErrorServiceMarkedForDelete = 1072;
    private const int ErrorAccessDenied = 5;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceAlreadyRunning = 1056;

    private const uint ServiceChangeConfig = 0x0002;
    private const uint NoChange = 0xFFFFFFFF;

    public static ServiceStatusReport Query(string name)
    {
        var manager = OpenSCManagerW(null, null, ScManagerConnect);

        if (manager == nint.Zero)
        {
            return new ServiceStatusReport(false, false, null);
        }

        try
        {
            var service = OpenServiceW(manager, name, ServiceQueryConfig | ServiceQueryStatus);

            if (service == nint.Zero)
            {
                return new ServiceStatusReport(false, false, null);
            }

            try
            {
                var running = QueryServiceStatus(service, out var status) && status.CurrentState == ServiceRunning;
                return new ServiceStatusReport(true, running, ImagePath(service));
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    /// <summary>
    /// Stops the service if it is running, so its executable can be replaced.
    /// <para>
    /// Reinstalling over a running service is the ordinary case during development and the one that
    /// fails most confusingly: the copy fails with "the file is in use" and the owner is left with a
    /// service pointing at a build that no longer exists.
    /// </para>
    /// </summary>
    public static bool StopIfRunning(string name)
    {
        var manager = OpenSCManagerW(null, null, ScManagerConnect);

        if (manager == nint.Zero)
        {
            return false;
        }

        try
        {
            var service = OpenServiceW(manager, name, ServiceStop | ServiceQueryStatus);

            if (service == nint.Zero)
            {
                return false;
            }

            try
            {
                var status = new ServiceStatus();

                if (!ControlService(service, 1, ref status))
                {
                    return false;
                }

                for (var i = 0; i < 40; i++)
                {
                    if (!QueryServiceStatus(service, out var current) || current.CurrentState != ServiceRunning)
                    {
                        return true;
                    }

                    Thread.Sleep(250);
                }

                return false;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    public static ServiceCommandResult Install(string name, string displayName, string commandLine)
    {
        var manager = OpenSCManagerW(null, null, ScManagerConnect | ScManagerCreateService);

        if (manager == nint.Zero)
        {
            var error = Marshal.GetLastWin32Error();

            return new ServiceCommandResult(
                false,
                error == ErrorAccessDenied
                    ? "Installing a service needs an elevated terminal."
                    : $"The service control manager could not be opened (error {error}).");
        }

        try
        {
            var service = CreateServiceW(
                manager,
                name,
                displayName,
                ServiceQueryStatus | ServiceStart | ServiceStop,
                ServiceWin32OwnProcess,
                ServiceAutoStart,
                ServiceErrorNormal,
                commandLine,
                null,
                nint.Zero,
                null,

                // LocalSystem. The Secure Desktop grants access to SYSTEM and nothing else, which is
                // the entire reason this service exists; any lesser account would be installed
                // privilege that buys nothing.
                "LocalSystem",
                null);

            if (service == nint.Zero)
            {
                var error = Marshal.GetLastWin32Error();

                if (error == ErrorServiceExists)
                {
                    return Reconfigure(manager, name, displayName, commandLine);
                }

                return new ServiceCommandResult(false, error switch
                {
                    ErrorServiceMarkedForDelete => $"{name} is being removed; restart the PC and install again.",
                    ErrorAccessDenied => "Installing a service needs an elevated terminal.",
                    _ => $"The service could not be created (error {error}).",
                });
            }

            try
            {
                Describe(service, "Lets an authenticated Nexus session see the UAC Secure Desktop. Starts a "
                    + "capture process in the console session on request from the signed-in user, and does "
                    + "nothing else.");

                if (!StartServiceW(service, 0, null))
                {
                    var error = Marshal.GetLastWin32Error();
                    return new ServiceCommandResult(
                        true, $"{displayName} installed, but did not start (error {error}). It will start at boot.");
                }

                return new ServiceCommandResult(true, $"{displayName} installed and started.");
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    public static ServiceCommandResult Uninstall(string name)
    {
        var manager = OpenSCManagerW(null, null, ScManagerConnect);

        if (manager == nint.Zero)
        {
            return new ServiceCommandResult(false, "Removing a service needs an elevated terminal.");
        }

        try
        {
            var service = OpenServiceW(manager, name, ServiceStop | ServiceDelete | ServiceQueryStatus);

            if (service == nint.Zero)
            {
                var error = Marshal.GetLastWin32Error();

                return error == ErrorServiceDoesNotExist
                    ? new ServiceCommandResult(true, $"{name} is not installed.")
                    : new ServiceCommandResult(false, $"{name} could not be opened (error {error}).");
            }

            try
            {
                var stop = new ServiceStatus();
                ControlService(service, 1, ref stop);

                return DeleteService(service)
                    ? new ServiceCommandResult(true, $"{name} removed.")
                    : new ServiceCommandResult(false, $"{name} could not be removed (error {Marshal.GetLastWin32Error()}).");
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    /// <summary>Points an already-installed service at this build and starts it again.</summary>
    private static ServiceCommandResult Reconfigure(nint manager, string name, string displayName, string commandLine)
    {
        var service = OpenServiceW(manager, name, ServiceChangeConfig | ServiceStart | ServiceQueryStatus);

        if (service == nint.Zero)
        {
            return new ServiceCommandResult(
                false, $"{name} is installed but could not be reconfigured (error {Marshal.GetLastWin32Error()}).");
        }

        try
        {
            if (!ChangeServiceConfigW(
                    service, NoChange, ServiceAutoStart, NoChange, commandLine, null, nint.Zero, null, null, null, displayName))
            {
                return new ServiceCommandResult(
                    false, $"{name} could not be pointed at this build (error {Marshal.GetLastWin32Error()}).");
            }

            if (!StartServiceW(service, 0, null))
            {
                var error = Marshal.GetLastWin32Error();

                return error == ErrorServiceAlreadyRunning
                    ? new ServiceCommandResult(true, $"{displayName} updated; it was already running.")
                    : new ServiceCommandResult(true, $"{displayName} updated, but did not start (error {error}).");
            }

            return new ServiceCommandResult(true, $"{displayName} updated and started.");
        }
        finally
        {
            CloseServiceHandle(service);
        }
    }

    private static void Describe(nint service, string description)
    {
        var text = Marshal.StringToHGlobalUni(description);

        try
        {
            var info = new ServiceDescription { Description = text };
            ChangeServiceConfig2W(service, ServiceConfigDescription, ref info);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    private static string? ImagePath(nint service)
    {
        QueryServiceConfigW(service, nint.Zero, 0, out var needed);

        if (needed == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)needed);

        try
        {
            if (!QueryServiceConfigW(service, buffer, needed, out _))
            {
                return null;
            }

            // The image path is a pointer inside the returned block; QUERY_SERVICE_CONFIG puts it at
            // offset 12 on x64 (three DWORDs, then the aligned pointer).
            var pointer = Marshal.ReadIntPtr(buffer, 16);
            return pointer == nint.Zero ? null : Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDescription
    {
        public nint Description;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenServiceW(nint manager, string name, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateServiceW(
        nint manager,
        string name,
        string displayName,
        uint desiredAccess,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPath,
        string? loadOrderGroup,
        nint tagId,
        string? dependencies,
        string? serviceStartName,
        string? password);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(nint service, int argc, string[]? argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(nint service, int control, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(nint service, out ServiceStatus status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(nint service, nint config, uint size, out uint needed);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(nint service, int level, ref ServiceDescription info);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfigW(
        nint service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPath,
        string? loadOrderGroup,
        nint tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(nint service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
}
