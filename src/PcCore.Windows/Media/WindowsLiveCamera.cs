using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using PcCore.Capabilities.Media;
using PcCore.Capabilities.Screen;
using PcCore.Protocol;

using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

using WinRT;

namespace PcCore.Windows.Media;

/// <summary>
/// The webcam, as a continuous video stream.
/// <para>
/// <b>What this replaces, and why.</b> The first version took photographs in a loop. That is a
/// legitimate way to get pictures out of <c>MediaCapture</c> and it was measured at about 1.2 frames
/// a second on this machine's webcam — because a photograph is a request to the camera, and every
/// request goes through a pipeline built to produce one good still rather than a sequence. The
/// camera itself streams thirty frames a second the whole time; the photo API was simply not the
/// way to ask for them.
/// </para>
/// <para>
/// <c>MediaFrameReader</c> is. It attaches to the preview pin the camera is already running and
/// hands over each frame as it arrives, which is the difference between asking thirty times a second
/// and being told. The frames then go through the same H.264 encoder the desktop stream uses, so a
/// room at 720p costs a fraction of what a sequence of JPEG stills did and arrives as video rather
/// than as a slideshow.
/// </para>
/// <para>
/// <b>Windows' own privacy machinery is untouched and relied upon.</b> This goes through
/// <c>MediaCapture</c> like any other application, so the camera privacy setting still applies, the
/// hardware indicator light still comes on, and the camera appears in Windows' "recently used" list.
/// Nexus adds its own indicator on top of that rather than in place of it — if Windows says no, this
/// fails, and that is correct.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowsLiveCamera : ILiveCameraSource, ILiveVideoControl
{
    /// <summary>
    /// How early a frame may arrive and still count as the next one.
    /// <para>
    /// A webcam's frames are not evenly spaced. Pacing with an exact interval rejects every frame
    /// that turns up a millisecond early, which at a thirty-frame plan against a thirty-frame camera
    /// throws away something like every other frame and delivers eighteen.
    /// </para>
    /// </summary>
    private static readonly TimeSpan PacingSlack = TimeSpan.FromMilliseconds(10);

    private readonly ILogger<WindowsLiveCamera> _logger;
    private readonly IVideoEncoderFactory _encoders;
    private readonly IFrameEncoder _stillEncoder;
    private readonly MediaQualityStore _quality;

    private (int Width, int Height)? _nativeSize;
    private int _keyframeWanted;
    private int _congestion;
    private long _arrived;
    private long _paced;
    private long _encodeTicks;
    private long _encoded;

    public WindowsLiveCamera(
        ILogger<WindowsLiveCamera> logger,
        IVideoEncoderFactory encoders,
        IFrameEncoder stillEncoder,
        MediaQualityStore quality)
    {
        _logger = logger;
        _encoders = encoders;
        _stillEncoder = stillEncoder;
        _quality = quality;
    }

    public string Name => "camera";

    public void RequestKeyframe() => Interlocked.Exchange(ref _keyframeWanted, 1);

    public void ReportCongestion() => Interlocked.Increment(ref _congestion);

    /// <summary>
    /// Frames the camera delivered, frames kept after pacing, and the encode cost, for the self-test.
    /// <para>
    /// The distinction matters because the two ways a live view is slow look identical from the phone:
    /// a camera that only produces fifteen frames in a dim room, and a pipeline that cannot keep up
    /// with thirty.
    /// </para>
    /// </summary>
    public string Diagnostics()
    {
        var encoded = Math.Max(1, Interlocked.Read(ref _encoded));

        return $"camera delivered {Interlocked.Read(ref _arrived)}, kept {Interlocked.Read(ref _paced)}, "
            + $"{Interlocked.Read(ref _encodeTicks) * 1000.0 / Stopwatch.Frequency / encoded:F1} ms to convert and encode";
    }

    /// <summary>
    /// The camera's own resolution, as far as it is known.
    /// <para>
    /// Needed before a stream starts, so <c>media.quality.get</c> can say what "Auto" means for the
    /// camera without opening the device and lighting the indicator to answer a settings query.
    /// Falls back to 720p until a stream has actually run once.
    /// </para>
    /// </summary>
    public (int Width, int Height) NativeSize => _nativeSize ?? (1280, 720);

    public async ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = await DeviceInformation
                .FindAllAsync(DeviceClass.VideoCapture)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            return devices.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Enumerating cameras failed.");
            return false;
        }
    }

    public async IAsyncEnumerable<LiveFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(cancellationToken).ConfigureAwait(false);

        if (opened is null)
        {
            yield break;
        }

        var (capture, reader, format) = opened.Value;

        // One raw frame in hand at a time, newest wins. Dropping here is free and safe: nothing has
        // been encoded yet, so a discarded frame costs nothing and breaks nothing. This is the same
        // policy the desktop stream uses, applied at the only place it is harmless.
        var raw = Channel.CreateBounded<CameraFrame>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            try
            {
                Interlocked.Increment(ref _arrived);
                using var reference = sender.TryAcquireLatestFrame();
                var bitmap = reference?.VideoMediaFrame?.SoftwareBitmap;

                if (bitmap is null)
                {
                    return;
                }

                using (bitmap)
                {
                    var frame = Copy(bitmap);

                    if (frame is not null)
                    {
                        raw.Writer.TryWrite(frame);
                    }
                }
            }
            catch (Exception ex)
            {
                // One bad frame is not a reason to end a live view; the camera may be re-focusing or
                // another application may have taken it briefly.
                _logger.LogDebug(ex, "A live camera frame was dropped.");
            }
        }

        reader.FrameArrived += OnFrameArrived;

        var started = await reader.StartAsync().AsTask(cancellationToken).ConfigureAwait(false);

        if (started != MediaFrameReaderStartStatus.Success)
        {
            _logger.LogWarning("The camera would not start streaming: {Status}", started);
            reader.FrameArrived -= OnFrameArrived;
            reader.Dispose();
            capture.Dispose();
            yield break;
        }

        _nativeSize = ((int)format.VideoFormat.Width, (int)format.VideoFormat.Height);

        _logger.LogInformation(
            "Live camera started at {Width}x{Height} {Fps} fps ({Subtype}).",
            format.VideoFormat.Width,
            format.VideoFormat.Height,
            format.FrameRate.Denominator == 0 ? 0 : format.FrameRate.Numerator / format.FrameRate.Denominator,
            format.Subtype);

        var nativeWidth = (int)format.VideoFormat.Width;
        var nativeHeight = (int)format.VideoFormat.Height;
        var adaptive = new AdaptiveStreamController();
        var plan = MediaPlanner.Resolve(_quality.Current.Camera, MediaTarget.Camera, nativeWidth, nativeHeight);

        IVideoEncoder? video = null;
        byte[]? codecConfig = null;
        var packets = new List<VideoPacket>();
        var lastSent = Stopwatch.GetTimestamp();
        var nextHealthCheck = Stopwatch.GetTimestamp();
        var producedAtLastCheck = 0L;
        var produced = 0L;
        var settingsChanged = 1;

        void OnSettingsChanged(MediaQualitySettings _) => Interlocked.Exchange(ref settingsChanged, 1);

        _quality.Changed += OnSettingsChanged;
        Interlocked.Exchange(ref _congestion, 0);

        try
        {
            await foreach (var frame in raw.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // A settings change, or an adaptive step, rebuilds the encoder around the next frame.
                // The camera itself keeps running at its native format; only what is encoded changes,
                // so a change never lights the indicator off and on again.
                if (Interlocked.Exchange(ref settingsChanged, 0) == 1)
                {
                    var wanted = MediaPlanner.Resolve(
                        _quality.Current.Camera, MediaTarget.Camera, nativeWidth, nativeHeight, adaptive.Level);

                    if (video is not null
                        && !wanted.NeedsNewEncoder(plan)
                        && (wanted.BitrateKbps == plan.BitrateKbps || video.TryChangeBitrate(wanted.BitrateKbps)))
                    {
                        // Same size and rate: the running encoder takes the new bitrate, with no
                        // keyframe and no gap in the picture.
                        plan = wanted;
                    }
                    else if (video is null || wanted != plan)
                    {
                        plan = wanted;
                        video?.Dispose();
                        video = null;
                        codecConfig = null;
                        video = CreateEncoder(plan);
                        adaptive.NoteDisruption();
                    }
                }

                // Paced here rather than by asking the camera for a lower rate: a webcam's advertised
                // frame rates are coarse, and skipping a raw frame is cheaper than making the sensor
                // work differently.
                var interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, plan.Fps));

                if (Stopwatch.GetElapsedTime(lastSent) < interval - PacingSlack)
                {
                    continue;
                }

                lastSent = Stopwatch.GetTimestamp();
                Interlocked.Increment(ref _paced);

                if (Stopwatch.GetElapsedTime(nextHealthCheck) >= TimeSpan.Zero)
                {
                    nextHealthCheck = Stopwatch.GetTimestamp() + Stopwatch.Frequency;

                    var congestion = Interlocked.Exchange(ref _congestion, 0);

                    var health = new StreamHealth(
                        congestion > 0 ? AdaptiveStreamController.BacklogLimit + congestion : 0,
                        0,
                        plan.Fps,
                        -1,
                        -1);

                    producedAtLastCheck = produced;

                    if (_quality.Current.Camera.Profile == StreamingProfile.Auto && adaptive.Observe(health))
                    {
                        _logger.LogInformation(
                            "Live camera moved to adaptive step {Level} because {Reason}.",
                            adaptive.Level,
                            adaptive.Reason);

                        Interlocked.Exchange(ref settingsChanged, 1);
                    }
                }

                if (video is null)
                {
                    var still = EncodeStill(frame, plan);

                    if (still is not null)
                    {
                        produced++;
                        yield return still;
                    }

                    continue;
                }

                if (Interlocked.Exchange(ref _keyframeWanted, 0) == 1)
                {
                    video.RequestKeyframe();
                }

                packets.Clear();

                var encodeStarted = Stopwatch.GetTimestamp();

                video.Encode(
                    frame.Pixels,
                    frame.Width,
                    frame.Height,
                    frame.Stride,
                    frame.CapturedUnixMs,
                    packets);

                Interlocked.Add(ref _encodeTicks, Stopwatch.GetTimestamp() - encodeStarted);
                Interlocked.Increment(ref _encoded);

                if (video.CodecConfig is { Length: > 0 } sets && !sets.AsSpan().SequenceEqual(codecConfig))
                {
                    codecConfig = sets;

                    yield return new LiveFrame(
                        sets, plan.Width, plan.Height, FrameEncoding.H264, FrameFlags.CodecConfig);
                }

                foreach (var packet in packets)
                {
                    produced++;

                    yield return new LiveFrame(
                        packet.Bytes,
                        plan.Width,
                        plan.Height,
                        FrameEncoding.H264,
                        FrameFlags.Complete
                        | (packet.Flags.HasFlag(VideoPacketFlags.Keyframe)
                            ? FrameFlags.Keyframe
                            : FrameFlags.None));
                }
            }
        }
        finally
        {
            _quality.Changed -= OnSettingsChanged;
            reader.FrameArrived -= OnFrameArrived;

            // Stopped and disposed promptly, so the camera light goes out when the owner stops the
            // stream rather than whenever a finaliser runs.
            try
            {
                await reader.StopAsync().AsTask(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "The camera frame reader did not stop cleanly.");
            }

            video?.Dispose();
            reader.Dispose();
            capture.Dispose();

            _logger.LogInformation("The live camera was released.");
        }
    }

    private IVideoEncoder? CreateEncoder(MediaPlan plan)
    {
        if (!_encoders.IsAvailable)
        {
            return null;
        }

        try
        {
            var encoder = _encoders.Create(plan.ToEncoderSettings());
            encoder.RequestKeyframe();

            _logger.LogInformation(
                "Live camera encoding {Plan} at {Width}x{Height}, {Fps} fps, {Bitrate} kbit/s via {Encoder}.",
                plan.Label,
                plan.Width,
                plan.Height,
                plan.Fps,
                plan.BitrateKbps,
                encoder.BackendName);

            return encoder;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            _logger.LogWarning(
                "The live camera could not use H.264 ({Reason}); sending stills instead.", ex.Message);
            return null;
        }
    }

    /// <summary>The fallback: one JPEG per frame, for a PC with no video encoder.</summary>
    private LiveFrame? EncodeStill(CameraFrame frame, MediaPlan plan)
    {
        try
        {
            var encoded = _stillEncoder.Encode(
                new RawFrame(
                    frame.Pixels,
                    frame.Width,
                    frame.Height,
                    frame.Stride,
                    DateTimeOffset.FromUnixTimeMilliseconds(frame.CapturedUnixMs),
                    0,
                    0,
                    false),
                plan.Width,
                plan.Height,
                70);

            return new LiveFrame(encoded.Bytes, encoded.Width, encoded.Height);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A live camera frame could not be encoded as a still.");
            return null;
        }
    }

    private async Task<(MediaCapture Capture, MediaFrameReader Reader, MediaFrameFormat Format)?> OpenAsync(
        CancellationToken cancellationToken)
    {
        MediaCapture? capture = null;

        try
        {
            var groups = await MediaFrameSourceGroup.FindAllAsync().AsTask(cancellationToken).ConfigureAwait(false);

            var candidate = groups
                .Select(group => new
                {
                    Group = group,
                    Source = group.SourceInfos.FirstOrDefault(info =>
                        info.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord
                        && info.SourceKind == MediaFrameSourceKind.Color),
                })
                .FirstOrDefault(x => x.Source is not null);

            if (candidate is null)
            {
                _logger.LogWarning("No colour camera source was found for live monitoring.");
                return null;
            }

            capture = new MediaCapture();

            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = candidate.Group,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,

                // CPU memory, deliberately: the encoder takes system-memory frames, and asking for
                // GPU surfaces here would mean a read-back per frame to get them out again.
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            }).AsTask(cancellationToken).ConfigureAwait(false);

            PreferFrameRate(capture);

            var source = capture.FrameSources[candidate.Source!.Id];
            var wanted = _quality.Current.Camera;
            var targetHeight = TargetHeight(wanted);
            var format = ChooseFormat(source, targetHeight);

            if (format is not null && format != source.CurrentFormat)
            {
                await source.SetFormatAsync(format).AsTask(cancellationToken).ConfigureAwait(false);
            }

            // Bgra8 rather than the camera's native subtype: the frame reader converts in native
            // code, which is faster and far less code than handling NV12, YUY2 and MJPG here.
            var reader = await capture
                .CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;

            return (capture, reader, source.CurrentFormat);
        }
        catch (Exception ex)
        {
            // The usual causes are another application holding the camera and the Windows camera
            // privacy setting being off. Both are the system working; neither is worth a stack trace.
            _logger.LogWarning("The camera could not be opened for live monitoring: {Reason}", ex.Message);
            capture?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Asks the camera to hold its frame rate rather than trade it for exposure.
    /// <para>
    /// Webcams lengthen their exposure in dim light, and a longer exposure than one frame interval
    /// means fewer frames: this machine's camera, formatted for thirty, delivered twenty-one in an
    /// ordinary room. For a live view, motion that stays fluid is worth a slightly darker picture.
    /// The control is optional on a device, and a device that does not offer it is left as it was.
    /// </para>
    /// </summary>
    private void PreferFrameRate(MediaCapture capture)
    {
        try
        {
            var controller = capture.VideoDeviceController;

            if (controller.ExposurePriorityVideoControl.Supported)
            {
                controller.ExposurePriorityVideoControl.Enabled = false;
            }

            _logger.LogInformation(
                "Camera exposure priority: {Exposure}.",
                controller.ExposurePriorityVideoControl.Supported
                    ? "disabled, so dim light does not lower the frame rate"
                    : "not offered by this device");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The camera did not accept frame-rate preferences.");
        }
    }

    private static int TargetHeight(MediaQuality quality) => quality.Resolution switch
    {
        StreamingResolution.P360 => 360,
        StreamingResolution.P540 => 540,
        StreamingResolution.P1080 => 1080,
        StreamingResolution.Native => 2160,
        _ => 720,
    };

    /// <summary>
    /// Picks a camera format: the closest to the wanted height, then the fastest, then the cheapest
    /// to convert.
    /// <para>
    /// Height first because it is what the owner chose; frame rate second because a camera that can
    /// only do 1080p15 makes a worse room monitor than one doing 720p30; subtype last because it
    /// only affects how much work the conversion is, not what the picture looks like.
    /// </para>
    /// </summary>
    private static MediaFrameFormat? ChooseFormat(MediaFrameSource source, int targetHeight) =>
        source.SupportedFormats
            .Where(f => f.VideoFormat.Width > 0 && f.VideoFormat.Height > 0)
            .OrderBy(f => Math.Abs((int)f.VideoFormat.Height - targetHeight))
            .ThenByDescending(f => f.FrameRate.Denominator == 0
                ? 0
                : Math.Min(60, f.FrameRate.Numerator / f.FrameRate.Denominator))
            .ThenBy(f => SubtypeCost(f.Subtype))
            .FirstOrDefault();

    private static int SubtypeCost(string subtype) => subtype?.ToUpperInvariant() switch
    {
        "NV12" => 0,
        "YUY2" => 1,
        "RGB32" or "ARGB32" => 2,
        "MJPG" => 3,
        _ => 4,
    };

    /// <summary>
    /// Copies one frame's pixels out of a WinRT bitmap.
    /// <para>
    /// A copy rather than a reference because the frame is recycled the moment this returns and the
    /// encoder runs on another thread. The same <c>QueryInterface</c> route as the microphone: the
    /// cast every UWP sample uses throws under CsWinRT, because the object is a projection rather
    /// than a raw COM pointer.
    /// </para>
    /// </summary>
    private static unsafe CameraFrame? Copy(SoftwareBitmap bitmap)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();

        var access = reference.As<IMemoryBufferByteAccess>();
        access.GetBuffer(out var data, out var capacity);

        if (capacity == 0 || data is null)
        {
            return null;
        }

        var description = buffer.GetPlaneDescription(0);
        var stride = description.Stride;
        var height = bitmap.PixelHeight;
        var needed = stride * height;

        if (needed <= 0 || description.StartIndex + needed > capacity)
        {
            return null;
        }

        var pixels = new byte[needed];
        new Span<byte>(data + description.StartIndex, needed).CopyTo(pixels);

        return new CameraFrame(
            pixels,
            bitmap.PixelWidth,
            height,
            stride,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private sealed record CameraFrame(byte[] Pixels, int Width, int Height, int Stride, long CapturedUnixMs);


    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        unsafe void GetBuffer(out byte* buffer, out uint capacity);
    }
}

/// <summary>
/// The real sizes the planner scales from: the primary display, and the camera's own format.
/// <para>
/// Both are needed to answer <c>media.quality.get</c>, which has to say what "Auto" resolves to
/// before anything has been started. Asking the devices directly would mean lighting the camera
/// indicator to answer a settings query, so the camera's answer is whatever the last stream saw and
/// a reasonable guess before that.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowsMediaSourceSizes : IMediaSourceSizes
{
    private readonly IDisplayCatalog _displays;
    private readonly WindowsLiveCamera _camera;

    public WindowsMediaSourceSizes(IDisplayCatalog displays, WindowsLiveCamera camera)
    {
        _displays = displays;
        _camera = camera;
    }

    public (int Width, int Height) ScreenSize()
    {
        var displays = _displays.GetDisplays();
        var display = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();

        return display is null ? (1920, 1080) : (display.Width, display.Height);
    }

    public (int Width, int Height) CameraSize() => _camera.NativeSize;
}
