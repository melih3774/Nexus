using System.Security.Cryptography;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Security;
using PcCore.Protocol;

namespace PcCore.Capabilities.Files;

/// <summary>A place Files can start from: a drive, or a configured folder.</summary>
public sealed record FileRoot
{
    public required string Path { get; init; }

    /// <summary>What to call it: <c>Windows (C:)</c>, or a folder's own name.</summary>
    public required string Name { get; init; }

    /// <summary><c>drive</c> or <c>folder</c>.</summary>
    public required string Kind { get; init; }

    public long? TotalBytes { get; init; }

    public long? FreeBytes { get; init; }
}

/// <summary>A shortcut into the boundary: the owner's profile and the usual folders inside it.</summary>
public sealed record FilePlace(string Name, string Path);

public sealed record FileRootsResult
{
    public required IReadOnlyList<FileRoot> Roots { get; init; }

    public required IReadOnlyList<FilePlace> Places { get; init; }
}

/// <summary>
/// <c>files.roots</c>: where browsing starts.
/// <para>
/// The session handshake already hands the phone the allowed roots as bare paths; this adds what a
/// person needs to choose between them - a drive's label and free space - and the handful of folders
/// the owner actually means when they open Files, so that <c>C:\Users\you</c> is one tap rather than three.
/// Every path here is one the authorizer accepts; nothing outside the boundary is described.
/// </para>
/// </summary>
public sealed class ListFileRootsAction(
    PathAuthorizer authorizer,
    IKnownFolderResolver knownFolders,
    FileProtections? protections = null) : CapabilityAction<NoParameters, FileRootsResult>
{
    private static readonly (string Name, string Token)[] PlaceTokens =
    [
        ("Home", "userprofile"),
        ("Desktop", "desktop"),
        ("Documents", "documents"),
        ("Downloads", "downloads"),
        ("Pictures", "pictures"),
        ("Videos", "videos"),
    ];

    private readonly FileProtections _protections = protections ?? FileProtections.None;

    public override string Name => "files.roots";

    public override string Description => "The drives and folders Files can browse, with the usual places inside them.";

    protected override ValueTask<FileRootsResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var roots = authorizer.Roots.Roots.Select(Describe).ToList();
        var places = new List<FilePlace>();

        foreach (var (name, token) in PlaceTokens)
        {
            var path = knownFolders.Resolve(token);

            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                continue;
            }

            var authorized = authorizer.Authorize(path);

            if (authorized.Allowed && !_protections.IsProtected(authorized.RealPath!)
                && places.All(p => !string.Equals(p.Path, authorized.RealPath, StringComparison.OrdinalIgnoreCase)))
            {
                places.Add(new FilePlace(name, authorized.RealPath!));
            }
        }

        return ValueTask.FromResult(new FileRootsResult { Roots = roots, Places = places });
    }

    private static FileRoot Describe(string root)
    {
        var isDrive = string.Equals(Path.GetPathRoot(root), root, StringComparison.OrdinalIgnoreCase);

        if (!isDrive)
        {
            return new FileRoot { Path = root, Name = Path.GetFileName(root), Kind = "folder" };
        }

        var letter = root.TrimEnd('\\');

        try
        {
            var drive = new DriveInfo(root);
            var label = drive.IsReady ? drive.VolumeLabel : null;

            return new FileRoot
            {
                Path = root,
                Name = string.IsNullOrWhiteSpace(label) ? $"Local disk ({letter})" : $"{label} ({letter})",
                Kind = "drive",
                TotalBytes = drive.IsReady ? drive.TotalSize : null,
                FreeBytes = drive.IsReady ? drive.AvailableFreeSpace : null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new FileRoot { Path = root, Name = $"Drive ({letter})", Kind = "drive" };
        }
    }
}

public sealed record ReadFileParameters
{
    public string? Path { get; init; }

    public long Offset { get; init; }

    public int? MaxBytes { get; init; }

    /// <summary>
    /// The size and modification time the caller saw when it started, from a listing or its first
    /// range. Either differing now is <c>file_changed</c>: ranges of two versions of a file are not a file.
    /// </summary>
    public long? ExpectedSizeBytes { get; init; }

    public DateTimeOffset? ExpectedModifiedUtc { get; init; }
}

/// <summary>One range of a file. The same shape as an evidence or APK range, plus the file's identity.</summary>
public sealed record FileChunk
{
    public required string Path { get; init; }

    public required long SizeBytes { get; init; }

    public DateTimeOffset? ModifiedUtc { get; init; }

    public required long Offset { get; init; }

    public required int ChunkBytes { get; init; }

    public required bool EndOfFile { get; init; }

    public string Encoding { get; init; } = "base64";

    public string? Data { get; init; }
}

public sealed record HashFileParameters
{
    public string? Path { get; init; }

    public long? ExpectedSizeBytes { get; init; }

    public DateTimeOffset? ExpectedModifiedUtc { get; init; }
}

public sealed record FileHashResult(string Path, long SizeBytes, DateTimeOffset? ModifiedUtc, string Sha256);

