using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using PcCore.Protocol;

namespace PcCore.Capabilities.Media;

/// <summary>Which sensors are being sent to the phone right now.</summary>
public readonly record struct LiveSensorState(bool Camera, bool Microphone)
{
    public static readonly LiveSensorState Idle = new(false, false);

    public bool Any => Camera || Microphone;

    /// <summary>
    /// The one phrase used by the tray, the tooltip and the on-screen badge alike.
    /// <para>
    /// Deliberately one function rather than three strings. The local indicator is the only thing
    /// standing between this feature and a hidden surveillance mode, and three places that describe
    /// the state independently are three places one of them can be wrong or forgotten.
    /// </para>
    /// </summary>
    public string Describe() => (Camera, Microphone) switch
    {
        (true, true) => "Nexus · Camera + Microphone LIVE",
        (true, false) => "Nexus · Camera LIVE",
        (false, true) => "Nexus · Microphone LIVE",
        _ => "",
    };
}

/// <summary>One captured frame on its way to the phone, before it is framed for the wire.</summary>
/// <param name="Payload">JPEG bytes, or signed 16-bit little-endian PCM.</param>
/// <param name="WidthOrSampleRate">Pixel width, or samples per second.</param>
/// <param name="HeightOrChannels">Pixel height, or channel count.</param>
public sealed record LiveFrame(byte[] Payload, int WidthOrSampleRate, int HeightOrChannels);

/// <summary>
/// A source of live frames.
/// <para>
/// Separate from <c>ISecurityEvidenceSource</c> even though both end up at the same physical camera.
/// Evidence is captured by the PC about an incident and written to disk; this is watched by the
/// owner and written nowhere. Sharing an interface would invite sharing a code path, and the first
/// time somebody added "just save a copy" it would apply to both.
/// </para>
/// </summary>
public interface ILiveSensorSource
{
    string Name { get; }

    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Produces frames until cancelled. Must stop touching the device promptly on cancellation, so
    /// the Windows in-use indicator goes out when the owner stops the stream.
    /// </summary>
    IAsyncEnumerable<LiveFrame> CaptureAsync(CancellationToken cancellationToken);
}

/// <summary>Marker for the camera, so both can be registered.</summary>
public interface ILiveCameraSource : ILiveSensorSource;

/// <summary>Marker for the microphone.</summary>
public interface ILiveAudioSource : ILiveSensorSource;

/// <summary>A source with no device behind it.</summary>
public sealed class UnavailableLiveSource : ILiveCameraSource, ILiveAudioSource
{
    public UnavailableLiveSource(string name) => Name = name;

    public string Name { get; }

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);

    public async IAsyncEnumerable<LiveFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await ValueTask.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}

/// <summary>Why a live stream could not start.</summary>
public enum LiveStartFailure
{
    None,
    NoDevice,
    AlreadyRunning,
    NotAuthorized,
}

public sealed record LiveStartOutcome(bool Started, LiveStartFailure Failure, string? Reason);

/// <summary>
/// One running sensor stream, and the channel its frames leave by.
/// <para>
/// Bounded at one frame and dropping the oldest, exactly as the desktop stream is: monitoring is
/// only useful if it is current, and a queue that grows under a slow link turns a live view into a
/// delayed one that gets steadily worse.
/// </para>
/// </summary>
public sealed class LiveSensorSession
{
    private readonly Channel<byte[]> _frames;

    internal LiveSensorSession(LiveSensorKind kind, string connectionId)
    {
        Kind = kind;
        ConnectionId = connectionId;

        _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    public LiveSensorKind Kind { get; }

    /// <summary>The connection this belongs to. A stream never outlives its connection.</summary>
    public string ConnectionId { get; }

    public ChannelReader<byte[]> Frames => _frames.Reader;

    internal void Publish(byte[] message) => _frames.Writer.TryWrite(message);

    internal void Complete() => _frames.Writer.TryComplete();
}

/// <summary>Where live frames are handed to whichever connection asked for them.</summary>
public interface ILiveSensorSink
{
    void AttachSensor(string connectionId, LiveSensorSession session);

    void DetachSensor(string connectionId, LiveSensorKind kind);
}

/// <summary>A sink that drops everything. The default when no transport is wired.</summary>
public sealed class NullLiveSensorSink : ILiveSensorSink
{
    public static readonly NullLiveSensorSink Instance = new();

    public void AttachSensor(string connectionId, LiveSensorSession session)
    {
    }

