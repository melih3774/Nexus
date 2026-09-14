using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Identity;
using PcCore.Protocol;

namespace PcCore.Capabilities.Assistant;

// The assistant.* surface. Every action here needs the owner's Full Remote session: none is in the
// policy's WithoutOwnerAuthorization list, so a paired phone whose owner has not authenticated reaches
// none of it. None of them runs anything on the PC itself. They ask PC Assistant, which applies its own
// AUTO/ASK policy and approval cards to whatever the phone starts, exactly as for a task typed at the PC.

public sealed record AssistantTaskListParameters
{
    public int? Limit { get; init; }
}

public sealed record AssistantTaskParameters
{
    public string? TaskId { get; init; }
}

public sealed record AssistantCreateTaskParameters
{
    /// <summary>Only <c>general</c>. Workspace tasks are created on the PC, where the folder is chosen.</summary>
    public string? Kind { get; init; }

    /// <summary><c>auto</c> or <c>ask</c>.</summary>
    public string? Mode { get; init; }

    public string? Prompt { get; init; }
}

public sealed record AssistantSendParameters
{
    public string? TaskId { get; init; }

    public string? Text { get; init; }
}

public sealed record AssistantSetModeParameters
{
    public string? TaskId { get; init; }

    public string? Mode { get; init; }
}

public sealed record AssistantResolveParameters
{
    public string? ApprovalId { get; init; }

    /// <summary><c>allow_once</c> or <c>deny</c>. There is no "allow for this task" from a phone.</summary>
    public string? Decision { get; init; }
}

/// <summary>The checks PC Core makes before relaying anything, so a malformed request never reaches PC Assistant.</summary>
internal static class AssistantInput
{
    /// <summary>Matches PC Assistant's own limit; refusing here costs the phone less than a round trip.</summary>
    public const int MaxPromptChars = 32_000;

    public static string TaskId(string? value) =>
        Guid.TryParse(value, out var id)
            ? id.ToString("D")
            : throw new CapabilityException(ErrorCodes.InvalidParameters, "task_id must be a task id.");

    public static string Mode(string? value) =>
        value is "auto" or "ask"
            ? value
            : throw new CapabilityException(ErrorCodes.InvalidParameters, "mode must be \"auto\" or \"ask\".");

    public static string Prompt(string? value, string name)
    {
        var text = value?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, $"{name} is required.");
        }

        return text.Length <= MaxPromptChars
            ? text
            : throw new CapabilityException(ErrorCodes.InvalidParameters, $"{name} is limited to {MaxPromptChars} characters.");
    }
}

/// <summary><c>assistant.status</c>: whether the phone can use PC Assistant, and if not, why.</summary>
public sealed class GetAssistantStatusAction(AssistantBridge bridge) : CapabilityAction<NoParameters, AssistantStatus>
{
    public override string Name => "assistant.status";

    public override string Description => "Whether PC Assistant is reachable and ready, and if not, why.";

    protected override async ValueTask<AssistantStatus> ExecuteAsync(NoParameters parameters, CapabilityContext context, CancellationToken cancellationToken) =>
        await bridge.RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// <c>assistant.subscribe</c>: relay PC Assistant's task, message, progress and approval events to this
/// connection, for as long as it keeps the owner's session.
/// </summary>
public sealed class SubscribeAssistantAction(AssistantBridge bridge) : CapabilityAction<NoParameters, AssistantSubscription>
{
    public override string Name => "assistant.subscribe";

    public override string Description => "Receive PC Assistant events on this connection while the owner session lasts.";

    protected override async ValueTask<AssistantSubscription> ExecuteAsync(NoParameters parameters, CapabilityContext context, CancellationToken cancellationToken)
    {
        bridge.Subscribe(context.Caller.ConnectionId);
        var status = await bridge.RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
        return new AssistantSubscription(true, status);
    }
}

/// <summary><c>assistant.unsubscribe</c>.</summary>
public sealed class UnsubscribeAssistantAction(AssistantBridge bridge) : CapabilityAction<NoParameters, AssistantSubscription>
{
    public override string Name => "assistant.unsubscribe";

