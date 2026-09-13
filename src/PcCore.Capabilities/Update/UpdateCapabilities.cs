using System.Reflection;
using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Update;

/// <summary>
/// Which Android builds this PC Core serves.
/// <para>
/// The minimum is stamped from <c>NexusMinAndroidVersionCode</c> in <c>Directory.Build.props</c>, so
/// it is written down once, beside the product version, and the release manifest reads the same
/// number.
/// </para>
/// </summary>
public static class ClientCompatibility
{
    /// <summary>
    /// The last Android build before phones reported their version. A phone that reports nothing is
    /// taken to be this one - old enough to predate the update path, and no older.
    /// </summary>
    public const int LegacyVersionCode = 14;

    public static int MinimumAndroidVersionCode { get; } =
        typeof(ClientCompatibility).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(a => a.Key == "NexusMinAndroidVersionCode")
            .Select(a => int.TryParse(a.Value, out var value) ? value : (int?)null)
            .FirstOrDefault()
        ?? LegacyVersionCode;

    public static bool RequiresUpdate(int? reportedVersionCode, int? minimum = null) =>
        (reportedVersionCode ?? LegacyVersionCode) < (minimum ?? MinimumAndroidVersionCode);
}

public sealed record UpdateCheckParameters
{
    /// <summary>The caller's app build. A phone sends it; the control panel does not.</summary>
    public int? ClientVersionCode { get; init; }

    /// <summary>Read the release store again instead of answering from the last check.</summary>
    public bool Refresh { get; init; }
}

/// <summary>The Android package the current release offers.</summary>
public sealed record AndroidUpdateInfo
{
    public required string VersionName { get; init; }

    public required int VersionCode { get; init; }

    public required long SizeBytes { get; init; }

    public required string Sha256 { get; init; }

    public required string SigningCertSha256 { get; init; }

    public int MinSdk { get; init; }

    /// <summary>Newer than the build the caller said it runs.</summary>
    public bool UpdateAvailable { get; init; }

    /// <summary>The caller's build is below what this PC Core serves.</summary>
    public bool Required { get; init; }
}

/// <summary>A component that needs administrator approval to replace.</summary>
public sealed record ElevatedComponentInfo(
    string Name,
    string? InstalledVersion,
    string? AvailableVersion,
    bool UpdateAvailable,
    string Reason);

/// <summary>What <c>update.check</c> answers.</summary>
public sealed record UpdateCheckResult
{
    /// <summary>The PC Core answering, as it is running now.</summary>
    public required string RunningVersion { get; init; }

    /// <summary>Whether a release has been published on this PC at all.</summary>
    public bool ReleaseAvailable { get; init; }

    public string? LatestVersion { get; init; }

    public int? LatestRelease { get; init; }

    /// <summary>The release the updater last installed, when it has installed one.</summary>
    public int? InstalledRelease { get; init; }

    /// <summary>Whether installing the current release would change anything on this PC.</summary>
    public bool PcUpdateAvailable { get; init; }

    /// <summary>How many PC Core files an update would add, replace or remove. Local callers only.</summary>
    public int PcChangedFiles { get; init; }

    public bool NeedsAdministratorApproval { get; init; }

    /// <summary>SecureCapture and the HID driver. Local callers only.</summary>
    public IReadOnlyList<ElevatedComponentInfo> Components { get; init; } = [];

    public AndroidUpdateInfo? Android { get; init; }

    public int MinimumAndroidVersionCode { get; init; }

    /// <summary>
    /// The last or current PC update: <c>idle</c>, <c>staging</c>, <c>waiting_for_approval</c>,
    /// <c>installing</c>, <c>restarting</c>, <c>succeeded</c>, <c>failed</c> or <c>rolled_back</c>.
    /// </summary>
    public required string State { get; init; }

    public string? Message { get; init; }

    /// <summary>Why the release store could not be used, when it could not.</summary>
    public string? Problem { get; init; }

