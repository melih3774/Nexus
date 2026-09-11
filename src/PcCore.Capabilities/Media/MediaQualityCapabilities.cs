using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Media;

/// <summary>One target's settings, as they cross the wire.</summary>
public sealed record MediaQualityView
{
    public required string Profile { get; init; }
    public required string Resolution { get; init; }
    public required int CustomHeight { get; init; }
    public required int CustomFps { get; init; }
    public required int CustomBitrateKbps { get; init; }

    /// <summary>What those settings actually resolve to right now, so the phone shows numbers not names.</summary>
    public required int PlannedWidth { get; init; }
    public required int PlannedHeight { get; init; }
    public required int PlannedFps { get; init; }
    public required int PlannedBitrateKbps { get; init; }

    public static MediaQualityView From(MediaQuality quality, MediaPlan plan) => new()
    {
        Profile = quality.Profile.ToString().ToLowerInvariant(),
        Resolution = Wire(quality.Resolution),
        CustomHeight = quality.CustomHeight,
        CustomFps = quality.CustomFps,
        CustomBitrateKbps = quality.CustomBitrateKbps,
        PlannedWidth = plan.Width,
        PlannedHeight = plan.Height,
        PlannedFps = plan.Fps,
        PlannedBitrateKbps = plan.BitrateKbps,
    };

    /// <summary>
    /// The wire spelling of a resolution.
    /// <para>
    /// Written out rather than taken from the enum name, because the enum names have to start with a
    /// letter and the thing everybody calls these is "720p". A client's list of options is a
    /// user-facing list, and it should not have to translate <c>P720</c> for the owner.
    /// </para>
    /// </summary>
    public static string Wire(StreamingResolution resolution) => resolution switch
    {
        StreamingResolution.P360 => "360p",
        StreamingResolution.P540 => "540p",
        StreamingResolution.P720 => "720p",
        StreamingResolution.P1080 => "1080p",
        StreamingResolution.Native => "native",
        _ => "auto",
    };

    public static StreamingResolution Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "360p" or "360" => StreamingResolution.P360,
        "540p" or "540" => StreamingResolution.P540,
        "720p" or "720" => StreamingResolution.P720,
        "1080p" or "1080" => StreamingResolution.P1080,
        "native" => StreamingResolution.Native,
        _ => StreamingResolution.Auto,
    };

    public static StreamingProfile ParseProfile(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "smooth" => StreamingProfile.Smooth,
        "balanced" => StreamingProfile.Balanced,
        "quality" => StreamingProfile.Quality,
        "custom" => StreamingProfile.Custom,
        _ => StreamingProfile.Auto,
    };
}

public sealed record MediaQualityResult
{
    public required MediaQualityView Screen { get; init; }
    public required MediaQualityView Camera { get; init; }

    /// <summary>What this PC will actually encode with, so the phone never claims hardware it has not got.</summary>
    public required string Encoder { get; init; }

    public required bool HardwareVideo { get; init; }
}

public sealed record SetMediaQualityParameters
{
    /// <summary><c>screen</c> or <c>camera</c>.</summary>
    public string? Target { get; init; }

    public string? Profile { get; init; }

    public string? Resolution { get; init; }

    public int? CustomHeight { get; init; }

    public int? CustomFps { get; init; }

    public int? CustomBitrateKbps { get; init; }
}

/// <summary>
/// Reads the streaming settings, and what they currently resolve to.
/// <para>
/// The resolved numbers are returned alongside the names because "Auto" is not an answer to "what
/// am I about to get". The phone shows both: the choice the owner made, and what it means on this
/// PC with this display right now.
/// </para>
/// </summary>
public sealed class GetMediaQualityAction : CapabilityAction<NoParameters, MediaQualityResult>
{
    private readonly MediaQualityStore _store;
    private readonly IVideoEncoderFactory _encoders;
    private readonly IMediaSourceSizes _sizes;

    public GetMediaQualityAction(MediaQualityStore store, IVideoEncoderFactory encoders, IMediaSourceSizes sizes)
    {
        _store = store;
        _encoders = encoders;
        _sizes = sizes;
    }

    public override string Name => "media.quality.get";

    public override string Description => "The streaming quality settings for the screen and the camera.";

    protected override ValueTask<MediaQualityResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(MediaQualityActions.Describe(_store, _encoders, _sizes));
}

