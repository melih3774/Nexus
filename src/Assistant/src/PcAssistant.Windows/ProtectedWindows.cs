using System;

namespace PcAssistant.Windows;

/// <summary>
/// PC Assistant's own windows are off limits to PC Assistant's own input tools.
///
/// This exists because of the privileged path. Approval cards — including administrator
/// approval cards — are ordinary WPF controls in PC Assistant's window, so without this a
/// model could in principle propose clicking "Allow" on its own request. Every input and UI
/// Automation action is already gated by the owner, which makes such a sequence expensive
/// rather than free, but "expensive" is the wrong guarantee for an approval button.
///
/// The rule is unconditional and cannot be approved past: it is a property of the target,
/// not a permission.
/// </summary>
public static class ProtectedWindows
{
    /// <summary>Environment variable the bridge sets when it starts the PC tools process.</summary>
    public const string OwnerProcessVariable = "PCASSISTANT_OWNER_PID";

    /// <summary>PC Assistant's process id, or 0 when the tools were not started by it.</summary>
    public static int OwnerProcessId { get; set; }

    public static void InitializeFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(OwnerProcessVariable);
        OwnerProcessId = int.TryParse(value, out var pid) && pid > 0 ? pid : 0;
    }

    public static bool IsProtectedProcess(int processId)
        => OwnerProcessId != 0 && processId == OwnerProcessId;

    public static bool IsProtectedWindow(long hwnd)
    {
        if (OwnerProcessId == 0) return false;
        try
        {
            Native.GetWindowThreadProcessId((nint)hwnd, out var processId);
            return processId == (uint)OwnerProcessId;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void EnsureNotProtectedWindow(long hwnd)
    {
        if (IsProtectedWindow(hwnd)) throw new InvalidOperationException(Refusal);
    }

    public static void EnsureNotProtectedProcess(int processId)
    {
        if (IsProtectedProcess(processId)) throw new InvalidOperationException(Refusal);
    }

    internal const string Refusal =
        "That window belongs to PC Assistant itself. PC Assistant never operates its own interface, " +
        "because its approval cards live there. Ask the owner to do it, or explain what you needed.";
}
