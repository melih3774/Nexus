using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Security;

/// <summary>Outcome of authorising one caller-supplied path.</summary>
public readonly record struct PathAuthorizationResult
{
    private PathAuthorizationResult(string? realPath, string? errorCode, string? reason)
    {
        RealPath = realPath;
        ErrorCode = errorCode;
        Reason = reason;
    }

    /// <summary>The resolved real path. Only this value may be passed to the filesystem.</summary>
    public string? RealPath { get; }

    public string? ErrorCode { get; }

    public string? Reason { get; }

    public bool Allowed => RealPath is not null;

    public static PathAuthorizationResult Allow(string realPath) => new(realPath, null, null);

    public static PathAuthorizationResult Deny(string reason, string code = ErrorCodes.PathNotAllowed) =>
        new(null, code, reason);

    /// <summary>Returns the real path, or throws the matching protocol error.</summary>
    public string EnsureAllowed()
    {
        if (RealPath is not null)
        {
            return RealPath;
        }

        // The reason is a category ("path escapes the allowed roots"), never the caller's path
        // or the resolved location — that would confirm the existence of files outside the boundary.
        throw new CapabilityException(ErrorCode ?? ErrorCodes.PathNotAllowed, Reason ?? "Path is not permitted.");
    }
}

/// <summary>
/// Enforces the filesystem boundary. Every caller-supplied path passes through
/// <see cref="Authorize"/> before any I/O happens. See SPEC.md 6 for the full pipeline and
/// for the Windows edge cases that remain.
/// </summary>
public sealed class PathAuthorizer
{
    private static readonly char[] ForbiddenCharacters = ['<', '>', '"', '|', '*', '?'];

    private readonly AllowedRoots _roots;
    private readonly IRealPathResolver _resolver;
    private readonly ILogger<PathAuthorizer> _logger;

    public PathAuthorizer(AllowedRoots roots, IRealPathResolver resolver, ILogger<PathAuthorizer> logger)
    {
        _roots = roots;
        _resolver = resolver;
        _logger = logger;
    }

    public AllowedRoots Roots => _roots;

    public PathAuthorizationResult Authorize(string? requestedPath)
    {
        // --- Step 1: reject by shape, before touching the filesystem -------------------------
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return PathAuthorizationResult.Deny("Parameter 'path' is required.", ErrorCodes.InvalidParameters);
        }

        if (requestedPath.Contains('\0'))
        {
            return Denied("embedded null character");
        }

        if (requestedPath.Any(char.IsControl))
        {
            return Denied("control character in path");
        }

        // Device and extended-length namespaces bypass normal path parsing entirely.
        if (StartsWithDeviceNamespace(requestedPath))
        {
            return Denied("device or extended-length namespace path");
        }

        // Drive-relative ("C:foo") and root-relative ("\foo") paths resolve against ambient
        // process state. We never let the caller depend on a current directory.
        if (!Path.IsPathFullyQualified(requestedPath))
        {
            return Denied("path is not absolute");
        }

        if (requestedPath.IndexOfAny(ForbiddenCharacters) >= 0)
        {
            return Denied("wildcard or reserved character in path");
        }

        // --- Step 2: canonicalise lexically ---------------------------------------------------
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(requestedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Denied("path could not be normalised");
        }

        // UNC shares are outside the local boundary by definition. Checked after normalisation
        // so that "//server/share" and "\\server\share" are both caught.
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return Denied("UNC path");
        }

        // Alternate data streams: "notes.txt:hidden" is a distinct stream on the same file, and
        // a boundary that only inspects directory names would never see it.
        if (HasStreamSeparator(fullPath))
        {
            return Denied("alternate data stream specifier");
        }

        if (_roots.IsEmpty)
        {
            return Denied("no filesystem roots are configured");
        }

        // --- Step 3: resolve links, junctions, mount points, and 8.3 names --------------------
        var realPath = _resolver.Resolve(fullPath);
        if (realPath is null)
        {
            // Unresolvable is always a denial. Falling back to the lexical path here would
            // reintroduce exactly the junction escape this step exists to prevent.
            _logger.LogDebug("Path could not be resolved to a real path: {Path}", fullPath);
            return Denied("path could not be resolved");
        }

        realPath = Path.TrimEndingDirectorySeparator(realPath);

        // --- Step 4: containment by segment, not by string prefix ----------------------------
        foreach (var root in _roots.Roots)
        {
            if (PathContainment.IsWithin(realPath, root))
            {
                _logger.LogDebug("Path authorised: {Path} (root {Root})", realPath, root);
                return PathAuthorizationResult.Allow(realPath);
            }
        }

        _logger.LogDebug("Path denied, outside all roots: {Path}", realPath);
        return Denied("path is outside the allowed roots");
    }

    private PathAuthorizationResult Denied(string reason)
    {
        // A single opaque message for every denial reason. The internal reason is logged, not
        // returned, so a caller cannot use error text to probe the filesystem.
        _logger.LogDebug("Path denied ({Reason})", reason);
        return PathAuthorizationResult.Deny("Path is outside the configured allowed roots.");
    }

    private static bool StartsWithDeviceNamespace(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal)
        || path.StartsWith(@"\\.\", StringComparison.Ordinal)
        || path.StartsWith("//?/", StringComparison.Ordinal)
        || path.StartsWith("//./", StringComparison.Ordinal)
        || path.StartsWith(@"\\?/", StringComparison.Ordinal)
        || path.StartsWith(@"\\./", StringComparison.Ordinal);

    private static bool HasStreamSeparator(string fullPath)
    {
        var segments = PathContainment.Split(fullPath);

        // Segment 0 is the drive specifier ("C:"), where the colon is legitimate.
        for (var i = 1; i < segments.Length; i++)
        {
            if (segments[i].Contains(':'))
            {
                return true;
            }
        }

        return false;
    }
}
