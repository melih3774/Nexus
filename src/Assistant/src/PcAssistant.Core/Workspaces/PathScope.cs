using System.Runtime.InteropServices;

namespace PcAssistant.Core.Workspaces;

/// <summary>
/// Canonicalization and containment checks for filesystem paths.
/// Everything the policy engine decides about "inside" vs "outside" goes through here.
/// </summary>
public static class PathScope
{
    private static readonly StringComparison Cmp = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// Produces an absolute path with <c>..</c> segments removed and reparse points
    /// (junctions / symlinks) resolved to their final target where the OS can do so.
    /// Returns null for input that cannot be turned into a usable absolute path.
    /// </summary>
    public static string? Canonicalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0) return null;

        // Reject obviously non-filesystem values early.
        if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;

        string full;
        try
        {
            full = Path.GetFullPath(trimmed);
        }
        catch
        {
            return null;
        }

        // Resolve reparse points so a junction cannot be used to leave an allowed root.
        try
        {
            var resolved = ResolveFinalTarget(full);
            if (resolved is not null) full = Path.GetFullPath(resolved);
        }
        catch
        {
            // A path that does not exist yet cannot be a reparse point; keep the lexical form.
        }

        return TrimTrailingSeparator(full);
    }

    private static string? ResolveFinalTarget(string full)
    {
        // Walk from the deepest existing ancestor so not-yet-created files still resolve
        // through a junctioned parent directory.
        var existing = full;
        var tail = new List<string>();
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent) || parent == existing) return null;
            tail.Add(Path.GetFileName(existing));
            existing = parent;
        }

        FileSystemInfo? info = Directory.Exists(existing)
            ? new DirectoryInfo(existing)
            : new FileInfo(existing);

        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        var basePath = target?.FullName ?? existing;

        tail.Reverse();
        return tail.Count == 0 ? basePath : Path.Combine(new[] { basePath }.Concat(tail).ToArray());
    }

    private static string TrimTrailingSeparator(string p)
    {
        if (p.Length <= 3) return p; // keep "C:\"
        return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> itself or below it.</summary>
    public static bool IsInside(string? root, string? candidate)
    {
        var r = Canonicalize(root);
        var c = Canonicalize(candidate);
        if (r is null || c is null) return false;

        if (string.Equals(r, c, Cmp)) return true;

        var prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return c.StartsWith(prefix, Cmp);
    }

    /// <summary>True when the path sits in a Windows/system location that is never an ordinary workspace.</summary>
    public static bool IsProtectedSystemLocation(string? path)
    {
        var c = Canonicalize(path);
        if (c is null) return true;

        foreach (var folder in ProtectedFolders())
        {
            if (string.IsNullOrEmpty(folder)) continue;
            if (IsInside(folder, c)) return true;
        }

        // A bare drive root is never an acceptable workspace.
        var root = Path.GetPathRoot(c);
        if (root is not null && string.Equals(TrimTrailingSeparator(root), c, Cmp)) return true;

        return false;
    }

    private static IEnumerable<string?> ProtectedFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Environment.GetEnvironmentVariable("SystemRoot");
    }
}
