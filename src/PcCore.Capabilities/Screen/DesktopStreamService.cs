using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Media;
using PcCore.Protocol;

namespace PcCore.Capabilities.Screen;

public sealed class StreamOptions
{
    public const string SectionName = "PcCore:Stream";

    /// <summary>
    /// JPEG quality for the fallback path.
    /// <para>
    /// Only reached on a PC with no H.264 encoder, or after one has failed mid-stream. It is a
    /// degraded mode and it is meant to look like one — a desktop at a few frames a second is still
    /// worth having when the alternative is a black screen and an error message.
    /// </para>
    /// </summary>
    public int Quality { get; set; } = 65;

    /// <summary>Frames a second for the fallback path. The video path takes its rate from the plan.</summary>
    public int FallbackFps { get; set; } = 12;

    /// <summary>Bounds for the fallback path, which has no plan to take a size from.</summary>
    public int MaxWidth { get; set; } = 1280;

    public int MaxHeight { get; set; } = 1280;

    /// <summary>
    /// One at a time. There is one owner and one phone; a second concurrent stream would double the
    /// capture cost to show the same desktop twice.
    /// </summary>
    public int MaxConcurrentStreams { get; set; } = 1;

    /// <summary>How long a capture attempt waits before reporting that nothing changed.</summary>
    public int FrameTimeoutMs { get; set; } = 250;

    /// <summary>How often the capture loop logs its counters. Zero disables it.</summary>
    public int DiagnosticsIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Encoded frames allowed to be waiting for the socket before a capture is abandoned.
    /// <para>
    /// This is the whole backpressure policy for the video path, and it has to sit here rather than
    /// in the channel. With independent JPEG frames, dropping a queued frame cost one repaint. With
    /// H.264 it costs every frame until the next keyframe, because each picture is coded against the
    /// last. So nothing queued is ever thrown away; instead, when the queue is already this deep, the
    /// next capture simply does not happen. The frame that would have been stale is never encoded,
    /// which is cheaper than encoding it and better than sending it late.
    /// </para>
    /// </summary>
    public int MaxBacklog { get; set; } = 2;
}

/// <summary>What the phone reports about what it did with the frames it was sent.</summary>
/// <param name="LatencyMs">
/// Capture to render, measured against a clock offset the two ends agreed on. The only number in
/// this system that describes the thing the owner actually experiences.
/// </param>
public readonly record struct ClientPlayback(double LatencyMs, double DecodeMs, double RenderedFps, long Dropped);

/// <summary>What the stream did. Local diagnostics only — never sent anywhere but the log and the owner's own UI.</summary>
public sealed record StreamStatistics(
    long FramesCaptured,
    long FramesEncoded,
    long FramesDropped,
    long FramesSkipped,
    long BytesEncoded,
    long CaptureLosses,
    double AverageFrameBytes,
    double CaptureFps,
    double DeliveredFps,
    double BitrateKbps,
    double EncodeMs,
    TimeSpan Elapsed,
    string Encoder,
    MediaPlan Plan,
    int AdaptiveLevel,
    string AdaptiveReason,
    ClientPlayback Client);

/// <summary>
/// One live stream.
/// <para>
/// <b>The queue is short, and it never discards.</b> It holds a few frames rather than one, because
/// an H.264 picture is only meaningful after the pictures before it: throwing a queued frame away
/// corrupts everything the phone draws until the next keyframe, which is exactly the "sometimes it
/// goes to mush" failure that a naive latest-frame-wins policy produces once there is a codec in the
/// path. Staleness is controlled upstream instead — see <see cref="StreamOptions.MaxBacklog"/> — by
/// not capturing a frame that would arrive late rather than by dropping it afterwards. A drop here
/// is a last resort, and when it happens the encoder is asked for a keyframe so the damage lasts one
/// frame instead of two seconds.
/// </para>
/// </summary>
public sealed class DesktopStreamSession : IAsyncDisposable
{
    private readonly Channel<byte[]> _frames;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Action<DesktopStreamSession> _onDisposed;
    private readonly Lock _gate = new();

    private long _captured;
    private long _encoded;
    private long _dropped;
    private long _skipped;
    private long _bytes;
    private long _losses;
    private long _delivered;
    private double _encodeMs;
    private int _backlog;
    private int _keyframeWanted = 1;
    private ClientPlayback _client = new(-1, -1, 0, 0);

