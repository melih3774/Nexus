using PcCore.Update.Windows;

namespace PcCore.Update;

/// <summary>
/// A per-user folder under <c>%LOCALAPPDATA%</c> that exists twice: where every Nexus process expects
/// it, and as the private copy Windows keeps for one packaged app.
/// <para>
/// <b>How the copy comes to exist.</b> When a process started from inside a packaged (MSIX) desktop
/// app - its terminal, a script run there, anything those start in turn - creates a folder under
/// AppData that does not exist there yet, Windows creates it in
/// <c>%LOCALAPPDATA%\Packages\&lt;package&gt;\LocalCache\Local</c> instead, and from then on shows that
/// copy in place of the real folder to every process started from the app. Processes started from the
/// Start Menu, at sign-in or from Explorer keep seeing the real folder. Nothing fails: two PC Cores
/// simply read and write two pairings, two release stores and two update records, and which one the
/// owner gets depends on how Nexus was last started.
/// </para>
/// <para>
/// That is how an update installed from a terminal inside such an app was undone by restarting Nexus
/// from the Start Menu: the update was recorded in the copy, and the restarted PC Core read the real
/// folder, which had never heard of it. Where the real folder already exists and no copy does, the same
/// processes write straight through to it, so the copy - not the app - is the problem.
/// </para>
/// </summary>
/// <param name="Folder">The folder as every Nexus process names it.</param>
/// <param name="PrivateCopy">The packaged app's copy of it.</param>
/// <param name="Package">The package's folder name under <c>%LOCALAPPDATA%\Packages</c>.</param>
/// <param name="UsedByThisProcess">Whether this process was started from inside that app, and so reads and writes the copy.</param>
public sealed record AppDataRedirection(string Folder, string PrivateCopy, string Package, bool UsedByThisProcess)
{
    public static string LocalAppData => Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);

    /// <summary>What the owner is told, from whichever side of the split this process is on.</summary>
    public string Describe() => UsedByThisProcess
        ? $"This process was started from inside {Package}, and Windows shows it a private copy of {Folder} ({PrivateCopy}). "
            + "Nexus started from the Start Menu or at sign-in never sees that copy, so nothing was done. "
            + $"Start this from outside {Package}, or move what is needed out of the copy into {Folder} and delete the copy."
        : $"{Folder} also exists as a private copy for {Package} ({PrivateCopy}). "
            + $"Nexus started from inside {Package} uses that copy instead, so a release, update or pairing made from there is not visible here. "
            + $"Move what is needed out of the copy into {Folder} and delete the copy.";

    /// <summary>
    /// The private copy of <paramref name="relativeFolder"/>, preferring one this process uses, or null
    /// when no packaged app holds one.
    /// </summary>
    /// <param name="directoryIdentity">Which directory a path opens; see <see cref="DirectoryIdentity"/>.</param>
    public static AppDataRedirection? Find(string localAppData, string relativeFolder, Func<string, string?> directoryIdentity)
    {
        var folder = Path.Combine(localAppData, relativeFolder);
        var packages = Path.Combine(localAppData, "Packages");
        AppDataRedirection? unused = null;
        string? mine = null;

        try
        {
            if (!Directory.Exists(packages))
            {
                return null;
            }

            foreach (var package in Directory.EnumerateDirectories(packages))
            {
                var copy = Path.Combine(package, "LocalCache", "Local", relativeFolder);

                if (!Directory.Exists(copy))
                {
                    continue;
                }

                // Opened through the path this process uses for the folder: when Windows redirects that
                // path, what opens is the copy itself.
                mine ??= directoryIdentity(folder);

                var used = mine is not null && string.Equals(directoryIdentity(copy), mine, StringComparison.Ordinal);
                var redirection = new AppDataRedirection(folder, copy, Path.GetFileName(package), used);

                if (used)
                {
                    return redirection;
                }

                unused ??= redirection;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to look is not evidence of a copy.
        }

        return unused;
    }

    /// <summary>The first of <paramref name="relativeFolders"/> that this process reaches only as a private copy.</summary>
    public static AppDataRedirection? FirstUsedByThisProcess(string localAppData, IEnumerable<string> relativeFolders, Func<string, string?> directoryIdentity) =>
        relativeFolders
            .Select(folder => Find(localAppData, folder, directoryIdentity))
            .FirstOrDefault(redirection => redirection is { UsedByThisProcess: true });

    /// <summary><see cref="Find"/> for this user, identifying directories the way Windows opens them.</summary>
    public static AppDataRedirection? ForCurrentUser(string relativeFolder) =>
        OperatingSystem.IsWindows() ? Find(LocalAppData, relativeFolder, DirectoryIdentity.Of) : null;
}
