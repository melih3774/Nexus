namespace PcAssistant.ClaudeCode;

public sealed record ClaudeInstall(string ExecutablePath, string? VersionHint, string Source);

/// <summary>
/// Finds the locally installed Claude Code.
///
/// On this machine Claude Code ships inside the Claude desktop app rather than on PATH,
/// and the packaged app's data directory is also reachable through an MSIX redirect, so
/// discovery probes every known location and prefers the highest version it finds.
/// </summary>
public static class ClaudeCodeLocator
{
    public static ClaudeInstall? Locate(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var canonical = Path.GetFullPath(overridePath.Trim().Trim('"'));
            if (File.Exists(canonical)) return new ClaudeInstall(canonical, null, "configured override");
            return null;
        }

        var candidates = new List<ClaudeInstall>();

        foreach (var dir in VersionedRoots())
            candidates.AddRange(FromVersionedRoot(dir));

        foreach (var direct in DirectCandidates())
        {
            if (File.Exists(direct.Path))
                candidates.Add(new ClaudeInstall(direct.Path, null, direct.Source));
        }

        var onPath = FromPath();
        if (onPath is not null) candidates.Add(onPath);

        return candidates
            .OrderByDescending(c => ParseVersion(c.VersionHint))
            .ThenBy(c => c.Source == "PATH" ? 0 : 1)
            .FirstOrDefault();
    }

    private static IEnumerable<string> VersionedRoots()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
            yield return Path.Combine(appData, "Claude", "claude-code");

        // MSIX-packaged Claude desktop keeps its roaming data under LocalCache.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData)) yield break;

        var packages = Path.Combine(localAppData, "Packages");
        if (!Directory.Exists(packages)) yield break;

        IEnumerable<string> packageDirs;
        try
        {
            packageDirs = Directory.EnumerateDirectories(packages, "Claude_*");
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var pkg in packageDirs)
            yield return Path.Combine(pkg, "LocalCache", "Roaming", "Claude", "claude-code");
    }

    private static IEnumerable<ClaudeInstall> FromVersionedRoot(string root)
    {
        if (!Directory.Exists(root)) yield break;

        IEnumerable<string> versionDirs;
        try
        {
            versionDirs = Directory.EnumerateDirectories(root);
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var versionDir in versionDirs)
        {
            var exe = Path.Combine(versionDir, "claude.exe");
            if (File.Exists(exe))
                yield return new ClaudeInstall(exe, Path.GetFileName(versionDir), "Claude desktop bundle");
        }
    }

    private static IEnumerable<(string Path, string Source)> DirectCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            yield return (Path.Combine(home, ".local", "bin", "claude.exe"), "native install");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            yield return (Path.Combine(appData, "npm", "claude.cmd"), "npm global");
            yield return (Path.Combine(appData, "npm", "claude.exe"), "npm global");
        }
    }

    private static ClaudeInstall? FromPath()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "claude.exe", "claude.cmd", "claude.bat" })
            {
                try
                {
                    var full = Path.Combine(dir.Trim().Trim('"'), name);
                    if (File.Exists(full)) return new ClaudeInstall(full, null, "PATH");
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry; skip it.
                }
            }
        }
        return null;
    }

    private static Version ParseVersion(string? hint)
        => Version.TryParse(hint, out var v) ? v : new Version(0, 0);
}