    internal DesktopStreamSession(
        uint streamId,
        string ownerId,
        DisplayInfo display,
        MediaPlan plan,
        string encoder,
        int queueDepth,
        Action<DesktopStreamSession> onDisposed)
    {
        StreamId = streamId;
        OwnerId = ownerId;
        Display = display;
        Plan = plan;
        Encoder = encoder;
        _onDisposed = onDisposed;

        _frames = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(Math.Max(2, queueDepth))
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            },
            itemDropped: _ =>
            {
                Interlocked.Increment(ref _dropped);
                Interlocked.Decrement(ref _backlog);

                // A dropped picture breaks every picture coded against it. Asking for a keyframe
                // turns an indefinite smear into one bad frame.
                RequestKeyframe();
            });
    }

    public uint StreamId { get; }

    /// <summary>The connection this stream belongs to. A stream never outlives its connection.</summary>
    public string OwnerId { get; }

    public DisplayInfo Display { get; }

    /// <summary>What the encoder is currently set to. Changes when the adaptive controller moves.</summary>
    public MediaPlan Plan { get; internal set; }

    /// <summary>Which encoder is behind this, named rather than described as "hardware".</summary>
    public string Encoder { get; internal set; }

    /// <summary>Encoded frames in order. The transport reads from here.</summary>
    public ChannelReader<byte[]> Frames => _frames.Reader;

    /// <summary>Frames written but not yet handed to the socket.</summary>
    public int Backlog => Volatile.Read(ref _backlog);

    internal CancellationToken Stopping => _stopping.Token;

    internal Task? Pump { get; set; }

    internal int AdaptiveLevel { get; set; }

    internal string AdaptiveReason { get; set; } = "";

    /// <summary>Asks the capture loop to make the next frame a keyframe.</summary>
    public void RequestKeyframe() => Interlocked.Exchange(ref _keyframeWanted, 1);

    internal bool TakeKeyframeRequest() => Interlocked.Exchange(ref _keyframeWanted, 0) == 1;

    /// <summary>Records what the phone says it is experiencing. Feeds the adaptive controller.</summary>
    public void ReportClient(ClientPlayback playback)
    {
        lock (_gate)
        {
            _client = playback;
        }
    }

    public ClientPlayback Client
    {
        get
        {
            lock (_gate)
            {
                return _client;
            }
        }
    }

    internal void Publish(byte[] message, int payloadBytes)
    {
        Interlocked.Increment(ref _encoded);
        Interlocked.Add(ref _bytes, payloadBytes);
        Interlocked.Increment(ref _backlog);

        // Always succeeds: the channel drops the oldest rather than refusing this one, and the drop
        // callback above corrects the backlog and asks for a keyframe.
        _frames.Writer.TryWrite(message);
    }

    /// <summary>Called by the transport once a frame is actually on the socket.</summary>
    public void ReportSent()
    {
        Interlocked.Decrement(ref _backlog);
        Interlocked.Increment(ref _delivered);
    }

    internal void CountCapture() => Interlocked.Increment(ref _captured);

    internal void CountLoss() => Interlocked.Increment(ref _losses);

    internal void CountSkip() => Interlocked.Increment(ref _skipped);

    internal void CountEncodeTime(double milliseconds)
    {
        lock (_gate)
        {
            _encodeMs += milliseconds;
        }
    }

    public StreamStatistics Statistics()
    {
        var encoded = Interlocked.Read(ref _encoded);
        var delivered = Interlocked.Read(ref _delivered);
        var bytes = Interlocked.Read(ref _bytes);
        var elapsed = _clock.Elapsed;
        var seconds = Math.Max(0.001, elapsed.TotalSeconds);

        double encodeMs;

        lock (_gate)
        {
            encodeMs = encoded == 0 ? 0 : _encodeMs / encoded;
        }

        return new StreamStatistics(
            Interlocked.Read(ref _captured),
            encoded,
            Interlocked.Read(ref _dropped),
            Interlocked.Read(ref _skipped),
            bytes,
            Interlocked.Read(ref _losses),
            encoded == 0 ? 0 : (double)bytes / encoded,
            Interlocked.Read(ref _captured) / seconds,
            delivered / seconds,
            bytes * 8 / seconds / 1000,
            encodeMs,
            elapsed,
            Encoder,
            Plan,
            AdaptiveLevel,
            AdaptiveReason,
            Client);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _frames.Writer.TryComplete();

        if (Pump is not null)
        {
            try
            {
                await Pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the loop is being stopped.
            }
        }

        _stopping.Dispose();
        _onDisposed(this);
    }
}

