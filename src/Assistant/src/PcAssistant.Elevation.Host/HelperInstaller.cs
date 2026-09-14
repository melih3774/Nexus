using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;

namespace PcAssistant.Elevation.Host;

/// <summary>
/// The one-time, owner-visible, UAC-confirmed step that creates the privileged boundary —
/// and the equally explicit step that removes it again.
///
/// Installing to Program Files is not cosmetic. The service runs as LocalSystem, so if its
/// binaries lived anywhere the unelevated owner account could write, any process running as
/// the owner could replace them and be SYSTEM after the next service start. The install
/// refuses to proceed if its target is not under Program Files.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class HelperInstaller
{
    public static int Install(string sourceDirectory, string clientExecutable, string ownerSid, string? reportPath)
    {
        try
        {
            RequireElevated();

            if (!Directory.Exists(sourceDirectory))
                throw new InvalidOperationException("Source directory does not exist: " + sourceDirectory);
            if (!File.Exists(clientExecutable))
                throw new InvalidOperationException("PC Assistant executable does not exist: " + clientExecutable);

            var owner = new SecurityIdentifier(ownerSid);
            var account = TranslateOrSid(owner);

            var programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            var target = Path.GetFullPath(HelperPaths.InstallRoot);
            if (!target.StartsWith(programFiles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to install a SYSTEM service outside Program Files. Target was " + target);
            }

            RemoveService();

            Directory.CreateDirectory(target);
            CopyPayload(sourceDirectory, target);

            var state = new HelperState
            {
                OwnerSid = owner.Value,
                OwnerAccount = account,
                ClientExecutable = Path.GetFullPath(clientExecutable),
                InstalledVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                Protocol = PrivilegedProtocol.Version,
                InstalledAt = DateTimeOffset.Now,
            };
            File.WriteAllText(HelperPaths.StateFile,
                JsonSerializer.Serialize(state, new JsonSerializerOptions(PrivilegedProtocol.Json) { WriteIndented = true }),
                new UTF8Encoding(false));

            CreateService();
            StartService();

            AuditLog.Write("Install", null, null, target, "Ok", "owner=" + account + " client=" + state.ClientExecutable);
            return Report(reportPath, true, $"Privileged helper installed for {account} and started.");
        }
        catch (Exception ex)
        {
            return Report(reportPath, false, ex.Message);
        }
    }

    public static int Uninstall(string? reportPath)
    {
        try
        {
            RequireElevated();
            RemoveService();

            var target = Path.GetFullPath(HelperPaths.InstallRoot);
            var removed = new List<string>();
            if (Directory.Exists(target))
            {
                // Deleting the directory this process is running from would fail; PC Assistant
                // deliberately runs uninstall from its own bundled copy, not the installed one.
                if (string.Equals(Path.GetDirectoryName(Environment.ProcessPath), target, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Uninstall was started from inside the installation directory. Run it from PC Assistant's own copy.");
                }

                Directory.Delete(target, recursive: true);
                removed.Add(target);
            }

            var parent = Path.GetDirectoryName(target);
            if (parent is not null && Directory.Exists(parent) &&
                Directory.GetFileSystemEntries(parent).Length == 0)
            {
                Directory.Delete(parent);
                removed.Add(parent);
            }

            return Report(reportPath, true, removed.Count == 0
                ? "Nothing was installed; no privileged helper remained."
                : "Privileged helper service removed and " + string.Join(", ", removed) + " deleted.");
        }
        catch (Exception ex)
        {
            return Report(reportPath, false, ex.Message);
        }
    }

    // ------------------------------------------------------------- internals

    private static void RequireElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("This step must run elevated. It is started through a normal Windows UAC prompt.");
    }

    private static string TranslateOrSid(SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch (Exception) { return sid.Value; }
    }

    private static void CopyPayload(string source, string target)
    {
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyOverExitingHelper(file, destination);
        }

        if (!File.Exists(HelperPaths.HostExecutable))
            throw new InvalidOperationException("The copied payload does not contain " + HelperPaths.HostExecutable);
    }

    /// <summary>
    /// Deleting a service tells the SCM to forget it; it does not wait for the service's
    /// process to finish exiting, and until it does, Windows still has its assemblies mapped.
    /// An upgrade that copies straight over them fails with a sharing violation — and it does
    /// so *after* the old service has already been removed, which is the worst moment to give
    /// up. Waiting is the right answer, and retrying covers anything else that is momentarily
    /// reading the directory.
    /// </summary>
    private static void CopyOverExitingHelper(string source, string destination)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 60 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>Waits for any process still running the installed helper binary to exit.</summary>
    private static void WaitForHelperProcessesToExit()
    {
        var installed = Path.GetFullPath(HelperPaths.HostExecutable);
        if (!File.Exists(installed)) return;

        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (!AnyProcessIsRunning(installed)) return;
            Thread.Sleep(250);
        }
    }

    private static bool AnyProcessIsRunning(string imagePath)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(imagePath)))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    // This installer runs from PC Assistant's own copy of the payload, so only
                    // a process running the *installed* binary is one we have to wait for.
                    if (string.Equals(process.MainModule?.FileName, imagePath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception)
                {
                    // Exited between enumeration and inspection, or not readable. Either way
                    // it is not something to keep waiting on by name alone.
                }
            }
        }

        return false;
    }

    private static void CreateService()
    {
        var manager = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerAllAccess);
        if (manager == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the Service Control Manager.");

        try
        {
            var binaryPath = "\"" + HelperPaths.HostExecutable + "\" --service";
            var service = NativeMethods.CreateService(
                manager, PrivilegedProtocol.ServiceName, PrivilegedProtocol.ServiceDisplayName,
                NativeMethods.ServiceAllAccess, NativeMethods.ServiceWin32OwnProcess,
                NativeMethods.ServiceAutoStart, NativeMethods.ServiceErrorNormal, binaryPath,
                null, IntPtr.Zero, null, null, null);   // null account == LocalSystem

            if (service == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the privileged helper service.");

            try
            {
                var description = new NativeMethods.ServiceDescription
                {
                    lpDescription = "Performs individually owner-approved administrator operations requested by " +
                                    "PC Assistant. Each operation runs with the signed-in owner's own elevated token " +
                                    "in their desktop session, never as SYSTEM.",
                };
                NativeMethods.ChangeServiceConfig2(service, NativeMethods.ServiceConfigDescription, ref description);
            }
            finally
            {
                NativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(manager);
        }
    }

    private static void StartService()
    {
        using var controller = new ServiceController(PrivilegedProtocol.ServiceName);
        if (controller.Status != ServiceControllerStatus.Running)
        {
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>Stops and deletes the service if it exists. Safe to call when it does not.</summary>
    private static void RemoveService()
    {
        try
        {
            using var controller = new ServiceController(PrivilegedProtocol.ServiceName);
            if (controller.Status != ServiceControllerStatus.Stopped)
            {
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (InvalidOperationException)
        {
            return;     // not registered
        }
        catch (Exception)
        {
            // Already stopping, or refusing to stop; deletion below still reports honestly.
        }

        var manager = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerAllAccess);
        if (manager == IntPtr.Zero) return;
        try
        {
            var service = NativeMethods.OpenService(manager, PrivilegedProtocol.ServiceName, NativeMethods.ServiceAllAccess);
            if (service == IntPtr.Zero) return;
            try
            {
                if (!NativeMethods.DeleteService(service))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 1072)  // already marked for deletion
                        throw new Win32Exception(error, "Could not delete the privileged helper service.");
                }
            }
            finally
            {
                NativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(manager);
        }

        WaitForServiceToDisappear();
        WaitForHelperProcessesToExit();
    }

    private static void WaitForServiceToDisappear()
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using var controller = new ServiceController(PrivilegedProtocol.ServiceName);
                _ = controller.Status;
            }
            catch (InvalidOperationException)
            {
                return;
            }
            Thread.Sleep(250);
        }
    }

    /// <summary>
    /// The elevated process has no console the unelevated app can read, so the outcome is
    /// written to a file PC Assistant named in advance.
    /// </summary>
    private static int Report(string? reportPath, bool ok, string message)
    {
        var payload = JsonSerializer.Serialize(new { ok, message }, PrivilegedProtocol.Json);
        Console.Out.WriteLine(payload);

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                File.WriteAllText(reportPath, payload, new UTF8Encoding(false));
            }
            catch (Exception) { /* the caller falls back to a live status check */ }
        }

        return ok ? 0 : 1;
    }
}
