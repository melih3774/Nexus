using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using PcAssistant.Core.Approvals;
using PcAssistant.Core.Bridge;
using PcAssistant.Core.Model;
using PcAssistant.Core.Persistence;
using PcAssistant.Core.Workspaces;

namespace PcAssistant.Core.Tasks;

public sealed record TaskCreationRequest
{
    public required TaskKind Kind { get; init; }
    public required AssistantMode Mode { get; init; }
    public string? WorkspacePath { get; init; }
    public required string FirstMessage { get; init; }
    public Guid? ContinuedFromTaskId { get; init; }
}

/// <summary>
/// The assistant's task engine. It owns task lifecycle, conversation, and persistence,
/// and delegates all provider work to <see cref="IClaudeCodeBridge"/>.
/// It knows nothing about Claude Code's command line.
/// </summary>
public sealed class AssistantService : IDisposable
{
    private readonly TaskStore _store;
    private readonly IClaudeCodeBridge _bridge;
    private readonly WorkspaceService _workspaces;
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<Guid, AssistantTask> _tasks = new();

    public AssistantService(
        TaskStore store,
        IClaudeCodeBridge bridge,
        WorkspaceService workspaces,
        AppPaths paths,
        AppSettings settings,
        IApprovalBroker approvals)
    {
        _store = store;
        _bridge = bridge;
        _workspaces = workspaces;
        _paths = paths;
        _settings = settings;
        Approvals = approvals;

        // Honest restart semantics: nothing survives the process that was running it.
        _store.MarkRunningTasksInterrupted();
        foreach (var t in _store.LoadTasks()) _tasks[t.Id] = t;
    }

    public IApprovalBroker Approvals { get; }

    public event EventHandler<AssistantEvent>? EventAppended;
    public event EventHandler<TaskMessage>? MessageAppended;
    public event EventHandler<AssistantTask>? TaskChanged;

    public IReadOnlyList<AssistantTask> Tasks =>
        _tasks.Values.OrderByDescending(t => t.UpdatedAt).ToList();

    public AssistantTask? Find(Guid id) => _tasks.TryGetValue(id, out var t) ? t : null;

    public IReadOnlyList<TaskMessage> MessagesFor(Guid taskId) => _store.LoadMessages(taskId);
    public IReadOnlyList<AssistantEvent> EventsFor(Guid taskId) => _store.LoadEvents(taskId);

    public bool IsRunning(Guid taskId) => _running.ContainsKey(taskId);

    /// <summary>Creates a task and returns it without starting work.</summary>
    public AssistantTask CreateTask(TaskCreationRequest request)
    {
        string workingDirectory;
        string? workspacePath = null;

        if (request.Kind == TaskKind.Workspace)
        {
            var validation = _workspaces.ValidateWorkspace(request.WorkspacePath);
            if (!validation.Ok)
                throw new InvalidOperationException(validation.Error ?? "Invalid workspace.");

            workspacePath = validation.CanonicalPath!;
            workingDirectory = workspacePath;
        }
        else
        {
            // General tasks discuss ideas; they run in PC Assistant's own sandbox so a
            // conversation never has to begin inside a code repository.
            workingDirectory = _paths.GeneralSandbox;
            Directory.CreateDirectory(workingDirectory);
        }

        var task = new AssistantTask
        {
            Title = TitleGenerator.FromPrompt(request.FirstMessage),
            Kind = request.Kind,
            Mode = request.Mode,
            WorkingDirectory = workingDirectory,
            WorkspacePath = workspacePath,
            State = TaskState.Draft,
            ClaudeSessionId = Guid.NewGuid().ToString(),
            ContinuedFromTaskId = request.ContinuedFromTaskId,
        };
        task.RawLogPath = _paths.RawLogFor(task.Id);

        _tasks[task.Id] = task;
        _store.UpsertTask(task);

        _settings.LastMode = request.Mode;
        _settings.Save();

        TaskChanged?.Invoke(this, task);
        return task;
    }

