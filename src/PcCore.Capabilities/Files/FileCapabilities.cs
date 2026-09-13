using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Security;
using PcCore.Protocol;

namespace PcCore.Capabilities.Files;

public enum FileEntryType
{
    File,
    Directory,
}

/// <summary>One entry in a directory listing, or the subject of <c>files.stat</c>.</summary>
public sealed record FileEntry
{
    public required string Name { get; init; }

    /// <summary>
    /// The entry's full path, as PC Core resolved it.
    /// <para>
    /// Added in v0.5 so a client never has to build a path itself. A phone browsing into a
    /// subdirectory previously had to join the listing's path with this name and guess at the
    /// separator — reconstruction that a client has no business doing when the server already knows
    /// the answer. Every path a caller sends back is still re-authorised from scratch; this removes
    /// a class of client bug, not a boundary check.
    /// </para>
    /// </summary>
    public required string Path { get; init; }

    public required FileEntryType Type { get; init; }

    /// <summary>Size in bytes for files; <see langword="null"/> for directories.</summary>
    public long? SizeBytes { get; init; }

    public DateTimeOffset? ModifiedUtc { get; init; }
    public DateTimeOffset? CreatedUtc { get; init; }
    public bool IsHidden { get; init; }
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// True for symbolic links, directory junctions and mount points. PC Core never follows these
    /// during a listing and never discloses their targets.
    /// </summary>
    public bool IsLink { get; init; }
}

public sealed record ListFilesParameters
{
    /// <summary>Absolute path of a directory inside an allowed root.</summary>
    public string? Path { get; init; }

    public bool IncludeHidden { get; init; }
}

public sealed record ListFilesResult
{
    public required string Path { get; init; }
    public required int EntryCount { get; init; }
    public required IReadOnlyList<FileEntry> Entries { get; init; }

    /// <summary>Entries the OS refused to describe. They are counted, never named.</summary>
    public required int SkippedCount { get; init; }

    /// <summary>
    /// The directory above this one, when it is inside the boundary too; <see langword="null"/> at a
    /// root. Added in v1.4 so "up" is a path the PC produced, like every other path the phone sends.
    /// </summary>
    public string? Parent { get; init; }

    /// <summary>
    /// More entries exist than <see cref="ListFilesAction.MaxEntries"/>; the first ones in listing
    /// order were returned. A folder like <c>C:\Windows\WinSxS</c> is tens of thousands of entries, which
    /// is megabytes of JSON for a list nobody scrolls.
    /// </summary>
    public bool Truncated { get; init; }
}

public sealed record StatParameters
{
    public string? Path { get; init; }
}

public sealed record StatResult
{
    public required string Path { get; init; }
    public required FileEntry Entry { get; init; }
    public DateTimeOffset? AccessedUtc { get; init; }
}

/// <summary>Shared filesystem helpers for the read-only file capabilities.</summary>
internal static class FileMetadata
{
    public static FileEntry Describe(FileSystemInfo info, bool isDirectory)
    {
        var attributes = SafeAttributes(info);

        return new FileEntry
        {
            Name = info.Name,
            Path = info.FullName,
            Type = isDirectory ? FileEntryType.Directory : FileEntryType.File,
            SizeBytes = isDirectory ? null : SafeLength(info),
            ModifiedUtc = SafeTime(() => info.LastWriteTimeUtc),
            CreatedUtc = SafeTime(() => info.CreationTimeUtc),
            IsHidden = attributes.HasFlag(FileAttributes.Hidden),
            IsReadOnly = attributes.HasFlag(FileAttributes.ReadOnly),
            IsLink = attributes.HasFlag(FileAttributes.ReparsePoint),
        };
    }

    public static FileAttributes SafeAttributes(FileSystemInfo info)
    {
        try
        {
            return info.Attributes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FileAttributes.None;
        }
    }

