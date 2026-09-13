namespace PcCore.Update;

/// <summary>One step the swap took, recorded before it was taken.</summary>
public sealed record SwapJournalEntry
{
    public required string Path { get; init; }

    public required FileChangeKind Kind { get; init; }

    /// <summary>Whether a previous file was moved into the backup directory.</summary>
    public bool BackedUp { get; init; }
}

public sealed record SwapJournal
{
    public required int Release { get; init; }

    public required string InstallDirectory { get; init; }

    public required string BackupDirectory { get; init; }

    public List<SwapJournalEntry> Entries { get; init; } = [];
}

/// <summary>
/// Replaces PC Core's files, and puts them back.
/// <para>
/// <b>Move aside, then copy in.</b> Each file that is about to be replaced or removed is first moved
/// into a backup directory on the same volume. A move is a rename, which Windows allows even for an
/// executable some other process still has mapped, and it is what makes rollback exact: the previous
/// bytes are not reconstructed, they are the same file moved back.
/// </para>
/// <para>
/// <b>The journal is written before each step.</b> If the updater itself dies half way, the next run
/// finds the journal and undoes what it describes before doing anything else, so an interrupted
/// update ends as the previous installation rather than a mixture.
/// </para>
/// </summary>
public sealed class PcCoreFileSwap
{
    private readonly string _journalPath;
    private readonly Action<string> _log;

    public PcCoreFileSwap(string journalPath, Action<string>? log = null)
    {
        _journalPath = journalPath;
        _log = log ?? (_ => { });
    }

    public bool HasPendingJournal => File.Exists(_journalPath);

    /// <summary>
    /// Applies <paramref name="changes"/> from <paramref name="stagedPcCore"/>. On any failure it rolls
    /// back what it had done and rethrows, so the caller sees either every change or none.
    /// </summary>
    public void Apply(int release, IReadOnlyList<FileChange> changes, string stagedPcCore, string installDirectory, string backupDirectory)
    {
        if (HasPendingJournal)
        {
            throw new InvalidOperationException("A previous update did not finish; roll it back first.");
        }

        if (Directory.Exists(backupDirectory))
        {
            Directory.Delete(backupDirectory, recursive: true);
        }

        Directory.CreateDirectory(backupDirectory);

        var journal = new SwapJournal
        {
            Release = release,
            InstallDirectory = Path.GetFullPath(installDirectory),
            BackupDirectory = Path.GetFullPath(backupDirectory),
        };

        Save(journal);

        try
        {
            foreach (var change in changes)
            {
                var target = ReleaseStore.Contained(installDirectory, change.Path)
                    ?? throw new UpdateIntegrityException($"{change.Path} would resolve outside the install directory.");
                var backup = ReleaseStore.Contained(backupDirectory, change.Path)!;
                var exists = File.Exists(target);

                journal.Entries.Add(new SwapJournalEntry { Path = change.Path, Kind = change.Kind, BackedUp = exists });
                Save(journal);

                if (exists)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Move(target, backup, overwrite: true);
                }

                if (change.Kind == FileChangeKind.Remove)
                {
                    _log($"Removed {change.Path}");
                    continue;
                }

                var staged = ReleaseStore.Contained(stagedPcCore, change.Path)
                    ?? throw new UpdateIntegrityException($"{change.Path} is not staged.");

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(staged, target, overwrite: true);

                // Checked where it will run from, not only where it was staged.
                if (change.Sha256 is not null && !FileHash.Matches(target, change.Sha256, change.Size))
                {
                    throw new UpdateIntegrityException($"{change.Path} did not arrive intact in the install directory.");
                }

                _log($"{(change.Kind == FileChangeKind.Add ? "Added" : "Replaced")} {change.Path}");
            }
        }
        catch
        {
            RollBack();
            throw;
        }
    }

    /// <summary>
    /// Undoes whatever the journal says was done, newest first. Safe to call when there is nothing to
    /// undo. Returns the paths that could not be restored, which should be empty.
    /// </summary>
    public IReadOnlyList<string> RollBack()
    {
        if (!File.Exists(_journalPath))
        {
            return [];
        }

        SwapJournal journal;

        try
        {
            journal = UpdateJson.Read<SwapJournal>(_journalPath) ?? throw new IOException("empty journal");
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            _log($"The update journal could not be read ({ex.Message}); nothing was rolled back.");
            return ["(journal unreadable)"];
        }

        var failed = new List<string>();

        foreach (var entry in Enumerable.Reverse(journal.Entries))
        {
            try
            {
                var target = ReleaseStore.Contained(journal.InstallDirectory, entry.Path);
                var backup = ReleaseStore.Contained(journal.BackupDirectory, entry.Path);

                if (target is null || backup is null)
                {
                    failed.Add(entry.Path);
                    continue;
                }

                if (entry.Kind != FileChangeKind.Remove && File.Exists(target) && (!entry.BackedUp || File.Exists(backup)))
                {
                    File.Delete(target);
                }

                if (entry.BackedUp && File.Exists(backup))
                {
                    File.Move(backup, target, overwrite: true);
                }

                _log($"Restored {entry.Path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log($"Could not restore {entry.Path}: {ex.Message}");
                failed.Add(entry.Path);
            }
        }

        if (failed.Count == 0)
        {
            TryDeleteFile(_journalPath);
            TryDeleteDirectory(journal.BackupDirectory);
        }

        return failed;
    }

    /// <summary>The update is good: forget how to undo it.</summary>
    public void Commit()
    {
        if (!File.Exists(_journalPath))
        {
            return;
        }

        var journal = UpdateJson.Read<SwapJournal>(_journalPath);
        TryDeleteFile(_journalPath);

        if (journal is not null)
        {
            TryDeleteDirectory(journal.BackupDirectory);
        }
    }

    private void Save(SwapJournal journal) => UpdateJson.WriteAtomic(_journalPath, journal);

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A backup of a running image can stay locked until that process exits; it is only
            // disk space, and the next update clears it.
        }
    }
}
