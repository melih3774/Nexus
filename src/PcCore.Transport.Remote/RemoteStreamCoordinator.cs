using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Indicator;
using PcCore.Capabilities.Input;
using PcCore.Capabilities.Media;
using PcCore.Capabilities.Screen;
using PcCore.Protocol;

namespace PcCore.Transport.Remote;

/// <summary>
/// A connection that can carry binary frames.
/// <para>
/// Implemented by <c>RemoteConnection</c>. It exists so the coordinator can hand a stream to
/// "whoever is on the other end of this connection" without knowing what a WebSocket is.
/// </para>
/// </summary>
internal interface IFrameSink
{
    /// <summary>Starts pumping this session's frames to the peer. Replaces any previous session.</summary>
    void Attach(DesktopStreamSession session);

    /// <summary>Stops pumping. The session itself is disposed by the coordinator.</summary>
    void Detach();

    /// <summary>Starts pumping a live sensor stream. Independent of the desktop stream.</summary>
    void AttachSensor(LiveSensorSession session);

    /// <summary>Stops pumping one live sensor.</summary>
    void DetachSensor(LiveSensorKind kind);
}

/// <summary>
/// Joins a capability call to the connection that will carry its frames.
/// <para>
/// <c>screen.stream.start</c> arrives at the dispatcher like any other action, knowing nothing about
/// the socket it came from beyond a connection id. This maps that id back to the live connection,
/// which is what lets the capability layer stay free of transport concepts while still producing
/// something that only makes sense on a connection able to send binary.
/// </para>
/// <para>
/// A caller with no registered sink — the loopback transport, or an internal call — is refused
/// rather than quietly started: a stream nobody can receive would spend capture and encoding
/// producing frames for a channel with no reader.
/// </para>
/// </summary>
public sealed class RemoteStreamCoordinator : IStreamCoordinator, IIndicatorActivityProbe, ILiveSensorSink
{
    private readonly DesktopStreamService _streams;
    private readonly RemoteIndicatorService _indicator;
    private readonly IInputService _input;
    private readonly ILogger<RemoteStreamCoordinator> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IFrameSink> _sinks = new(StringComparer.Ordinal);

    public RemoteStreamCoordinator(
        DesktopStreamService streams,
        RemoteIndicatorService indicator,
        IInputService input,
        ILogger<RemoteStreamCoordinator> logger)
    {
        _streams = streams;
        _indicator = indicator;
        _input = input;
        _logger = logger;
    }

    public int Viewers => _streams.ActiveStreams;

    public int InputSessions => _input.Active.Count;

    internal void Register(string connectionId, IFrameSink sink)
    {
        lock (_gate)
        {
            _sinks[connectionId] = sink;
        }
    }

    internal void Unregister(string connectionId)
    {
        lock (_gate)
        {
            _sinks.Remove(connectionId);
        }
    }

    /// <summary>
    /// Set by the composition root so a departing connection can stop whatever it was watching.
    /// <para>
    /// A property rather than a constructor argument because the live sensor service and this both
    /// have to exist before either can be told about the other; the alternative is a cycle.
    /// </para>
    /// </summary>
    public LiveSensorService? LiveSensors { get; set; }

    /// <summary>Hands a live sensor stream to the connection that asked for it.</summary>
    public void AttachSensor(string connectionId, LiveSensorSession session)
    {
        IFrameSink? sink;

        lock (_gate)
        {
            _sinks.TryGetValue(connectionId, out sink);
        }

        if (sink is null)
        {
            _logger.LogWarning(
                "A live {Sensor} stream was started for connection {ConnectionId}, which is no longer here.",
                session.Kind,
                connectionId);
            return;
        }

        sink.AttachSensor(session);
    }

    public void DetachSensor(string connectionId, LiveSensorKind kind)
    {
        IFrameSink? sink;

        lock (_gate)
        {
            _sinks.TryGetValue(connectionId, out sink);
        }

        sink?.DetachSensor(kind);
    }

    /// <summary>Stops any live sensor a departing connection had open.</summary>
    internal void StopLiveSensorsFor(string connectionId) =>
        LiveSensors?.StopForConnection(connectionId, "the phone disconnected");

    public async Task<DesktopStreamSession> StartAsync(
        CallerDescriptor caller,
        int? displayIndex,
        CancellationToken cancellationToken)
    {
        IFrameSink? sink;

        lock (_gate)
        {
            _sinks.TryGetValue(caller.ConnectionId, out sink);
        }

        if (sink is null)
        {
            throw new CapabilityException(
                ErrorCodes.Unsupported,
                "Live streaming needs a connection that can carry binary frames. "
                + "Use the remote transport rather than the local test page.");
        }

        // Replaces any stream this connection already had, so a client that lost track of its own
        // stream can simply start another instead of being told it already has one.
        sink.Detach();
        await _streams.StopAsync(caller.ConnectionId).ConfigureAwait(false);

        var session = _streams.Start(caller.ConnectionId, displayIndex);
        sink.Attach(session);

        _indicator.SetViewers(_streams.ActiveStreams);

        return session;
    }

    public Task<StreamStatistics?> StopAsync(CallerDescriptor caller, CancellationToken cancellationToken) =>
        StopForConnectionAsync(caller.ConnectionId);

    /// <summary>
    /// Stops whatever a connection was streaming.
    /// <para>
    /// Called by <c>screen.stream.stop</c> and — more importantly — whenever a connection ends,
    /// including when nobody asked it to. A capture loop that outlived its viewer would keep a
    /// display duplicated and a GPU busy for a phone that is no longer there.
    /// </para>
    /// </summary>
    internal async Task<StreamStatistics?> StopForConnectionAsync(string connectionId)
    {
        IFrameSink? sink;

        lock (_gate)
        {
            _sinks.TryGetValue(connectionId, out sink);
        }

        sink?.Detach();

        var stats = await _streams.StopAsync(connectionId).ConfigureAwait(false);

        if (stats is not null)
        {
            _logger.LogDebug("Stopped the stream for connection {ConnectionId}.", connectionId);
            _indicator.SetViewers(_streams.ActiveStreams);
        }

        return stats;
    }
}