    public void DetachSensor(string connectionId, LiveSensorKind kind)
    {
    }
}

/// <summary>
/// Owner-initiated live camera and microphone monitoring.
/// <para>
/// <b>What this is for.</b> The owner, already authenticated to their own PC, wants to see or hear
/// the room it is in. That is a legitimate thing to want and a dangerous thing to build carelessly,
/// so the shape of it matters more than the features:
/// </para>
/// <list type="bullet">
/// <item>It only runs while a Full Remote session holds it. Authority is re-checked on a timer, not
/// only at start, so a session that lapses stops the camera rather than leaving it on.</item>
/// <item>It stops on disconnect, on PC Core shutting down, and on the owner saying so — from the
/// phone or from the PC's own tray.</item>
/// <item>Nothing is written to disk. There is no file path in this class and no option that adds
/// one; a recording is what the evidence subsystem does, deliberately and visibly.</item>
/// <item>While anything is live, the PC says so on screen and in the notification area. That is not
/// configurable down to nothing — the indicator mode can hide the badge for ordinary remote control,
/// and it cannot hide this.</item>
/// </list>
/// </summary>
public sealed class LiveSensorService : IAsyncDisposable
{
    private readonly ILiveCameraSource _camera;
    private readonly ILiveAudioSource _audio;
    private readonly ILogger<LiveSensorService> _logger;
    private readonly Lock _gate = new();

    private readonly Dictionary<LiveSensorKind, Running> _running = [];

    private ILiveSensorSink _sink = NullLiveSensorSink.Instance;
    private Func<string, bool> _isAuthorized = _ => false;
    private Action<LiveSensorState>? _onChanged;
    private Timer? _authorityWatch;
    private bool _disposed;

    public LiveSensorService(
        ILiveCameraSource camera,
        ILiveAudioSource audio,
        ILogger<LiveSensorService> logger)
    {
        _camera = camera;
        _audio = audio;
        _logger = logger;
    }

    /// <summary>What the tray and the indicator show.</summary>
    public LiveSensorState TrayState
    {
        get
        {
            lock (_gate)
            {
                return new LiveSensorState(
                    _running.ContainsKey(LiveSensorKind.Camera),
                    _running.ContainsKey(LiveSensorKind.Microphone));
            }
        }
    }

    /// <summary>
    /// Wires the transport, the authority check and the indicator.
    /// <para>
    /// Injected after construction because the transport is built later in the composition root and
    /// this service has to exist before it — the alternative is a circular dependency between the
    /// thing that captures and the thing that delivers.
    /// </para>
    /// </summary>
    public void Connect(
        ILiveSensorSink sink,
        Func<string, bool> isAuthorized,
        Action<LiveSensorState> onChanged)
    {
        lock (_gate)
        {
            _sink = sink;
            _isAuthorized = isAuthorized;
            _onChanged = onChanged;

            // Authority is not a fact you check once. A Full Remote session expires on its own
            // schedule, and the camera must go off when it does rather than when the owner next
            // happens to send a request.
            _authorityWatch ??= new Timer(_ => EnforceAuthority(), null, AuthorityInterval, AuthorityInterval);
        }

        Notify();
    }

    private static readonly TimeSpan AuthorityInterval = TimeSpan.FromSeconds(5);

    public async ValueTask<LiveStartOutcome> StartAsync(
        LiveSensorKind kind,
        string connectionId,
        CancellationToken cancellationToken)
    {
        if (kind is not (LiveSensorKind.Camera or LiveSensorKind.Microphone))
        {
            return new LiveStartOutcome(false, LiveStartFailure.NoDevice, "Unknown sensor.");
        }

        if (!_isAuthorized(connectionId))
        {
            // Belt and braces: the capability policy already refuses this action without Full
            // Remote. Checking again here means the service is safe to call from anywhere, and that
            // a future caller cannot reach it through a path that forgot.
            return new LiveStartOutcome(
                false, LiveStartFailure.NotAuthorized, "This session is not authorised for live monitoring.");
        }

        var source = Source(kind);

        if (!await source.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return new LiveStartOutcome(
                false, LiveStartFailure.NoDevice, $"This PC has no usable {Describe(kind)}.");
        }

        LiveSensorSession session;

        lock (_gate)
        {
            if (_running.ContainsKey(kind))
            {
                return new LiveStartOutcome(
                    false, LiveStartFailure.AlreadyRunning, $"The {Describe(kind)} is already live.");
            }

            session = new LiveSensorSession(kind, connectionId);
            var stopping = new CancellationTokenSource();
            var running = new Running(session, stopping);
            _running[kind] = running;

            running.Pump = Task.Run(() => PumpAsync(kind, session, source, stopping.Token));
        }

        _sink.AttachSensor(connectionId, session);

        _logger.LogInformation(
            "Live {Sensor} started for connection {ConnectionId}. The PC is showing an on-screen indicator.",
            Describe(kind),
            connectionId);

        Notify();
        return new LiveStartOutcome(true, LiveStartFailure.None, null);
    }

    public void Stop(LiveSensorKind kind, string reason)
    {
        Running? running;

        lock (_gate)
        {
            if (!_running.Remove(kind, out running))
            {
                return;
            }
        }

        Shutdown(kind, running, reason);
        Notify();
    }

