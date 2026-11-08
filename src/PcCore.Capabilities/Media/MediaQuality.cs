using Microsoft.Extensions.Logging;

using PcCore.Capabilities.Storage;

namespace PcCore.Capabilities.Media;

/// <summary>Which of the two things this PC can stream a setting applies to.</summary>
public enum MediaTarget
{
    Screen,
    Camera,
}

/// <summary>
/// What the owner is asking the encoder to optimise for.
/// <para>
/// Four named points and an escape hatch, rather than a page of sliders. Bitrate, frame rate and
/// resolution are not independent choices — picking a high bitrate at a low frame rate is a way of
/// making a stream worse at both — and a named profile is a decision about the trade-off, which is
/// the thing the owner actually has an opinion about.
/// </para>
/// </summary>
public enum StreamingProfile
{
    /// <summary>Adapts to what the link and the encoder are actually managing.</summary>
    Auto,

    /// <summary>Frame rate and latency first. Fewer bits per picture, more pictures.</summary>
    Smooth,

    /// <summary>The default. Readable text at a frame rate that feels attached to the pointer.</summary>
    Balanced,

    /// <summary>Small text and still images first, at fewer frames a second.</summary>
    Quality,

    /// <summary>Exactly what the owner typed.</summary>
    Custom,
}

/// <summary>How many pixels to send, independent of how many bits to spend on them.</summary>
public enum StreamingResolution
{
    /// <summary>Chosen from the profile, and adjusted by the adaptive controller when it may.</summary>
    Auto,

    P360,
    P540,
    P720,
    P1080,

    /// <summary>Whatever the source is, up to a sane ceiling. Meaningless above the source size.</summary>
    Native,
}

/// <summary>
/// One target's settings, as the owner left them.
/// <para>
/// Screen and camera get their own copy because they are different problems: a desktop is sharp
/// edges and small text that survive a low frame rate, a room is motion that does not. Sharing one
/// setting would force the owner to compromise one for the other every time they changed it.
/// </para>
/// </summary>
public sealed record MediaQuality
{
    public StreamingProfile Profile { get; init; } = StreamingProfile.Auto;

    public StreamingResolution Resolution { get; init; } = StreamingResolution.Auto;

    /// <summary>Custom height in lines. Width follows from the source's aspect ratio.</summary>
    public int CustomHeight { get; init; } = 720;

    public int CustomFps { get; init; } = 30;

    public int CustomBitrateKbps { get; init; } = 2500;

    public static readonly MediaQuality Default = new();

    /// <summary>Clamps anything a client could have sent into a range the encoder will accept.</summary>
    public MediaQuality Sanitised() => this with
    {
        CustomHeight = Math.Clamp(CustomHeight, 144, 2160),
        CustomFps = Math.Clamp(CustomFps, 1, 60),
        CustomBitrateKbps = Math.Clamp(CustomBitrateKbps, 100, 60_000),
    };
}

/// <summary>Both targets together, which is what gets persisted and what a client reads and writes.</summary>
public sealed record MediaQualitySettings
{
    public MediaQuality Screen { get; init; } = MediaQuality.Default;

    public MediaQuality Camera { get; init; } = new()
    {
        // A room is motion rather than text, and a webcam that cannot produce more than 720p should
        // not be asked to. Balanced by default rather than Auto: adaptation is for a link under
        // pressure, and the camera stream is small enough that it rarely is.
        Profile = StreamingProfile.Balanced,
        Resolution = StreamingResolution.P720,
    };

    public static readonly MediaQualitySettings Default = new();

    public MediaQuality For(MediaTarget target) => target == MediaTarget.Screen ? Screen : Camera;

    public MediaQualitySettings With(MediaTarget target, MediaQuality quality) =>
        target == MediaTarget.Screen ? this with { Screen = quality } : this with { Camera = quality };

    public MediaQualitySettings Sanitised() =>
        new() { Screen = Screen.Sanitised(), Camera = Camera.Sanitised() };
}

/// <summary>What the encoder is actually going to be set to, once everything has been resolved.</summary>
public sealed record MediaPlan(int Width, int Height, int Fps, int BitrateKbps, int GopSeconds, string Label)
{
    public VideoEncoderSettings ToEncoderSettings() => new(Width, Height, Fps, BitrateKbps, GopSeconds);

    /// <summary>True when the two plans differ in a way that needs the encoder rebuilt.</summary>
    public bool NeedsNewEncoder(MediaPlan other) =>
        other is null || Width != other.Width || Height != other.Height || Fps != other.Fps;
}

