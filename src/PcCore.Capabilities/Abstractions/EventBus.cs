using Microsoft.Extensions.Logging;
using PcCore.Protocol;

namespace PcCore.Capabilities.Abstractions;

/// <summary>Emits server-originated events. Publishing never blocks the caller.</summary>
public interface IEventPublisher
{
    void Publish(string eventName, object? payload = null);
}

/// <summary>Receives server-originated events. Implemented by transports that can push.</summary>
public interface IEventStream
{
    IDisposable Subscribe(Action<EventEnvelope> handler);
}

/// <summary>
/// Decides, per event and per remote connection, whether an event outside the transport's own
/// allowlist may be delivered.
/// <para>
/// The remote transport's rule is an allowlist of event names, and for <c>security.*</c> the name is
/// enough. Some events are not: a PC Assistant transcript must reach only the connection that asked for
/// it, and only while that connection holds the owner's session — which is a question about the
/// connection at the moment of sending, not about the event. This is where that question is answered.
/// Nothing registered means nothing extra is delivered.
/// </para>
/// </summary>
public interface IEventAudience
{
    bool MayReceive(EventEnvelope envelope, CallerDescriptor caller);

    /// <summary>The connection has ended; forget anything held for it.</summary>
    void ConnectionClosed(string connectionId);
}

/// <summary>
/// A minimal in-process fan-out. v0.1 publishes exactly one event (<c>core.connected</c>) so the
/// channel is real and exercised rather than speculative; the <c>ai.*</c> events described in
/// SPEC.md 11 will use this unchanged.
/// </summary>
public sealed class EventBus : IEventPublisher, IEventStream
{
    private readonly List<Action<EventEnvelope>> _subscribers = [];
    private readonly Lock _gate = new();
    private readonly ILogger<EventBus> _logger;

    public EventBus(ILogger<EventBus> logger) => _logger = logger;

    public IDisposable Subscribe(Action<EventEnvelope> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_gate)
        {
            _subscribers.Add(handler);
        }

        return new Subscription(this, handler);
    }

    public void Publish(string eventName, object? payload = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("n"),
            EventName = eventName,
            Payload = payload,
            TimestampUtc = DateTimeOffset.UtcNow,
        };

        Action<EventEnvelope>[] snapshot;
        lock (_gate)
        {
            if (_subscribers.Count == 0)
            {
                return;
            }

            snapshot = [.. _subscribers];
        }

        foreach (var subscriber in snapshot)
        {
            try
            {
                subscriber(envelope);
            }
            catch (Exception ex)
            {
                // One broken subscriber (a closed socket, say) must not stop the others.
                _logger.LogWarning(ex, "Event subscriber threw while handling {EventName}", eventName);
            }
        }
    }

    private void Unsubscribe(Action<EventEnvelope> handler)
    {
        lock (_gate)
        {
            _subscribers.Remove(handler);
        }
    }

    private sealed class Subscription(EventBus bus, Action<EventEnvelope> handler) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            bus.Unsubscribe(handler);
        }
    }
}
