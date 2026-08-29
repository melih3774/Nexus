namespace PcCore.Capabilities.Security;

/// <summary>
/// Segment-wise containment testing.
/// <para>
/// A string prefix test is <em>not</em> a containment test: <c>"C:\Users\Me\Desktop2"</c> starts
/// with <c>"C:\Users\Me\Desktop"</c> but is a different directory. Comparing whole path segments
/// is what closes that hole, and it is why this lives in its own type with its own tests.
/// </para>
/// </summary>
public static class PathContainment
{
    /// <summary>
    /// Windows filesystems are case-insensitive by default, so containment must be too —
    /// otherwise <c>c:\users\me\desktop\..\..\..\windows</c> style tricks differ only by case.
    /// See SPEC.md 6.4 for the per-directory case-sensitivity caveat.
    /// </summary>
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="root"/> itself or lies beneath it.
    /// Both arguments must already be real, canonical paths.
    /// </summary>
    public static bool IsWithin(string candidate, string root)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(root))
        {
            return false;
        }

        var candidateSegments = Split(candidate);
        var rootSegments = Split(root);

        if (rootSegments.Length == 0 || candidateSegments.Length < rootSegments.Length)
        {
            return false;
        }

        for (var i = 0; i < rootSegments.Length; i++)
        {
            if (!string.Equals(candidateSegments[i], rootSegments[i], Comparison))
            {
                return false;
            }
        }

        return true;
    }

    public static string[] Split(string path) =>
        Path.TrimEndingDirectorySeparator(path)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
}
