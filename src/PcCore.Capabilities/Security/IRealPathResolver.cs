namespace PcCore.Capabilities.Security;

/// <summary>
/// Resolves a fully-qualified path to its <em>real</em> canonical form, following symbolic links,
/// directory junctions and mount points, and expanding 8.3 short names.
/// <para>
/// This exists because lexical normalisation alone is not a security boundary: <c>Path.GetFullPath</c>
/// collapses <c>..</c>, but it cannot see that <c>C:\Users\Me\Desktop\escape</c> is a junction to
/// <c>C:\Windows</c>, nor that <c>C:\PROGRA~1</c> is <c>C:\Program Files</c>.
/// </para>
/// <para>
/// The Windows implementation opens a handle and asks the OS
/// (<c>GetFinalPathNameByHandle</c>); <see cref="PortableRealPathResolver"/> is the managed
/// fallback used when no handle can be obtained.
/// </para>
/// </summary>
public interface IRealPathResolver
{
    /// <summary>
    /// Returns the resolved real path, or <see langword="null"/> if it cannot be determined.
    /// <para>
    /// A <see langword="null"/> result must always be treated as <em>deny</em>. Callers must never
    /// fall back to the unresolved path.
    /// </para>
    /// <para>
    /// Paths that do not exist are still resolved: the deepest existing ancestor is resolved and
    /// the remaining segments appended, so a missing file beneath a junctioned parent is judged by
    /// the junction's real target.
    /// </para>
    /// </summary>
    /// <param name="fullyQualifiedPath">A path already normalised by <c>Path.GetFullPath</c>.</param>
    string? Resolve(string fullyQualifiedPath);
}

/// <summary>
/// Managed resolver built on <see cref="FileSystemInfo.ResolveLinkTarget"/>. It walks the path
/// one segment at a time so that a reparse point <em>anywhere</em> in the chain is followed, not
/// just one on the final component.
/// <para>
/// Limitation compared with the Windows implementation: it does not expand 8.3 short names.
/// It is therefore the fallback, not the default, on Windows.
/// </para>
/// </summary>
public sealed class PortableRealPathResolver : IRealPathResolver
{
    /// <summary>Guards against a pathological or cyclic link chain.</summary>
    private const int MaxSegments = 512;

    public string? Resolve(string fullyQualifiedPath)
    {
        if (string.IsNullOrWhiteSpace(fullyQualifiedPath) || !Path.IsPathFullyQualified(fullyQualifiedPath))
        {
            return null;
        }

        try
        {
            var root = Path.GetPathRoot(fullyQualifiedPath);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var remainder = fullyQualifiedPath[root.Length..];
            var segments = remainder.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length > MaxSegments)
            {
                return null;
            }

            var current = root;

            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);

                FileSystemInfo info = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);

                if (!info.Exists)
                {
                    // Nothing below this point exists, so nothing below can be a link.
                    continue;
                }

                if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    continue;
                }

                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null)
                {
                    // A reparse point we cannot resolve (an unsupported tag, for instance).
                    // Deny rather than guess.
                    return null;
                }

                current = target.FullName;
            }

            return Normalize(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Includes the cyclic-link IOException raised by ResolveLinkTarget.
            return null;
        }
    }

    internal static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
