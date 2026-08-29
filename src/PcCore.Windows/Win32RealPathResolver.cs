using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PcCore.Capabilities.Security;
using PcCore.Windows.Interop;

namespace PcCore.Windows;

/// <summary>
/// Resolves a path to its true form by asking the kernel: open a handle, then
/// <c>GetFinalPathNameByHandle</c>.
/// <para>
/// This is the authoritative answer. It resolves symbolic links, directory junctions and volume
/// mount points anywhere in the path, and it expands 8.3 short names such as
/// <c>C:\PROGRA~1</c> — an alias a purely lexical check would happily accept.
/// </para>
/// <para>
/// The handle is opened with <c>dwDesiredAccess = 0</c>, so resolution needs no read permission
/// on the target, and with full sharing so it never blocks another process.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32RealPathResolver : IRealPathResolver
{
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    /// <summary>Bound on how far we walk up looking for an existing ancestor.</summary>
    private const int MaxAncestorWalk = 256;

    private readonly PortableRealPathResolver _fallback = new();

    public string? Resolve(string fullyQualifiedPath)
    {
        if (string.IsNullOrWhiteSpace(fullyQualifiedPath) || !Path.IsPathFullyQualified(fullyQualifiedPath))
        {
            return null;
        }

        // The common case: the path exists and the kernel answers directly.
        var direct = TryGetFinalPath(fullyQualifiedPath);
        if (direct is not null)
        {
            return direct;
        }

        // The path does not exist (yet). Resolve the deepest existing ancestor and re-attach the
        // missing tail, so a would-be file beneath a junctioned parent is still judged by the
        // junction's real target rather than by the name the caller typed.
        var current = Path.TrimEndingDirectorySeparator(fullyQualifiedPath);
        var tail = new Stack<string>();

        for (var depth = 0; depth < MaxAncestorWalk; depth++)
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent))
            {
                break;
            }

            var name = Path.GetFileName(current);
            if (!string.IsNullOrEmpty(name))
            {
                tail.Push(name);
            }

            var resolvedParent = TryGetFinalPath(parent);
            if (resolvedParent is not null)
            {
                return tail.Count == 0
                    ? resolvedParent
                    : Path.Combine(resolvedParent, Path.Combine([.. tail]));
            }

            current = Path.TrimEndingDirectorySeparator(parent);
        }

        // Nothing along the chain could be opened — a locked volume, an unusual filesystem, or a
        // path we simply cannot judge. Defer to the managed resolver, which still follows reparse
        // points; if that also fails the caller treats null as deny.
        return _fallback.Resolve(fullyQualifiedPath);
    }

    private static unsafe string? TryGetFinalPath(string path)
    {
        using var handle = NativeMethods.CreateFile(
            path,
            NativeMethods.AccessNone,
            NativeMethods.ShareAll,
            lpSecurityAttributes: 0,
            NativeMethods.OpenExisting,
            // Note the absence of FILE_FLAG_OPEN_REPARSE_POINT: we deliberately follow links so
            // that the handle refers to the real target we are about to authorise.
            NativeMethods.FileFlagBackupSemantics,
            hTemplateFile: 0);

        if (handle.IsInvalid)
        {
            return null;
        }

        // Ask for the required length first; MAX_PATH is not a safe assumption.
        var length = NativeMethods.GetFinalPathNameByHandle(handle, null, 0, NativeMethods.FileNameNormalizedVolumeNameDos);
        if (length == 0)
        {
            return null;
        }

        var buffer = new char[length + 1];
        uint written;
        fixed (char* pinned = buffer)
        {
            written = NativeMethods.GetFinalPathNameByHandle(
                handle,
                pinned,
                (uint)buffer.Length,
                NativeMethods.FileNameNormalizedVolumeNameDos);
        }

        if (written == 0 || written >= buffer.Length)
        {
            return null;
        }

        return StripExtendedPrefix(new string(buffer, 0, (int)written));
    }

    /// <summary>
    /// <c>GetFinalPathNameByHandle</c> always returns the extended-length form. We strip it so the
    /// result compares against configured roots, which are ordinary drive-letter paths.
    /// </summary>
    internal static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.Ordinal))
        {
            // \\?\UNC\server\share -> \\server\share, which the authorizer then rejects as UNC.
            return @"\\" + path[ExtendedUncPrefix.Length..];
        }

        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            return path[ExtendedPrefix.Length..];
        }

        return path;
    }
}

/// <summary>
/// Windows known-folder lookup. Overrides <c>Downloads</c>, which has no
/// <see cref="Environment.SpecialFolder"/> and is frequently relocated by the user — falling back
/// to <c>%USERPROFILE%\Downloads</c> would silently authorise the wrong directory.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsKnownFolderResolver : PortableKnownFolderResolver
{
    public override string? Resolve(string token)
    {
        if (token.Equals("downloads", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveDownloads() ?? base.Resolve(token);
        }

        return base.Resolve(token);
    }

    private static unsafe string? ResolveDownloads()
    {
        var id = NativeMethods.FolderIdDownloads;
        nint buffer = 0;

        try
        {
            var hr = NativeMethods.SHGetKnownFolderPath(&id, NativeMethods.KnownFolderDontVerify, 0, out buffer);
            return hr != 0 ? null : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            if (buffer != 0)
            {
                NativeMethods.CoTaskMemFree(buffer);
            }
        }
    }
}