    public override string Description => "Stop receiving PC Assistant events on this connection.";

    protected override ValueTask<AssistantSubscription> ExecuteAsync(NoParameters parameters, CapabilityContext context, CancellationToken cancellationToken)
    {
        bridge.Unsubscribe(context.Caller.ConnectionId);
        return ValueTask.FromResult(new AssistantSubscription(false, bridge.CurrentStatus));
    }
}

/// <summary><c>assistant.tasks.list</c>: the most recently active tasks, desktop and phone alike.</summary>
public sealed class ListAssistantTasksAction(AssistantBridge bridge) : CapabilityAction<AssistantTaskListParameters, AssistantTaskList>
{
    public override string Name => "assistant.tasks.list";

    public override string Description => "PC Assistant's tasks, most recently active first.";

    protected override async ValueTask<AssistantTaskList> ExecuteAsync(AssistantTaskListParameters parameters, CapabilityContext context, CancellationToken cancellationToken) =>
        await bridge.CallAsync<AssistantTaskList>(
            "tasks.list",
            new { limit = Math.Clamp(parameters.Limit ?? 50, 1, 200) },
            needsClaude: false,
            cancellationToken).ConfigureAwait(false);
}

/// <summary><c>assistant.tasks.get</c>: one task, its conversation, its progress and its pending approvals.</summary>
public sealed class GetAssistantTaskAction(AssistantBridge bridge) : CapabilityAction<AssistantTaskParameters, AssistantTaskDetail>
{
    public override string Name => "assistant.tasks.get";

    public override string Description => "One PC Assistant task with its messages, progress and pending approvals.";

    protected override async ValueTask<AssistantTaskDetail> ExecuteAsync(AssistantTaskParameters parameters, CapabilityContext context, CancellationToken cancellationToken) =>
        await bridge.CallAsync<AssistantTaskDetail>(
            "tasks.get",
            new { task_id = AssistantInput.TaskId(parameters.TaskId) },
            needsClaude: false,
            cancellationToken).ConfigureAwait(false);
}

/// <summary><c>assistant.tasks.create</c>: a General task, started with its first message.</summary>
public sealed class CreateAssistantTaskAction(AssistantBridge bridge) : CapabilityAction<AssistantCreateTaskParameters, AssistantTaskResult>
{
    public override string Name => "assistant.tasks.create";

    public override string Description => "Start a General PC Assistant task in AUTO or ASK mode.";

