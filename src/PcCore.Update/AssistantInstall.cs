using System.Security.Cryptography;
using System.Text;

namespace PcCore.Update;

/// <summary>
/// PC Assistant as a component Nexus installs: where it goes, how the updater reaches a running copy,
/// and what was installed last.
/// <para>
/// <b>Installed beside PC Core, run on its own.</b> The files go to <c>Assistant</c> under PC Core's
/// install directory, but nothing about the two processes changes: PC Core does not reference or start
/// PC Assistant, and they still meet only over the owner-only named pipe, each checking which executable
/// is on the other end. Its own state - tasks, the database, settings, logs - stays under
/// <c>%LOCALAPPDATA%\PcAssistant</c>, and Claude Code's sign-in stays with Claude Code; the updater
/// writes neither.
/// </para>
/// </summary>
public static class AssistantInstall
{
    public const string Folder = "Assistant";

    public const string ExecutableName = "PcAssistant.Desktop.exe";

    public const string ComponentName = "PC Assistant";

    /// <summary>
    /// The named event an installed PC Assistant waits on, derived from its full path so the updater
    /// reaches exactly the copy it installed. PC Assistant derives the same name
    /// (<c>PcAssistant.Desktop.UpdateStopListener.EventNameFor</c>); both test suites pin it to one
    /// literal. Unlike PC Core, PC Assistant may refuse: it stays open while a task runs or an approval
    /// waits, and the updater then changes nothing.
    /// </summary>
    public static string StopEventName(string executablePath)
    {
        var full = Path.GetFullPath(executablePath).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(full));
        return @"Local\PcAssistant.UpdateStop." + Convert.ToHexString(hash, 0, 8);
    }
}

/// <summary>
/// The updater's record of the PC Assistant it installed. Kept apart from PC Core's record because the
/// two are replaced at different moments: PC Assistant can be updated while PC Core keeps running, and a
/// PC Core rollback does not undo it.
/// </summary>
public sealed record InstalledAssistantRecord
{
    public required int Release { get; init; }

    public required string Version { get; init; }

    public DateTimeOffset InstalledAtUtc { get; init; }

    public required IReadOnlyList<ReleaseFile> Files { get; init; }
}
