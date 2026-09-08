using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PcCore.Host;

/// <summary>What is in front, and whether Nexus can drive it.</summary>
internal sealed record ForegroundWindow(
    uint ProcessId,
    string ProcessName,
    string IntegrityLevel,
    bool Elevated,
    string? Diagnostic);

/// <summary>
/// Identifies the window the owner has in front, and how privileged it is.
/// <para>
/// <b>Why this exists.</b> "Elevated applications are controllable" is the one claim in the native
/// input work that cannot be proved without an elevated application, and Nexus must not create one:
/// elevating something is the owner's decision, taken at their own UAC prompt. So instead of faking
/// it, this reports the truth about whatever the owner has actually put in front — its integrity
/// level, and whether input reached it — which turns the test into one command they run with an
/// Administrator window focused.
/// </para>
/// <para>
/// It reads a process token's integrity level and its name. Nothing else: not the window title, not
/// the command line, not the contents.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ForegroundWindowProbe
{
    public static ForegroundWindow Describe()
    {
        var window = GetForegroundWindow();

        if (window == nint.Zero)
        {
            return new ForegroundWindow(0, "(none)", "UNKNOWN", false, "no window has focus");
        }

        GetWindowThreadProcessId(window, out var processId);

        if (processId == 0)
        {
            return new ForegroundWindow(0, "(unknown)", "UNKNOWN", false, "the window has no process");
        }

        var name = "(unknown)";

        try
        {
            name = Process.GetProcessById((int)processId).ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The process ended between the two calls.
        }

        var (level, elevated, diagnostic) = IntegrityOf(processId);

        return new ForegroundWindow(processId, name, level, elevated, diagnostic);
    }

    /// <summary>
    /// The integrity level of a process, by its token.
    /// <para>
    /// A medium-integrity process can open a high-integrity one for limited query rights — that is
    /// how Task Manager lists elevated processes — but it cannot open the token. So the level is
    /// read where possible and reported as unknown-but-probably-higher where it is not, which is
    /// itself the answer worth having: if PC Core cannot read the token, PC Core cannot send it
    /// input either.
    /// </para>
    /// </summary>
    private static (string Level, bool Elevated, string? Diagnostic) IntegrityOf(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);

        if (process.IsInvalid)
        {
            process.Dispose();
            return ("UNKNOWN", false, new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                token.Dispose();

                // Denied here almost always means the target is more privileged than we are.
                return ("HIGH_OR_ABOVE", true, "the token could not be opened, which means it is more privileged");
            }

            using (token)
            {
                GetTokenInformation(token, TokenIntegrityLevel, nint.Zero, 0, out var needed);

                var buffer = Marshal.AllocHGlobal((int)needed);

                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _))
                    {
                        return ("UNKNOWN", false, new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    }

                    // TOKEN_MANDATORY_LABEL is a SID_AND_ATTRIBUTES: the SID pointer comes first.
                    var sid = Marshal.ReadIntPtr(buffer);
                    var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    var rid = (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));

                    return rid switch
                    {
                        < 0x1000 => ("UNTRUSTED", false, null),
                        < 0x2000 => ("LOW", false, null),
                        < 0x3000 => ("MEDIUM", false, null),
                        < 0x4000 => ("HIGH", true, null),
                        _ => ("SYSTEM", true, null),
                    };
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(
        uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        Microsoft.Win32.SafeHandles.SafeProcessHandle process,
        uint access,
        out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token,
        int informationClass,
        nint information,
        uint length,
        out uint returnLength);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthorityCount(nint sid);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthority(nint sid, uint index);

    /// <summary>A short, printable summary. Never the window title.</summary>
    public static string Summarise(ForegroundWindow window)
    {
        var builder = new StringBuilder();
        builder.Append(window.ProcessName).Append(" (pid ").Append(window.ProcessId).Append(')');
        builder.Append(", integrity ").Append(window.IntegrityLevel);

        if (window.Diagnostic is { } diagnostic)
        {
            builder.Append(" — ").Append(diagnostic);
        }

        return builder.ToString();
    }
}