    /// <summary>Sends the owner's message and runs one turn. Safe to call for follow-ups.</summary>
    public async Task<BridgeTurnResult> SendAsync(Guid taskId, string message, CancellationToken externalToken = default)
    {
        var task = Find(taskId) ?? throw new InvalidOperationException("Unknown task.");

        if (_running.ContainsKey(taskId))
            throw new InvalidOperationException("That task is already working. Stop it first.");

        if (string.IsNullOrWhiteSpace(message))
            throw new InvalidOperationException("Message is empty.");

        AppendMessage(task, MessageRole.Owner, message);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        if (!_running.TryAdd(taskId, cts))
        {
            cts.Dispose();
            throw new InvalidOperationException("That task is already working.");
        }

        UpdateState(task, TaskState.Running, clearError: true);
        AppendEvent(task, AssistantEventKind.TaskStarted, task.HasClaudeSession ? "Continuing task" : "Task started");
        var transcript = new TurnTranscript(this, task);

        try
        {
            var request = new BridgeTurnRequest
            {
                TaskId = task.Id,
                Prompt = message,
                WorkingDirectory = task.WorkingDirectory,
                Mode = task.Mode,
                SessionId = task.ClaudeSessionId!,
                Resume = task.HasClaudeSession,
                TaskTitle = task.Title,
            };

            var result = await _bridge.RunTurnAsync(request, e =>
            {
                if (e.Kind is AssistantEventKind.AssistantMessageDelta or AssistantEventKind.AssistantMessage)
                {
                    transcript.Receive(e);
                    if (e.Kind == AssistantEventKind.AssistantMessage) AppendEvent(task, e);
                }
                else
                {
                    transcript.Flush();
                    AppendEvent(task, e);
                }
            }, cts.Token)
                                      .ConfigureAwait(false);

            transcript.Flush();

            if (result.SessionId is { Length: > 0 } sid)
            {
                task.ClaudeSessionId = sid;
                task.HasClaudeSession = true;
            }

            if (result.Cancelled)
            {
                Conclude(task, cts, TaskState.Cancelled, AssistantEventKind.TaskCancelled, "Stopped by you");
            }
            else if (result.Success)
            {
                if (!string.IsNullOrWhiteSpace(result.FinalText) && !transcript.CompleteFinal(result.FinalText!))
                    AppendMessage(task, MessageRole.Assistant, result.FinalText!);

                Conclude(task, cts, TaskState.Completed, AssistantEventKind.TaskCompleted, "Done");
            }
            else
            {
                task.LastError = result.Error;
                if (!string.IsNullOrWhiteSpace(result.Error))
                    AppendMessage(task, MessageRole.System, result.Error!);

                Conclude(task, cts, TaskState.Failed, AssistantEventKind.TaskFailed, "Failed", result.Error);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            transcript.Flush();
            Conclude(task, cts, TaskState.Cancelled, AssistantEventKind.TaskCancelled, "Stopped by you");
            return new BridgeTurnResult(false, true, task.ClaudeSessionId, null, null);
        }
        catch (Exception ex)
        {
            transcript.Flush();
            task.LastError = ex.Message;
            AppendMessage(task, MessageRole.System, "PC Assistant could not run this turn: " + ex.Message);
            Conclude(task, cts, TaskState.Failed, AssistantEventKind.TaskFailed, "Failed", ex.Message);
            return new BridgeTurnResult(false, false, task.ClaudeSessionId, null, ex.Message);
        }
        finally
        {
            Release(taskId, cts);
        }
    }

    /// <summary>
    /// Ends a turn in a fixed order: the closing progress line, then the task stops counting as
    /// running, then the terminal state is announced. Anything that reacts to "completed" by sending
    /// the next message — the phone does, the instant the answer lands — must find the task free,
    /// and must not see that turn's "Done" arrive after its own "Task started".
    /// </summary>
    private void Conclude(
        AssistantTask task, CancellationTokenSource turn, TaskState state, AssistantEventKind kind, string title, string? detail = null)
    {
        AppendEvent(task, kind, title, detail);
        Release(task.Id, turn);
        UpdateState(task, state);
    }

    /// <summary>Releases this turn only. A follow-up may already have claimed the task for its own.</summary>
    private void Release(Guid taskId, CancellationTokenSource turn)
    {
        if (_running.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(taskId, turn))) turn.Dispose();
    }

