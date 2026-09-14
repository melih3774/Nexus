using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PcAssistant.Core.Approvals;
using PcAssistant.Core.Bridge;
using PcAssistant.Core.Model;
using PcAssistant.Core.Tasks;

namespace PcAssistant.Core.NexusBridge;

/// <summary>
/// One connected Nexus client, already verified by whoever accepted the connection.
///
/// It speaks to <see cref="AssistantService"/> and the approval broker exactly as the desktop window
/// does, which is the point: a task started from the phone is an ordinary task in the same database,
/// visible and controllable here, and an approval answered from the phone resolves the same card the
/// desktop shows. There is no second engine and no second copy of any state.
///
/// Nothing here decides anything about Claude, tools or policy. The only rule it adds is the one the
/// phone needs and the desktop does not: administrator approvals are not answerable from here.
/// </summary>
public sealed class NexusBridgeSession : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly AssistantService _service;
    private readonly Func<CancellationToken, Task<ClaudeStatus>> _claudeStatus;
    private readonly Action<string>? _log;

    private readonly Channel<string> _outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });

    /// <summary>Which task each pending approval belongs to, so a resolution can say so.</summary>
    private readonly ConcurrentDictionary<Guid, Guid> _approvalTasks = new();

    private readonly CancellationTokenSource _stop = new();
    private int _helloDone;
    private int _subscribed;

    public NexusBridgeSession(
        Stream stream,
        AssistantService service,
        Func<CancellationToken, Task<ClaudeStatus>> claudeStatus,
        Action<string>? log = null)
    {
        _stream = stream;
        _service = service;
        _claudeStatus = claudeStatus;
        _log = log;
    }

    public static string ServerVersion { get; } =
        typeof(NexusBridgeSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "0";

    /// <summary>Serves the connection until the client leaves or <paramref name="cancellationToken"/> ends it.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var token = linked.Token;

        var writer = Task.Run(() => WriteLoopAsync(token), CancellationToken.None);
        var inFlight = new List<Task>();

        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await ReadLineAsync(token).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;

                inFlight.RemoveAll(t => t.IsCompleted);
                inFlight.Add(HandleLineAsync(line, token));
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        catch (IOException)
        {
            // The client went away.
        }
        catch (ObjectDisposedException)
        {
            // Closed underneath.
        }
        finally
        {
            Unsubscribe();
            try { await Task.WhenAll(inFlight).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception) { /* a request that failed on the way out has nobody to tell */ }

            _outbound.Writer.TryComplete();
            try { await writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception) { /* the pipe is going regardless */ }
        }
    }

    /// <summary>
    /// Refuses a client before it has said anything useful. Used by the listener when the caller is
    /// not the Nexus the owner trusted: the answer is sent as a reply to the client's first request,
    /// so Nexus can tell the owner why rather than reporting a dead pipe.
    /// </summary>
    public static async Task RefuseAsync(Stream stream, string code, string message, CancellationToken cancellationToken)
    {
        string? id = null;
        try
        {
            var first = await ReadLineAsync(stream, cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
            if (first is not null) id = TryReadId(first);
        }
        catch (Exception)
        {
            // Nothing readable; answer anyway.
        }

        var reply = JsonSerializer.Serialize(new BridgeResponse { Id = id, Ok = false, Error = new BridgeError(code, message) },
            NexusBridgeProtocol.Json) + "\n";

        try
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(reply), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Gone already.
        }
    }

    // ------------------------------------------------------------ requests

    private async Task HandleLineAsync(string line, CancellationToken token)
    {
        string? id = null;
        BridgeResponse response;

        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                response = Fail(null, NexusBridgeProtocol.Errors.InvalidRequest, "A request must be a JSON object.");
            }
            else
            {
                id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()
                    : null;

                var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
                var parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement.Clone() : default;

                response = type != "request" || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(method)
                    ? Fail(id, NexusBridgeProtocol.Errors.InvalidRequest, "Expected type \"request\" with an id and a method.")
                    : await DispatchAsync(id, method, parameters, token).ConfigureAwait(false);
            }
        }
        catch (JsonException)
        {
            response = Fail(id, NexusBridgeProtocol.Errors.InvalidRequest, "That line is not valid JSON.");
        }
        catch (BridgeException ex)
        {
            response = Fail(id, ex.Code, ex.Message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // Logged here, not sent: an exception message can carry paths and internals.
            _log?.Invoke("nexus bridge request failed: " + ex.GetType().Name + ": " + ex.Message);
            response = Fail(id, NexusBridgeProtocol.Errors.Internal, "PC Assistant could not complete that request.");
        }

        await EnqueueAsync(response, token).ConfigureAwait(false);
    }

    private async Task<BridgeResponse> DispatchAsync(string id, string method, JsonElement parameters, CancellationToken token)
    {
        if (method == NexusBridgeProtocol.Methods.Hello)
            return Ok(id, await HelloAsync(parameters, token).ConfigureAwait(false));

        if (Volatile.Read(ref _helloDone) == 0)
            throw new BridgeException(NexusBridgeProtocol.Errors.HelloRequired, "Say hello first.");

        return method switch
        {
            NexusBridgeProtocol.Methods.Status => Ok(id, await StatusAsync(token).ConfigureAwait(false)),
            NexusBridgeProtocol.Methods.TasksList => Ok(id, ListTasks(parameters)),
            NexusBridgeProtocol.Methods.TasksGet => Ok(id, GetTask(parameters)),
            NexusBridgeProtocol.Methods.TasksCreate => Ok(id, CreateTask(parameters)),
            NexusBridgeProtocol.Methods.TasksSend => Ok(id, SendToTask(parameters)),
            NexusBridgeProtocol.Methods.TasksStop => Ok(id, StopTask(parameters)),
            NexusBridgeProtocol.Methods.TasksSetMode => Ok(id, SetMode(parameters)),
            NexusBridgeProtocol.Methods.ApprovalsResolve => Ok(id, ResolveApproval(parameters)),
            _ => throw new BridgeException(NexusBridgeProtocol.Errors.UnknownMethod, $"PC Assistant has no method '{method}'."),
        };
    }

    private async Task<HelloResult> HelloAsync(JsonElement parameters, CancellationToken token)
    {
        var version = parameters.ValueKind == JsonValueKind.Object
                      && parameters.TryGetProperty("protocol_version", out var v)
                      && v.ValueKind == JsonValueKind.Number
                      && v.TryGetInt32(out var parsed)
            ? parsed
            : 0;

        if (version != NexusBridgeProtocol.Version)
        {
            throw new BridgeException(NexusBridgeProtocol.Errors.UnsupportedProtocol,
                $"PC Assistant {ServerVersion} speaks bridge protocol {NexusBridgeProtocol.Version}; the client asked for {version}.");
        }

        var claude = await ClaudeAsync(token).ConfigureAwait(false);

        if (Interlocked.Exchange(ref _helloDone, 1) == 0) Subscribe();

        return new HelloResult
        {
            ProtocolVersion = NexusBridgeProtocol.Version,
            Server = "pc-assistant",
            ServerVersion = ServerVersion,
            Claude = claude,
        };
    }

    private async Task<StatusResult> StatusAsync(CancellationToken token)
    {
        var claude = await ClaudeAsync(token).ConfigureAwait(false);
        return new StatusResult(claude,
            _service.Tasks.Count(t => _service.IsRunning(t.Id)),
            _service.Approvals.Pending.Count);
    }

    private async Task<BridgeClaudeStatus> ClaudeAsync(CancellationToken token)
    {
        try
        {
            var status = await _claudeStatus(token).ConfigureAwait(false);
            // The reason travels with a not-ready answer: "signed out" and "signed in but refused by the
            // account" need different things from the owner. Ready carries none; its detail is where the
            // executable was found, which Nexus has no use for.
            var detail = status.Availability == ClaudeAvailability.Ready
                ? null
                : NexusBridgeProtocol.Cap(string.IsNullOrWhiteSpace(status.Detail) ? status.DisplayText : status.Detail, 400).Text;
            return new BridgeClaudeStatus(NexusBridgeProtocol.Claude(status.Availability), status.Version, detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BridgeClaudeStatus("unknown", null, "PC Assistant could not check Claude Code.");
        }
    }

    private TaskListResult ListTasks(JsonElement parameters)
    {
        var limit = Math.Clamp(OptionalInt(parameters, "limit") ?? 50, 1, 200);
        var pending = _service.Approvals.Pending;
        return new TaskListResult(_service.Tasks.Take(limit).Select(t => Describe(t, pending)).ToList());
    }

    private TaskDetailResult GetTask(JsonElement parameters)
    {
        var task = RequireTask(parameters);
        var pending = _service.Approvals.Pending;

        var budget = NexusBridgeProtocol.TranscriptBudgetChars;
        var omitted = false;

        // Newest first until the budget runs out, then back into order. A long conversation loses its
        // beginning on the phone, never its end: the end is what the owner opened it to see.
        var messages = new List<BridgeMessage>();
        foreach (var message in _service.MessagesFor(task.Id).Reverse())
        {
            var wire = Describe(message);
            if (budget - wire.Text.Length < 0 || messages.Count >= 400)
            {
                omitted = true;
                break;
            }

            budget -= wire.Text.Length;
            messages.Add(wire);
        }

        messages.Reverse();

        var progress = _service.EventsFor(task.Id)
            .Where(NexusBridgeProtocol.IsProgress)
            .ToList();

        if (progress.Count > 300)
        {
            omitted = true;
            progress = progress[^300..];
        }

        return new TaskDetailResult
        {
            Task = Describe(task, pending),
            Messages = messages,
            Progress = progress.Select(Describe).ToList(),
            PendingApprovals = pending.Where(p => p.TaskId == task.Id).Select(Describe).ToList(),
            OlderOmitted = omitted,
        };
    }

    private TaskResult CreateTask(JsonElement parameters)
    {
        var kind = OptionalString(parameters, "kind") ?? "general";
        if (!string.Equals(kind, "general", StringComparison.OrdinalIgnoreCase))
        {
            // A workspace task needs a folder chosen and validated against the owner's roots, which is
            // a decision for the desktop, not for a remote form.
            throw new BridgeException(NexusBridgeProtocol.Errors.Unsupported, "Only General tasks can be created through the bridge.");
        }

        var mode = NexusBridgeProtocol.ParseMode(OptionalString(parameters, "mode"))
                   ?? throw new BridgeException(NexusBridgeProtocol.Errors.InvalidParams, "mode must be \"auto\" or \"ask\".");
        var prompt = RequirePrompt(parameters, "prompt");

        var task = _service.CreateTask(new TaskCreationRequest
        {
            Kind = TaskKind.General,
            Mode = mode,
            FirstMessage = prompt,
        });

        StartTurn(task.Id, prompt);
        return new TaskResult(Describe(task, _service.Approvals.Pending));
    }

    private TaskResult SendToTask(JsonElement parameters)
    {
        var task = RequireTask(parameters);
        var text = RequirePrompt(parameters, "text");

        if (_service.IsRunning(task.Id))
            throw new BridgeException(NexusBridgeProtocol.Errors.TaskBusy, "That task is still working. Stop it first, or wait for it to finish.");

        StartTurn(task.Id, text);
        return new TaskResult(Describe(task, _service.Approvals.Pending));
    }

    /// <summary>
    /// Starts a turn and returns at once. The answer arrives as events, exactly as it reaches the
    /// desktop window; holding a request open for the length of a Claude turn would tie the pipe to
    /// the slowest thing in the system.
    /// </summary>
    private void StartTurn(Guid taskId, string prompt)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _service.SendAsync(taskId, prompt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // SendAsync reports turn failures on the task itself; only a refusal to start lands here.
                _log?.Invoke("nexus bridge turn did not start: " + ex.Message);
            }
        });
    }

    private StopResult StopTask(JsonElement parameters)
    {
        var task = RequireTask(parameters);
        var running = _service.IsRunning(task.Id);
        _service.Stop(task.Id);
        return new StopResult(running);
    }

    private TaskResult SetMode(JsonElement parameters)
    {
        var task = RequireTask(parameters);
        var mode = NexusBridgeProtocol.ParseMode(OptionalString(parameters, "mode"))
                   ?? throw new BridgeException(NexusBridgeProtocol.Errors.InvalidParams, "mode must be \"auto\" or \"ask\".");

        _service.SetMode(task.Id, mode);
        return new TaskResult(Describe(task, _service.Approvals.Pending));
    }

    private ResolveResult ResolveApproval(JsonElement parameters)
    {
        var id = OptionalGuid(parameters, "approval_id")
                 ?? throw new BridgeException(NexusBridgeProtocol.Errors.InvalidParams, "approval_id is required.");

        var decision = OptionalString(parameters, "decision");
        bool allow = decision switch
        {
            "allow_once" => true,
            "deny" => false,
            // "Allow for this task" exists on the desktop and deliberately not here.
            _ => throw new BridgeException(NexusBridgeProtocol.Errors.InvalidParams, "decision must be \"allow_once\" or \"deny\"."),
        };

        var request = _service.Approvals.Pending.FirstOrDefault(p => p.Id == id)
                      ?? throw new BridgeException(NexusBridgeProtocol.Errors.NotFound,
                          "That approval is no longer waiting. It was answered, timed out, or its task stopped.");

        if (!NexusBridgeProtocol.IsRemotelyResolvable(request))
        {
            throw new BridgeException(NexusBridgeProtocol.Errors.RequiresLocalApproval,
                "Administrator operations are approved on the PC itself.");
        }

        var resolved = _service.Approvals.Resolve(id, allow);
        return new ResolveResult(resolved, resolved ? (allow ? "allowed" : "denied") : "not_pending");
    }

    // -------------------------------------------------------------- events

    private void Subscribe()
    {
        foreach (var pending in _service.Approvals.Pending) _approvalTasks[pending.Id] = pending.TaskId;

        _service.TaskChanged += OnTaskChanged;
        _service.MessageAppended += OnMessageAppended;
        _service.EventAppended += OnEventAppended;
        _service.Approvals.ApprovalRequested += OnApprovalRequested;
        _service.Approvals.ApprovalResolved += OnApprovalResolved;
        Volatile.Write(ref _subscribed, 1);
    }

    private void Unsubscribe()
    {
        if (Interlocked.Exchange(ref _subscribed, 0) == 0) return;

        _service.TaskChanged -= OnTaskChanged;
        _service.MessageAppended -= OnMessageAppended;
        _service.EventAppended -= OnEventAppended;
        _service.Approvals.ApprovalRequested -= OnApprovalRequested;
        _service.Approvals.ApprovalResolved -= OnApprovalResolved;
    }

    private void OnTaskChanged(object? sender, AssistantTask task) =>
        Publish(NexusBridgeProtocol.Events.TaskChanged, new TaskResult(Describe(task, _service.Approvals.Pending)));

    private void OnMessageAppended(object? sender, TaskMessage message) =>
        Publish(NexusBridgeProtocol.Events.MessageAppended, new MessageAppendedEvent(message.TaskId, Describe(message)));

    private void OnEventAppended(object? sender, AssistantEvent evt)
    {
        if (NexusBridgeProtocol.IsProgress(evt))
            Publish(NexusBridgeProtocol.Events.TaskProgress, new ProgressEvent(evt.TaskId, Describe(evt)));
    }

    private void OnApprovalRequested(object? sender, ApprovalRequest request)
    {
        _approvalTasks[request.Id] = request.TaskId;
        Publish(NexusBridgeProtocol.Events.ApprovalRequested, Describe(request));
    }

    private void OnApprovalResolved(object? sender, ApprovalResolution resolution)
    {
        Guid? task = _approvalTasks.TryRemove(resolution.RequestId, out var found) ? found : null;
        Publish(NexusBridgeProtocol.Events.ApprovalResolved,
            new ApprovalResolvedEvent(resolution.RequestId, task, NexusBridgeProtocol.Outcome(resolution.Outcome)));
    }

    /// <summary>
    /// Events come from whichever thread raised them and must never block it: that thread may be the
    /// one running a Claude turn. A client too slow to keep up for four thousand events is closed
    /// rather than allowed to back pressure into the task engine.
    /// </summary>
    private void Publish(string name, object data)
    {
        if (Volatile.Read(ref _subscribed) == 0) return;

        string line;
        try
        {
            line = JsonSerializer.Serialize(new BridgeEvent(name, data), NexusBridgeProtocol.Json);
        }
        catch (Exception ex)
        {
            _log?.Invoke("nexus bridge event could not be encoded: " + ex.Message);
            return;
        }

        if (!_outbound.Writer.TryWrite(line))
        {
            _log?.Invoke("nexus bridge client fell too far behind; closing it.");
            try { _stop.Cancel(); } catch (ObjectDisposedException) { /* already ended */ }
        }
    }

    // ------------------------------------------------------------ plumbing

    private async Task EnqueueAsync(BridgeResponse response, CancellationToken token)
    {
        var line = JsonSerializer.Serialize(response, NexusBridgeProtocol.Json);
        try
        {
            await _outbound.Writer.WriteAsync(line, token).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // Closing.
        }
    }

    private async Task WriteLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var line in _outbound.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await _stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await _stream.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The client is gone or the session is ending.
        }
        finally
        {
            try { _stop.Cancel(); } catch (ObjectDisposedException) { /* already ended */ }
        }
    }

    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _bufferStart;
    private int _bufferEnd;

    private Task<string?> ReadLineAsync(CancellationToken token) => ReadLineAsync(_stream, token, this);

    private static Task<string?> ReadLineAsync(Stream stream, CancellationToken token) => ReadLineAsync(stream, token, null);

    /// <summary>A bounded line reader: a client that never sends a newline cannot grow memory without limit.</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token, NexusBridgeSession? state)
    {
        var buffer = state?._buffer ?? new byte[4096];
        var start = state?._bufferStart ?? 0;
        var end = state?._bufferEnd ?? 0;
        using var line = new MemoryStream();

        try
        {
            while (true)
            {
                for (var i = start; i < end; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;

                    line.Write(buffer, start, i - start);
                    start = i + 1;
                    return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
                }

                line.Write(buffer, start, end - start);
                if (line.Length > NexusBridgeProtocol.MaxRequestBytes)
                    throw new IOException("A bridge message exceeded the size limit.");

                start = 0;
                end = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (end == 0) return null;
            }
        }
        finally
        {
            if (state is not null)
            {
                state._bufferStart = start;
                state._bufferEnd = end;
            }
        }
    }

    private static string? TryReadId(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("id", out var id)
                   && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private AssistantTask RequireTask(JsonElement parameters)
    {
        var id = OptionalGuid(parameters, "task_id")
                 ?? throw new BridgeException(NexusBridgeProtocol.Errors.InvalidParams, "task_id is required.");
        return _service.Find(id) ?? throw new BridgeException(NexusBridgeProtocol.Errors.NotFound, "There is no such task.");
    }

    private static string RequirePrompt(JsonElement parameters, string name)
    {
        var text = OptionalString(parameters, name)?.Trim();
        if (string.IsNullOrEmpty(text))
            throw new BridgeException(NexusBridgeProtocol.Errors.InvalidParams, $"{name} is required.");
        if (text.Length > NexusBridgeProtocol.MaxPromptChars)
            throw new BridgeException(NexusBridgeProtocol.Errors.InvalidParams,
                $"{name} is limited to {NexusBridgeProtocol.MaxPromptChars} characters.");
        return text;
    }

    private static string? OptionalString(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? OptionalInt(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static Guid? OptionalGuid(JsonElement parameters, string name) =>
        Guid.TryParse(OptionalString(parameters, name), out var parsed) ? parsed : null;

    private BridgeTask Describe(AssistantTask task, IReadOnlyList<ApprovalRequest> pending) => new()
    {
        Id = task.Id,
        Title = task.Title,
        Kind = NexusBridgeProtocol.Kind(task.Kind),
        Mode = NexusBridgeProtocol.Mode(task.Mode),
        State = NexusBridgeProtocol.State(task.State),
        Running = _service.IsRunning(task.Id),
        PendingApprovals = pending.Count(p => p.TaskId == task.Id),
        WorkspaceName = task.WorkspacePath is { Length: > 0 } path ? Path.GetFileName(path.TrimEnd('\\', '/')) : null,
        CreatedAt = task.CreatedAt.ToUniversalTime(),
        UpdatedAt = task.UpdatedAt.ToUniversalTime(),
        LastError = NexusBridgeProtocol.Cap(task.LastError, NexusBridgeProtocol.MaxDetailChars).Text is { Length: > 0 } e ? e : null,
    };

    private static BridgeMessage Describe(TaskMessage message)
    {
        var (text, truncated) = NexusBridgeProtocol.Cap(message.Text, NexusBridgeProtocol.MaxMessageChars);
        return new BridgeMessage
        {
            Id = message.Id,
            Role = NexusBridgeProtocol.Role(message.Role),
            Text = text,
            Truncated = truncated,
            CreatedAt = message.CreatedAt.ToUniversalTime(),
        };
    }

    private static BridgeProgress Describe(AssistantEvent evt) => new()
    {
        Id = evt.Id,
        Kind = NexusBridgeProtocol.EventKind(evt.Kind),
        Title = NexusBridgeProtocol.Cap(evt.Title, 500).Text,
        Detail = evt.Detail is null ? null : NexusBridgeProtocol.Cap(evt.Detail, NexusBridgeProtocol.MaxDetailChars).Text,
        ToolName = evt.ToolName,
        CreatedAt = evt.CreatedAt.ToUniversalTime(),
    };

    private static BridgeApproval Describe(ApprovalRequest request) => new()
    {
        Id = request.Id,
        TaskId = request.TaskId,
        TaskTitle = request.TaskTitle,
        ToolName = request.ToolName,
        Action = request.Action,
        Target = NexusBridgeProtocol.Cap(request.Target, 2_000).Text,
        Reason = NexusBridgeProtocol.Cap(request.Reason, 2_000).Text,
        Category = NexusBridgeProtocol.Category(request.Category),
        RemoteResolvable = NexusBridgeProtocol.IsRemotelyResolvable(request),
        CreatedAt = request.CreatedAt.ToUniversalTime(),
    };

    private static BridgeResponse Ok(string id, object result) => new() { Id = id, Ok = true, Result = result };

    private static BridgeResponse Fail(string? id, string code, string message) =>
        new() { Id = id, Ok = false, Error = new BridgeError(code, message) };

    public ValueTask DisposeAsync()
    {
        Unsubscribe();
        _outbound.Writer.TryComplete();

        // Cancelled rather than disposed: a write loop finishing on another thread may still reach it.
        try { _stop.Cancel(); } catch (ObjectDisposedException) { /* already ended */ }
        return ValueTask.CompletedTask;
    }

    private sealed class BridgeException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
