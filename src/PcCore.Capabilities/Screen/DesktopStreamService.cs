using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Screen;

public sealed class StreamOptions
{
    public const string SectionName = "PcCore:Stream";

    /// <summary>
    /// Frames per second the capture loop aims for.
    /// <para>
    /// Fifteen. This is a personal remote-control tool over a WireGuard tunnel, not a game stream:
    /// at 15 the pointer feels attached to your finger, and doubling the rate would double both the
    /// bandwidth and the CPU cost to make text that is already readable arrive slightly sooner.
    /// </para>
    /// </summary>
    public int MaxFps { get; set; } = 15;

    /// <summary>
    /// JPEG quality.
    /// <para>
    /// The defaults below were measured on this machine rather than guessed, against a
    /// continuously-changing 1920x1080 desktop — the worst case, since a still desktop produces no
    /// frames at all:
    /// </para>
    /// <code>
    /// 1600 wide, q70   101 KB/frame   10.7 Mbit/s   12.9 ms encode
    /// 1280 wide, q70    73 KB/frame    7.7 Mbit/s    9.6 ms encode
    /// 1280 wide, q65    68 KB/frame    7.2 Mbit/s    9.5 ms encode
    /// 1024 wide, q65    47 KB/frame    5.0 Mbit/s    8.1 ms encode
    /// </code>
    /// <para>
    /// 1280x720 at 65 is the chosen point: ten megabits to a phone is more than a tunnel over
    /// mobile data should be asked for, and 1280 is already more pixels than a phone screen shows.
    /// Dropping to 1024 saves another two megabits at a visible cost to small text, which is the
    /// thing a remote desktop is usually being read for.
    /// </para>
    /// </summary>
    public int Quality { get; set; } = 65;

    /// <summary>Frames are downscaled to fit these bounds. A phone cannot show 4K, and sending it wastes the link.</summary>
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
    public int DiagnosticsIntervalSeconds { get; set; } = 30;
}

/// <summary>What the stream did. Local diagnostics only — never sent anywhere but the log and the owner's own UI.</summary>
public sealed record StreamStatistics(
    long FramesCaptured,
    long FramesEncoded,
    long FramesDropped,
    long BytesEncoded,
    long CaptureLosses,
    double AverageFrameBytes,
    double CaptureFps,
    TimeSpan Elapsed);

/// <summary>
/// One live stream.
/// <para>
/// The frame channel holds exactly one message and drops the older one when a new frame arrives.
/// That single line is the whole backpressure policy: if the network or the phone falls behind, what
/// they miss is desktop content that has already been superseded. Queueing instead would spend
/// memory to deliver a stale desktop late, which is worse than not delivering it at all.
/// </para>
/// </summary>
public sealed class DesktopStreamSession : IAsyncDisposable
{
    private readonly Channel<byte[]> _frames;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Action<DesktopStreamSession> _onDisposed;

    private long _captured;
    private long _encoded;
    private long _dropped;
    private long _bytes;
    private long _losses;

    internal DesktopStreamSession(
        uint streamId,
        string ownerId,
        DisplayInfo display,
        Action<DesktopStreamSession> onDisposed)
    {
        StreamId = streamId;
        OwnerId = ownerId;
        Display = display;
        _onDisposed = onDisposed;

        _frames = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            },
            itemDropped: _ => Interlocked.Increment(ref _dropped));
    }

    public uint StreamId { get; }

    /// <summary>The connection this stream belongs to. A stream never outlives its connection.</summary>
    public string OwnerId { get; }

    public DisplayInfo Display { get; }

    /// <summary>Encoded frames, newest only. The transport reads from here.</summary>
    public ChannelReader<byte[]> Frames => _frames.Reader;

    internal CancellationToken Stopping => _stopping.Token;

    internal Task? Pump { get; set; }

    internal void Publish(byte[] message, int payloadBytes)
    {
        Interlocked.Increment(ref _encoded);
        Interlocked.Add(ref _bytes, payloadBytes);

        // Always succeeds: the channel drops the previous frame rather than refusing this one.
        _frames.Writer.TryWrite(message);
    }

    internal void CountCapture() => Interlocked.Increment(ref _captured);

    internal void CountLoss() => Interlocked.Increment(ref _losses);

    public StreamStatistics Statistics()
    {
        var encoded = Interlocked.Read(ref _encoded);
        var elapsed = _clock.Elapsed;

        return new StreamStatistics(
            Interlocked.Read(ref _captured),
            encoded,
            Interlocked.Read(ref _dropped),
            Interlocked.Read(ref _bytes),
            Interlocked.Read(ref _losses),
            encoded == 0 ? 0 : (double)Interlocked.Read(ref _bytes) / encoded,
            elapsed.TotalSeconds <= 0 ? 0 : Interlocked.Read(ref _captured) / elapsed.TotalSeconds,
            elapsed);
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
            catch (Exception ex) when (ex is OperationCanceledException)
            {
                // Expected: the loop is being stopped.
            }
        }

        _stopping.Dispose();
        _onDisposed(this);
    }
}

