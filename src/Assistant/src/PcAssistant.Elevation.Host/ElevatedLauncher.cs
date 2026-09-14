using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PcAssistant.Elevation.Host;

internal sealed record LaunchedProcess(int ProcessId, IntPtr ProcessHandle, IntPtr ThreadHandle, bool Elevated)
{
    public void CloseHandles()
    {
        if (ThreadHandle != IntPtr.Zero) NativeMethods.CloseHandle(ThreadHandle);
        if (ProcessHandle != IntPtr.Zero) NativeMethods.CloseHandle(ProcessHandle);
    }
}

/// <summary>
/// Creates a process in the owner's interactive session with the owner's elevated token.
///
/// Two documented details matter here and are both deliberate:
///
/// * <c>lpDesktop</c> is "winsta0\default". Without it Windows would place the process on a
///   non-interactive window station where it has no visible window and can receive no input,
///   which is the classic reason "my service started notepad and nothing appeared".
/// * <c>bInheritHandles</c> is false. Handles cannot be inherited across sessions, so a
///   captured-output operation cannot simply hand the child a pipe; that is why the runner
///   stub exists and does its capture inside the owner's own session.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ElevatedLauncher
{
    private const string InteractiveDesktop = @"winsta0\default";

    public static LaunchedProcess Launch(
        ElevatedContext context,
        string executable,
        string commandLine,
        string? workingDirectory,
        bool visible,
        bool suspended)
    {
        if (!NativeMethods.CreateEnvironmentBlock(out var environment, context.Token, inherit: false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Could not build the environment block for the elevated process.");
        }

        try
        {
            var startupInfo = new NativeMethods.StartupInfo
            {
                cb = Marshal.SizeOf<NativeMethods.StartupInfo>(),
                lpDesktop = InteractiveDesktop,
                dwFlags = visible ? NativeMethods.StartfUseShowWindow : 0,
                wShowWindow = visible ? NativeMethods.SwShowNormal : (short)0,
            };

            var flags = NativeMethods.CreateUnicodeEnvironment |
                        (visible ? NativeMethods.CreateNewConsole : NativeMethods.CreateNoWindow) |
                        (suspended ? NativeMethods.CreateSuspended : 0);

            var directory = workingDirectory ?? ProfileDirectory(environment);
            var mutableCommandLine = new StringBuilder(commandLine);

            if (!NativeMethods.CreateProcessAsUserW(
                    context.Token, executable, mutableCommandLine, IntPtr.Zero, IntPtr.Zero,
                    inheritHandles: false, flags, environment, directory,
                    ref startupInfo, out var information))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows refused to start the elevated process: " +
                    new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }

            // Elevation is read back from the process Windows actually created, not assumed
            // from the token that was passed in.
            var elevated = ProcessIsElevated(information.hProcess);

            return new LaunchedProcess(information.dwProcessId, information.hProcess, information.hThread, elevated);
        }
        finally
        {
            NativeMethods.DestroyEnvironmentBlock(environment);
        }
    }

    public static bool ProcessIsElevated(IntPtr process)
    {
        if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery, out var token)) return false;
        using (token) return SessionElevation.IsElevated(token);
    }

    /// <summary>
    /// USERPROFILE from the freshly built block, so a launched terminal opens somewhere the
    /// owner expects rather than in the service's system32 working directory.
    /// </summary>
    private static string? ProfileDirectory(IntPtr environment)
    {
        try
        {
            var offset = 0;
            while (true)
            {
                var entry = Marshal.PtrToStringUni(environment + offset);
                if (string.IsNullOrEmpty(entry)) return null;
                offset += (entry.Length + 1) * sizeof(char);

                if (entry.StartsWith("USERPROFILE=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = entry["USERPROFILE=".Length..];
                    return Directory.Exists(value) ? value : null;
                }
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A job object that owns exactly one operation's process tree, so cancelling an
    /// operation cannot reach anything PC Assistant did not start.
    /// </summary>
    public static IntPtr CreateKillOnCloseJob()
    {
        var job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;

        var limits = new NativeMethods.JobExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose;

        var size = Marshal.SizeOf<NativeMethods.JobExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            NativeMethods.SetInformationJobObject(job, NativeMethods.JobObjectExtendedLimitInformation, buffer, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return job;
    }
}