    private static long? SafeLength(FileSystemInfo info)
    {
        try
        {
            return info is FileInfo file ? file.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTimeOffset? SafeTime(Func<DateTime> read)
    {
        try
        {
            var value = read();
            // Windows reports 1601-01-01 for entries whose timestamps are unavailable.
            return value.Year <= 1601 ? null : new DateTimeOffset(value, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

public sealed class ListFilesAction : CapabilityAction<ListFilesParameters, ListFilesResult>
{
    /// <summary>The most entries one listing returns. See <see cref="ListFilesResult.Truncated"/>.</summary>
    public const int MaxEntries = 5000;

    private readonly PathAuthorizer _authorizer;
    private readonly FileProtections _protections;

    public ListFilesAction(PathAuthorizer authorizer, FileProtections? protections = null)
    {
        _authorizer = authorizer;
        _protections = protections ?? FileProtections.None;
    }

    public override string Name => "files.list";

    public override string Description => "List the immediate contents of a directory inside an allowed root.";

    protected override ValueTask<ListFilesResult> ExecuteAsync(
        ListFilesParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var path = _protections.EnsureReachable(_authorizer.Authorize(parameters.Path).EnsureAllowed());

        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            throw File.Exists(path)
                ? new CapabilityException(ErrorCodes.InvalidParameters, "Path is a file, not a directory.")
                : new CapabilityException(ErrorCodes.NotFound, "Directory does not exist.");
        }

        var entries = new List<FileEntry>();
        var skipped = 0;
        var truncated = false;

        IEnumerator<FileSystemInfo> enumerator;
        try
        {
            // EnumerateFileSystemInfos does not follow reparse points, which is what we want:
            // a junction inside the root is reported as an entry, never walked into.
            //
            // IgnoreInaccessible stays false. With it on, a directory this account may not list at all
            // - C:\System Volume Information, another user's profile - enumerates as empty, and the
            // phone would show "This folder is empty" for a folder Windows refused to open. The first
            // MoveNext is where that refusal arrives, so it is taken here and reported as such.
            enumerator = directory.EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.None,
                ReturnSpecialDirectories = false,
            }).GetEnumerator();

            if (!enumerator.MoveNext())
            {
                enumerator.Dispose();
                return ValueTask.FromResult(Result(path, [], 0, truncated: false));
            }
        }
        catch (Exception ex) when (FileErrors.IsFileSystemFailure(ex))
        {
            throw FileErrors.From(ex, "access to that folder");
        }

        using var _ = enumerator;
        var first = true;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileSystemInfo child;
            try
            {
                if (!first && !enumerator.MoveNext())
                {
                    break;
                }

                first = false;
                child = enumerator.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The directory was opened, so this is a failure part-way through reading it. What was
                // read is still worth returning; an enumerator that has thrown is not worth asking again.
                skipped++;
                break;
            }

            var attributes = FileMetadata.SafeAttributes(child);

            if (!parameters.IncludeHidden && attributes.HasFlag(FileAttributes.Hidden))
            {
                continue;
            }

            if (entries.Count == MaxEntries)
            {
                truncated = true;
                break;
            }

            entries.Add(FileMetadata.Describe(child, child is DirectoryInfo));
        }

        entries.Sort(static (left, right) =>
        {
            // Directories first, then case-insensitive by name: predictable output for a UI.
            var byType = left.Type.CompareTo(right.Type);
            return byType != 0 ? -byType : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });

        return ValueTask.FromResult(Result(path, entries, skipped, truncated));
    }

    private ListFilesResult Result(string path, List<FileEntry> entries, int skipped, bool truncated) => new()
    {
        Path = path,
        EntryCount = entries.Count,
        Entries = entries,
        SkippedCount = skipped,
        Truncated = truncated,
        Parent = ParentOf(path),
    };

    /// <summary>The parent, authorised like any other path; null at a root or where the parent is not reachable.</summary>
    private string? ParentOf(string path)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));

        if (string.IsNullOrEmpty(parent))
        {
            return null;
        }

        var authorized = _authorizer.Authorize(parent);

        return authorized.Allowed && !_protections.IsProtected(authorized.RealPath!) ? authorized.RealPath : null;
    }
}

public sealed class StatFileAction : CapabilityAction<StatParameters, StatResult>
{
    private readonly PathAuthorizer _authorizer;
    private readonly FileProtections _protections;

    public StatFileAction(PathAuthorizer authorizer, FileProtections? protections = null)
    {
        _authorizer = authorizer;
        _protections = protections ?? FileProtections.None;
    }

    public override string Name => "files.stat";

    public override string Description => "Metadata for a single file or directory inside an allowed root.";

    protected override ValueTask<StatResult> ExecuteAsync(
        StatParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var path = _protections.EnsureReachable(_authorizer.Authorize(parameters.Path).EnsureAllowed());

        var isDirectory = Directory.Exists(path);
        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);

        if (!info.Exists)
        {
            throw new CapabilityException(ErrorCodes.NotFound, "Path does not exist.");
        }

        DateTimeOffset? accessed = null;
        try
        {
            var value = info.LastAccessTimeUtc;
            accessed = value.Year <= 1601 ? null : new DateTimeOffset(value, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left null; not worth failing the call.
        }

        return ValueTask.FromResult(new StatResult
        {
            Path = path,
            Entry = FileMetadata.Describe(info, isDirectory),
            AccessedUtc = accessed,
        });
    }
}
