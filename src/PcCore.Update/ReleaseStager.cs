namespace PcCore.Update;

/// <summary>
/// Copies what an update needs out of the release store into a private staging directory, and
/// proves every byte against the manifest on the way.
/// <para>
/// This is the "download". The store is on the same disk, but a copy that is verified where it lands
/// is still the only thing worth installing from: a publish can be pruned, rewritten or interrupted
/// underneath a reader, and the staged copy cannot.
/// </para>
/// <para>
/// <b>Interruption.</b> Every file is written to <c>.part</c> and renamed only once its hash
/// matches, so a staged file under its real name is complete by construction. Running again resumes:
/// files already staged with the right hash are kept, anything else is copied again.
/// </para>
/// </summary>
public sealed class ReleaseStager
{
    public const string PcCoreFolder = "pccore";
    public const string SecureCaptureFolder = "securecapture";
    public const string HidDriverFolder = "driver";
    public const string ManifestFileName = "manifest.json";

    private readonly Action<string> _log;

    public ReleaseStager(Action<string>? log = null) => _log = log ?? (_ => { });

    /// <summary>Where a release is staged under <paramref name="stagingRoot"/>.</summary>
    public static string DirectoryFor(string stagingRoot, int release) =>
        Path.Combine(stagingRoot, release.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Stages everything <paramref name="plan"/> will install. Returns the staged release directory.</summary>
    public string Stage(UpdatePlan plan, string stagingRoot, CancellationToken cancellationToken = default)
    {
        var manifest = plan.Manifest;
        var staged = DirectoryFor(stagingRoot, manifest.Release);

        Directory.CreateDirectory(staged);
        UpdateJson.WriteAtomic(Path.Combine(staged, ManifestFileName), manifest);

        var pcCore = manifest.Components.PcCore;
        var wanted = plan.PcCore
            .Where(c => c.Kind != FileChangeKind.Remove)
            .Select(c => c.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in pcCore.Files.Where(f => wanted.Contains(f.Path)))
        {
            StageFile(plan.ReleaseDirectory, pcCore, file, Path.Combine(staged, PcCoreFolder), cancellationToken);
        }

        // The privileged components are staged whole: their installers take a directory, not a diff.
        if (plan.SecureCapture.Updates && manifest.Components.SecureCapture is { } secure)
        {
            foreach (var file in secure.Files)
            {
                StageFile(plan.ReleaseDirectory, secure, file, Path.Combine(staged, SecureCaptureFolder), cancellationToken);
            }
        }

        if (plan.HidDriver.Updates && manifest.Components.HidDriver is { } driver)
        {
            foreach (var file in driver.Files)
            {
                StageFile(plan.ReleaseDirectory, driver, file, Path.Combine(staged, HidDriverFolder), cancellationToken);
            }
        }

        return staged;
    }

    private void StageFile(
        string releaseDirectory,
        FileSetComponent component,
        ReleaseFile file,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var source = ReleaseStore.ComponentFile(releaseDirectory, component, file);
        var target = ReleaseStore.Contained(destinationRoot, file.Path)
            ?? throw new UpdateIntegrityException($"{file.Path} would stage outside the staging directory.");

        if (FileHash.Matches(target, file.Sha256, file.Size))
        {
            // Already staged by an earlier, interrupted attempt.
            return;
        }

        if (!File.Exists(source))
        {
            throw new UpdateIntegrityException($"The release is incomplete: {component.Directory}/{file.Path} is missing.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var partial = target + ".part";

        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024))
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            if (!FileHash.Matches(partial, file.Sha256, file.Size))
            {
                throw new UpdateIntegrityException(
                    $"{component.Directory}/{file.Path} does not match its SHA-256 in the release manifest.");
            }

            File.Move(partial, target, overwrite: true);
            _log($"Staged {component.Directory}/{file.Path}");
        }
        finally
        {
            TryDelete(partial);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .part is harmless: it is never read, and the next attempt overwrites it.
        }
    }
}
