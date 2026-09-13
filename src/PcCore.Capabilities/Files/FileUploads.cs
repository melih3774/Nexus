using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.Storage;
using PcCore.Protocol;

namespace PcCore.Capabilities.Files;

public sealed record BeginUploadParameters
{
    /// <summary>The folder to upload into. Authorised like any other path.</summary>
    public string? Directory { get; init; }

    /// <summary>A file name, not a path: no separators, no stream, no reserved device name.</summary>
    public string? Name { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>Replace a file already there. The phone sets it only after the owner has said yes.</summary>
    public bool Overwrite { get; init; }
}

public sealed record UploadState
{
    public required string UploadId { get; init; }

    /// <summary>Where the file will be once it is finished.</summary>
    public required string Path { get; init; }

    public required long SizeBytes { get; init; }

    /// <summary>How much the PC already holds. Nonzero when <see cref="Resumed"/>: send from here.</summary>
    public required long ReceivedBytes { get; init; }

    /// <summary>The largest range one <c>files.upload.chunk</c> may carry.</summary>
    public required int MaxChunkBytes { get; init; }

    public bool Resumed { get; init; }
}

public sealed record UploadChunkParameters
{
    public string? UploadId { get; init; }

    public long Offset { get; init; }

    /// <summary>Base64.</summary>
    public string? Data { get; init; }
}

public sealed record UploadChunkResult(string UploadId, long ReceivedBytes);

public sealed record FinishUploadParameters
{
    public string? UploadId { get; init; }

    /// <summary>SHA-256 of the whole file, lowercase or uppercase hex, as the sender computed it.</summary>
    public string? Sha256 { get; init; }
}

public sealed record FinishUploadResult
{
    public required string Path { get; init; }

    public required long SizeBytes { get; init; }

    public DateTimeOffset? ModifiedUtc { get; init; }

    public required string Sha256 { get; init; }
}

public sealed record CancelUploadParameters
{
    public string? UploadId { get; init; }
}

public sealed record CancelUploadResult(bool Cancelled);

/// <summary>Checks a caller-supplied file name before it is joined to anything.</summary>
public static class FileNames
{
    public const string PartSuffix = ".nexus-upload";

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    private static readonly char[] Invalid = [.. Path.GetInvalidFileNameChars(), '\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>The name, if it names exactly one file in one folder; otherwise <c>invalid_parameters</c>.</summary>
    public static string Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Length > 255
            || name.IndexOfAny(Invalid) >= 0
            || name is "." or ".."

            // Windows drops trailing dots and spaces when it opens a name, so "notes.txt." is
            // "notes.txt": a different name that writes over the same file.
            || name.EndsWith('.')
            || name.EndsWith(' ')
            || ReservedDeviceNames.Contains(name.Split('.')[0].TrimEnd(' '))
            || name.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, "That is not a file name Windows can use.");
        }

        return name;
    }
}

/// <summary>
/// Uploads from the phone into folders on the PC.
/// <para>
/// <b>Where the bytes go.</b> Into a hidden <c>*.nexus-upload</c> file beside the destination, renamed
/// over it only when the whole file has arrived and hashes to what the phone said it sent. Beside it
/// rather than in a staging folder of Nexus's own for two reasons: a file created in the destination
/// inherits that folder's permissions the way anything saved there does, where one moved in from
/// <c>%LOCALAPPDATA%</c> would carry AppData's owner-only ACL with it; and a folder this account cannot
/// write to says so at the first byte, not after the last one.
/// </para>
/// <para>
/// <b>What survives what.</b> An upload belongs to the device that began it, not to its socket, so a
/// dropped connection can resume: beginning the same upload again answers with how much the PC
/// already holds. An upload that stops receiving is abandoned after
/// <see cref="FilesOptions.UploadIdleMinutes"/>, and PC Core stopping abandons them all - a partial
/// file is deleted either way. A crash cannot run that, so the partial files are also written to a
/// small journal, and whatever it still lists is deleted at the next start.
/// </para>
/// </summary>
public sealed class FileUploadService : IDisposable
{
    /// <summary>
    /// The largest range one chunk carries. A megabyte is about 1.4 MB of base64, which fits the remote
    /// transport's limit for an authenticated message with room for the envelope.
    /// </summary>
    public const int MaxChunkBytes = 1024 * 1024;