/// <summary>
/// Owns live desktop streams: their capture, their encoding, and their lifetime.
/// <para>
/// It knows nothing about sockets. A stream produces encoded frames into a short channel and the
/// transport takes them from there — which is what lets the stream be tested end to end against a
/// fake capture source and a fake encoder with no network and no GPU involved, and what keeps
/// "somebody is watching" from being the same fact as "somebody may type".
/// </para>
/// <para>
/// <b>Two encoders, one loop.</b> The normal path is H.264 from whatever transform Windows has.
/// Behind it, unchanged, is the original still-image path, used when a PC has no video encoder and
/// when one fails while running. The loop does not branch on which: it asks for packets and sends
/// whatever it gets, so the fallback is a different encoder rather than a different design.
/// </para>
/// </summary>
public sealed class DesktopStreamService : IAsyncDisposable
{
    private readonly IDesktopStream _capture;
    private readonly IFrameEncoder _stillEncoder;
    private readonly IVideoEncoderFactory _videoEncoders;
    private readonly MediaQualityStore _quality;
    private readonly StreamOptions _options;
    private readonly ILogger<DesktopStreamService> _logger;
    private readonly Lock _gate = new();
    private readonly List<DesktopStreamSession> _sessions = [];

    private uint _nextStreamId;

    public DesktopStreamService(
        IDesktopStream capture,
        IFrameEncoder stillEncoder,
        IVideoEncoderFactory videoEncoders,
        MediaQualityStore quality,
        StreamOptions options,
        ILogger<DesktopStreamService> logger)
    {
        _capture = capture;
        _stillEncoder = stillEncoder;
        _videoEncoders = videoEncoders;
        _quality = quality;
        _options = options;
        _logger = logger;
    }

    public IReadOnlyList<DisplayInfo> GetDisplays() => _capture.GetDisplays();

