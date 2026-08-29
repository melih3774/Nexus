using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PcCore.Capabilities.Storage;

/// <summary>
/// Where PC Core keeps everything that must survive a restart.
/// <para>
/// Default root is <c>%LOCALAPPDATA%\Nexus</c> — user-local, outside the repository, and never
/// committed. The root is constructor-injected so tests use a throwaway directory instead.
/// </para>
/// </summary>
public sealed class StateDirectory
{
    public const string DefaultFolderName = "Nexus";

    public StateDirectory(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    /// <summary>The real per-user location: <c>%LOCALAPPDATA%\Nexus</c>.</summary>
    public static StateDirectory ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        DefaultFolderName));

    public string File(string fileName) => Path.Combine(Root, fileName);

    public void Ensure() => Directory.CreateDirectory(Root);
}

/// <summary>Why a load returned nothing.</summary>
public enum StateLoadStatus
{
    /// <summary>The file was read and parsed.</summary>
    Loaded,

    /// <summary>No file yet — first run.</summary>
    Missing,

    /// <summary>The file existed but could not be used. It has been quarantined.</summary>
    Corrupt,
}

public readonly record struct StateLoadResult<T>(StateLoadStatus Status, T? Value) where T : class
{
    public bool IsLoaded => Status == StateLoadStatus.Loaded && Value is not null;
}

/// <summary>
/// A single small JSON document on disk, written atomically.
/// <para>
/// <b>Atomicity.</b> Writes go to a sibling <c>.tmp</c> file which is flushed to the physical
/// device, then moved over the target. On Windows that move is <c>MoveFileEx</c> with
/// <c>MOVEFILE_REPLACE_EXISTING</c>, which is atomic within a volume. A crash therefore leaves
/// either the whole previous document or the whole new one — never a half-written mixture, which
/// for security state would be worse than either.
/// </para>
/// <para>
/// <b>Corruption.</b> An unreadable or unparseable file is renamed aside with a timestamp rather
/// than deleted, and reported as <see cref="StateLoadStatus.Corrupt"/> so each caller can choose
/// its own safe fallback. Nothing is silently reconstructed here.
/// </para>
/// <para>
/// This and <c>PcCore.Protocol</c> are the only places JSON is touched: the wire codec for
/// messages, this store for disk. No capability, transport, or Windows type sees JSON.
/// </para>
/// </summary>
public sealed class AtomicJsonFileStore<T> where T : class
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly StateDirectory _directory;
    private readonly string _fileName;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    public AtomicJsonFileStore(StateDirectory directory, string fileName, ILogger logger)
    {
        _directory = directory;
        _fileName = fileName;
        _logger = logger;
    }

    public string Path => _directory.File(_fileName);

    public bool Exists => global::System.IO.File.Exists(Path);

    public StateLoadResult<T> Load()
    {
        lock (_gate)
        {
            if (!global::System.IO.File.Exists(Path))
            {
                return new StateLoadResult<T>(StateLoadStatus.Missing, null);
            }

            try
            {
                var json = global::System.IO.File.ReadAllText(Path);
                var value = JsonSerializer.Deserialize<T>(json, Options);

                if (value is null)
                {
                    return Quarantine("file deserialised to null");
                }

                return new StateLoadResult<T>(StateLoadStatus.Loaded, value);
            }
            catch (JsonException ex)
            {
                return Quarantine($"invalid JSON ({ex.Message})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not corruption — the file may simply be locked. Report it as corrupt so the
                // caller still takes its safe path, but do not quarantine a file we could not read.
                _logger.LogError(ex, "Could not read state file {StateFile}.", _fileName);
                return new StateLoadResult<T>(StateLoadStatus.Corrupt, null);
            }
        }
    }

    public void Save(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        lock (_gate)
        {
            _directory.Ensure();

            var temporary = Path + ".tmp";
            var json = JsonSerializer.Serialize(value, Options);

            using (var stream = new FileStream(
                       temporary,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                // Force the bytes out before the rename, so a power loss cannot leave the rename
                // committed while the contents are still in a cache.
                stream.Flush(flushToDisk: true);
            }

            global::System.IO.File.Move(temporary, Path, overwrite: true);
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            try
            {
                if (global::System.IO.File.Exists(Path))
                {
                    global::System.IO.File.Delete(Path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not delete state file {StateFile}.", _fileName);
                throw;
            }
        }
    }

    private StateLoadResult<T> Quarantine(string reason)
    {
        // Renamed rather than deleted: the owner (or a later forensic pass) may want to see what
        // was there, and destroying evidence of a corrupt security-state file is the wrong default.
        var quarantined = $"{Path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}";

        try
        {
            global::System.IO.File.Move(Path, quarantined, overwrite: false);
            _logger.LogError(
                "State file {StateFile} is unusable ({Reason}). Moved aside to {Quarantined}.",
                _fileName,
                reason,
                quarantined);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "State file {StateFile} is unusable ({Reason}) and could not be moved aside.", _fileName, reason);
        }

        return new StateLoadResult<T>(StateLoadStatus.Corrupt, null);
    }
}