    /// <summary>Uploads open at once, across every caller. Each holds a file handle.</summary>
    public const int MaxActiveUploads = 8;

    private const string JournalFile = "uploads.json";

    private readonly PathAuthorizer _authorizer;
    private readonly FileProtections _protections;
    private readonly TimeSpan _idle;
    private readonly ILogger<FileUploadService> _logger;
    private readonly TimeProvider _time;
    private readonly AtomicJsonFileStore<UploadJournal>? _journal;
    private readonly ITimer _sweeper;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Upload> _uploads = new(StringComparer.Ordinal);
    private bool _disposed;

    public FileUploadService(
        PathAuthorizer authorizer,
        FilesOptions options,
        ILogger<FileUploadService> logger,
        FileProtections? protections = null,
        StateDirectory? state = null,
        TimeProvider? time = null)
    {
        _authorizer = authorizer;
        _protections = protections ?? FileProtections.None;
        _idle = TimeSpan.FromMinutes(Math.Max(1, options.UploadIdleMinutes));
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _journal = state is null ? null : new AtomicJsonFileStore<UploadJournal>(state, JournalFile, logger);

        DeleteLeftovers();

        _sweeper = _time.CreateTimer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _uploads.Count;
            }
        }
    }

    public UploadState Begin(BeginUploadParameters parameters, CallerDescriptor caller)
    {
        var name = FileNames.Validate(parameters.Name);

        if (parameters.SizeBytes < 0)
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, "size_bytes must not be negative.");
        }

        var directory = _protections.EnsureReachable(_authorizer.Authorize(parameters.Directory).EnsureAllowed());

        if (!Directory.Exists(directory))
        {
            throw File.Exists(directory)
                ? new CapabilityException(ErrorCodes.InvalidParameters, "That is a file, not a folder.")
                : new CapabilityException(ErrorCodes.NotFound, "That folder no longer exists on the PC.");
        }

        // Authorised again as a whole: an existing entry of that name could itself be a link.
        var target = _protections.EnsureReachable(_authorizer.Authorize(Path.Combine(directory, name)).EnsureAllowed());
        var owner = OwnerOf(caller);

        Sweep();

        Upload? existing;

        lock (_gate)
        {
            existing = _uploads.Values.FirstOrDefault(u =>
                u.Owner == owner && string.Equals(u.TargetPath, target, StringComparison.OrdinalIgnoreCase));
        }

        if (existing is not null && existing.SizeBytes == parameters.SizeBytes)
        {
            existing.Overwrite = parameters.Overwrite;
            existing.Touch(_time);

            // Read under the upload's own gate, so the count is not taken half-way through a write.
            existing.Gate.Wait();
            try
            {
                if (!existing.Closed)
                {
                    return existing.State(resumed: true);
                }
            }
            finally
            {
                existing.Gate.Release();
            }
        }
        else if (existing is not null)
        {
            // Never under _gate: Abort waits for the upload's gate, and a chunk holding that gate may be
            // waiting for _gate.
            Abort(existing, "replaced by an upload of a different size");
        }

        if (Directory.Exists(target))
        {
            throw new CapabilityException(ErrorCodes.AlreadyExists, "A folder with that name is already there.");
        }

        if (File.Exists(target) && !parameters.Overwrite)
        {
            throw new CapabilityException(ErrorCodes.AlreadyExists, "A file with that name is already there.");
        }

        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        var part = Path.Combine(directory, $"{(name.Length > 200 ? name[..200] : name)}.{id[..8]}{FileNames.PartSuffix}");

        lock (_gate)
        {
            if (_uploads.Count >= MaxActiveUploads)
            {
                throw new CapabilityException(ErrorCodes.RateLimited, "Too many uploads are already in progress on the PC.");
            }
        }

        FileStream stream;
        try
        {
            stream = new FileStream(part, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                BufferSize = 0,

                // Asks the volume for the space up front, so a disk that cannot hold the file says so
                // now rather than a gigabyte in.
                PreallocationSize = parameters.SizeBytes,
            });
        }
        catch (Exception ex) when (FileErrors.IsFileSystemFailure(ex))
        {
            throw FileErrors.From(ex, "writing to that folder");
        }

        try
        {
            File.SetAttributes(part, File.GetAttributes(part) | FileAttributes.Hidden);
        }
        catch (Exception ex) when (FileErrors.IsFileSystemFailure(ex))
        {
            // Cosmetic: a visible partial file is still deleted on every path out.
        }

        var upload = new Upload(id, owner, target, part, parameters.SizeBytes, parameters.Overwrite, stream);
        upload.Touch(_time);

        lock (_gate)
        {
            _uploads[id] = upload;
            SaveJournal();
        }

        _logger.LogInformation("Upload {UploadId} started: {Size} bytes into {Target}.", id, parameters.SizeBytes, target);

        return upload.State(resumed: false);
    }

    public async ValueTask<UploadChunkResult> WriteAsync(UploadChunkParameters parameters, CallerDescriptor caller, CancellationToken cancellationToken)
    {
        var upload = Find(parameters.UploadId, caller);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(parameters.Data ?? string.Empty);
        }
        catch (FormatException)
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, "data is not base64.");
        }

        if (bytes.Length > MaxChunkBytes)
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, $"A chunk may carry at most {MaxChunkBytes} bytes.");
        }

        await upload.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (upload.Closed)
            {
                throw Gone();
            }

            if (parameters.Offset != upload.ReceivedBytes)
            {
                // Most often a retry of a chunk whose answer was lost. Beginning the upload again says
                // how much actually arrived.
                throw new CapabilityException(
                    ErrorCodes.InvalidParameters,
                    $"The PC holds {upload.ReceivedBytes} bytes of this upload; continue from there.",
                    $"received_bytes={upload.ReceivedBytes}");
            }

            if (upload.ReceivedBytes + bytes.Length > upload.SizeBytes)
            {
                throw new CapabilityException(ErrorCodes.InvalidParameters, "That is more than the size the upload was begun with.");
            }

            try
            {
                await upload.Stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (FileErrors.IsFileSystemFailure(ex))
            {
                Abort(upload, "a write failed", holdingGate: true);
                throw FileErrors.From(ex, "writing that file");
            }

            upload.Hash.AppendData(bytes);
            upload.ReceivedBytes += bytes.Length;
            upload.Touch(_time);

            return new UploadChunkResult(upload.Id, upload.ReceivedBytes);
        }
        finally
        {
            upload.Gate.Release();
        }
    }

    public async ValueTask<FinishUploadResult> FinishAsync(FinishUploadParameters parameters, CallerDescriptor caller, CancellationToken cancellationToken)
    {
        var upload = Find(parameters.UploadId, caller);

        await upload.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (upload.Closed)
            {
                throw Gone();
            }

            if (upload.ReceivedBytes != upload.SizeBytes)
            {
                throw new CapabilityException(
                    ErrorCodes.InvalidParameters,
                    $"The PC holds {upload.ReceivedBytes} of {upload.SizeBytes} bytes; the upload is not complete.",
                    $"received_bytes={upload.ReceivedBytes}");
            }

            var digest = Convert.ToHexStringLower(upload.Hash.GetCurrentHash());

            if (!string.IsNullOrEmpty(parameters.Sha256) && !string.Equals(parameters.Sha256, digest, StringComparison.OrdinalIgnoreCase))
            {
                Abort(upload, "its checksum did not match", holdingGate: true);
                throw new CapabilityException(ErrorCodes.ChecksumMismatch, "What arrived did not match the file on the phone, so it was discarded.");
            }

            try
            {
                await upload.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                upload.Stream.Flush(flushToDisk: true);
                await upload.Stream.DisposeAsync().ConfigureAwait(false);

                File.SetAttributes(upload.PartPath, File.GetAttributes(upload.PartPath) & ~FileAttributes.Hidden);
                File.Move(upload.PartPath, upload.TargetPath, overwrite: upload.Overwrite);
            }
            catch (Exception ex) when (FileErrors.IsFileSystemFailure(ex))
            {
                Abort(upload, "it could not be put in place", holdingGate: true);
                throw FileErrors.From(ex, "replacing that file");
            }

            lock (_gate)
            {
                upload.Closed = true;
                _uploads.Remove(upload.Id);
                SaveJournal();
            }

            upload.Hash.Dispose();

            _logger.LogInformation("Upload {UploadId} finished: {Target} ({Size} bytes).", upload.Id, upload.TargetPath, upload.SizeBytes);

            return new FinishUploadResult
            {
                Path = upload.TargetPath,
                SizeBytes = upload.SizeBytes,
                ModifiedUtc = FileReading.ModifiedUtc(upload.TargetPath),
                Sha256 = digest,
            };
        }
        finally
        {
            upload.Gate.Release();
        }
    }

    public CancelUploadResult Cancel(CancelUploadParameters parameters, CallerDescriptor caller)
    {
        Upload? upload;

        lock (_gate)
        {
            upload = _uploads.GetValueOrDefault(parameters.UploadId ?? string.Empty);
        }

        if (upload is null || upload.Owner != OwnerOf(caller))
        {
            return new CancelUploadResult(false);
        }

        Abort(upload, "the sender cancelled it");
        return new CancelUploadResult(true);
    }

    /// <summary>Abandons every upload that has received nothing for the idle interval.</summary>
    internal void Sweep()
    {
        var now = _time.GetUtcNow();
        Upload[] stale;

        lock (_gate)
        {
            stale = [.. _uploads.Values.Where(u => now - u.LastActivityUtc > _idle)];
        }

        foreach (var upload in stale)
        {
            Abort(upload, $"nothing arrived for {_idle.TotalMinutes:0} minutes");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sweeper.Dispose();

        Upload[] all;
        lock (_gate)
        {
            all = [.. _uploads.Values];
        }

        foreach (var upload in all)
        {
            Abort(upload, "PC Core is stopping");
        }
    }

    /// <summary>
    /// Uploads belong to the device, not the connection: a phone that reconnects is the same phone.
    /// Local callers share one owner, because the loopback transport has no stable connection to key on
    /// and every local caller is already the owner at the PC.
    /// </summary>
    private static string OwnerOf(CallerDescriptor caller) =>
        caller.ActorType == ActorType.Device ? $"device:{caller.DeviceId}" : caller.ActorType.ToString();

    private Upload Find(string? id, CallerDescriptor caller)
    {
        lock (_gate)
        {
            // Someone else's upload id is reported exactly like an unknown one.
            return _uploads.GetValueOrDefault(id ?? string.Empty) is { } upload && upload.Owner == OwnerOf(caller)
                ? upload
                : throw Gone();
        }
    }

    private static CapabilityException Gone() =>
        new(ErrorCodes.NotFound, "That upload is no longer in progress on the PC. Start it again.");

    private void Abort(Upload upload, string reason, bool holdingGate = false)
    {
        if (!holdingGate)
        {
            // Waits for a chunk being written, which is bounded by one megabyte of disk.
            upload.Gate.Wait();
        }

        try
        {
            if (upload.Closed)
            {
                return;
            }

            upload.Closed = true;

            try
            {
                upload.Stream.Dispose();
            }
            catch (Exception ex) when (FileErrors.IsFileSystemFailure(ex))
            {
                _logger.LogDebug(ex, "Closing upload {UploadId} failed.", upload.Id);
            }

            upload.Hash.Dispose();
            TryDelete(upload.PartPath);

            lock (_gate)
            {
                _uploads.Remove(upload.Id);
                SaveJournal();
            }

            _logger.LogInformation("Upload {UploadId} into {Target} abandoned: {Reason}.", upload.Id, upload.TargetPath, reason);
        }
        finally
        {
            if (!holdingGate)
            {
                upload.Gate.Release();
            }
        }
    }

    private void TryDelete(string partPath)
    {
        try
        {
            if (File.Exists(partPath))
            {
                File.SetAttributes(partPath, FileAttributes.Normal);
                File.Delete(partPath);
            }
        }
        catch (Exception ex) when (FileErrors.IsFileSystemFailure(ex))
        {
            _logger.LogWarning("A partial upload could not be deleted and was left at {PartPath}: {Reason}", partPath, ex.Message);
        }
    }

    /// <summary>Called with <see cref="_gate"/> held.</summary>
    private void SaveJournal()
    {
        if (_journal is null)
        {
            return;
        }

        try
        {
            _journal.Save(new UploadJournal { PartFiles = [.. _uploads.Values.Select(u => u.PartPath)] });
        }
        catch (Exception ex) when (FileErrors.IsFileSystemFailure(ex))
        {
            _logger.LogWarning(ex, "The upload journal could not be written; a crash now could leave a partial upload behind.");
        }
    }

    private void DeleteLeftovers()
    {
        if (_journal?.Load() is not { IsLoaded: true, Value: { } journal })
        {
            return;
        }

        foreach (var part in journal.PartFiles)
        {
            // Only ever a file this service named. The journal is Nexus's own, but it is still a list of
            // paths read from disk, and it is not going to delete anything else on the strength of it.
            if (part.EndsWith(FileNames.PartSuffix, StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(part))
            {
                _logger.LogInformation("Deleting a partial upload left by the last run: {PartPath}", part);
                TryDelete(part);
            }
        }

        lock (_gate)
        {
            SaveJournal();
        }
    }

    private sealed class Upload(string id, string owner, string targetPath, string partPath, long sizeBytes, bool overwrite, FileStream stream)
    {
        public string Id { get; } = id;

        public string Owner { get; } = owner;

        public string TargetPath { get; } = targetPath;

        public string PartPath { get; } = partPath;

        public long SizeBytes { get; } = sizeBytes;

        public bool Overwrite { get; set; } = overwrite;

        public FileStream Stream { get; } = stream;

        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public long ReceivedBytes { get; set; }

        public bool Closed { get; set; }

        public DateTimeOffset LastActivityUtc { get; private set; }

        public void Touch(TimeProvider time) => LastActivityUtc = time.GetUtcNow();

        public UploadState State(bool resumed) => new()
        {
            UploadId = Id,
            Path = TargetPath,
            SizeBytes = SizeBytes,
            ReceivedBytes = ReceivedBytes,
            MaxChunkBytes = MaxChunkBytes,
            Resumed = resumed,
        };
    }

    public sealed class UploadJournal
    {
        public List<string> PartFiles { get; set; } = [];
    }
}

/// <summary><c>files.upload.begin</c>: start, or resume, an upload into a folder.</summary>
public sealed class BeginUploadAction(FileUploadService uploads) : CapabilityAction<BeginUploadParameters, UploadState>
{
    public override string Name => "files.upload.begin";

    public override string Description => "Start or resume uploading a file into a folder inside an allowed root.";

    protected override ValueTask<UploadState> ExecuteAsync(BeginUploadParameters parameters, CapabilityContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(uploads.Begin(parameters, context.Caller));
}

/// <summary><c>files.upload.chunk</c>: the next range of an upload, strictly in order.</summary>
public sealed class UploadChunkAction(FileUploadService uploads) : CapabilityAction<UploadChunkParameters, UploadChunkResult>
{
    public override string Name => "files.upload.chunk";

    public override string Description => "Append the next range of an upload.";

    protected override ValueTask<UploadChunkResult> ExecuteAsync(UploadChunkParameters parameters, CapabilityContext context, CancellationToken cancellationToken) =>
        uploads.WriteAsync(parameters, context.Caller, cancellationToken);
}

/// <summary><c>files.upload.finish</c>: check the digest and put the file in place.</summary>
public sealed class FinishUploadAction(FileUploadService uploads) : CapabilityAction<FinishUploadParameters, FinishUploadResult>
{
    public override string Name => "files.upload.finish";

    public override string Description => "Verify a complete upload and move it into place.";

    protected override ValueTask<FinishUploadResult> ExecuteAsync(FinishUploadParameters parameters, CapabilityContext context, CancellationToken cancellationToken) =>
        uploads.FinishAsync(parameters, context.Caller, cancellationToken);
}

/// <summary><c>files.upload.cancel</c>: abandon an upload and delete what arrived.</summary>
public sealed class CancelUploadAction(FileUploadService uploads) : CapabilityAction<CancelUploadParameters, CancelUploadResult>
{
    public override string Name => "files.upload.cancel";

    public override string Description => "Abandon an upload and delete its partial file.";

    protected override ValueTask<CancelUploadResult> ExecuteAsync(CancelUploadParameters parameters, CapabilityContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(uploads.Cancel(parameters, context.Caller));
}
