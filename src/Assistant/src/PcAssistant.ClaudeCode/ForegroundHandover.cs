using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcAssistant.ClaudeCode;

/// <summary>
/// Windows only lets the process that owns the foreground pass that right to another one. Answering an
/// approval card is exactly when PC Assistant owns it — the owner just clicked here — so this is the one
/// moment the PC tools process can be granted the right to put the target window back in front. Without
/// this, every approved click lands on a window the owner's own approval just pushed behind PC Assistant.
///
/// Kept as one of two activation paths alongside AttachThreadInput in ForegroundService. The regression
/// suite proves the atomic acquisition, not this call's individual necessity: disabling it alone left the
/// end-to-end test green on the development machine. It stays because it is the documented mechanism and
/// because the two paths fail under different foreground-lock configurations.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ForegroundHandover
{
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Best effort: it fails harmlessly when PC Assistant is not the foreground process.</summary>
    public static bool GrantTo(int processId)
    {
        if (processId <= 0) return false;
        try { return AllowSetForegroundWindow(processId); }
        catch (System.Exception) { return false; }
    }
}