    public int ActiveStreams
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>Every running stream, so a settings change can be applied to what is already live.</summary>
    public IReadOnlyList<DesktopStreamSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions];
            }
        }
    }

    /// <summary>
    /// Starts a stream on a display.
    /// <para>
    /// A start from an owner that already has one replaces it. Refusing instead would mean a phone
    /// that dropped its connection mid-stream could not resume until something else timed out, and
    /// being locked out of your own desktop is a worse failure than an extra restart.
    /// </para>
    /// </summary>
    public DesktopStreamSession Start(string ownerId, int? displayIndex)
    {
        var displays = _capture.GetDisplays();

        if (displays.Count == 0)
        {
            throw new CapabilityException(ErrorCodes.Unsupported, "No display is available to capture.");
        }

        var display = displayIndex is null
            ? displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0]
            : displays.FirstOrDefault(d => d.Index == displayIndex.Value)
              ?? throw new CapabilityException(ErrorCodes.InvalidParameters, "No such display.");

        var plan = MediaPlanner.Resolve(
            _quality.Current.Screen, MediaTarget.Screen, display.Width, display.Height);

        DesktopStreamSession[] superseded;
        DesktopStreamSession session;

        lock (_gate)
        {
            superseded = [.. _sessions.Where(s => s.OwnerId == ownerId)];

            if (_sessions.Count - superseded.Length >= _options.MaxConcurrentStreams)
            {
                throw new CapabilityException(
                    ErrorCodes.Unsupported,
                    $"Only {_options.MaxConcurrentStreams} live stream(s) may run at once.");
            }

            session = new DesktopStreamSession(
                ++_nextStreamId,
                ownerId,
                display,
                plan,
                _videoEncoders.IsAvailable ? _videoEncoders.Describe() : "JPEG still frames",
                _options.MaxBacklog + 2,
                Forget);

            _sessions.Add(session);
        }

        foreach (var old in superseded)
        {
            _ = old.DisposeAsync().AsTask();
        }

        session.Pump = Task.Run(() => CaptureLoopAsync(session));

        _logger.LogInformation(
            "Live stream {StreamId} started on {Display} for {Owner}: {Plan} at {Width}x{Height}, "
            + "{Fps} fps, {Bitrate} kbit/s, via {Encoder}.",
            session.StreamId,
            display,
            ownerId,
            plan.Label,
            plan.Width,
            plan.Height,
            plan.Fps,
            plan.BitrateKbps,
            session.Encoder);

        return session;
    }

    /// <summary>
    /// Stops every stream belonging to one owner, and returns what the last one did.
    /// <para>
    /// The statistics are read before disposal, because a disposed session has nothing left to
    /// report — and "how did that stream actually perform" is the question the stop response exists
    /// to answer.
    /// </para>
    /// </summary>
    public async Task<StreamStatistics?> StopAsync(string ownerId)
    {
        DesktopStreamSession[] owned;

        lock (_gate)
        {
            owned = [.. _sessions.Where(s => s.OwnerId == ownerId)];
        }

        StreamStatistics? last = null;

        foreach (var session in owned)
        {
            var stats = session.Statistics();
            last = stats;
            await session.DisposeAsync().ConfigureAwait(false);

            _logger.LogInformation(
                "Live stream {StreamId} stopped after {Elapsed:g}: {Delivered:F1} fps delivered, "
                + "{Bitrate:F0} kbit/s, {Encoded} frames, {Skipped} captures skipped, {Dropped} dropped, "
                + "{Encode:F1} ms/frame to encode, {Losses} capture loss(es).",
                session.StreamId,
                stats.Elapsed,
                stats.DeliveredFps,
                stats.BitrateKbps,
                stats.FramesEncoded,
                stats.FramesSkipped,
                stats.FramesDropped,
                stats.EncodeMs,
                stats.CaptureLosses);
        }

        return last;
    }

    private void Forget(DesktopStreamSession session)
    {
        lock (_gate)
        {
            _sessions.Remove(session);
        }
    }

    /// <summary>
    /// Capture, encode, publish — and survive everything the display can do while it runs.
    /// </summary>
    private async Task CaptureLoopAsync(DesktopStreamSession session)
    {
        var token = session.Stopping;
        var adaptive = new AdaptiveStreamController();
        var pacer = new FramePacer();
        var packets = new List<VideoPacket>();

        IDesktopCaptureSource? source = null;
        IVideoEncoder? video = null;
        byte[]? codecConfig = null;

        var sequence = 0UL;
        var plan = session.Plan;
        var nextDiagnostics = DateTimeOffset.UtcNow.AddSeconds(_options.DiagnosticsIntervalSeconds);
        var nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(1);
        var deliveredAtLastCheck = 0L;
        (TimeSpan Elapsed, long Encoded, long Delivered, long Bytes, double EncodeTotal, long Skipped, long Dropped)
            windowStart = (TimeSpan.Zero, 0, 0, 0, 0, 0, 0);
        var settingsChanged = 0;

        void OnSettingsChanged(MediaQualitySettings _) => Interlocked.Exchange(ref settingsChanged, 1);

        _quality.Changed += OnSettingsChanged;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(plan.Fps, 1, 60));

                if (source is null)
                {
                    try
                    {
                        source = _capture.Open(session.Display.Index);
                    }
                    catch (Exception ex) when (ex is CapabilityException or InvalidOperationException)
                    {
                        // The display may be mid-recreation. Back off and try again rather than
                        // killing a stream the owner is still watching.
                        session.CountLoss();
                        _logger.LogWarning(
                            "Stream {StreamId} could not open capture: {Reason}", session.StreamId, ex.Message);
                        await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                        continue;
                    }
                }

                // Re-plan before capturing, so a new encoder is built against the next frame rather
                // than in the middle of one.
                if (Interlocked.Exchange(ref settingsChanged, 0) == 1)
                {
                    // A deliberate change starts adaptation from the top of the new plan rather than
                    // carrying a step that was taken for the old one.
                    adaptive.Reset();
                    session.AdaptiveLevel = 0;
                    session.AdaptiveReason = "";

                    var wanted = MediaPlanner.Resolve(
                        _quality.Current.Screen,
                        MediaTarget.Screen,
                        session.Display.Width,
                        session.Display.Height);

                    if (Rebuild(session, wanted, ref plan, ref video, ref codecConfig))
                    {
                        _logger.LogInformation(
                            "Stream {StreamId} settings changed: {Plan} at {Width}x{Height}, {Fps} fps, {Bitrate} kbit/s.",
                            session.StreamId,
                            wanted.Label,
                            wanted.Width,
                            wanted.Height,
                            wanted.Fps,
                            wanted.BitrateKbps);
                    }
                }

                // Nothing queued may be thrown away once there is a codec in the path, so the way to
                // keep the stream current is not to capture what would arrive late.
                if (session.Backlog > _options.MaxBacklog)
                {
                    session.CountSkip();
                    await pacer.WaitAsync(interval, token).ConfigureAwait(false);
                    continue;
                }

                var result = source.TryGetFrame(TimeSpan.FromMilliseconds(
                    Math.Clamp(_options.FrameTimeoutMs, 20, 2000)));

                switch (result.Status)
                {
                    case CaptureStatus.Frame:
                        session.CountCapture();
                        Encode(session, result.Frame!, ref sequence, ref video, ref codecConfig, plan, packets);
                        break;

                    case CaptureStatus.Timeout:
                        // Nothing changed. The client keeps showing the last frame, which is still
                        // correct, so there is nothing to send.
                        break;

                    case CaptureStatus.Lost:
                        session.CountLoss();
                        _logger.LogInformation(
                            "Stream {StreamId} capture lost ({Reason}); reopening.",
                            session.StreamId, result.Diagnostic);
                        source.Dispose();
                        source = null;

                        // A new capture may be a new size, so the encoder is rebuilt from the
                        // display as it is now and the client is told with a fresh configuration.
                        video?.Dispose();
                        video = null;
                        codecConfig = null;
                        session.RequestKeyframe();
                        break;

                    case CaptureStatus.Unavailable:
                        session.CountLoss();
                        _logger.LogInformation(
                            "Stream {StreamId} capture unavailable ({Reason}); retrying.",
                            session.StreamId, result.Diagnostic);
                        source.Dispose();
                        source = null;
                        await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                        break;
                }

                var now = DateTimeOffset.UtcNow;

                if (now >= nextHealthCheck)
                {
                    var stats = session.Statistics();
                    var deliveredNow = stats.FramesEncoded - session.Backlog;

                    var health = new StreamHealth(
                        session.Backlog,
                        stats.FramesSkipped,
                        Math.Max(0, deliveredNow - deliveredAtLastCheck),
                        plan.Fps,
                        stats.Client.LatencyMs,
                        stats.Client.DecodeMs);

                    deliveredAtLastCheck = deliveredNow;
                    nextHealthCheck = now.AddSeconds(1);

                    if (adaptive.Observe(health) && _quality.Current.Screen.Profile == StreamingProfile.Auto)
                    {
                        var wanted = MediaPlanner.Resolve(
                            _quality.Current.Screen,
                            MediaTarget.Screen,
                            session.Display.Width,
                            session.Display.Height,
                            adaptive.Level);

                        session.AdaptiveLevel = adaptive.Level;
                        session.AdaptiveReason = adaptive.Reason;

                        _logger.LogInformation(
                            "Stream {StreamId} moved to {Plan} ({Width}x{Height}, {Fps} fps, {Bitrate} kbit/s) "
                            + "because {Reason}.",
                            session.StreamId,
                            wanted.Label,
                            wanted.Width,
                            wanted.Height,
                            wanted.Fps,
                            wanted.BitrateKbps,
                            adaptive.Reason);

                        Rebuild(session, wanted, ref plan, ref video, ref codecConfig);
                    }
                }

                if (_options.DiagnosticsIntervalSeconds > 0 && now >= nextDiagnostics)
                {
                    var stats = session.Statistics();
                    var window = stats.Elapsed - windowStart.Elapsed;
                    var seconds = Math.Max(0.001, window.TotalSeconds);
                    var delivered = stats.FramesEncoded - session.Backlog;
                    var encodeTotal = stats.EncodeMs * stats.FramesEncoded;
                    var framesInWindow = stats.FramesEncoded - windowStart.Encoded;

                    // Rates over the last interval, not the whole session. A profile change a minute
                    // in should show up in the next line, not be averaged into the first minute.
                    _logger.LogInformation(
                        "Stream {StreamId} [{Plan} {Width}x{Height}@{Planned} {PlanKbps}kbps]: "
                        + "{Delivered:F1} fps delivered, {Bitrate:F0} kbit/s, {Encode:F1} ms/frame, "
                        + "backlog {Backlog}, {Skipped} skipped, {Dropped} dropped | phone: {Latency}, "
                        + "{Rendered:F1} fps rendered, {Decode} decode, {PhoneDropped} dropped.",
                        session.StreamId,
                        plan.Label,
                        plan.Width,
                        plan.Height,
                        plan.Fps,
                        plan.BitrateKbps,
                        (delivered - windowStart.Delivered) / seconds,
                        (stats.BytesEncoded - windowStart.Bytes) * 8 / seconds / 1000,
                        framesInWindow <= 0 ? 0 : (encodeTotal - windowStart.EncodeTotal) / framesInWindow,
                        session.Backlog,
                        stats.FramesSkipped - windowStart.Skipped,
                        stats.FramesDropped - windowStart.Dropped,
                        stats.Client.LatencyMs < 0 ? "latency not reported" : $"{stats.Client.LatencyMs:F0} ms capture-to-render",
                        stats.Client.RenderedFps,
                        stats.Client.DecodeMs < 0 ? "n/a" : $"{stats.Client.DecodeMs:F1} ms",
                        stats.Client.Dropped);

                    windowStart = (stats.Elapsed, stats.FramesEncoded, delivered, stats.BytesEncoded, encodeTotal,
                        stats.FramesSkipped, stats.FramesDropped);
                    nextDiagnostics = now.AddSeconds(_options.DiagnosticsIntervalSeconds);
                }

                await pacer.WaitAsync(interval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            // A capture fault must never take the host down with it. The stream ends; screenshots,
            // input and everything else carry on.
            _logger.LogError(ex, "Stream {StreamId} capture loop failed and will stop.", session.StreamId);
        }
        finally
        {
            _quality.Changed -= OnSettingsChanged;
            source?.Dispose();
            video?.Dispose();
        }
    }

    /// <summary>Swaps the encoder for a new plan. Returns true when anything actually changed.</summary>
    private bool Rebuild(
        DesktopStreamSession session,
        MediaPlan wanted,
        ref MediaPlan plan,
        ref IVideoEncoder? video,
        ref byte[]? codecConfig)
    {
        if (video is not null && !wanted.NeedsNewEncoder(plan) && wanted.BitrateKbps == plan.BitrateKbps)
        {
            return false;
        }

        plan = wanted;
        session.Plan = wanted;

        video?.Dispose();
        video = null;
        codecConfig = null;

        // Not built here: the loop builds it on the next frame, which keeps every encoder creation
        // on one path with one set of failure handling.
        session.RequestKeyframe();
        return true;
    }

    private void Encode(
        DesktopStreamSession session,
        RawFrame frame,
        ref ulong sequence,
        ref IVideoEncoder? video,
        ref byte[]? codecConfig,
        MediaPlan plan,
        List<VideoPacket> packets)
    {
        var started = Stopwatch.GetTimestamp();

        if (video is null && _videoEncoders.IsAvailable)
        {
            try
            {
                video = _videoEncoders.Create(plan.ToEncoderSettings());
                session.Encoder = $"{video.BackendName} ({(video.IsHardware ? "hardware" : "software")})";
                session.RequestKeyframe();
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
            {
                _logger.LogWarning(
                    "Stream {StreamId} could not start an H.264 encoder ({Reason}); falling back to still frames.",
                    session.StreamId,
                    ex.Message);

                session.Encoder = "JPEG still frames";
            }
        }

        if (video is null)
        {
            EncodeStill(session, frame, ref sequence);
            session.CountEncodeTime(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return;
        }

        if (session.TakeKeyframeRequest())
        {
            video.RequestKeyframe();
        }

        packets.Clear();

        try
        {
            video.Encode(
                frame.Pixels,
                frame.Width,
                frame.Height,
                frame.Stride,
                frame.CapturedAtUtc.ToUnixTimeMilliseconds(),
                packets);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Stream {StreamId} could not encode a frame; falling back to still frames.", session.StreamId);

            video.Dispose();
            video = null;
            codecConfig = null;
            session.Encoder = "JPEG still frames";
            return;
        }

        // The parameter sets go out as their own frame the first time they are known, and again
        // whenever they change. A client that has them can start on any keyframe; one that does not
        // cannot start at all, and has no way to ask.
        if (video.CodecConfig is { Length: > 0 } sets && !sets.AsSpan().SequenceEqual(codecConfig))
        {
            codecConfig = sets;
            PublishVideo(session, sets, FrameFlags.CodecConfig, frame, plan, ref sequence);
        }

        foreach (var packet in packets)
        {
            var flags = FrameFlags.Complete
                | (packet.Flags.HasFlag(VideoPacketFlags.Keyframe) ? FrameFlags.Keyframe : FrameFlags.None)
                | (frame.CursorVisible ? FrameFlags.CursorBakedIn : FrameFlags.None);

            PublishVideo(session, packet.Bytes, flags, frame, plan, ref sequence, packet.CaptureUnixMs);
        }

        session.CountEncodeTime(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static void PublishVideo(
        DesktopStreamSession session,
        byte[] payload,
        FrameFlags flags,
        RawFrame frame,
        MediaPlan plan,
        ref ulong sequence,
        long? capturedUnixMs = null)
    {
        var header = new StreamFrameHeader
        {
            Version = StreamFrameHeader.CurrentVersion,
            Encoding = FrameEncoding.H264,
            Flags = flags,
            DisplayIndex = (byte)Math.Clamp(session.Display.Index, 0, 255),
            StreamId = session.StreamId,
            Sequence = ++sequence,
            TimestampUnixMs = capturedUnixMs ?? frame.CapturedAtUtc.ToUnixTimeMilliseconds(),
            Width = (ushort)plan.Width,
            Height = (ushort)plan.Height,
            DesktopLeft = session.Display.Left,
            DesktopTop = session.Display.Top,
            DesktopWidth = session.Display.Width,
            DesktopHeight = session.Display.Height,
            CursorX = frame.CursorX,
            CursorY = frame.CursorY,
            CursorVisible = frame.CursorVisible,
            PayloadLength = payload.Length,
        };

        session.Publish(header.ToMessage(payload), payload.Length);
    }

    private void EncodeStill(DesktopStreamSession session, RawFrame frame, ref ulong sequence)
    {
        EncodedFrame encoded;

        try
        {
            encoded = _stillEncoder.Encode(frame, _options.MaxWidth, _options.MaxHeight, _options.Quality);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stream {StreamId} could not encode a frame; skipping it.", session.StreamId);
            return;
        }

        var header = new StreamFrameHeader
        {
            Version = StreamFrameHeader.CurrentVersion,
            Encoding = _stillEncoder.Encoding,
            Flags = FrameFlags.Complete
                | FrameFlags.Keyframe
                | (frame.CursorVisible ? FrameFlags.CursorBakedIn : FrameFlags.None),
            DisplayIndex = (byte)Math.Clamp(session.Display.Index, 0, 255),
            StreamId = session.StreamId,
            Sequence = ++sequence,
            TimestampUnixMs = frame.CapturedAtUtc.ToUnixTimeMilliseconds(),
            Width = (ushort)encoded.Width,
            Height = (ushort)encoded.Height,
            DesktopLeft = session.Display.Left,
            DesktopTop = session.Display.Top,
            DesktopWidth = session.Display.Width,
            DesktopHeight = session.Display.Height,
            CursorX = frame.CursorX,
            CursorY = frame.CursorY,
            CursorVisible = frame.CursorVisible,
            PayloadLength = encoded.Bytes.Length,
        };

        session.Publish(header.ToMessage(encoded.Bytes), encoded.Bytes.Length);
    }

    public async ValueTask DisposeAsync()
    {
        DesktopStreamSession[] all;

        lock (_gate)
        {
            all = [.. _sessions];
        }

        foreach (var session in all)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