/// <summary>
/// Turns a profile, a resolution and a source size into numbers an encoder takes.
/// <para>
/// Deliberately a pure function with a table in it rather than a formula. A formula would be shorter
/// and would be wrong at the ends: bits per pixel is not constant across resolutions, because a
/// 360-line desktop is the same text at fewer pixels and needs proportionally more of them to stay
/// readable. The numbers below start from what this machine measured at 720p and scale by what
/// looked right at each end, and they are here in one place so they can be argued with.
/// </para>
/// </summary>
public static class MediaPlanner
{
    /// <summary>
    /// Ten seconds between unforced keyframes.
    /// <para>
    /// Shorter spends real bandwidth — an IDR is several times the size of a predicted frame — and
    /// buys very little, because every event that actually needs one (a client joining, a dropped
    /// packet, a resolution change) asks for one explicitly. It also costs picture quality: an IDR
    /// throws away what <see cref="StillRefinement"/> has built up, and at two seconds it did so
    /// before a still screen had finished sharpening. Longer would leave a client whose encoder
    /// refused a forced keyframe staring at a frozen picture for too long.
    /// </para>
    /// </summary>
    public const int GopSeconds = 10;

    /// <summary>Nothing is asked to encode more than this, whatever the display is.</summary>
    public const int MaxHeight = 1440;

    private static readonly int[] Ladder = [1080, 720, 540, 360, 288];

    /// <summary>Resolves a plan. <paramref name="level"/> is the adaptive step, 0 being the best.</summary>
    public static MediaPlan Resolve(
        MediaQuality quality,
        MediaTarget target,
        int sourceWidth,
        int sourceHeight,
        int level = 0)
    {
        ArgumentNullException.ThrowIfNull(quality);

        quality = quality.Sanitised();

        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            sourceWidth = 1280;
            sourceHeight = 720;
        }

        var profile = quality.Profile;

        if (profile == StreamingProfile.Custom)
        {
            var customHeight = Math.Min(quality.CustomHeight, sourceHeight);
            var (cw, ch) = Scale(sourceWidth, sourceHeight, customHeight);

            return new MediaPlan(cw, ch, quality.CustomFps, quality.CustomBitrateKbps, GopSeconds, "Custom");
        }

        var fps = FramesPerSecond(profile, target);
        var height = TargetHeight(quality, profile, sourceHeight);

        // The adaptive step only moves the resolution when the owner left it on Auto. A resolution
        // they picked is a decision, and quietly overriding it is how an adaptive system earns a
        // reputation for having a mind of its own.
        if (profile == StreamingProfile.Auto && level > 0)
        {
            if (quality.Resolution == StreamingResolution.Auto)
            {
                height = Step(height, level);
            }

            fps = level >= 4 ? Math.Min(fps, 15) : level >= 3 ? Math.Min(fps, 20) : fps;
        }

        var (width, encodedHeight) = Scale(sourceWidth, sourceHeight, height);
        var bitrate = Bitrate(profile, target, encodedHeight, fps);

        if (profile == StreamingProfile.Auto && level > 0)
        {
            // Each step below the top spends a fifth less, floored so that the picture degrades
            // rather than disintegrating.
            bitrate = Math.Max(250, bitrate * Math.Max(40, 100 - (20 * level)) / 100);
        }

