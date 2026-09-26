using System.Security.Cryptography;

namespace PcCore.Update;

/// <summary>SHA-256, the one digest every update check uses.</summary>
public static class FileHash
{
    public static string Sha256OfFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string Sha256OfBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static bool Matches(string path, string expectedSha256, long? expectedSize = null)
    {
        try
        {
            var info = new FileInfo(path);

            if (!info.Exists || (expectedSize is { } size && info.Length != size))
            {
                return false;
            }

            return string.Equals(Sha256OfFile(path), expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>A release could not be used as described.</summary>
public sealed class UpdateIntegrityException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What looking for the current release found.</summary>
public sealed record ReleaseLoad(ReleaseManifest? Manifest, string? ReleaseDirectory, string? Problem)
{
    public bool Found => Manifest is not null && ReleaseDirectory is not null;

    public static ReleaseLoad None(string problem) => new(null, null, problem);
}

/// <summary>
/// The local release store: <c>%LOCALAPPDATA%\Nexus\Releases</c>.
/// <para>
/// <b>Why a directory and not a server.</b> Nexus is one PC and one phone. The PC that builds a
/// release is the PC that installs it and the PC the phone downloads from, so the "update server"
/// is a folder the publish script writes and PC Core reads. The phone never sees a path: it asks PC
/// Core over its authenticated session and receives bytes.
/// </para>
/// </summary>
public sealed class ReleaseStore
{
    public const string PointerFileName = "latest.json";

    public ReleaseStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string PointerPath => Path.Combine(Root, PointerFileName);

    public static string DefaultRoot => RootIn(AppDataRedirection.LocalAppData);

    public static string RootIn(string localAppData) => Path.Combine(localAppData, UpdatePaths.StateFolder, "Releases");

    public static ReleaseStore ForCurrentUser() => new(DefaultRoot);

    /// <summary>
    /// Loads the release <c>latest.json</c> points at, and refuses it unless the manifest's hash is
    /// the one the pointer recorded and every path in it stays inside its release directory.
    /// </summary>
    public ReleaseLoad LoadLatest()
    {
        if (!File.Exists(PointerPath))
        {
            return ReleaseLoad.None("No Nexus release has been published on this PC.");
        }

        try
        {
            var pointer = UpdateJson.Read<ReleasePointer>(PointerPath)
                ?? throw new UpdateIntegrityException("latest.json is empty.");

            var manifestPath = Contained(Root, pointer.Manifest)
                ?? throw new UpdateIntegrityException("latest.json points outside the release store.");

            if (!File.Exists(manifestPath))
            {
                return ReleaseLoad.None($"Release {pointer.Release} is listed but its manifest is missing.");
            }

            var bytes = File.ReadAllBytes(manifestPath);

            if (!string.Equals(FileHash.Sha256OfBytes(bytes), pointer.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateIntegrityException($"The manifest of release {pointer.Release} does not match latest.json.");
            }

            var manifest = System.Text.Json.JsonSerializer.Deserialize<ReleaseManifest>(bytes, UpdateJson.Options)
                ?? throw new UpdateIntegrityException("The release manifest is empty.");

            if (manifest.Release != pointer.Release)
            {
                throw new UpdateIntegrityException("latest.json and the manifest disagree about the release number.");
            }

            ManifestValidator.ThrowIfInvalid(manifest);

            return new ReleaseLoad(manifest, Path.GetDirectoryName(manifestPath)!, null);
        }
        catch (UpdateIntegrityException ex)
        {
            return ReleaseLoad.None(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return ReleaseLoad.None($"The release store could not be read: {ex.Message}");
        }
    }

    /// <summary>The on-disk path of a file inside a component, or an exception if it would escape.</summary>
    public static string ComponentFile(string releaseDirectory, FileSetComponent component, ReleaseFile file) =>
        Contained(releaseDirectory, component.Directory + "/" + file.Path)
        ?? throw new UpdateIntegrityException($"{file.Path} would resolve outside its release.");

    public static string AndroidFile(string releaseDirectory, AndroidComponent android) =>
        Contained(releaseDirectory, android.File)
        ?? throw new UpdateIntegrityException("The APK path would resolve outside its release.");

    /// <summary>
    /// <paramref name="relative"/> under <paramref name="root"/>, or null if it is not a plain relative
    /// path that stays there.
    /// </summary>
    public static string? Contained(string root, string relative)
    {
        if (!ManifestValidator.IsSafeRelativePath(relative))
        {
            return null;
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

        return full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}

/// <summary>Refuses a manifest that could make the updater write, read or run something it should not.</summary>
public static class ManifestValidator
{
    public static void ThrowIfInvalid(ReleaseManifest manifest)
    {
        var problems = Problems(manifest);

        if (problems.Count > 0)
        {
            throw new UpdateIntegrityException("The release manifest is invalid: " + string.Join("; ", problems));
        }
    }

    public static IReadOnlyList<string> Problems(ReleaseManifest manifest)
    {
        var problems = new List<string>();

        if (manifest.Schema != ReleaseManifest.CurrentSchema)
        {
            problems.Add($"schema {manifest.Schema} is not understood by this build");
        }

        if (manifest.Release <= 0)
        {
            problems.Add("release must be positive");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            problems.Add("version is missing");
        }

        if (manifest.Components?.PcCore is null)
        {
            problems.Add("the PC Core component is missing");
            return problems;
        }

        CheckFileSet("pc_core", manifest.Components.PcCore, problems);

        if (!manifest.Components.PcCore.Files.Any(f => string.Equals(f.Path, "PcCore.Host.exe", StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add("pc_core does not contain PcCore.Host.exe");
        }

        if (manifest.Components.SecureCapture is { } secure)
        {
            CheckFileSet("secure_capture", secure, problems);
        }

        if (manifest.Components.HidDriver is { } driver)
        {
            CheckFileSet("hid_driver", driver, problems);
        }

        if (manifest.Components.Assistant is { } assistant)
        {
            CheckFileSet("assistant", assistant, problems);

            if (!(assistant.Files ?? []).Any(f => string.Equals(f.Path, AssistantInstall.ExecutableName, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"assistant does not contain {AssistantInstall.ExecutableName}");
            }
        }

        if (manifest.Components.Android is { } android)
        {
            if (!IsSafeRelativePath(android.File))
            {
                problems.Add("android.file is not a safe relative path");
            }

            if (!IsSha256(android.Sha256) || !IsSha256(android.SigningCertSha256))
            {
                problems.Add("android hashes are not SHA-256");
            }

            if (android.VersionCode <= 0 || android.Size <= 0)
            {
                problems.Add("android version_code and size must be positive");
            }
        }

        return problems;
    }

    private static void CheckFileSet(string name, FileSetComponent component, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(component.Version))
        {
            problems.Add($"{name}.version is missing");
        }

        if (!IsSafeRelativePath(component.Directory))
        {
            problems.Add($"{name}.directory is not a safe relative path");
        }

        if (component.Files is null || component.Files.Count == 0)
        {
            problems.Add($"{name} has no files");
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in component.Files)
        {
            if (!IsSafeRelativePath(file.Path))
            {
                problems.Add($"{name}: '{file.Path}' is not a safe relative path");
            }
            else if (!seen.Add(file.Path))
            {
                problems.Add($"{name}: '{file.Path}' is listed twice");
            }

            if (!IsSha256(file.Sha256) || file.Size < 0)
            {
                problems.Add($"{name}: '{file.Path}' has no valid SHA-256 or size");
            }
        }
    }

    /// <summary>
    /// A path made of ordinary names: not rooted, no drive or stream colon, no <c>..</c> or <c>.</c>
    /// segments, no empty segments, nothing a shell or the Win32 path parser would treat specially.
    /// </summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 260)
        {
            return false;
        }

        if (path.IndexOfAny([':', '*', '?', '"', '<', '>', '|', '\0']) >= 0)
        {
            return false;
        }

        var segments = path.Split('/', '\\');

        return segments.All(static s => s.Length > 0 && s != "." && s != ".." && s.Trim() == s && !s.EndsWith('.'));
    }

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
