using System.ComponentModel;
using ModelContextProtocol.Server;
using PcAssistant.Elevation;

namespace PcAssistant.PcTools;

/// <summary>
/// The administrator surface a model can see: one status question and one request, whose
/// shape is the four validated operations and nothing else.
///
/// There is no "run this string as SYSTEM" here, and no way to compose one. Every request is
/// classified by PC Assistant, put to the owner as its literal command, validated again by
/// the privileged helper, and executed with the owner's own administrator token.
/// </summary>
[McpServerToolType]
public sealed class ElevatedTools
{
    [McpServerTool(Name = "elevated_status", ReadOnly = true), Description(
        "Check whether owner-approved Administrator operations are possible before proposing one. Reports " +
        "whether the privileged helper is installed, running, protocol-compatible and able to elevate. " +
        "Asks the owner for nothing and changes nothing. If it is not ready, do not retry elevated_request: " +
        "tell the owner what you needed and why.")]
    public Task<string> ElevatedStatus(CancellationToken cancellationToken)
        => ElevationGate.StatusAsync(cancellationToken);

    [McpServerTool(Name = "elevated_request"), Description(
        "Ask the owner to approve one Administrator operation, then perform it through PC Assistant's " +
        "privileged helper and report the real result. The owner is always asked, in both AUTO and ASK: " +
        "elevation never becomes automatic and there is no persistent grant, so keep requests few and " +
        "meaningful. Always give a specific 'reason'; the owner sees it verbatim next to the literal command. " +
        "operation=terminal opens a visible Administrator PowerShell/cmd on the owner's desktop (you cannot " +
        "then type into it: elevated windows are outside this process's integrity level). operation=run " +
        "executes one program with an argument list and returns stdout, stderr and the exit code. " +
        "operation=launch starts a visible elevated program, such as an installer, and returns its process id. " +
        "operation=service starts, stops, restarts or queries one named Windows service. Give 'executable' as " +
        "a full path to a .exe; arguments are a list, never a shell string, and are never re-parsed by a shell. " +
        "A denial means nothing ran at all: do not retry it or look for an unprivileged way around it.")]
    public Task<string> ElevatedRequest(
        string operation,
        string reason,
        CancellationToken cancellationToken,
        string? shell = null,
        string? initialCommand = null,
        string? executable = null,
        string[]? arguments = null,
        string? workingDirectory = null,
        int timeoutSeconds = 0,
        string? serviceName = null,
        string? serviceAction = null)
    {
        var kind = operation?.Trim().ToLowerInvariant() switch
        {
            "terminal" => PrivilegedOperationKind.Terminal,
            "run" => PrivilegedOperationKind.Run,
            "launch" => PrivilegedOperationKind.Launch,
            "service" => PrivilegedOperationKind.Service,
            _ => throw new ArgumentException("operation must be terminal, run, launch or service."),
        };

        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Give the owner a specific reason for needing Administrator rights.");

        var request = new PrivilegedRequest
        {
            Kind = kind,
            Reason = reason.Trim(),
            Shell = shell,
            InitialCommand = initialCommand,
            Executable = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            TimeoutSeconds = timeoutSeconds,
            ServiceName = serviceName,
            ServiceAction = serviceAction,
        };

        // Validated here purely so an obviously wrong call fails immediately with a useful
        // message instead of spending the owner's attention. PC Assistant and the privileged
        // helper both validate it again; neither trusts this.
        var validation = OperationValidator.Validate(request);
        if (!validation.Ok) throw new ArgumentException(validation.Error);

        return ElevationGate.RequestAsync(request, cancellationToken);
    }
}
