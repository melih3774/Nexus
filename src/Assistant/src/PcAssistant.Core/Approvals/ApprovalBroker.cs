using System.Collections.Concurrent;
using PcAssistant.Core.Policy;

namespace PcAssistant.Core.Approvals;

public enum ApprovalOutcome
{
    Allowed = 0,
    Denied = 1,
    /// <summary>Nobody answered within the window. Treated exactly like a denial.</summary>
    TimedOut = 2,
    /// <summary>The task was cancelled or the app is shutting down. Treated like a denial.</summary>
    Cancelled = 3,
}

public sealed record ApprovalRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TaskId { get; init; }
    public required string ToolName { get; init; }
    public required string Action { get; init; }
    public required string Target { get; init; }
    public required string Reason { get; init; }
    public RiskCategory Category { get; init; }
    public string? ScopeKey { get; init; }
    public string? ScopeDescription { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    // --- consequence-aware fields -----------------------------------------
    // Optional, so ordinary approvals are unchanged. A privileged request fills them all in:
    // an owner deciding about administrator rights should not have to infer the consequence
    // from a command string.

    /// <summary>What privilege the operation would run with, in plain words.</summary>
    public string? Privilege { get; init; }

    /// <summary>What will actually happen to the machine if this is allowed.</summary>
    public string? Consequence { get; init; }

    /// <summary>Why the assistant says it needs this. Shown as a claim, never as authorization.</summary>
    public string? Justification { get; init; }

    /// <summary>The task this request belongs to, so a card is never anonymous.</summary>
    public string? TaskTitle { get; init; }

    /// <summary>Identical pending requests collapse onto one card instead of stacking up.</summary>
    public string DedupeKey => $"{TaskId}|{ToolName}|{Category}|{Target}";
}

public sealed record ApprovalResolution(Guid RequestId, ApprovalOutcome Outcome);

public interface IApprovalBroker
{
    event EventHandler<ApprovalRequest>? ApprovalRequested;
    event EventHandler<ApprovalResolution>? ApprovalResolved;

    Task<ApprovalOutcome> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken);
    bool Resolve(Guid requestId, bool allow);
    bool ResolveForTask(Guid requestId) => false;
    void CancelTask(Guid taskId);
    void CancelAll();
    IReadOnlyList<ApprovalRequest> Pending { get; }
}

/// <summary>
/// The single place an approval decision is made. The bridge asks, the UI answers.
/// Waiting is event-driven; nothing polls.
/// </summary>
public sealed class ApprovalBroker : IApprovalBroker, IDisposable
{
    private sealed class PendingEntry
    {
        public required ApprovalRequest Request { get; init; }
        public required TaskCompletionSource<ApprovalOutcome> Completion { get; init; }
        public CancellationTokenRegistration Registration { get; set; }
        public CancellationTokenSource? TimeoutSource { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, PendingEntry> _byId = new();
    private readonly ConcurrentDictionary<string, Guid> _byDedupeKey = new();
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<(Guid TaskId, string Scope), byte> _taskGrants = new();
    private bool _disposed;

    public ApprovalBroker(TimeSpan? timeout = null)
        => _timeout = timeout ?? TimeSpan.FromMinutes(10);

    public event EventHandler<ApprovalRequest>? ApprovalRequested;
    public event EventHandler<ApprovalResolution>? ApprovalResolved;

    public IReadOnlyList<ApprovalRequest> Pending =>
        _byId.Values.Select(e => e.Request).OrderBy(r => r.CreatedAt).ToList();

    public Task<ApprovalOutcome> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (_disposed) return Task.FromResult(ApprovalOutcome.Cancelled);
        if (cancellationToken.IsCancellationRequested) return Task.FromResult(ApprovalOutcome.Cancelled);
        if (request.ScopeKey is { } scope && _taskGrants.ContainsKey((request.TaskId, scope)))
            return Task.FromResult(ApprovalOutcome.Allowed);

        // Collapse a repeat of an identical, still-pending request onto the existing card.
        if (_byDedupeKey.TryGetValue(request.DedupeKey, out var existingId) &&
            _byId.TryGetValue(existingId, out var existing))
        {
            return existing.Completion.Task;
        }

        var tcs = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new PendingEntry { Request = request, Completion = tcs };

        if (!_byId.TryAdd(request.Id, entry)) return Task.FromResult(ApprovalOutcome.Cancelled);
        _byDedupeKey[request.DedupeKey] = request.Id;

        entry.Registration = cancellationToken.Register(
            static state => ((ApprovalBroker.PendingEntryClosure)state!).Complete(ApprovalOutcome.Cancelled),
            new PendingEntryClosure(this, request.Id));

        var timeoutSource = new CancellationTokenSource(_timeout);
        entry.TimeoutSource = timeoutSource;
        timeoutSource.Token.Register(
            static state => ((ApprovalBroker.PendingEntryClosure)state!).Complete(ApprovalOutcome.TimedOut),
            new PendingEntryClosure(this, request.Id));

        ApprovalRequested?.Invoke(this, request);
        return tcs.Task;
    }

    private sealed class PendingEntryClosure(ApprovalBroker broker, Guid requestId)
    {
        public void Complete(ApprovalOutcome outcome) => broker.Complete(requestId, outcome);
    }

    public bool Resolve(Guid requestId, bool allow)
        => Complete(requestId, allow ? ApprovalOutcome.Allowed : ApprovalOutcome.Denied);

    public bool ResolveForTask(Guid requestId)
    {
        if (!_byId.TryGetValue(requestId, out var entry) || entry.Request.ScopeKey is not { } scope)
            return false;
        if (!Complete(requestId, ApprovalOutcome.Allowed)) return false;
        _taskGrants[(entry.Request.TaskId, scope)] = 0;
        return true;
    }

    public void CancelTask(Guid taskId)
    {
        foreach (var key in _taskGrants.Keys.Where(k => k.TaskId == taskId)) _taskGrants.TryRemove(key, out _);
        foreach (var entry in _byId.Values.Where(e => e.Request.TaskId == taskId).ToList())
            Complete(entry.Request.Id, ApprovalOutcome.Cancelled);
    }

    public void CancelAll()
    {
        _taskGrants.Clear();
        foreach (var id in _byId.Keys.ToList())
            Complete(id, ApprovalOutcome.Cancelled);
    }

    private bool Complete(Guid requestId, ApprovalOutcome outcome)
    {
        if (!_byId.TryRemove(requestId, out var entry)) return false;

        _byDedupeKey.TryRemove(new KeyValuePair<string, Guid>(entry.Request.DedupeKey, requestId));
        entry.Registration.Dispose();
        entry.TimeoutSource?.Dispose();

        if (!entry.Completion.TrySetResult(outcome)) return false;

        ApprovalResolved?.Invoke(this, new ApprovalResolution(requestId, outcome));
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelAll();
    }
}
