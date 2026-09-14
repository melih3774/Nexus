using System.Text.Json;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Assistant;

/// <summary>
/// PC Core's side of the PC Assistant bridge: one connection, a status the phone can read, and the
/// events PC Assistant pushes, relayed to whichever phones asked for them.
/// <para>
/// <b>What this is not.</b> It is not an AI layer and it decides nothing about tasks, tools or approvals
/// — PC Assistant owns all of that, and its own policy and approval cards apply to everything a phone
/// starts. What PC Core adds is who may ask: every <c>assistant.*</c> action needs the owner's Full Remote
/// session (see <see cref="DefaultCapabilityPolicy"/>), and every <c>assistant.*</c> event is delivered only to
/// a connection that subscribed and still holds that session when the event is sent
/// (<see cref="AssistantEventAudience"/>).
/// </para>
/// <para>
/// <b>Connection lifetime.</b> Opened on first use, never by PC Core starting up. Kept while any phone
/// is subscribed, and re-attempted every few seconds while one is, so "PC Assistant was opened" reaches a
/// phone that is watching without it having to poll. Nexus never starts PC Assistant.
/// </para>
/// </summary>
public sealed class AssistantBridge : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);

    /// <summary>One wording for "nothing is there", however it was noticed, so noticing it twice is not two changes.</summary>
    public const string NotRunningDetail =
        "PC Assistant is not running, its Nexus bridge is turned off, or another program is already connected to it.";

    private readonly AssistantBridgeOptions _options;
    private readonly IAssistantPipeConnector _connector;
    private readonly IEventPublisher _events;
    private readonly ILogger<AssistantBridge> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly HashSet<string> _subscribers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly MessageThrottle _messages;

    private AssistantPipeClient? _client;
    private AssistantStatus _status;
    private Task? _watcher;

    public AssistantBridge(
        AssistantBridgeOptions options,
        IAssistantPipeConnector connector,
        IEventPublisher events,
        ILogger<AssistantBridge> logger,
        TimeProvider? time = null)
    {
        _options = options;
        _connector = connector;
        _events = events;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _status = Configured() ?? Status(AssistantAvailability.NotRunning, "PC Assistant has not been reached yet.");
        _messages = new MessageThrottle(
            TimeSpan.FromMilliseconds(Math.Clamp(_options.MessageIntervalMilliseconds, 0, 5000)),
            _time,
            payload => Publish(AssistantEvents.Message, payload));
    }

    /// <summary>The last known status. Cheap; does not reach PC Assistant.</summary>
    public AssistantStatus CurrentStatus
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    // --- Status --------------------------------------------------------------------------------------

    /// <summary>
    /// Asks PC Assistant, connecting if needed. Never throws for an unavailable PC Assistant: that is a
    /// status, and the status says which kind.
    /// </summary>
    public async Task<AssistantStatus> RefreshStatusAsync(CancellationToken cancellationToken)
    {
        if (Configured() is { } off)
        {
            return SetStatus(off);
        }

        try
        {
            var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var status = await client.RequestAsync("status", null, cancellationToken).ConfigureAwait(false);
            return SetStatus(FromClaude(CurrentStatus, status.TryGetProperty("claude", out var claude) ? claude : default));
        }
        catch (AssistantUnavailableException ex)
        {
            return SetStatus(Status(ex.State, ex.Message));
        }
        catch (AssistantRequestException ex)
        {
            return SetStatus(Status(AssistantAvailability.Unavailable, ex.Message));
        }
    }

    // --- Requests ------------------------------------------------------------------------------------

    /// <summary>
    /// One request to PC Assistant, with its refusals turned into PC Core's error codes. Anything that
    /// starts or continues work also requires Claude Code to be ready, so a signed-out Claude is reported
    /// as that rather than as a turn that fails a moment later.
    /// </summary>
    public async Task<T> CallAsync<T>(string method, object? parameters, bool needsClaude, CancellationToken cancellationToken)
    {
        if (Configured() is { } off)
        {
            SetStatus(off);
            throw Unavailable(off);
        }

        AssistantPipeClient client;
        try
        {
            client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AssistantUnavailableException ex)
        {
            throw Unavailable(SetStatus(Status(ex.State, ex.Message)));
        }

        if (needsClaude)
        {
            var status = await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!status.CanWork)
            {
                throw Unavailable(status);
            }
        }

        try
        {
            return await client.RequestAsync<T>(method, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (AssistantUnavailableException ex)
        {
            throw Unavailable(SetStatus(Status(ex.State, ex.Message)));
        }
        catch (AssistantRequestException ex)
        {
            throw ex.Code switch
            {
                "not_found" => new CapabilityException(ErrorCodes.NotFound, ex.Message),
                "invalid_params" => new CapabilityException(ErrorCodes.InvalidParameters, ex.Message),
                "unsupported" => new CapabilityException(ErrorCodes.Unsupported, ex.Message),
                "task_busy" => new CapabilityException(ErrorCodes.AssistantTaskBusy, ex.Message),
                "requires_local_approval" => new CapabilityException(ErrorCodes.AssistantRequiresLocalApproval, ex.Message),
                "client_not_trusted" => Unavailable(SetStatus(Status(AssistantAvailability.NotTrusted, ex.Message))),
                "unsupported_protocol_version" => Unavailable(SetStatus(Status(AssistantAvailability.Incompatible, ex.Message))),
                _ => new CapabilityException(ErrorCodes.InternalError, "PC Assistant could not complete that request."),
            };
        }
    }

    // --- Subscriptions -------------------------------------------------------------------------------

    /// <summary>
    /// Starts relaying events to <paramref name="connectionId"/>. Delivery is still decided per event by
    /// <see cref="AssistantEventAudience"/>, which also withdraws this the first time the connection is
    /// found without the owner's session.
    /// </summary>
    public void Subscribe(string connectionId)
    {
        lock (_gate)
        {
            _subscribers.Add(connectionId);
            _watcher ??= Task.Run(() => WatchAsync(_stopping.Token));
        }
    }

    public void Unsubscribe(string connectionId)
    {
        lock (_gate)
        {
            _subscribers.Remove(connectionId);
        }
    }

    public bool IsSubscribed(string connectionId)
    {
        lock (_gate)
        {
            return _subscribers.Contains(connectionId);
        }
    }

    /// <summary>
    /// While anyone is subscribed: keep the connection, and notice PC Assistant arriving or leaving.
    /// With nobody subscribed it closes the connection and ends, so an idle PC Core holds nothing open.
    /// </summary>
    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (SubscriberCount == 0)
                {
                    await DropConnectionAsync().ConfigureAwait(false);

                    lock (_gate)
                    {
                        if (_subscribers.Count == 0)
                        {
                            _watcher = null;
                            return;
                        }
                    }

                    continue;
                }

                if (Configured() is null)
                {
                    var client = _client;

                    if (client is null || !client.IsOpen)
                    {
                        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                await Task.Delay(ReconnectInterval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // PC Core is stopping.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The PC Assistant watcher stopped unexpectedly.");

            lock (_gate)
            {
                _watcher = null;
            }
        }
    }

    // --- Connection ----------------------------------------------------------------------------------

    private async Task<AssistantPipeClient> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsOpen: true } open)
        {
            return open;
        }

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is { IsOpen: true } raced)
            {
                return raced;
            }

            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }

            var stream = await _connector.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var client = new AssistantPipeClient(stream, TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 5, 300)));
            client.EventReceived += OnEvent;
            client.Start();

            try
            {
                var hello = await client.RequestAsync(
                    "hello",
                    new HelloParameters(AssistantPipeClient.ProtocolVersion, "nexus-pc-core", ProductInfo.Version),
                    cancellationToken).ConfigureAwait(false);

                var version = hello.TryGetProperty("protocol_version", out var v) && v.TryGetInt32(out var parsed) ? parsed : 0;

                if (version != AssistantPipeClient.ProtocolVersion)
                {
                    throw new AssistantUnavailableException(
                        AssistantAvailability.Incompatible,
                        $"PC Assistant speaks bridge protocol {version}; this PC Core speaks {AssistantPipeClient.ProtocolVersion}.");
                }

                var server = hello.TryGetProperty("server_version", out var sv) && sv.ValueKind == JsonValueKind.String ? sv.GetString() : null;
                var status = FromClaude(
                    Status(AssistantAvailability.Ready, "PC Assistant is ready.") with { ServerVersion = server, ProtocolVersion = version },
                    hello.TryGetProperty("claude", out var claude) ? claude : default);

                _client = client;
                _ = client.Closed.ContinueWith(_ => OnClosed(client), TaskScheduler.Default);

                _logger.LogInformation("Connected to PC Assistant {Version}.", server ?? "?");
                SetStatus(status);
                return client;
            }
            catch (AssistantRequestException ex)
            {
                await client.DisposeAsync().ConfigureAwait(false);

                throw ex.Code switch
                {
                    "client_not_trusted" => new AssistantUnavailableException(AssistantAvailability.NotTrusted,
                        "PC Assistant has not approved this PC Core yet. Trust it in PC Assistant's Capabilities panel on the PC."),
                    "unsupported_protocol_version" => new AssistantUnavailableException(AssistantAvailability.Incompatible, ex.Message),
                    _ => new AssistantUnavailableException(AssistantAvailability.Unavailable, ex.Message),
                };
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private void OnClosed(AssistantPipeClient client)
    {
        if (!ReferenceEquals(_client, client))
        {
            return;
        }

        _logger.LogInformation("PC Assistant closed the bridge connection.");
        SetStatus(Configured() ?? Status(AssistantAvailability.NotRunning, NotRunningDetail));
    }

    private async Task DropConnectionAsync()
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                var client = _client;
                _client = null;
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    // --- Events --------------------------------------------------------------------------------------

    private void OnEvent(string name, JsonElement data)
    {
        if (SubscriberCount == 0)
        {
            return;
        }

        try
        {
            switch (name)
            {
                case "task.changed":
                    var task = data.GetProperty("task").Deserialize<AssistantTaskSummary>(AssistantPipeClient.Json);
                    if (task is null)
                    {
                        return;
                    }

                    // The last text of a reply must land before the state that says it is finished.
                    _messages.FlushTask(task.Id);
                    Publish(AssistantEvents.TaskChanged, new AssistantTaskResult(task));
                    break;

                case "message.appended":
                    var message = data.Deserialize<AssistantMessageEvent>(AssistantPipeClient.Json);
                    if (message is not null)
                    {
                        _messages.Offer(message);
                    }

                    break;

                case "task.progress":
                    var progress = data.Deserialize<AssistantProgressEvent>(AssistantPipeClient.Json);
                    if (progress is not null)
                    {
                        _messages.FlushTask(progress.TaskId);
                        Publish(AssistantEvents.Progress, progress);
                    }

                    break;

                case "approval.requested":
                    var approval = data.Deserialize<AssistantApproval>(AssistantPipeClient.Json);
                    if (approval is not null)
                    {
                        _messages.FlushTask(approval.TaskId);
                        Publish(AssistantEvents.ApprovalRequested, new AssistantApprovalRequestedEvent(approval));
                    }

                    break;

                case "approval.resolved":
                    var resolved = data.Deserialize<AssistantApprovalResolvedEvent>(AssistantPipeClient.Json);
                    if (resolved is not null)
                    {
                        Publish(AssistantEvents.ApprovalResolved, resolved);
                    }

                    break;

                default:
                    // A later PC Assistant may say more. Nothing reaches a phone until it is named here.
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            _logger.LogDebug("Ignored a PC Assistant event that was not in the expected shape: {Event}.", name);
        }
    }

    private void Publish(string name, object payload) => _events.Publish(name, payload);

    // --- Helpers -------------------------------------------------------------------------------------

    /// <summary>The two states decided by configuration alone, or null when configuration allows a connection.</summary>
    private AssistantStatus? Configured()
    {
        if (!_options.Enabled)
        {
            return Status(AssistantAvailability.Disabled, "The PC Assistant bridge is turned off in PC Core's settings.");
        }

        return string.IsNullOrWhiteSpace(_options.ServerExecutable)
            ? Status(AssistantAvailability.NotConfigured, "PC Core has not been told which PC Assistant to trust (PcCore:Assistant:ServerExecutable).")
            : null;
    }

    private static AssistantStatus Status(AssistantAvailability state, string detail) => new() { State = state, Detail = detail };

    private static AssistantStatus FromClaude(AssistantStatus basis, JsonElement claude)
    {
        if (claude.ValueKind != JsonValueKind.Object)
        {
            return basis;
        }

        var availability = claude.TryGetProperty("availability", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : "unknown";
        var version = claude.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // PC Assistant's own reason, when it gives one. "Signed out" and "signed in, but the account refuses
        // Claude Code" look alike from here and need different things from the owner.
        var reason = claude.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(d.GetString())
            ? d.GetString()!.Trim()
            : null;

        var ready = basis with
        {
            State = AssistantAvailability.Ready,
            Detail = "PC Assistant is ready.",
            Claude = availability,
            ClaudeVersion = version,
        };

        return availability switch
        {
            "ready" => ready,
            "signed_out" => ready with { State = AssistantAvailability.ClaudeNotReady, Detail = reason ?? "Claude Code is signed out on the PC. Run claude auth login there." },
            "not_installed" => ready with { State = AssistantAvailability.ClaudeNotReady, Detail = reason ?? "Claude Code is not installed on the PC." },
            "failed" => ready with { State = AssistantAvailability.ClaudeNotReady, Detail = reason ?? "Claude Code failed to start on the PC." },
            _ => ready with { State = AssistantAvailability.ClaudeNotReady, Detail = reason ?? "PC Assistant could not tell whether Claude Code is ready." },
        };
    }

    private AssistantStatus SetStatus(AssistantStatus status)
    {
        bool changed;

        lock (_gate)
        {
            changed = _status.State != status.State || _status.Detail != status.Detail || _status.Claude != status.Claude;

            // What was learned from hello is kept until a later hello says otherwise.
            if (status.ServerVersion is null && _status.ServerVersion is not null && status.State is AssistantAvailability.Ready or AssistantAvailability.ClaudeNotReady)
            {
                status = status with { ServerVersion = _status.ServerVersion, ProtocolVersion = _status.ProtocolVersion };
            }

            _status = status;
        }

        if (changed)
        {
            _logger.LogInformation("PC Assistant bridge: {State} ({Detail})", status.State, status.Detail);

            if (SubscriberCount > 0)
            {
                Publish(AssistantEvents.StatusChanged, status);
            }
        }

        return status;
    }

    private static CapabilityException Unavailable(AssistantStatus status) =>
        new(ErrorCodes.AssistantUnavailable, status.Detail, AssistantAvailabilityName(status.State));

    /// <summary>The wire spelling of a state, for error details.</summary>
    public static string AssistantAvailabilityName(AssistantAvailability state) => state switch
    {
        AssistantAvailability.Disabled => "DISABLED",
        AssistantAvailability.NotConfigured => "NOT_CONFIGURED",
        AssistantAvailability.NotRunning => "NOT_RUNNING",
        AssistantAvailability.NotTrusted => "NOT_TRUSTED",
        AssistantAvailability.Incompatible => "INCOMPATIBLE",
        AssistantAvailability.ClaudeNotReady => "CLAUDE_NOT_READY",
        AssistantAvailability.Ready => "READY",
        _ => "UNAVAILABLE",
    };

    public async ValueTask DisposeAsync()
    {
        try { await _stopping.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }

        var watcher = _watcher;
        if (watcher is not null)
        {
            try { await watcher.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception) { /* stopping regardless */ }
        }

        await DropConnectionAsync().ConfigureAwait(false);
        _messages.Dispose();
    }

    private sealed record HelloParameters(int ProtocolVersion, string Client, string ClientVersion);

    /// <summary>
    /// Forwards the growing text of each message at most once per interval, always ending on the latest.
    /// A reply that arrives in one snapshot is forwarded at once; one that streams for a minute costs a
    /// phone four messages a second instead of sixteen, and the last one is never lost.
    /// </summary>
    private sealed class MessageThrottle(TimeSpan interval, TimeProvider time, Action<AssistantMessageEvent> publish) : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<Guid, Entry> _entries = [];
        private bool _disposed;

        private sealed class Entry
        {
            public DateTimeOffset LastSent;
            public AssistantMessageEvent? Pending;
            public ITimer? Timer;
        }

        public void Offer(AssistantMessageEvent message)
        {
            AssistantMessageEvent? now = null;

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                PruneLocked();

                var at = time.GetUtcNow();

                if (!_entries.TryGetValue(message.Message.Id, out var entry))
                {
                    _entries[message.Message.Id] = entry = new Entry();
                }

                if (interval <= TimeSpan.Zero || at - entry.LastSent >= interval)
                {
                    entry.LastSent = at;
                    entry.Pending = null;
                    now = message;
                }
                else
                {
                    entry.Pending = message;
                    entry.Timer ??= time.CreateTimer(_ => Flush(message.Message.Id), null, interval - (at - entry.LastSent), Timeout.InfiniteTimeSpan);
                }
            }

            if (now is not null)
            {
                publish(now);
            }
        }

        public void FlushTask(Guid taskId)
        {
            List<Guid> ids;

            lock (_gate)
            {
                ids = _entries.Where(e => e.Value.Pending?.TaskId == taskId).Select(e => e.Key).ToList();
            }

            foreach (var id in ids)
            {
                Flush(id);
            }
        }

        private void Flush(Guid id)
        {
            AssistantMessageEvent? pending;

            lock (_gate)
            {
                if (!_entries.TryGetValue(id, out var entry))
                {
                    return;
                }

                pending = entry.Pending;
                entry.Pending = null;
                entry.Timer?.Dispose();
                entry.Timer = null;

                if (pending is not null)
                {
                    entry.LastSent = time.GetUtcNow();
                }
            }

            if (pending is not null)
            {
                publish(pending);
            }
        }

        /// <summary>Messages that stopped growing a while ago need no entry.</summary>
        private void PruneLocked()
        {
            if (_entries.Count < 256)
            {
                return;
            }

            var cutoff = time.GetUtcNow() - TimeSpan.FromMinutes(1);

            foreach (var stale in _entries.Where(e => e.Value.Pending is null && e.Value.LastSent < cutoff).Select(e => e.Key).ToList())
            {
                _entries.Remove(stale);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;

                foreach (var entry in _entries.Values)
                {
                    entry.Timer?.Dispose();
                }

                _entries.Clear();
            }
        }
    }
}