    /// <summary>
    /// Keeps one message row per streamed block, including stopped/failed turns. SQLite
    /// checkpoints are limited to twice a second while UI snapshots arrive at most ~16/s.
    /// Existing message rows and timestamps are reused; no schema migration is needed.
    /// </summary>
    private sealed class TurnTranscript(AssistantService owner, AssistantTask task)
    {
        private readonly Dictionary<Guid, BufferedMessage> _messages = new();
        private readonly object _gate = new();

        private sealed class BufferedMessage(AssistantEvent evt)
        {
            public Guid Id { get; } = evt.Id;
            public DateTimeOffset CreatedAt { get; } = evt.CreatedAt;
            public StringBuilder Text { get; } = new();
            public long LastStored { get; set; } = Stopwatch.GetTimestamp();
            public long LastPublished { get; set; }
            public bool StoreDirty { get; set; }
            public bool UiDirty { get; set; }
        }

        public void Receive(AssistantEvent evt)
        {
            if (string.IsNullOrEmpty(evt.Detail)) return;
            lock (_gate)
            {
                if (!_messages.TryGetValue(evt.Id, out var message))
                    _messages[evt.Id] = message = new BufferedMessage(evt);
                if (evt.Kind == AssistantEventKind.AssistantMessage) message.Text.Clear();
                message.Text.Append(evt.Detail);
                message.StoreDirty = message.UiDirty = true;
                Publish(message, force: evt.Kind == AssistantEventKind.AssistantMessage);
            }
        }

        public bool CompleteFinal(string text)
        {
            lock (_gate)
            {
                var trimmed = text.Trim();
                if (_messages.Values.Any(m => m.Text.ToString().Trim() == trimmed) ||
                    string.Join("\n\n", _messages.Values.Select(m => m.Text.ToString())).Trim() == trimmed)
                    return true;
                var last = _messages.Values.LastOrDefault();
                if (last is null || !text.StartsWith(last.Text.ToString(), StringComparison.Ordinal)) return false;
                last.Text.Clear().Append(text);
                last.StoreDirty = last.UiDirty = true;
                Publish(last, force: true);
                return true;
            }
        }

        public void Flush()
        {
            lock (_gate)
                foreach (var message in _messages.Values) Publish(message, force: true);
        }

        private void Publish(BufferedMessage message, bool force)
        {
            var store = message.StoreDirty && (force || Stopwatch.GetElapsedTime(message.LastStored).TotalMilliseconds >= 500);
            var publish = message.UiDirty && (force || Stopwatch.GetElapsedTime(message.LastPublished).TotalMilliseconds >= 60);
            if (!store && !publish) return;
            var snapshot = new TaskMessage
            {
                Id = message.Id, TaskId = task.Id, Role = MessageRole.Assistant,
                Text = message.Text.ToString(), CreatedAt = message.CreatedAt,
            };
            if (store)
            {
                owner._store.AddMessage(snapshot);
                message.LastStored = Stopwatch.GetTimestamp();
                message.StoreDirty = false;
            }
            if (publish)
            {
                owner.MessageAppended?.Invoke(owner, snapshot);
                message.LastPublished = Stopwatch.GetTimestamp();
                message.UiDirty = false;
            }
        }
    }

