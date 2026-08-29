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
    private readonly PathAuthorizer _authorizer;

    public ListFilesAction(PathAuthorizer authorizer) => _authorizer = authorizer;

    public override string Name => "files.list";

    public override string Description => "List the immediate contents of a directory inside an allowed root.";

    protected override ValueTask<ListFilesResult> ExecuteAsync(
        ListFilesParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var path = _authorizer.Authorize(parameters.Path).EnsureAllowed();

        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            throw File.Exists(path)
                ? new CapabilityException(ErrorCodes.InvalidParameters, "Path is a file, not a directory.")
                : new CapabilityException(ErrorCodes.NotFound, "Directory does not exist.");
        }

        var entries = new List<FileEntry>();
        var skipped = 0;

        IEnumerable<FileSystemInfo> children;
        try
        {
            // EnumerateFileSystemInfos does not follow reparse points, which is what we want:
            // a junction inside the root is reported as an entry, never walked into.
            children = directory.EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.None,
                ReturnSpecialDirectories = false,
            });
        }
        catch (UnauthorizedAccessException)
        {
            throw new CapabilityException(ErrorCodes.AccessDenied, "Access to the directory was denied.");
        }
        catch (IOException)
        {
            throw new CapabilityException(ErrorCodes.AccessDenied, "The directory could not be read.");
        }

        using var enumerator = children.GetEnumerator();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileSystemInfo child;
            try
            {
                if (!enumerator.MoveNext())
                {
                    break;
                }

                child = enumerator.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A single unreadable entry must not fail the whole listing.
                skipped++;
                continue;
            }

            var attributes = FileMetadata.SafeAttributes(child);

            if (!parameters.IncludeHidden && attributes.HasFlag(FileAttributes.Hidden))
            {
                continue;
            }

            entries.Add(FileMetadata.Describe(child, child is DirectoryInfo));
        }

        entries.Sort(static (left, right) =>
        {
            // Directories first, then case-insensitive by name: predictable output for a UI.
            var byType = left.Type.CompareTo(right.Type);
            return byType != 0 ? -byType : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });

        return ValueTask.FromResult(new ListFilesResult
        {
            Path = path,
            EntryCount = entries.Count,
            Entries = entries,
            SkippedCount = skipped,
        });
    }
}

public sealed class StatFileAction : CapabilityAction<StatParameters, StatResult>
{
    private readonly PathAuthorizer _authorizer;

    public StatFileAction(PathAuthorizer authorizer) => _authorizer = authorizer;

    public override string Name => "files.stat";

    public override string Description => "Metadata for a single file or directory inside an allowed root.";

    protected override ValueTask<StatResult> ExecuteAsync(
        StatParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var path = _authorizer.Authorize(parameters.Path).EnsureAllowed();

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
