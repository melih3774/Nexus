using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Storage;

namespace PcCore.Capabilities.Security.Sessions;

public sealed class SecurityStorageOptions
{
    public const string SectionName = "PcCore:Security:Storage";

    /// <summary>
    /// How long a resolved incident is kept.
    /// <para>
    /// Thirty days: long enough that "was anyone at my PC while I was away last month" is still
    /// answerable, short enough that a personal machine is not accumulating camera footage
    /// indefinitely. Evidence you no longer need is a liability, not an asset.
    /// </para>
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// Total evidence on disk, in megabytes, before the oldest resolved incidents are trimmed.
    /// <para>
    /// A second bound because time alone does not stop a single very active day filling a disk.
    /// </para>
    /// </summary>
    public int MaxEvidenceMegabytes { get; set; } = 2048;

    /// <summary>Below this much free disk space, new evidence stops. Metadata never does.</summary>
    public int MinimumFreeDiskMegabytes { get; set; } = 1024;

    /// <summary>How many incidents to keep at most, regardless of age.</summary>
    public int MaxSessions { get; set; } = 200;
}

/// <summary>Where security data lives. Under the existing state directory, never in the repository.</summary>
public sealed class SecurityPaths
{
    public SecurityPaths(StateDirectory directory)
    {
        Root = Path.Combine(directory.Root, "Security");
        EvidenceRoot = Path.Combine(Root, "evidence");
    }

    public string Root { get; }

    public string EvidenceRoot { get; }

    public string SessionsFile => "sessions.json";

    /// <summary>
    /// The folder for one session's files.
    /// <para>
    /// The session id is generated, not caller-supplied, but it is validated here anyway: this value
    /// is concatenated into a path, and a component that could contain a separator or a parent
    /// reference is the shape of a directory traversal even when today's caller is trustworthy.
    /// </para>
    /// </summary>
    public string EvidenceFolder(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || sessionId.Length > 64
            || !sessionId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-'))
        {
            throw new ArgumentException("session id is not a safe path component", nameof(sessionId));
        }

        return Path.Combine(EvidenceRoot, sessionId);
    }
}

/// <summary>
/// Reads and writes the incident history.
/// <para>
/// The whole history is one atomically-written JSON file. There are at most a couple of hundred
/// sessions on a personal machine, each holding text, and rewriting a few tens of kilobytes when an
/// incident changes is far simpler to reason about than a per-session file layout that can be
/// half-updated. Media never goes in here — only the paths to it.
/// </para>
/// </summary>
public interface ISecuritySessionStore
{
    IReadOnlyList<SecuritySession> All();

    SecuritySession? Find(string sessionId);

    /// <summary>The open incident, if there is one. At most one exists at a time.</summary>
    SecuritySession? Active();

    /// <summary>Inserts or replaces, then persists. Returns what was stored.</summary>
    SecuritySession Save(SecuritySession session);

    /// <summary>Removes an incident and everything it recorded. Irreversible.</summary>
    bool Delete(string sessionId);

    /// <summary>Applies retention. Returns how many incidents were removed.</summary>
    int Prune();

    /// <summary>Where a session's files live, creating the folder if needed.</summary>
    string EvidenceFolder(string sessionId);

    /// <summary>Full path of one evidence file, or null when it is missing or escapes the folder.</summary>
    string? ResolveEvidence(string sessionId, string fileName);

    long EvidenceBytes();

    bool HasRoomForEvidence();
}

public sealed class SecuritySessionStore : ISecuritySessionStore
{
    private readonly SecurityPaths _paths;
    private readonly SecurityStorageOptions _options;
    private readonly ILogger<SecuritySessionStore> _logger;
    private readonly AtomicJsonFileStore<SecuritySessionFile> _store;
    private readonly Lock _gate = new();

    private List<SecuritySession> _sessions = [];

    public SecuritySessionStore(
        StateDirectory directory,
        SecurityStorageOptions options,
        ILogger<SecuritySessionStore> logger)
    {
        _paths = new SecurityPaths(directory);
        _options = options;
        _logger = logger;

        Directory.CreateDirectory(_paths.Root);
        Directory.CreateDirectory(_paths.EvidenceRoot);

        _store = new AtomicJsonFileStore<SecuritySessionFile>(
            new StateDirectory(_paths.Root), "sessions.json", logger);

        var loaded = _store.Load();

        if (loaded.IsLoaded)
        {
            _sessions = [.. loaded.Value!.Sessions];
            _logger.LogInformation("Loaded {Count} security session(s).", _sessions.Count);
        }
        else
        {
            // A corrupt history is not a reason to refuse to protect the machine. The file has
            // already been renamed aside by the store; a fresh one starts here, and the incident
            // that matters is the one about to happen.
            _sessions = [];
        }
    }

    public IReadOnlyList<SecuritySession> All()
    {
        lock (_gate)
        {
            return [.. _sessions.OrderByDescending(s => s.StartedAtUtc)];
        }
    }

    public SecuritySession? Find(string sessionId)
    {
        lock (_gate)
        {
            return _sessions.FirstOrDefault(s => s.SessionId == sessionId);
        }
    }

    public SecuritySession? Active()
    {
        lock (_gate)
        {
            return _sessions
                .Where(s => s.Resolution != SessionResolution.AcknowledgedOwner && s.EndedAtUtc is null)
                .OrderByDescending(s => s.StartedAtUtc)
                .FirstOrDefault();
        }
    }