    /// <summary>Stops everything. Used by the tray, by disconnection, and by shutdown.</summary>
    public void StopAll(string reason)
    {
        Running[] all;
        LiveSensorKind[] kinds;

        lock (_gate)
        {
            kinds = [.. _running.Keys];
            all = [.. _running.Values];
            _running.Clear();
        }

        for (var i = 0; i < all.Length; i++)
        {
            Shutdown(kinds[i], all[i], reason);
        }

        if (all.Length > 0)
        {
            Notify();
        }
    }

    /// <summary>Stops whatever a departing connection was watching.</summary>
    public void StopForConnection(string connectionId, string reason)
    {
        Running[] all;
        LiveSensorKind[] kinds;

        lock (_gate)
        {
            var matching = _running.Where(p => p.Value.Session.ConnectionId == connectionId).ToArray();

            if (matching.Length == 0)
            {
                return;
            }

            kinds = [.. matching.Select(p => p.Key)];
            all = [.. matching.Select(p => p.Value)];

            foreach (var kind in kinds)
            {
                _running.Remove(kind);
            }
        }

        for (var i = 0; i < all.Length; i++)
        {
            Shutdown(kinds[i], all[i], reason);
        }

        Notify();
    }

    /// <summary>
    /// Stops any stream whose session has lost Full Remote authority.
    /// <para>
    /// This is the check that makes "the owner authenticated" mean something over time rather than
    /// once. A phone that locked, backgrounded past its window, or was told to lock, stops being
    /// authorised — and the camera goes off without anybody having to ask it to.
    /// </para>
    /// </summary>
    private void EnforceAuthority()
    {
        try
        {
            string[] lapsed;

            lock (_gate)
            {
                lapsed = [.. _running.Values
                    .Select(r => r.Session.ConnectionId)
                    .Distinct(StringComparer.Ordinal)
                    .Where(id => !_isAuthorized(id))];
            }

            foreach (var connectionId in lapsed)
            {
                _logger.LogInformation(
                    "Live capture stopped: connection {ConnectionId} no longer holds an owner-authenticated session.",
                    connectionId);

                StopForConnection(connectionId, "the owner-authenticated session ended");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The live-capture authority check failed.");
        }
    }

    private async Task PumpAsync(
        LiveSensorKind kind,
        LiveSensorSession session,
        ILiveSensorSource source,
        CancellationToken cancellationToken)
    {
        ulong sequence = 0;

        try
        {
            await foreach (var frame in source.CaptureAsync(cancellationToken).ConfigureAwait(false))
            {
                var header = new LiveSensorFrameHeader
                {
                    Version = LiveSensorFrameHeader.CurrentVersion,
                    Kind = kind,
                    Sequence = ++sequence,
                    TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    WidthOrSampleRate = frame.WidthOrSampleRate,
                    HeightOrChannels = frame.HeightOrChannels,
                    PayloadLength = frame.Payload.Length,
                };

                session.Publish(header.ToMessage(frame.Payload));
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped, which is the ordinary way this ends.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live {Sensor} capture ended unexpectedly.", Describe(kind));

            // A source that died leaves the service claiming a stream that is not producing, so the
            // state is corrected rather than left optimistic.
            Stop(kind, "the capture device stopped");
        }
        finally
        {
            session.Complete();
        }
    }

    private void Shutdown(LiveSensorKind kind, Running running, string reason)
    {
        try
        {
            running.Stopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already gone.
        }

        try
        {
            _sink.DetachSensor(running.Session.ConnectionId, kind);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Detaching the live {Sensor} sink failed.", Describe(kind));
        }

        running.Stopping.Dispose();

        _logger.LogInformation("Live {Sensor} stopped: {Reason}.", Describe(kind), reason);
    }

    private void Notify()
    {
        Action<LiveSensorState>? handler;
        LiveSensorState state = TrayState;

        lock (_gate)
        {
            handler = _onChanged;
        }

        try
        {
            handler?.Invoke(state);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The live-capture indicator could not be updated.");
        }
    }

    private ILiveSensorSource Source(LiveSensorKind kind) =>
        kind == LiveSensorKind.Camera ? _camera : _audio;

    private static string Describe(LiveSensorKind kind) =>
        kind == LiveSensorKind.Camera ? "camera" : "microphone";

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        StopAll("PC Core is shutting down");

        Timer? watch;

        lock (_gate)
        {
            watch = _authorityWatch;
            _authorityWatch = null;
        }

        if (watch is not null)
        {
            await watch.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Running
    {
        public Running(LiveSensorSession session, CancellationTokenSource stopping)
        {
            Session = session;
            Stopping = stopping;
        }

        public LiveSensorSession Session { get; }

        public CancellationTokenSource Stopping { get; }

        public Task? Pump { get; set; }
    }
}