/// <summary>Opening a file for a range read or a hash, the same way for both.</summary>
internal static class FileReading
{
    /// <summary>
    /// Shared for reading, writing and deletion by others. A log another program is still appending to
    /// is exactly the kind of file worth pulling to a phone, and taking it without denying anybody
    /// anything is what reading it from Explorer does. A program holding it exclusively still wins,
    /// and that arrives as <c>file_in_use</c>.
    /// </summary>
    public static FileStream Open(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        BufferSize = 0,
    });

    public static string AuthorizeFile(PathAuthorizer authorizer, FileProtections protections, string? requested)
    {
        var path = protections.EnsureReachable(authorizer.Authorize(requested).EnsureAllowed());

        if (Directory.Exists(path))
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, "That is a folder, not a file.");
        }

        return path;
    }

    public static DateTimeOffset? ModifiedUtc(string path)
    {
        try
        {
            var value = File.GetLastWriteTimeUtc(path);
            return value.Year <= 1601 ? null : new DateTimeOffset(value, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void EnsureUnchanged(long size, DateTimeOffset? modified, long? expectedSize, DateTimeOffset? expectedModified)
    {
        if ((expectedSize is { } wantSize && wantSize != size)
            || (expectedModified is { } wantModified && modified is { } actual && wantModified.UtcTicks != actual.UtcTicks))
        {
            throw new CapabilityException(ErrorCodes.FileChanged, "The file changed on the PC while it was being transferred.");
        }
    }
}

/// <summary>
/// <c>files.read</c>: a range of a file, for the phone to download.
/// <para>
/// Stateless on purpose. Each range opens the file, checks it is still the file the transfer started
/// on, reads, and closes it again, so there is nothing on the PC for a dropped connection to leave
/// behind, and resuming is simply asking from the length the phone already holds.
/// </para>
/// </summary>
public sealed class ReadFileAction(PathAuthorizer authorizer, FileProtections? protections = null)
    : CapabilityAction<ReadFileParameters, FileChunk>
{
    public const int DefaultChunkBytes = 1024 * 1024;
    public const int MaxChunkBytes = 4 * 1024 * 1024;

    private readonly FileProtections _protections = protections ?? FileProtections.None;

    public override string Name => "files.read";

    public override string Description => "A range of a file inside an allowed root.";

    protected override async ValueTask<FileChunk> ExecuteAsync(
        ReadFileParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        if (parameters.Offset < 0)
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, "offset must not be negative.");
        }

        var path = FileReading.AuthorizeFile(authorizer, _protections, parameters.Path);
        var wanted = Math.Clamp(parameters.MaxBytes ?? DefaultChunkBytes, 1, MaxChunkBytes);

        try
        {
            await using var file = FileReading.Open(path);

            var length = file.Length;
            var modified = FileReading.ModifiedUtc(path);

            FileReading.EnsureUnchanged(length, modified, parameters.ExpectedSizeBytes, parameters.ExpectedModifiedUtc);

            if (parameters.Offset > length)
            {
                throw new CapabilityException(ErrorCodes.FileChanged, "The file is shorter on the PC than what was already transferred.");
            }

            var count = (int)Math.Min(wanted, length - parameters.Offset);
            var buffer = new byte[count];

            if (count > 0)
            {
                file.Seek(parameters.Offset, SeekOrigin.Begin);
                await file.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            return new FileChunk
            {
                Path = path,
                SizeBytes = length,
                ModifiedUtc = modified,
                Offset = parameters.Offset,
                ChunkBytes = count,
                EndOfFile = parameters.Offset + count >= length,
                Data = Convert.ToBase64String(buffer),
            };
        }
        catch (EndOfStreamException)
        {
            // Truncated between reading its length and reading its bytes.
            throw new CapabilityException(ErrorCodes.FileChanged, "The file changed on the PC while it was being transferred.");
        }
        catch (Exception ex) when (FileErrors.IsFileSystemFailure(ex))
        {
            throw FileErrors.From(ex, "reading that file");
        }
    }
}

/// <summary>
/// <c>files.hash</c>: the SHA-256 of a file as it is on the PC now, so the phone can check what it
/// assembled from ranges against the file rather than against what the ranges said.
/// </summary>
public sealed class HashFileAction(PathAuthorizer authorizer, FileProtections? protections = null)
    : CapabilityAction<HashFileParameters, FileHashResult>
{
    private readonly FileProtections _protections = protections ?? FileProtections.None;

    public override string Name => "files.hash";

    public override string Description => "The SHA-256 of a file inside an allowed root.";

    protected override async ValueTask<FileHashResult> ExecuteAsync(
        HashFileParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var path = FileReading.AuthorizeFile(authorizer, _protections, parameters.Path);

        try
        {
            await using var file = FileReading.Open(path);

            var length = file.Length;
            var modified = FileReading.ModifiedUtc(path);

            FileReading.EnsureUnchanged(length, modified, parameters.ExpectedSizeBytes, parameters.ExpectedModifiedUtc);

            var digest = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);

            // Checked again afterwards: a file written to while it was being hashed has no one digest.
            FileReading.EnsureUnchanged(file.Length, FileReading.ModifiedUtc(path), length, modified);

            return new FileHashResult(path, length, modified, Convert.ToHexStringLower(digest));
        }
        catch (Exception ex) when (FileErrors.IsFileSystemFailure(ex))
        {
            throw FileErrors.From(ex, "reading that file");
        }
    }
}