        return new MediaPlan(width, encodedHeight, fps, bitrate, GopSeconds, Describe(profile, level));
    }

    /// <summary>How many adaptive steps exist. The controller never goes past this.</summary>
    public static int MaxLevel => 5;

    private static string Describe(StreamingProfile profile, int level) =>
        profile == StreamingProfile.Auto && level > 0 ? $"Auto (step {level})" : profile.ToString();

    private static int FramesPerSecond(StreamingProfile profile, MediaTarget target) => (profile, target) switch
    {
        // A room monitor gains nothing from forty frames a second and a webcam rarely offers them.
        (_, MediaTarget.Camera) => profile == StreamingProfile.Quality ? 24 : 30,

        (StreamingProfile.Smooth, _) => 40,
        (StreamingProfile.Quality, _) => 20,
        _ => 30,
    };

    private static int TargetHeight(MediaQuality quality, StreamingProfile profile, int sourceHeight)
    {
        var requested = quality.Resolution switch
        {
            StreamingResolution.P360 => 360,
            StreamingResolution.P540 => 540,
            StreamingResolution.P720 => 720,
            StreamingResolution.P1080 => 1080,
            StreamingResolution.Native => Math.Min(sourceHeight, MaxHeight),

            // Auto: the profile decides. Smooth trades pixels for frames, Quality the other way.
            _ => profile switch
            {
                StreamingProfile.Smooth => 540,
                StreamingProfile.Quality => 1080,
                _ => 720,
            },
        };

        return Math.Min(Math.Min(requested, sourceHeight), MaxHeight);
    }

    private static int Step(int height, int level)
    {
        // Find where this height sits on the ladder and walk down it, so a step is always a real
        // change rather than an arbitrary percentage that lands on an awkward size.
        var index = 0;

        while (index < Ladder.Length - 1 && Ladder[index] > height)
        {
            index++;
        }

        return Ladder[Math.Min(Ladder.Length - 1, index + level - 1)];
    }

    /// <summary>
    /// Bits a second for a height and a frame rate.
    /// <para>
    /// The 720p row is measured: this machine's desktop at Balanced settles around 2.5 Mbit/s with
    /// readable text. The rest is that row scaled by pixel count with a deliberate bias towards the
    /// small end, because halving the pixels does not halve what it costs to keep text legible.
    /// </para>
    /// </summary>
    private static int Bitrate(StreamingProfile profile, MediaTarget target, int height, int fps)
    {
        var atThirty = height switch
        {
            <= 288 => 500,
            <= 360 => 800,
            <= 540 => 1600,
            <= 720 => 2500,
            <= 1080 => 5000,
            _ => 8000,
        };

        var scaled = profile switch
        {
            StreamingProfile.Smooth => atThirty * 60 / 100,
            StreamingProfile.Quality => atThirty * 165 / 100,
            _ => atThirty,
        };

        if (target == MediaTarget.Camera)
        {
            // A camera is a lens on a room: soft, noisy, and far more compressible than a desktop of
            // hard-edged text. The same picture quality costs about a third less.
            scaled = scaled * 70 / 100;
        }

        // Not linear in frame rate: a doubled frame rate roughly halves the difference between
        // consecutive pictures, so it costs well under twice as much to encode them.
        var rate = (40 + (60 * fps / 30)) / 100.0;

        return Math.Max(250, (int)(scaled * rate));
    }

    private static (int Width, int Height) Scale(int sourceWidth, int sourceHeight, int height)
    {
        height = Math.Clamp(height, 144, Math.Max(144, sourceHeight));

        var width = (int)Math.Round((double)sourceWidth * height / sourceHeight);

        return VideoEncoderSettings.Even(width, height);
    }
}

/// <summary>
/// The owner's streaming settings, kept across restarts.
/// <para>
/// Persisted for the same reason the pairing is: a setting the owner changed once and has to change
/// again every time PC Core restarts is not a setting, it is a nuisance. Stored beside the other
/// state as plain JSON — there is nothing secret in a frame rate.
/// </para>
/// </summary>
public sealed class MediaQualityStore
{
    public const string FileName = "media-quality.json";

    private readonly AtomicJsonFileStore<MediaQualitySettings> _store;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    private MediaQualitySettings _current = MediaQualitySettings.Default;
    private bool _loaded;

    public MediaQualityStore(StateDirectory directory, ILogger<MediaQualityStore> logger)
    {
        _store = new AtomicJsonFileStore<MediaQualitySettings>(directory, FileName, logger);
        _logger = logger;
    }

    /// <summary>Raised after a successful change, so a running stream can be re-planned at once.</summary>
    public event Action<MediaQualitySettings>? Changed;

    public MediaQualitySettings Current
    {
        get
        {
            lock (_gate)
            {
                if (!_loaded)
                {
                    Load();
                }

                return _current;
            }
        }
    }

    public MediaQualitySettings Update(MediaTarget target, MediaQuality quality)
    {
        ArgumentNullException.ThrowIfNull(quality);

        MediaQualitySettings updated;

        lock (_gate)
        {
            if (!_loaded)
            {
                Load();
            }

            updated = _current.With(target, quality.Sanitised());
            _current = updated;

            try
            {
                _store.Save(updated);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The setting still applies to this session. Losing it at the next restart is worse
                // than a failed request, but not worse than refusing the change the owner just made.
                _logger.LogWarning("Streaming settings could not be saved: {Reason}", ex.Message);
            }
        }

        Changed?.Invoke(updated);
        return updated;
    }

    private void Load()
    {
        _loaded = true;

        var result = _store.Load();

        _current = result.IsLoaded
            ? result.Value!.Sanitised()
            : MediaQualitySettings.Default;

        if (result.Status == StateLoadStatus.Corrupt)
        {
            _logger.LogWarning("Streaming settings could not be read; the defaults are in use.");
        }
    }
}
