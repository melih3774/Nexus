using System.Runtime.Versioning;
using PcAssistant.Core.Approvals;
using PcAssistant.Core.Model;
using PcAssistant.Core.Persistence;
using PcAssistant.Core.Policy;
using PcAssistant.Elevation;

namespace PcAssistant.Core.Elevation;

/// <summary>Where a privileged request came from, so an approval card is never anonymous.</summary>
public sealed record PrivilegedRequestContext(Guid TaskId, string TaskTitle, Action<AssistantEvent>? OnEvent = null);

/// <summary>What the caller gets back, already shaped for the model to read.</summary>
public sealed record PrivilegedOutcome(bool Ran, PrivilegedStatus Status, string Message, PrivilegedResponse? Response)
{
    public bool Succeeded => Ran && Status == PrivilegedStatus.Ok;
}

/// <summary>
/// The trusted privileged client: the only path from "Claude asked for administrator rights"
/// to "the helper was sent an operation".
///
/// The order it enforces is the whole design:
///
///   Claude requests  →  PC Assistant classifies  →  the owner approves  →  this client sends
///   →  the helper validates independently  →  the operation runs  →  everyone records it
///
/// Two properties are load-bearing. First, nothing is sent to the helper before the owner
/// has answered — a denial means no privileged request exists at all, not that one was sent
/// and refused. Second, elevation never becomes automatic: AUTO mode does not weaken this,
/// there is no persistent grant, and every operation asks on its own merits.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PrivilegedOperationService
{
    private readonly IApprovalBroker _approvals;
    private readonly TaskStore _store;
    private readonly IPrivilegedClient _client;

    public PrivilegedOperationService(IApprovalBroker approvals, TaskStore store, IPrivilegedClient? client = null)
    {
        _approvals = approvals;
        _store = store;
        _client = client ?? new PrivilegedClient();
    }

    public Task<HelperStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => _client.GetStatusAsync(cancellationToken);

    public IReadOnlyList<PrivilegedOperationRecord> RecentHistory(int limit = 20, Guid? taskId = null)
        => _store.LoadPrivilegedOperations(limit, taskId);

    /// <summary>
    /// Reads helper readiness without asking the owner for anything. Safe for the model to
    /// call before proposing an administrator operation.
    /// </summary>
    public async Task<string> DescribeStatusAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return status.Availability == HelperAvailability.Ready
            ? $"{status.DisplayText}. {status.Detail}"
            : $"{status.DisplayText}. {status.Detail} " +
              "Administrator operations are unavailable until the owner installs or repairs the privileged helper " +
              "from PC Assistant's Capabilities panel. Do not retry; tell the owner what you needed and why.";
    }

    public async Task<PrivilegedOutcome> RequestAsync(
        PrivilegedRequestContext context, PrivilegedRequest request, CancellationToken cancellationToken = default)
    {
        // 1 - is elevation even possible? Asking the owner to approve something that cannot
        //     run is how approval loops start.
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.Availability != HelperAvailability.Ready)
        {
            var message = $"Administrator operations are unavailable: {status.DisplayText}. {status.Detail}";
            Record(context, request, "Not asked", "Unavailable", null, status.DisplayText);
            return new PrivilegedOutcome(false, PrivilegedStatus.Failed, message, null);
        }

        // 2 - validate before the owner is shown anything, so a malformed request costs no
        //     attention and never reaches the privileged boundary.
        var validation = OperationValidator.Validate(request);
        if (!validation.Ok)
        {
            Record(context, request, "Not asked", "Rejected", null, validation.Error);
            return new PrivilegedOutcome(false, PrivilegedStatus.Invalid,
                "PC Assistant refused this administrator request before asking the owner: " + validation.Error, null);
        }

        var description = validation.Description!;

        // 3 - the owner decides. Every time. There is no scoped or persistent privileged grant.
        var approval = new ApprovalRequest
        {
            TaskId = context.TaskId,
            ToolName = "mcp__pc-assistant__elevated_request",
            Action = "ADMINISTRATOR OPERATION — " + description.Action,
            Target = description.Target,
            Reason = "This needs Administrator rights. PC Assistant never runs administrator operations automatically, " +
                     "in either mode.",
            Category = RiskCategory.Elevation,
            Privilege = description.Privilege,
            Consequence = description.Impact,
            Justification = string.IsNullOrWhiteSpace(request.Reason)
                ? "Claude gave no reason. Ask it why before allowing this."
                : request.Reason,
            TaskTitle = context.TaskTitle,
            // No ScopeKey: a privileged operation never earns "allow for this task".
        };

        context.OnEvent?.Invoke(new AssistantEvent
        {
            TaskId = context.TaskId,
            Kind = AssistantEventKind.ApprovalRequired,
            Title = approval.Action + ": " + description.Target,
            Detail = description.Impact,
            ToolName = approval.ToolName,
        });

        var outcome = await _approvals.RequestAsync(approval, cancellationToken).ConfigureAwait(false);

        context.OnEvent?.Invoke(new AssistantEvent
        {
            TaskId = context.TaskId,
            Kind = AssistantEventKind.ApprovalResolved,
            Title = (outcome == ApprovalOutcome.Allowed ? "Allowed (administrator): " : "Not allowed (administrator): ")
                    + description.Target,
            ToolName = approval.ToolName,
        });

        if (outcome != ApprovalOutcome.Allowed)
        {
            var approvalText = outcome switch
            {
                ApprovalOutcome.Denied => "Denied",
                ApprovalOutcome.TimedOut => "No answer",
                _ => "Cancelled",
            };
            Record(context, request, approvalText, "Not run", null, null);

            return new PrivilegedOutcome(false, PrivilegedStatus.Denied, outcome switch
            {
                ApprovalOutcome.Denied =>
                    "The owner denied this administrator operation, so nothing was sent to the privileged helper and " +
                    "nothing ran. Do not retry it or look for an unprivileged way around it; say what you wanted to do " +
                    "and let the owner decide.",
                ApprovalOutcome.TimedOut =>
                    "The owner did not answer, so this administrator operation was treated as denied and nothing ran.",
                _ => "The task was stopped before the owner answered, so nothing ran.",
            }, null);
        }

        // 4 - only now does anything cross the privileged boundary.
        return await ExecuteAsync(context, request, description, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PrivilegedOutcome> ExecuteAsync(
        PrivilegedRequestContext context, PrivilegedRequest request,
        OperationDescription description, CancellationToken cancellationToken)
    {
        // An operation id chosen here, before the request is sent, is what makes cancelling a
        // long-running command possible at all: the helper's answer only arrives at the end.
        var operationId = request.Kind == PrivilegedOperationKind.Run
            ? request.OperationId ?? Guid.NewGuid().ToString("N")
            : request.OperationId;

        var outgoing = request with { OperationId = operationId };
        var cancelled = false;

        await using var cancellationHook = cancellationToken.Register(() =>
        {
            if (request.Kind != PrivilegedOperationKind.Run || operationId is null) return;
            cancelled = true;
            _ = CancelAsync(operationId);
        });

        PrivilegedResponse response;
        try
        {
            // Deliberately not cancellable at this call. Abandoning the pipe read would leave
            // an approved elevated command running with nobody listening for its result;
            // cancellation instead goes through the helper by operation id, above, and this
            // call then returns the helper's own account of what happened.
            response = await _client.SendAsync(outgoing, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Record(context, request, "Allowed", "Failed", null, ex.Message);
            return new PrivilegedOutcome(false, PrivilegedStatus.Failed,
                "The approved administrator operation could not be delivered to the privileged helper: " + ex.Message, null);
        }

        Record(context, request, "Allowed", response.Status.ToString(), response.ExitCode,
            Shorten(response.Message, 400));

        context.OnEvent?.Invoke(new AssistantEvent
        {
            TaskId = context.TaskId,
            Kind = response.Status == PrivilegedStatus.Ok ? AssistantEventKind.ToolCompleted : AssistantEventKind.ToolFailed,
            Title = "Administrator operation: " + description.Target,
            Detail = response.Message,
            ToolName = "mcp__pc-assistant__elevated_request",
        });

        var ran = response.Status is PrivilegedStatus.Ok or PrivilegedStatus.TimedOut or PrivilegedStatus.Cancelled;
        var message = cancelled && response.Status == PrivilegedStatus.Cancelled
            ? "The task was stopped, so the approved administrator command's own process tree was terminated. " +
              "Nothing else was touched. Check the system state before retrying."
            : response.Message;

        return new PrivilegedOutcome(ran, response.Status, message, response);
    }

    private async Task CancelAsync(string operationId)
    {
        try
        {
            await _client.SendAsync(new PrivilegedRequest
            {
                Kind = PrivilegedOperationKind.Cancel,
                OperationId = operationId,
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The operation may already have finished; the original call reports the truth.
        }
    }

    private void Record(
        PrivilegedRequestContext context, PrivilegedRequest request,
        string approval, string result, int? exitCode, string? detail)
    {
        try
        {
            _store.AddPrivilegedOperation(new PrivilegedOperationRecord
            {
                TaskId = context.TaskId == Guid.Empty ? null : context.TaskId,
                TaskTitle = context.TaskTitle,
                Operation = request.Kind.ToString(),
                Target = Shorten(DescribeTarget(request), 500),
                Approval = approval,
                Result = result,
                ExitCode = exitCode,
                Detail = detail,
            });
        }
        catch (Exception)
        {
            // History is valuable, but losing a row must never change what did or did not run.
        }
    }

    private static string DescribeTarget(PrivilegedRequest request) => request.Kind switch
    {
        PrivilegedOperationKind.Terminal => (request.Shell ?? "powershell") +
                                            (string.IsNullOrWhiteSpace(request.InitialCommand) ? "" : " > " + request.InitialCommand),
        PrivilegedOperationKind.Run or PrivilegedOperationKind.Launch =>
            OperationValidator.Display(request.Executable ?? "(none)", request.Arguments ?? Array.Empty<string>()),
        PrivilegedOperationKind.Service => (request.ServiceName ?? "(none)") + " " + (request.ServiceAction ?? ""),
        PrivilegedOperationKind.Cancel => "operation " + request.OperationId,
        _ => request.Kind.ToString(),
    };

    private static string Shorten(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }
}
