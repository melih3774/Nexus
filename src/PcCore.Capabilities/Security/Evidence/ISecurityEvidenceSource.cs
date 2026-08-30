using System.Text.Json.Serialization;

namespace PcCore.Capabilities.Security.Evidence;

/// <summary>How much screen evidence to record during an incident.</summary>
public enum ScreenEvidenceMode
{
    /// <summary>The default. A screenshot at meaningful moments, and a written timeline.</summary>
    [JsonStringEnumMemberName("SCREENSHOTS")]
    Screenshots,

    /// <summary>Bounded segments for the whole incident. Never on unless the owner chose it.</summary>
    [JsonStringEnumMemberName("CONTINUOUS")]
    Continuous,

    [JsonStringEnumMemberName("DISABLED")]
    Disabled,
}

/// <summary>How much camera evidence to record during an incident.</summary>
public enum CameraEvidenceMode
{
    /// <summary>The default. A still and a short clip at the start, and on later meaningful activity.</summary>
    [JsonStringEnumMemberName("EVENT_CLIPS")]
    EventClips,

    [JsonStringEnumMemberName("CONTINUOUS")]
    Continuous,

    [JsonStringEnumMemberName("DISABLED")]
    Disabled,
}

/// <summary>How much microphone evidence to record during an incident.</summary>
public enum AudioEvidenceMode
{
    /// <summary>The default. A short bounded clip at the start, and on later meaningful activity.</summary>
    [JsonStringEnumMemberName("EVENT_CLIPS")]
    EventClips,

    [JsonStringEnumMemberName("CONTINUOUS")]
    Continuous,

    [JsonStringEnumMemberName("DISABLED")]
    Disabled,
}

/// <summary>
/// One captured file, before it is recorded in the session.
/// </summary>
/// <param name="FileName">Name inside the session's evidence folder.</param>
/// <param name="ContentType">For example <c>image/jpeg</c>.</param>
/// <param name="DurationMs">Length of a recording; zero for a still.</param>
/// <param name="Note">Safe context — dimensions, device name. Never content.</param>
public sealed record CapturedEvidence(
    string FileName,
    string ContentType,
    long DurationMs = 0,
    string? Note = null);

/// <summary>
/// A source of security evidence.
/// <para>
/// Every one of these is best effort. A camera another application already owns, a microphone the
/// owner has muted in Windows privacy settings, a display that cannot be captured — none of them
/// are reasons to fail an incident. Returning null with a diagnostic is the normal way to say "not
/// this time", and the incident carries on recording whatever else it can.
/// </para>
/// </summary>
public interface ISecurityEvidenceSource
{
    /// <summary>What this source produces, for logs and timeline entries.</summary>
    string Name { get; }

    /// <summary>Whether the device exists and looks usable. Cheap; may still fail at capture time.</summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken);
}

/// <summary>Captures a still image of the desktop.</summary>
public interface IScreenEvidenceSource : ISecurityEvidenceSource
{
    ValueTask<CapturedEvidence?> CaptureStillAsync(string folder, CancellationToken cancellationToken);
}

/// <summary>Captures from the webcam.</summary>
public interface ICameraEvidenceSource : ISecurityEvidenceSource
{
    ValueTask<CapturedEvidence?> CaptureStillAsync(string folder, CancellationToken cancellationToken);

    /// <summary>A short clip. Bounded by <paramref name="duration"/>, never open-ended.</summary>
    ValueTask<CapturedEvidence?> CaptureClipAsync(string folder, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Cameras Windows can see. Empty when there are none.</summary>
    ValueTask<IReadOnlyList<EvidenceDevice>> ListDevicesAsync(CancellationToken cancellationToken);
}

/// <summary>Captures from the microphone.</summary>
public interface IAudioEvidenceSource : ISecurityEvidenceSource
{
    ValueTask<CapturedEvidence?> CaptureClipAsync(string folder, TimeSpan duration, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<EvidenceDevice>> ListDevicesAsync(CancellationToken cancellationToken);
}

/// <summary>A capture device, as the owner would recognise it.</summary>
public sealed record EvidenceDevice(string Id, string Name, bool IsDefault);

/// <summary>A source with no device behind it. Used in tests and where a platform has none.</summary>
public sealed class UnavailableEvidenceSource : IScreenEvidenceSource, ICameraEvidenceSource, IAudioEvidenceSource
{
    public UnavailableEvidenceSource(string name) => Name = name;

    public string Name { get; }

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);

    public ValueTask<CapturedEvidence?> CaptureStillAsync(string folder, CancellationToken cancellationToken) =>
        ValueTask.FromResult<CapturedEvidence?>(null);

    public ValueTask<CapturedEvidence?> CaptureClipAsync(
        string folder,
        TimeSpan duration,
        CancellationToken cancellationToken) => ValueTask.FromResult<CapturedEvidence?>(null);

    public ValueTask<IReadOnlyList<EvidenceDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<EvidenceDevice>>([]);
}