    public SecuritySession Save(SecuritySession session)
    {
        lock (_gate)
        {
            var index = _sessions.FindIndex(s => s.SessionId == session.SessionId);

            if (index >= 0)
            {
                _sessions[index] = session;
            }
            else
            {
                _sessions.Add(session);
            }

            Persist();
            return session;
        }
    }

    public bool Delete(string sessionId)
    {
        lock (_gate)
        {
            var removed = _sessions.RemoveAll(s => s.SessionId == sessionId) > 0;

            if (!removed)
            {
                return false;
            }

            TryDeleteFolder(sessionId);
            Persist();

            _logger.LogInformation("Security session {SessionId} and its evidence were deleted.", sessionId);
            return true;
        }
    }

    /// <summary>
    /// Applies the three bounds in order: age, count, then total size.
    /// <para>
    /// An open incident and one marked <c>NOT ME</c> are never removed by any of them. Retention
    /// exists to stop a disk filling with routine history, not to quietly discard the one recording
    /// the owner is most likely to want.
    /// </para>
    /// </summary>
    public int Prune()
    {
        lock (_gate)
        {
            var removable = _sessions
                .Where(IsPrunable)
                .OrderBy(s => s.StartedAtUtc)
                .ToList();

            var removed = new List<SecuritySession>();
            var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, _options.RetentionDays));

            foreach (var session in removable.Where(s => s.StartedAtUtc < cutoff))
            {
                removed.Add(session);
            }

            var keptCount = _sessions.Count - removed.Count;

            foreach (var session in removable.Except(removed))
            {
                if (keptCount <= _options.MaxSessions)
                {
                    break;
                }

                removed.Add(session);
                keptCount--;
            }

            var budget = (long)Math.Max(64, _options.MaxEvidenceMegabytes) * 1024 * 1024;
            var used = EvidenceBytesLocked();

            foreach (var session in removable.Except(removed))
            {
                if (used <= budget)
                {
                    break;
                }

                used -= FolderBytes(session.SessionId);
                removed.Add(session);
            }

            foreach (var session in removed)
            {
                _sessions.Remove(session);
                TryDeleteFolder(session.SessionId);
            }

            if (removed.Count > 0)
            {
                Persist();
                _logger.LogInformation("Retention removed {Count} security session(s).", removed.Count);
            }

            return removed.Count;
        }
    }

    /// <summary>An incident is only ever removed automatically once it is closed and unremarkable.</summary>
    private static bool IsPrunable(SecuritySession session) =>
        !session.HighAlert
        && session.Resolution == SessionResolution.AcknowledgedOwner
        && session.EndedAtUtc is not null;

    public string EvidenceFolder(string sessionId)
    {
        var folder = _paths.EvidenceFolder(sessionId);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// Turns a session id and a file name into a real path, or refuses.
    /// <para>
    /// The file name arrives from a request. Both the name and the resolved path are checked, so a
    /// name containing a separator, a parent reference, or an absolute path cannot reach outside the
    /// session's own folder — the same containment rule the filesystem capability has used since
    /// v0.1, applied here because this is a second place caller-supplied text becomes a path.
    /// </para>
    /// </summary>
    public string? ResolveEvidence(string sessionId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Length > 128
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || fileName.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(fileName)
            || fileName.Any(c => char.IsControl(c)))
        {
            return null;
        }

        string folder;
        try
        {
            folder = _paths.EvidenceFolder(sessionId);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(folder, fileName));

        // Belt and braces: even with the name checked, the resolved path must still be inside.
        if (!full.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(full) ? full : null;
    }

    public long EvidenceBytes()
    {
        lock (_gate)
        {
            return EvidenceBytesLocked();
        }
    }

    /// <summary>
    /// Whether there is room to record more.
    /// <para>
    /// Checked before each capture rather than after. Filling the system drive would be a far worse
    /// outcome than a gap in an incident's evidence, and the metadata — which is what makes the
    /// incident legible at all — costs nothing and never stops.
    /// </para>
    /// </summary>
    public bool HasRoomForEvidence()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(_paths.Root) ?? "C:\\");
            var freeMegabytes = drive.AvailableFreeSpace / 1024 / 1024;

            if (freeMegabytes < _options.MinimumFreeDiskMegabytes)
            {
                _logger.LogWarning(
                    "Only {Free} MB free; security evidence capture is paused (metadata continues).", freeMegabytes);
                return false;
            }

            return EvidenceBytes() < (long)Math.Max(64, _options.MaxEvidenceMegabytes) * 1024 * 1024;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // If free space cannot be determined, err towards recording: a missed incident is worse
            // than an approximate disk bound.
            return true;
        }
    }

    private long EvidenceBytesLocked() => _sessions.Sum(s => FolderBytes(s.SessionId));

    private long FolderBytes(string sessionId)
    {
        try
        {
            var folder = _paths.EvidenceFolder(sessionId);

            return Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFiles().Sum(f => f.Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    private void TryDeleteFolder(string sessionId)
    {
        try
        {
            var folder = _paths.EvidenceFolder(sessionId);

            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning("Evidence for session {SessionId} could not be deleted.", sessionId);
        }
    }

    private void Persist() => _store.Save(new SecuritySessionFile { Sessions = [.. _sessions] });

    /// <summary>
    /// SHA-256 of a file, lowercase hex. Computed for every piece of evidence as it is written.
    /// <para>
    /// Assumes the file is finished and closed. That is the capture source's contract: a recorder
    /// does not report a clip until the file behind it is readable, because only the recorder knows
    /// when its pipeline has finished with it.
    /// </para>
    /// </summary>
    public static string ComputeDigest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
