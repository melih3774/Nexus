using Microsoft.Extensions.Logging;

namespace PcCore.Capabilities.Security;

/// <summary>Configuration for the filesystem boundary. Nothing is reachable unless listed here.</summary>
public sealed class FilesOptions
{
    public const string SectionName = "PcCore:Files";

    /// <summary>
    /// Absolute paths, or well-known-folder tokens such as <c>{Desktop}</c>, <c>{Documents}</c>,
    /// <c>{Downloads}</c>, <c>{Pictures}</c>, <c>{Music}</c>, <c>{Videos}</c>, <c>{UserProfile}</c>,
    /// or <c>{Drives}</c> for every local drive (the v1.4 default, so Files can browse the PC).
    /// </summary>
    public IList<string> AllowedRoots { get; set; } = new List<string>();

    /// <summary>How long an upload may sit without a chunk before its partial file is deleted.</summary>
    public int UploadIdleMinutes { get; set; } = 10;
}

/// <summary>
/// Expands well-known-folder tokens. The portable implementation is enough for most folders;
/// Windows overrides it because <c>Downloads</c> has no <see cref="Environment.SpecialFolder"/>
/// and is commonly relocated by the user.
/// </summary>
public interface IKnownFolderResolver
{
    /// <summary>Returns the path for a token name (without braces), or <see langword="null"/>.</summary>
    string? Resolve(string token);
}

public class PortableKnownFolderResolver : IKnownFolderResolver
{
    public virtual string? Resolve(string token) => token.ToLowerInvariant() switch
    {
        "desktop" => Folder(Environment.SpecialFolder.DesktopDirectory),
        "documents" => Folder(Environment.SpecialFolder.MyDocuments),
        "pictures" => Folder(Environment.SpecialFolder.MyPictures),
        "music" => Folder(Environment.SpecialFolder.MyMusic),
        "videos" => Folder(Environment.SpecialFolder.MyVideos),
        "userprofile" => Folder(Environment.SpecialFolder.UserProfile),
        // No SpecialFolder exists for Downloads; this is the conventional location and the
        // Windows resolver replaces it with the real known-folder lookup.
        "downloads" => Combine(Folder(Environment.SpecialFolder.UserProfile), "Downloads"),
        _ => null,
    };

    protected static string? Folder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static string? Combine(string? left, string right) =>
        string.IsNullOrWhiteSpace(left) ? null : Path.Combine(left, right);
}

/// <summary>
/// The resolved filesystem boundary: a fixed list of real, canonical root paths, computed once at
/// startup so that a junctioned or short-named root is compared in its true form.
/// </summary>
public sealed class AllowedRoots
{
    private AllowedRoots(IReadOnlyList<string> roots) => Roots = roots;

    /// <summary>Real, canonical, separator-trimmed root paths. May be empty (fail closed).</summary>
    public IReadOnlyList<string> Roots { get; }

    public bool IsEmpty => Roots.Count == 0;

    public static AllowedRoots Create(
        IEnumerable<string> configuredRoots,
        IRealPathResolver resolver,
        IKnownFolderResolver knownFolders,
        ILogger logger,
        Func<IEnumerable<string>>? drives = null)
    {
        ArgumentNullException.ThrowIfNull(configuredRoots);
        drives ??= LocalDrives;

        var resolved = new List<string>();

        foreach (var (configured, expanded) in configuredRoots
                     .Where(c => !string.IsNullOrWhiteSpace(c))
                     .SelectMany(c => ExpandAll(c.Trim(), knownFolders, drives).Select(e => (c, e))))
        {
            if (expanded is null)
            {
                logger.LogWarning("Allowed root '{Root}' could not be expanded and was dropped.", configured);
                continue;
            }

            if (!Path.IsPathFullyQualified(expanded))
            {
                logger.LogWarning("Allowed root '{Root}' is not an absolute path and was dropped.", configured);
                continue;
            }

            if (!Directory.Exists(expanded))
            {
                logger.LogWarning("Allowed root '{Root}' does not exist and was dropped.", expanded);
                continue;
            }

            var real = resolver.Resolve(Path.GetFullPath(expanded));
            if (real is null)
            {
                logger.LogWarning("Allowed root '{Root}' could not be resolved to a real path and was dropped.", expanded);
                continue;
            }

            real = Path.TrimEndingDirectorySeparator(real);

            if (resolved.Any(existing => PathContainment.IsWithin(real, existing)))
            {
                logger.LogDebug("Allowed root '{Root}' is already covered by a configured root; skipped.", real);
                continue;
            }

            // A newly added root may subsume roots added earlier.
            resolved.RemoveAll(existing => PathContainment.IsWithin(existing, real));
            resolved.Add(real);
        }

        if (resolved.Count == 0)
        {
            // Failing closed is the whole point: no roots means no filesystem access at all,
            // never "everything".
            logger.LogWarning(
                "No filesystem roots resolved. All files.* requests will be denied with 'path_not_allowed'.");
        }
        else
        {
            logger.LogInformation("Filesystem boundary: {Count} allowed root(s): {Roots}", resolved.Count, resolved);
        }

        return new AllowedRoots(resolved);
    }

    /// <summary>Creates a boundary from already-real paths. Used by tests.</summary>
    public static AllowedRoots FromRealPaths(params string[] realRoots) =>
        new([.. realRoots.Select(Path.TrimEndingDirectorySeparator)]);

    /// <summary>The token that stands for every local drive: <c>{Drives}</c>.</summary>
    public const string DrivesToken = "{Drives}";

    /// <summary>
    /// Fixed and removable drives that are ready now. Not network drives: a mapped letter is a UNC
    /// share underneath, and the authorizer refuses those whatever the roots say. Not CD-ROMs or RAM
    /// disks either - nothing worth browsing from a phone.
    /// </summary>
    public static IEnumerable<string> LocalDrives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool usable;
            try
            {
                usable = drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                usable = false;
            }

            if (usable)
            {
                yield return drive.RootDirectory.FullName;
            }
        }
    }

    private static IEnumerable<string?> ExpandAll(string configured, IKnownFolderResolver knownFolders, Func<IEnumerable<string>> drives)
    {
        if (configured.Equals(DrivesToken, StringComparison.OrdinalIgnoreCase))
        {
            return drives();
        }

        return [Expand(configured, knownFolders)];
    }

    private static string? Expand(string configured, IKnownFolderResolver knownFolders)
    {
        if (configured.StartsWith('{') && configured.EndsWith('}'))
        {
            return knownFolders.Resolve(configured[1..^1]);
        }

        var expanded = Environment.ExpandEnvironmentVariables(configured);
        return string.IsNullOrWhiteSpace(expanded) ? null : expanded;
    }
}