    protected override async ValueTask<AssistantTaskResult> ExecuteAsync(AssistantCreateTaskParameters parameters, CapabilityContext context, CancellationToken cancellationToken)
    {
        if (parameters.Kind is not (null or "general"))
        {
            throw new CapabilityException(ErrorCodes.Unsupported, "Only General tasks can be started from the phone.");
        }

        return await bridge.CallAsync<AssistantTaskResult>(
            "tasks.create",
            new
            {
                kind = "general",
                mode = AssistantInput.Mode(parameters.Mode),
                prompt = AssistantInput.Prompt(parameters.Prompt, "prompt"),
            },
            needsClaude: true,
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>assistant.tasks.send</c>: a follow-up, continuing the task's own Claude session.</summary>
public sealed class SendAssistantMessageAction(AssistantBridge bridge) : CapabilityAction<AssistantSendParameters, AssistantTaskResult>
{
    public override string Name => "assistant.tasks.send";

    public override string Description => "Send a follow-up to a PC Assistant task.";

    protected override async ValueTask<AssistantTaskResult> ExecuteAsync(AssistantSendParameters parameters, CapabilityContext context, CancellationToken cancellationToken) =>
        await bridge.CallAsync<AssistantTaskResult>(
            "tasks.send",
            new { task_id = AssistantInput.TaskId(parameters.TaskId), text = AssistantInput.Prompt(parameters.Text, "text") },
            needsClaude: true,
            cancellationToken).ConfigureAwait(false);
}

/// <summary><c>assistant.tasks.stop</c>.</summary>
public sealed class StopAssistantTaskAction(AssistantBridge bridge) : CapabilityAction<AssistantTaskParameters, AssistantStopResult>
{
    public override string Name => "assistant.tasks.stop";

    public override string Description => "Stop a running PC Assistant task.";

    protected override async ValueTask<AssistantStopResult> ExecuteAsync(AssistantTaskParameters parameters, CapabilityContext context, CancellationToken cancellationToken) =>
        await bridge.CallAsync<AssistantStopResult>(
            "tasks.stop",
            new { task_id = AssistantInput.TaskId(parameters.TaskId) },
            needsClaude: false,
            cancellationToken).ConfigureAwait(false);
}

/// <summary><c>assistant.tasks.set_mode</c>: AUTO or ASK, taking effect at the task's next tool call.</summary>
public sealed class SetAssistantModeAction(AssistantBridge bridge) : CapabilityAction<AssistantSetModeParameters, AssistantTaskResult>
{
    public override string Name => "assistant.tasks.set_mode";

    public override string Description => "Switch a PC Assistant task between AUTO and ASK.";

    protected override async ValueTask<AssistantTaskResult> ExecuteAsync(AssistantSetModeParameters parameters, CapabilityContext context, CancellationToken cancellationToken) =>
        await bridge.CallAsync<AssistantTaskResult>(
            "tasks.set_mode",
            new { task_id = AssistantInput.TaskId(parameters.TaskId), mode = AssistantInput.Mode(parameters.Mode) },
            needsClaude: false,
            cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// <c>assistant.approvals.resolve</c>: allow once, or deny, an ordinary approval card. Administrator
/// approvals are refused by PC Assistant itself and stay on the PC.
/// </summary>
public sealed class ResolveAssistantApprovalAction(AssistantBridge bridge) : CapabilityAction<AssistantResolveParameters, AssistantResolveResult>
{
    public override string Name => "assistant.approvals.resolve";

    public override string Description => "Allow once, or deny, a pending PC Assistant approval.";

    protected override async ValueTask<AssistantResolveResult> ExecuteAsync(AssistantResolveParameters parameters, CapabilityContext context, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(parameters.ApprovalId, out var approval))
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, "approval_id must be an approval id.");
        }

        if (parameters.Decision is not ("allow_once" or "deny"))
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, "decision must be \"allow_once\" or \"deny\".");
        }

        return await bridge.CallAsync<AssistantResolveResult>(
            "approvals.resolve",
            new { approval_id = approval.ToString("D"), decision = parameters.Decision },
            needsClaude: false,
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Who may receive <c>assistant.*</c> events: a connection that subscribed and, at the moment of sending,
/// still holds the owner's Full Remote session. A paired phone whose owner has not authenticated, or
/// whose session has lapsed or been locked, receives nothing — and loses the subscription, so unlocking
/// again means asking again.
/// </summary>
public sealed class AssistantEventAudience(AssistantBridge bridge, IOwnerSessionService sessions) : IEventAudience
{
    public bool MayReceive(EventEnvelope envelope, CallerDescriptor caller)
    {
        if (!envelope.EventName.StartsWith(AssistantEvents.Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!bridge.IsSubscribed(caller.ConnectionId))
        {
            return false;
        }

        // Local callers already hold the session token for the whole of PC Core; the owner at the PC
        // is not asked to prove presence to themselves.
        if (caller.ActorType == ActorType.Local)
        {
            return true;
        }

        if (sessions.HoldsFullRemote(caller))
        {
            return true;
        }

        bridge.Unsubscribe(caller.ConnectionId);
        return false;
    }

    public void ConnectionClosed(string connectionId) => bridge.Unsubscribe(connectionId);
}
