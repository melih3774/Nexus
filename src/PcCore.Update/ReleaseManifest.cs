using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcCore.Update;

/// <summary>One file a release ships, relative to its component's directory.</summary>
public sealed record ReleaseFile
{
    /// <summary>Forward-slash relative path, for example <c>wwwroot/index.html</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Lower-case hex SHA-256 of the file's bytes.</summary>
    public required string Sha256 { get; init; }

    public required long Size { get; init; }
}

/// <summary>A component that is a directory of files: PC Core, SecureCapture, the HID driver package, PC Assistant.</summary>
public sealed record FileSetComponent
{
    /// <summary>
    /// The component's own version. For PC Core it is the product version; SecureCapture and the
    /// driver carry their own, because replacing either needs administrator approval and the updater
    /// only asks for that when this number changes.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>Hash of the component's source inputs when it was built. Informational.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>Where the files are, relative to the release directory.</summary>
    public required string Directory { get; init; }

    public required IReadOnlyList<ReleaseFile> Files { get; init; }
}

/// <summary>The Android app.</summary>
public sealed record AndroidComponent
{
    public required string VersionName { get; init; }

    public required int VersionCode { get; init; }

    /// <summary>The APK, relative to the release directory.</summary>
    public required string File { get; init; }

    public required string Sha256 { get; init; }

    public required long Size { get; init; }

    /// <summary>
    /// SHA-256 of the signing certificate. The phone refuses a package whose signer differs from
    /// the one it was installed with, and Android would refuse the upgrade anyway; saying so before
    /// the download is kinder than failing after it.
    /// </summary>
    public required string SigningCertSha256 { get; init; }

    public string? Fingerprint { get; init; }

    public int MinSdk { get; init; }
}

public sealed record ReleaseComponents
{
    public required FileSetComponent PcCore { get; init; }

    public FileSetComponent? SecureCapture { get; init; }

    public FileSetComponent? HidDriver { get; init; }

    /// <summary>
    /// PC Assistant, installed per user beside PC Core. Compared by its own version, like SecureCapture:
    /// replacing it means closing a window the owner may be working in, and a rebuild of unchanged
    /// source must not cost that.
    /// </summary>
    public FileSetComponent? Assistant { get; init; }

    public AndroidComponent? Android { get; init; }
}

public sealed record ReleaseCompatibility
{
    /// <summary>The wire protocol version PC Core speaks.</summary>
    public int ProtocolVersion { get; init; } = 1;

    /// <summary>The oldest Android versionCode this release's PC Core serves normally.</summary>
    public int MinAndroidVersionCode { get; init; }
}

/// <summary>
/// Everything one Nexus release is: which versions, which files, which hashes.
/// <para>
/// Written by <c>tools/publish-release.ps1</c>, read by PC Core and by the updater. It is the only
/// description of a release; nothing is inferred from file names or timestamps.
/// </para>
/// </summary>
public sealed record ReleaseManifest
{
    public const int CurrentSchema = 1;

    public int Schema { get; init; } = CurrentSchema;

    public string Product { get; init; } = "Nexus";

    /// <summary>The Nexus product version, for example <c>1.3.0</c>.</summary>
    public required string Version { get; init; }

    /// <summary>
    /// A number that increases with every publish. Two publishes of the same product version are
    /// still two releases, and this is what tells them apart.
    /// </summary>
    public required int Release { get; init; }

    public DateTimeOffset PublishedAtUtc { get; init; }

    public string? Commit { get; init; }

    /// <summary>Whether the working tree had uncommitted changes when this was published.</summary>
    public bool Dirty { get; init; }

    public ReleaseCompatibility Compatibility { get; init; } = new();

    public required ReleaseComponents Components { get; init; }
}

/// <summary>
/// <c>latest.json</c>: which release is current, and the hash its manifest must have.
/// <para>
/// Written last by the publish script, after every artifact is in place. A release directory without
/// a pointer to it is invisible, so a publish that dies half way leaves nothing anyone will install.
/// </para>
/// </summary>
public sealed record ReleasePointer
{
    public required int Release { get; init; }

    public required string Version { get; init; }

    /// <summary>The manifest, relative to the release store root.</summary>
    public required string Manifest { get; init; }

    public required string ManifestSha256 { get; init; }
}

/// <summary>The one JSON configuration every update document is read and written with.</summary>
public static class UpdateJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        MaxDepth = 16,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static T? Read<T>(string path) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Options);

    /// <summary>Temp file, flushed, then moved over the target: a reader sees the old document or the new one.</summary>
    public static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        var temporary = path + ".tmp";

        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value, Options);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