    /// <summary>Stops the task's Claude work and resolves anything it was waiting on.</summary>
    public void Stop(Guid taskId)
    {
        Approvals.CancelTask(taskId);

        if (_running.TryGetValue(taskId, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public void SetMode(Guid taskId, AssistantMode mode)
    {
        var task = Find(taskId);
        if (task is null || task.Mode == mode) return;

        task.Mode = mode;
        task.UpdatedAt = DateTimeOffset.Now;
        _store.UpsertTask(task);

        _settings.LastMode = mode;
        _settings.Save();

        TaskChanged?.Invoke(this, task);
    }

    /// <summary>
    /// Branches a fresh task that carries a short handover summary instead of the whole
    /// history. The owner decides when to do this; there is no automatic context management.
    /// </summary>
    public AssistantTask ContinueAsNewTask(Guid taskId)
    {
        var previous = Find(taskId) ?? throw new InvalidOperationException("Unknown task.");
        var summary = BuildHandoverSummary(previous);

        var task = new AssistantTask
        {
            Title = TitleGenerator.ForContinuation(previous.Title),
            Kind = previous.Kind,
            Mode = previous.Mode,
            WorkingDirectory = previous.WorkingDirectory,
            WorkspacePath = previous.WorkspacePath,
            State = TaskState.Draft,
            ClaudeSessionId = Guid.NewGuid().ToString(),
            ContinuedFromTaskId = previous.Id,
        };
        task.RawLogPath = _paths.RawLogFor(task.Id);

        _tasks[task.Id] = task;
        _store.UpsertTask(task);
        AppendMessage(task, MessageRole.System, summary);

        TaskChanged?.Invoke(this, task);
        return task;
    }

    /// <summary>
    /// A concise, deterministic handover. It keeps the link back to the old task so the
    /// owner can still open the original conversation and raw log.
    /// </summary>
    public string BuildHandoverSummary(AssistantTask previous)
    {
        var messages = _store.LoadMessages(previous.Id);
        var lines = new List<string>
        {
            $"Continuing from task \"{previous.Title}\" ({previous.Id}).",
        };

        if (previous.WorkspacePath is not null)
            lines.Add($"Workspace: {previous.WorkspacePath}");

        var firstOwner = messages.FirstOrDefault(m => m.Role == MessageRole.Owner);
        if (firstOwner is not null)
            lines.Add("Original request: " + Trim(firstOwner.Text, 400));

        var lastAssistant = messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
        if (lastAssistant is not null)
            lines.Add("Where it left off: " + Trim(lastAssistant.Text, 800));

        lines.Add("Continue from here. Ask if anything from the earlier task is unclear.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string Trim(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }

    public void AppendMessage(AssistantTask task, MessageRole role, string text)
    {
        var message = new TaskMessage { TaskId = task.Id, Role = role, Text = text };
        _store.AddMessage(message);

        task.UpdatedAt = DateTimeOffset.Now;
        _store.UpsertTask(task);

        MessageAppended?.Invoke(this, message);
    }

    private void AppendEvent(AssistantTask task, AssistantEventKind kind, string title, string? detail = null)
        => AppendEvent(task, new AssistantEvent { TaskId = task.Id, Kind = kind, Title = title, Detail = detail });

    private void AppendEvent(AssistantTask task, AssistantEvent evt)
    {
        _store.AddEvent(evt);

        // An assistant message arriving mid-turn is part of the conversation, not just progress.
        if (evt.Kind == AssistantEventKind.Question && !string.IsNullOrWhiteSpace(evt.Detail))
            _store.AddMessage(new TaskMessage { TaskId = task.Id, Role = MessageRole.Assistant, Text = evt.Detail! });

        if (evt.Kind == AssistantEventKind.ApprovalRequired)
            UpdateState(task, TaskState.WaitingForApproval);
        else if (evt.Kind == AssistantEventKind.ApprovalResolved && task.State == TaskState.WaitingForApproval)
            UpdateState(task, TaskState.Running);

        EventAppended?.Invoke(this, evt);
    }

    private void UpdateState(AssistantTask task, TaskState state, bool clearError = false)
    {
        task.State = state;
        task.UpdatedAt = DateTimeOffset.Now;
        if (clearError) task.LastError = null;

        _store.UpsertTask(task);
        TaskChanged?.Invoke(this, task);
    }

    public Task<ClaudeStatus> GetClaudeStatusAsync(CancellationToken ct = default)
        => _bridge.GetStatusAsync(ct);

    public void Dispose()
    {
        foreach (var cts in _running.Values)
        {
            try { cts.Cancel(); cts.Dispose(); } catch (ObjectDisposedException) { }
        }
        _running.Clear();
    }
}