/// <summary>
/// Owns live desktop streams: their capture loops, their encoding, and their lifetime.
/// <para>
/// It knows nothing about sockets. A stream produces encoded frames into a one-slot channel and the
/// transport takes them from there — which is what lets the stream be tested end to end against a
/// fake capture source with no network involved, and what keeps "somebody is watching" from being
/// the same fact as "somebody may type".
/// </para>
/// </summary>
public sealed class DesktopStreamService : IAsyncDisposable
{
    private readonly IDesktopStream _capture;
    private readonly IFrameEncoder _encoder;
    private readonly StreamOptions _options;
    private readonly ILogger<DesktopStreamService> _logger;
    private readonly Lock _gate = new();
    private readonly List<DesktopStreamSession> _sessions = [];

    private uint _nextStreamId;

    public DesktopStreamService(
        IDesktopStream capture,
        IFrameEncoder encoder,
        StreamOptions options,
        ILogger<DesktopStreamService> logger)
    {
        _capture = capture;
        _encoder = encoder;
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
                Forget);

            _sessions.Add(session);
        }

        foreach (var old in superseded)
        {
            _ = old.DisposeAsync().AsTask();
        }

        session.Pump = Task.Run(() => CaptureLoopAsync(session));

        _logger.LogInformation(
            "Live stream {StreamId} started on {Display} for {Owner} ({Fps} fps, quality {Quality}, max {MaxWidth}x{MaxHeight}).",
            session.StreamId,
            display,
            ownerId,
            _options.MaxFps,
            _options.Quality,
            _options.MaxWidth,
            _options.MaxHeight);

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
                "Live stream {StreamId} stopped after {Elapsed:g}: {Encoded} frames sent, {Dropped} dropped, "
                + "{Fps:F1} fps captured, {Average:F0} bytes/frame, {Losses} capture loss(es).",
                session.StreamId,
                stats.Elapsed,
                stats.FramesEncoded,
                stats.FramesDropped,
                stats.CaptureFps,
                stats.AverageFrameBytes,
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
        var interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(_options.MaxFps, 1, 60));
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(_options.FrameTimeoutMs, 20, 2000));

        IDesktopCaptureSource? source = null;
        var sequence = 0UL;
        var nextDiagnostics = DateTimeOffset.UtcNow.AddSeconds(_options.DiagnosticsIntervalSeconds);

        try
        {
            while (!token.IsCancellationRequested)
            {
                var frameStarted = DateTimeOffset.UtcNow;

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
                        _logger.LogWarning("Stream {StreamId} could not open capture: {Reason}", session.StreamId, ex.Message);
                        await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                        continue;
                    }
                }

                var result = source.TryGetFrame(timeout);

                switch (result.Status)
                {
                    case CaptureStatus.Frame:
                        session.CountCapture();
                        Encode(session, result.Frame!, ref sequence);
                        break;

                    case CaptureStatus.Timeout:
                        // Nothing changed. The client keeps showing the last frame, which is still
                        // correct, so there is nothing to send.
                        break;

                    case CaptureStatus.Lost:
                        session.CountLoss();
                        _logger.LogInformation(
                            "Stream {StreamId} capture lost ({Reason}); reopening.", session.StreamId, result.Diagnostic);
                        source.Dispose();
                        source = null;
                        break;

                    case CaptureStatus.Unavailable:
                        session.CountLoss();
                        _logger.LogInformation(
                            "Stream {StreamId} capture unavailable ({Reason}); retrying.", session.StreamId, result.Diagnostic);
                        source.Dispose();
                        source = null;
                        await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                        break;
                }

                if (_options.DiagnosticsIntervalSeconds > 0 && DateTimeOffset.UtcNow >= nextDiagnostics)
                {
                    var stats = session.Statistics();
                    _logger.LogInformation(
                        "Stream {StreamId}: {Fps:F1} fps captured, {Encoded} encoded, {Dropped} dropped, {Average:F0} bytes/frame.",
                        session.StreamId,
                        stats.CaptureFps,
                        stats.FramesEncoded,
                        stats.FramesDropped,
                        stats.AverageFrameBytes);

                    nextDiagnostics = DateTimeOffset.UtcNow.AddSeconds(_options.DiagnosticsIntervalSeconds);
                }

                // Pace to the target rate. Capture can be much faster than this and there is no
                // point encoding frames nobody asked for.
                var spent = DateTimeOffset.UtcNow - frameStarted;
                if (spent < interval)
                {
                    await Task.Delay(interval - spent, token).ConfigureAwait(false);
                }
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
            source?.Dispose();
        }
    }

    private void Encode(DesktopStreamSession session, RawFrame frame, ref ulong sequence)
    {
        EncodedFrame encoded;

        try
        {
            encoded = _encoder.Encode(frame, _options.MaxWidth, _options.MaxHeight, _options.Quality);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stream {StreamId} could not encode a frame; skipping it.", session.StreamId);
            return;
        }

        var header = new StreamFrameHeader
        {
            Version = StreamFrameHeader.CurrentVersion,
            Encoding = _encoder.Encoding,
            Flags = FrameFlags.Complete | (frame.CursorVisible ? FrameFlags.CursorBakedIn : FrameFlags.None),
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
