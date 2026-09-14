using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

using PcCore.Capabilities.Assistant;

namespace PcCore.Tests.TestSupport;

/// <summary>
/// PC Assistant's end of the bridge, scripted. It speaks the same line protocol the real one does, over
/// an in-memory duplex stream, so the PC Core half can be tested without a second program running.
/// The real PC Assistant is exercised against its own implementation in its own repository.
/// </summary>
public sealed class FakePcAssistant : IAssistantPipeConnector, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly Lock _gate = new();
    private readonly List<(string Method, JsonElement Params)> _requests = [];
    private Connection? _current;

    /// <summary>False makes the connector report that nothing is listening.</summary>
    public bool Running { get; set; } = true;

    /// <summary>An error code to answer <c>hello</c> with, such as <c>client_not_trusted</c>.</summary>
    public string? HelloError { get; set; }

    public int ProtocolVersion { get; set; } = 1;

    /// <summary>PC Assistant's word for Claude Code: ready, signed_out, not_installed, failed.</summary>
    public string Claude { get; set; } = "ready";

    /// <summary>PC Assistant's reason when Claude is not ready.</summary>
    public string? ClaudeDetail { get; set; }

    /// <summary>Answers a request, or throws <see cref="Refusal"/>. Unset methods get a plausible default.</summary>
    public Func<string, JsonElement, object?>? Handler { get; set; }

    public int Connections { get; private set; }

    public IReadOnlyList<(string Method, JsonElement Params)> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Requests other than the bridge's own bookkeeping.</summary>
    public IReadOnlyList<string> WorkRequests => Requests.Select(r => r.Method).Where(m => m is not ("hello" or "status")).ToList();

    public Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!Running)
        {
            throw new AssistantUnavailableException(AssistantAvailability.NotRunning, "PC Assistant is not running.");
        }

        var toServer = new Pipe();
        var toClient = new Pipe();

        var connection = new Connection(new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream()));
        connection.Loop = Task.Run(() => ServeAsync(connection));

        lock (_gate)
        {
            _current = connection;
            Connections++;
        }

        return Task.FromResult<Stream>(new DuplexStream(toClient.Reader.AsStream(), toServer.Writer.AsStream()));
    }

    /// <summary>Pushes an event, the way PC Assistant does when a task changes.</summary>
    public async Task PushAsync(string eventName, object data)
    {
        var connection = _current ?? throw new InvalidOperationException("Nothing is connected.");
        await connection.WriteAsync(JsonSerializer.Serialize(new { type = "event", @event = eventName, data }, Json)).ConfigureAwait(false);
    }

    /// <summary>PC Assistant closing: the pipe ends.</summary>
    public async Task CloseAsync()
    {
        var connection = _current;
        _current = null;

        if (connection is not null)
        {
            await connection.Stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ServeAsync(Connection connection)
    {
        using var reader = new StreamReader(connection.Stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var id = root.GetProperty("id").GetString();
                var method = root.GetProperty("method").GetString()!;
                var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;

                lock (_gate)
                {
                    _requests.Add((method, parameters));
                }

                object reply;

                try
                {
                    reply = new { type = "response", id, ok = true, result = Answer(method, parameters) };
                }
                catch (Refusal refusal)
                {
                    reply = new { type = "response", id, ok = false, error = new { code = refusal.Code, message = refusal.Message } };
                }

                await connection.WriteAsync(JsonSerializer.Serialize(reply, Json)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Closed.
        }
    }

    private object? Answer(string method, JsonElement parameters)
    {
        if (method == "hello")
        {
            if (HelloError is not null)
            {
                throw new Refusal(HelloError, "refused by the fake");
            }

            return new
            {
                protocol_version = ProtocolVersion,
                server = "pc-assistant",
                server_version = "0.9.0-test",
                claude = new { availability = Claude, version = "2.1-test", detail = ClaudeDetail },
            };
        }

        if (method == "status")
        {
            return new { claude = new { availability = Claude, version = "2.1-test", detail = ClaudeDetail }, running_tasks = 0, pending_approvals = 0 };
        }

        if (Handler is not null)
        {
            var answer = Handler(method, parameters);
            if (answer is not null)
            {
                return answer;
            }
        }

        return method switch
        {
            "tasks.list" => new { tasks = new[] { TaskShape(Guid.NewGuid(), "running") } },
            "tasks.get" => new
            {
                task = TaskShape(Guid.Parse(parameters.GetProperty("task_id").GetString()!), "completed"),
                messages = new[] { new { id = Guid.NewGuid(), role = "assistant", text = "hello", created_at = DateTimeOffset.UtcNow } },
                progress = Array.Empty<object>(),
                pending_approvals = Array.Empty<object>(),
            },
            "tasks.create" => new { task = TaskShape(Guid.NewGuid(), "running", parameters.GetProperty("mode").GetString()!) },
            "tasks.send" or "tasks.set_mode" => new { task = TaskShape(Guid.Parse(parameters.GetProperty("task_id").GetString()!), "running") },
            "tasks.stop" => new { was_running = true },
            "approvals.resolve" => new { resolved = true, outcome = parameters.GetProperty("decision").GetString() == "deny" ? "denied" : "allowed" },
            _ => throw new Refusal("unknown_method", "no such method"),
        };
    }

    public static object TaskShape(Guid id, string state, string mode = "auto") => new
    {
        id,
        title = "A task",
        kind = "general",
        mode,
        state,
        running = state is "running" or "waiting_for_approval",
        pending_approvals = 0,
        created_at = DateTimeOffset.UtcNow,
        updated_at = DateTimeOffset.UtcNow,
    };

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    public sealed class Refusal(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private sealed class Connection(DuplexStream stream)
    {
        private readonly SemaphoreSlim _write = new(1, 1);

        public DuplexStream Stream { get; } = stream;

        public Task? Loop { get; set; }

        public async Task WriteAsync(string line)
        {
            await _write.WaitAsync().ConfigureAwait(false);
            try
            {
                await Stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n")).ConfigureAwait(false);
                await Stream.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _write.Release();
            }
        }
    }

    /// <summary>Two one-way streams presented as one, which is what a named pipe is.</summary>
    private sealed class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => write.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => read.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => read.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => write.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => write.WriteAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                read.Dispose();
                write.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await read.DisposeAsync().ConfigureAwait(false);
            await write.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
