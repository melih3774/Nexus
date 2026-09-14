using System.Text.Json;

namespace PcAssistant.Elevation;

/// <summary>
/// Where the privileged helper lives, and the record written when the owner installed it.
///
/// Everything the helper trusts is kept under Program Files, whose default ACL is
/// administrators-only. That matters: if the service binary or its state lived anywhere the
/// unelevated owner account can write, any process running as the owner could replace them
/// and be SYSTEM after the next service start. The install flow verifies this.
/// </summary>
public static class HelperPaths
{
    public static string InstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PcAssistant", "Elevation");

    public static string HostExecutable => Path.Combine(InstallRoot, "PcAssistant.Elevation.Host.exe");

    public static string StateFile => Path.Combine(InstallRoot, "helper-state.json");

    public static string AuditLogFile => Path.Combine(InstallRoot, "privileged-audit.log");

    /// <summary>True when the helper binary is present. Says nothing about the service running.</summary>
    public static bool IsInstalled => File.Exists(HostExecutable);

    public static HelperState? ReadState()
    {
        try
        {
            return File.Exists(StateFile)
                ? JsonSerializer.Deserialize<HelperState>(File.ReadAllText(StateFile), PrivilegedProtocol.Json)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Written once by the elevated installer. The owner SID and client path are the identity
/// the helper enforces on every later connection.
/// </summary>
public sealed record HelperState
{
    public string OwnerSid { get; init; } = string.Empty;
    public string OwnerAccount { get; init; } = string.Empty;

    /// <summary>Full path of the PC Assistant executable that installed this helper.</summary>
    public string ClientExecutable { get; init; } = string.Empty;

    public string InstalledVersion { get; init; } = string.Empty;
    public int Protocol { get; init; } = PrivilegedProtocol.Version;
    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.Now;
}