    public DateTimeOffset CheckedAtUtc { get; init; }
}

public sealed record GetAndroidPackageParameters
{
    /// <summary>
    /// The build being downloaded. Every chunk names it, so a download that straddles a new publish
    /// fails cleanly instead of stitching two APKs together.
    /// </summary>
    public int VersionCode { get; init; }

    public long Offset { get; init; }

    public int? MaxBytes { get; init; }
}

public sealed record AndroidPackageChunk
{
    public required int VersionCode { get; init; }

    public required long SizeBytes { get; init; }

    public required string Sha256 { get; init; }

    public required long Offset { get; init; }

    public required int ChunkBytes { get; init; }

    public required bool EndOfFile { get; init; }

    public string Encoding { get; init; } = "base64";

    public string? Data { get; init; }
}

public sealed record InstallUpdateResult(bool Started, string Message, int? Release);

/// <summary>The update machinery, as the capability layer sees it. Implemented on the Windows side.</summary>
public interface IUpdateService
{
    UpdateCheckResult Check(bool refresh, int? clientVersionCode, bool includePcDetails);

    ValueTask<AndroidPackageChunk> ReadAndroidPackageAsync(int versionCode, long offset, int maxBytes, CancellationToken cancellationToken);

    InstallUpdateResult StartPcUpdate();
}

/// <summary>
/// <c>update.check</c>: which Nexus release this PC has, and whether the caller or the PC is behind
/// it. Available to the phone before its owner authenticates, and to a phone too old for anything
/// else - see <see cref="DefaultCapabilityPolicy"/>.
/// </summary>
public sealed class CheckForUpdatesAction(IUpdateService updates) : CapabilityAction<UpdateCheckParameters, UpdateCheckResult>
{
    public override string Name => "update.check";

    public override string Description => "The current Nexus release, and whether this PC or the calling phone is behind it.";

    protected override ValueTask<UpdateCheckResult> ExecuteAsync(
        UpdateCheckParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var local = context.Caller.ActorType == ActorType.Local;

        return ValueTask.FromResult(updates.Check(
            parameters.Refresh,
            parameters.ClientVersionCode ?? context.Caller.ClientVersionCode,
            includePcDetails: local));
    }
}

/// <summary>
/// <c>update.apk.get</c>: the Nexus APK, in ranges. The same shape as <c>security.evidence.get</c>,
/// so the phone downloads it the way it already downloads a clip: append, resume from the length it
/// has, check the whole against the digest.
/// </summary>
public sealed class GetAndroidPackageAction(IUpdateService updates) : CapabilityAction<GetAndroidPackageParameters, AndroidPackageChunk>
{
    public const int DefaultChunkBytes = 1024 * 1024;
    public const int MaxChunkBytes = 4 * 1024 * 1024;

    public override string Name => "update.apk.get";

    public override string Description => "A range of the Nexus Android package offered by the current release.";

    protected override ValueTask<AndroidPackageChunk> ExecuteAsync(
        GetAndroidPackageParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        if (parameters.Offset < 0)
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, "offset must not be negative.");
        }

        return updates.ReadAndroidPackageAsync(
            parameters.VersionCode,
            parameters.Offset,
            Math.Clamp(parameters.MaxBytes ?? DefaultChunkBytes, 1, MaxChunkBytes),
            cancellationToken);
    }
}

/// <summary>
/// <c>update.install</c>: start updating this PC to the current release. Local-only - it is absent
/// from the device allowlist, so the phone can see that the PC is behind but cannot restart it.
/// </summary>
public sealed class InstallUpdateAction(IUpdateService updates) : CapabilityAction<NoParameters, InstallUpdateResult>
{
    public override string Name => "update.install";

    public override string Description => "Update this PC to the current Nexus release. Restarts PC Core.";

    protected override ValueTask<InstallUpdateResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(updates.StartPcUpdate());
}