/// <summary>Changes one target's settings. A running stream picks the change up on its next frame.</summary>
public sealed class SetMediaQualityAction : CapabilityAction<SetMediaQualityParameters, MediaQualityResult>
{
    private readonly MediaQualityStore _store;
    private readonly IVideoEncoderFactory _encoders;
    private readonly IMediaSourceSizes _sizes;

    public SetMediaQualityAction(MediaQualityStore store, IVideoEncoderFactory encoders, IMediaSourceSizes sizes)
    {
        _store = store;
        _encoders = encoders;
        _sizes = sizes;
    }

    public override string Name => "media.quality.set";

    public override string Description => "Change the streaming quality for the screen or the camera.";

    protected override ValueTask<MediaQualityResult> ExecuteAsync(
        SetMediaQualityParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var target = parameters.Target?.Trim().ToLowerInvariant() switch
        {
            "camera" => MediaTarget.Camera,
            "screen" or null or "" => MediaTarget.Screen,
            _ => throw new CapabilityException(ErrorCodes.InvalidParameters, "Target must be screen or camera."),
        };

        var current = _store.Current.For(target);

        // Every field is optional and absent means unchanged, so a client that only wants to move
        // the resolution does not have to restate a custom bitrate it has never looked at.
        var updated = current with
        {
            Profile = parameters.Profile is null
                ? current.Profile
                : MediaQualityView.ParseProfile(parameters.Profile),
            Resolution = parameters.Resolution is null
                ? current.Resolution
                : MediaQualityView.Parse(parameters.Resolution),
            CustomHeight = parameters.CustomHeight ?? current.CustomHeight,
            CustomFps = parameters.CustomFps ?? current.CustomFps,
            CustomBitrateKbps = parameters.CustomBitrateKbps ?? current.CustomBitrateKbps,
        };

        _store.Update(target, updated);

        return ValueTask.FromResult(MediaQualityActions.Describe(_store, _encoders, _sizes));
    }
}

internal static class MediaQualityActions
{
    public static MediaQualityResult Describe(
        MediaQualityStore store,
        IVideoEncoderFactory encoders,
        IMediaSourceSizes sizes)
    {
        var settings = store.Current;
        var screen = sizes.ScreenSize();
        var camera = sizes.CameraSize();

        return new MediaQualityResult
        {
            Screen = MediaQualityView.From(
                settings.Screen,
                MediaPlanner.Resolve(settings.Screen, MediaTarget.Screen, screen.Width, screen.Height)),
            Camera = MediaQualityView.From(
                settings.Camera,
                MediaPlanner.Resolve(settings.Camera, MediaTarget.Camera, camera.Width, camera.Height)),
            Encoder = encoders.Describe(),
            HardwareVideo = encoders.IsAvailable,
        };
    }
}

/// <summary>
/// Where the planner gets the sizes it scales from.
/// <para>
/// An interface rather than a display lookup, because the camera has a native size too and it does
/// not come from anything the screen knows about. Both answers are needed before a stream starts —
/// this is what makes <c>media.quality.get</c> able to say what Auto means without opening a device.
/// </para>
/// </summary>
public interface IMediaSourceSizes
{
    (int Width, int Height) ScreenSize();

    (int Width, int Height) CameraSize();
}

/// <summary>
/// The PC's clock, for a client that wants to measure latency rather than guess at it.
/// <para>
/// <b>Why this exists.</b> The frame header carries the moment the pixels were captured, in the PC's
/// clock. A phone that subtracts that from its own clock is measuring the difference between two
/// unsynchronised clocks, which on a phone that last synchronised over mobile data can be seconds
/// out — enough to report a latency that is negative, or a hundred times the truth. Three round
/// trips against this action establish the offset, and after that every frame's age is a real
/// measurement.
/// </para>
/// <para>
/// It deliberately returns nothing but a number. No uptime, no timezone, no locale: a clock reading
/// is not a place to leak anything about the machine.
/// </para>
/// </summary>
public sealed class MediaTimeAction : CapabilityAction<NoParameters, MediaTimeResult>
{
    private readonly TimeProvider _time;

    public MediaTimeAction(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public override string Name => "media.time";

    public override string Description => "This PC's clock, for measuring stream latency.";

    protected override ValueTask<MediaTimeResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new MediaTimeResult { UnixMs = _time.GetUtcNow().ToUnixTimeMilliseconds() });
}

public sealed record MediaTimeResult
{
    public required long UnixMs { get; init; }
}
