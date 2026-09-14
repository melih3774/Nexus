using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcCore.Capabilities.Assistant;

/// <summary>A stream to PC Assistant, already connected and already checked to be the trusted executable.</summary>
public interface IAssistantPipeConnector
{
    /// <summary>
    /// The owner-only pipe PC Assistant listens on while its Nexus bridge is turned on.
    /// </summary>
    /// <exception cref="AssistantUnavailableException">Nothing trusted is listening.</exception>
    Task<Stream> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>Why PC Assistant cannot be used, as a state the phone can show.</summary>
public sealed class AssistantUnavailableException(AssistantAvailability state, string detail, Exception? inner = null)
    : Exception(detail, inner)
{
    public AssistantAvailability State { get; } = state;
}

/// <summary>A refusal PC Assistant sent back, with its own code.</summary>
public sealed class AssistantRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// PC Assistant's bridge protocol, from the client side: one JSON object per line, requests matched
/// to responses by id, events handed to <see cref="EventReceived"/>.
/// <para>
/// Nothing in here knows what a task is. It moves envelopes and enforces the two limits that keep a
/// misbehaving peer from costing more than it should: a line may not grow without bound, and a
/// request may not wait forever.
/// </para>
/// </summary>
public sealed class AssistantPipeClient : IAsyncDisposable
{
    public const int ProtocolVersion = 1;

    /// <summary>
    /// The largest line accepted. PC Assistant bounds a task's transcript at about a million
    /// characters; JSON escaping and UTF-8 can multiply that, so this allows for the worst of both.
    /// </summary>
    public const int MaxLineBytes = 8 * 1024 * 1024;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 32,
    };

    private readonly Stream _stream;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _reader;
    private long _nextId;

    public AssistantPipeClient(Stream stream, TimeSpan requestTimeout)
    {
        _stream = stream;
        _timeout = requestTimeout;
    }

    /// <summary>Raised on the reading thread for every event line. Must not block.</summary>
    public event Action<string, JsonElement>? EventReceived;

    /// <summary>Completes when the connection has ended, for whatever reason.</summary>
    public Task Closed => _closed.Task;

    public bool IsOpen => !_closed.Task.IsCompleted;

    public void Start() => _reader ??= Task.Run(ReadLoopAsync);

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            throw new AssistantUnavailableException(AssistantAvailability.NotRunning, "PC Assistant closed the connection.");
        }

        var id = Interlocked.Increment(ref _nextId).ToString(global::System.Globalization.CultureInfo.InvariantCulture);
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;

        try
        {
            var line = JsonSerializer.Serialize(new WireRequest(id, method, parameters ?? new { }), Json) + "\n";

            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            JsonElement response;
            try
            {
                response = await waiter.Task.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new AssistantUnavailableException(
                    AssistantAvailability.Unavailable, "PC Assistant did not answer in time.", ex);
            }

            if (response.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                return response.TryGetProperty("result", out var result) ? result : default;
            }

            var code = "internal_error";
            var message = "PC Assistant refused the request.";

            if (response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : code;
                message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : message;
            }

            throw new AssistantRequestException(code, message);
        }
        catch (IOException ex)
        {
            Close();
            throw new AssistantUnavailableException(AssistantAvailability.NotRunning, "PC Assistant closed the connection.", ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new AssistantUnavailableException(AssistantAvailability.NotRunning, "PC Assistant closed the connection.", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task<T> RequestAsync<T>(string method, object? parameters, CancellationToken cancellationToken)
    {
        var result = await RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);

        try
        {
            return result.Deserialize<T>(Json)
                   ?? throw new AssistantUnavailableException(AssistantAvailability.Unavailable, $"PC Assistant sent no result for {method}.");
        }
        catch (JsonException ex)
        {
            throw new AssistantUnavailableException(
                AssistantAvailability.Incompatible, $"PC Assistant's answer to {method} was not in the expected shape.", ex);
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream();

        try
        {
            while (!_closing.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buffer, _closing.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        continue;
                    }

                    line.Write(buffer, start, i - start);
                    Dispatch(line.GetBuffer().AsSpan(0, (int)line.Length));
                    line.SetLength(0);
                    start = i + 1;
                }

                line.Write(buffer, start, read - start);

                if (line.Length > MaxLineBytes)
                {
                    // A peer that stops sending newlines is not a peer to keep reading from.
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Closed.
        }
        finally
        {
            Close();
        }
    }

    private void Dispatch(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            return;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type))
        {
            return;
        }

        if (type.GetString() == "event")
        {
            if (root.TryGetProperty("event", out var name) && name.ValueKind == JsonValueKind.String &&
                root.TryGetProperty("data", out var data))
            {
                try
                {
                    EventReceived?.Invoke(name.GetString()!, data);
                }
                catch (Exception)
                {
                    // A subscriber's failure must not end the connection.
                }
            }

            return;
        }

        var id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()
            : null;

        if (id is not null && _pending.TryGetValue(id, out var waiter))
        {
            waiter.TrySetResult(root);
            return;
        }

        if (id is null)
        {
            // PC Assistant answers a refused connection before it has read an id: that answer belongs
            // to whatever is waiting, which on a fresh connection is hello.
            foreach (var pending in _pending.Values)
            {
                pending.TrySetResult(root);
            }
        }
    }

    private void Close()
    {
        if (!_closed.TrySetResult())
        {
            return;
        }

        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new AssistantUnavailableException(
                AssistantAvailability.NotRunning, "PC Assistant closed the connection."));
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _closing.Cancel(); } catch (ObjectDisposedException) { }

        await _stream.DisposeAsync().ConfigureAwait(false);

        if (_reader is not null)
        {
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception) { /* closing regardless */ }
        }

        Close();
    }

    private sealed record WireRequest(string Id, string Method, object Params)
    {
        public string Type => "request";
    }
}
