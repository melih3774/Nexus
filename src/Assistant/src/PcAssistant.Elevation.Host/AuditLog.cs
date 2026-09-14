using System.Runtime.Versioning;
using System.Text;

namespace PcAssistant.Elevation.Host;

/// <summary>
/// The privileged side's own record of what it was asked to do and what happened.
///
/// It lives inside the administrators-only install directory, which is the point: PC
/// Assistant keeps a parallel history for the owner to read in the UI, but that copy is
/// written by an unelevated process and could be edited by anything running as the owner.
/// This one cannot.
///
/// Command output is deliberately not recorded. The line says what ran and how it ended;
/// it does not accumulate whatever an elevated command happened to print.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AuditLog
{
    private const long MaxBytes = 4L * 1024 * 1024;
    private static readonly Lock Gate = new();

    public static void Write(
        string kind, ClientIdentity? client, string? taskId, string target, string outcome, string? detail = null)
    {
        var line = string.Join(" | ",
            DateTimeOffset.Now.ToString("o"),
            "kind=" + kind,
            "task=" + (string.IsNullOrWhiteSpace(taskId) ? "-" : taskId),
            "client=" + (client is null ? "-" : client.UserName + "/pid " + client.ProcessId + "/session " + client.SessionId),
            "target=" + Sanitize(target, 400),
            "outcome=" + outcome,
            detail is null ? "" : "detail=" + Sanitize(detail, 400)).TrimEnd(' ', '|');

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(HelperPaths.InstallRoot);
                var file = new FileInfo(HelperPaths.AuditLogFile);
                if (file.Exists && file.Length > MaxBytes)
                {
                    var previous = HelperPaths.AuditLogFile + ".1";
                    File.Delete(previous);
                    File.Move(HelperPaths.AuditLogFile, previous);
                }

                File.AppendAllText(HelperPaths.AuditLogFile, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // An unwritable audit file must not stop the service; it must not silently
                // grant anything either, and it does not: nothing here decides anything.
            }
        }
    }

    /// <summary>Newest-last tail, for the owner-facing diagnostics panel.</summary>
    public static IReadOnlyList<string> Tail(int lines)
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(HelperPaths.AuditLogFile)) return [];
                var all = File.ReadAllLines(HelperPaths.AuditLogFile);
                return all.Length <= lines ? all : all[^lines..];
            }
            catch (Exception)
            {
                return [];
            }
        }
    }

    private static string Sanitize(string value, int max)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }
}
