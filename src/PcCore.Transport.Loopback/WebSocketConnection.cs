using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Transport.Loopback;

/// <summary>
/// One WebSocket client: carries the same request/response envelopes as <c>/rpc</c>, and is the
/// channel server-originated <see cref="EventEnvelope"/>s are pushed down.
/// <para>
/// This class knows how to move framed text and nothing else. Every decision about what a request
/// is allowed to do belongs to the capability layer.
/// </para>
/// </summary>
internal sealed class WebSocketConnection : IAsyncDisposable
{
    /// <summary>Cap on requests handled at once, so one client cannot spawn unbounded work.</summary>
    private const int MaxConcurrentRequests = 4;

    private const int ReceiveBufferSize = 8 * 1024;

    private readonly WebSocket _socket;
    private readonly ICapabilityDispatcher _dispatcher;
    private readonly CallerDescriptor _caller;
    private readonly ILogger _logger;
    private readonly int _maxMessageBytes;

    // A WebSocket permits only one send at a time; responses and events share this gate.
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentRequests, MaxConcurrentRequests);

    public WebSocketConnection(
        WebSocket socket,
        ICapabilityDispatcher dispatcher,
        CallerDescriptor caller,
        int maxMessageBytes,
        ILogger logger)
    {
        _socket = socket;
        _dispatcher = dispatcher;
        _caller = caller;
        _maxMessageBytes = maxMessageBytes;
        _logger = logger;
    }

    /// <summary>
    /// Starts forwarding published events to this client.
    /// <para>
    /// Separate from <see cref="RunAsync"/> so the caller can subscribe <em>before</em> publishing
    /// anything about this connection. Subscribing inside the receive loop would race: an event
    /// published between accept and the first await would be delivered to nobody.
    /// </para>
    /// </summary>
    public IDisposable SubscribeTo(IEventStream events, CancellationToken cancellationToken) =>
        // Send failures are swallowed inside SendAsync; a dead socket must not break the publisher.
        events.Subscribe(envelope => _ = SendAsync(ProtocolCodec.Encode(envelope), cancellationToken));

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferSize];
        var pending = new List<Task>();

        try
        {
            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveMessageAsync(buffer, cancellationToken).ConfigureAwait(false);

                if (message is null)
                {
                    break;
                }

                if (message.Length == 0)
                {
                    continue;
                }

                pending.RemoveAll(static task => task.IsCompleted);
                pending.Add(HandleMessageAsync(message, cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket {ConnectionId} closed unexpectedly.", _caller.ConnectionId);
        }
        finally
        {
            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "In-flight requests faulted while closing {ConnectionId}.", _caller.ConnectionId);
            }
        }
    }

    /// <summary>Reads one complete text message, or null when the peer closed the socket.</summary>
    private async Task<string?> ReceiveMessageAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();

        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                await CloseAsync(WebSocketCloseStatus.InvalidMessageType, "text frames only", cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            if (message.Length + result.Count > _maxMessageBytes)
            {
                await CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            message.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }

    private async Task HandleMessageAsync(string json, CancellationToken cancellationToken)
    {
        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var decoded = ProtocolCodec.DecodeRequest(json);

            var response = decoded.IsSuccess
                ? await _dispatcher.DispatchAsync(decoded.Request, _caller, cancellationToken).ConfigureAwait(false)
                : ResponseEnvelope.Fail(decoded.Id ?? string.Empty, null, decoded.Error);

            await SendAsync(ProtocolCodec.Encode(response), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle a WebSocket message on {ConnectionId}.", _caller.ConnectionId);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task SendAsync(string payload, CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                return;
            }

            await _socket.SendAsync(
                Encoding.UTF8.GetBytes(payload),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug("Send failed on {ConnectionId}; the client has gone away.", _caller.ConnectionId);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken cancellationToken)
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseAsync(status, description, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The peer is already gone; nothing useful to do.
        }
    }

    public ValueTask DisposeAsync()
    {
        _sendGate.Dispose();
        _concurrency.Dispose();
        return ValueTask.CompletedTask;
    }
}
