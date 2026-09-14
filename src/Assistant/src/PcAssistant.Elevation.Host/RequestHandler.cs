using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;

namespace PcAssistant.Elevation.Host;

/// <summary>
/// Everything the privileged helper decides, in one place.
///
/// The order is deliberate and none of it depends on the client having been well behaved:
///
///   who is calling  →  is the protocol the one I speak  →  is this request fresh and new
///   →  are the parameters valid  →  do it  →  record it
///
/// "Claude said this was safe" is not an input to any of these steps. Neither is PC
/// Assistant's own claim that the owner approved: the owner's approval is what stops the
/// request from being sent at all, and this side re-checks everything it can check itself.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RequestHandler
{
    private const int MediumIntegrity = 0x2000;
    private const int MaxRememberedRequests = 512;
    private const int MaxConcurrentOperations = 4;

    private readonly HelperState _state;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RunningOperation> _running = new(StringComparer.Ordinal);

    private static readonly string HelperVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    /// <summary>
    /// Holds the session rather than a raw job handle, so cancelling takes the same lock the
    /// session's teardown does and can never act on a handle that has already been closed.
    /// </summary>
    private sealed record RunningOperation(
        string Id, ElevatedRunnerSession Session, string ClientSid, string Target,
        bool Interruptible, DateTimeOffset StartedAt);

    public RequestHandler(HelperState state, Action<string> log)
    {
        _state = state;
        _log = log;
    }

    public string Handle(ClientIdentity client, string requestLine)
    {
        PrivilegedRequest? request = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(requestLine))
                request = JsonSerializer.Deserialize<PrivilegedRequest>(requestLine, PrivilegedProtocol.Json);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null)
        {
            AuditLog.Write("Malformed", client, null, "-", "Invalid", "unparsable or oversized request");
            return Serialize(new PrivilegedResponse
            {
                Status = PrivilegedStatus.Invalid,
                Message = "The privileged helper could not read that request.",
                HelperVersion = HelperVersion,
            });
        }

        var response = Evaluate(client, request);
        return Serialize(response with { HelperVersion = HelperVersion, RequestId = request.RequestId });
    }

    private PrivilegedResponse Evaluate(ClientIdentity client, PrivilegedRequest request)
    {
        // 1 - identity. Refused before anything about the operation is even looked at.
        if (Authorize(client) is { } refusal)
        {
            AuditLog.Write(request.Kind.ToString(), client, request.TaskId, "-", "Denied", refusal);
            return PrivilegedResponse.For(request, PrivilegedStatus.Denied, refusal);
        }

        // 2 - protocol. An older helper must not act on a newer client's request shape.
        if (request.Protocol != PrivilegedProtocol.Version)
        {
            var message = $"This privileged helper speaks protocol {PrivilegedProtocol.Version} " +
                          $"(version {HelperVersion}); the client sent protocol {request.Protocol} " +
                          $"(version {request.ClientVersion ?? "unknown"}). Reinstall the helper from PC Assistant.";
            AuditLog.Write(request.Kind.ToString(), client, request.TaskId, "-", "VersionMismatch", message);
            return PrivilegedResponse.For(request, PrivilegedStatus.VersionMismatch, message);
        }

        // 3 - freshness and replay.
        if (Freshness(request) is { } stale)
        {
            AuditLog.Write(request.Kind.ToString(), client, request.TaskId, "-", "Invalid", stale);
            return PrivilegedResponse.For(request, PrivilegedStatus.Invalid, stale);
        }

        // 4 - parameters, validated here and not because the client validated them.
        var validation = OperationValidator.Validate(request);
        if (!validation.Ok)
        {
            AuditLog.Write(request.Kind.ToString(), client, request.TaskId, Describe(request), "Invalid", validation.Error);
            return PrivilegedResponse.For(request, PrivilegedStatus.Invalid, validation.Error!);
        }

        var description = validation.Description!;

        try
        {
            var response = request.Kind switch
            {
                PrivilegedOperationKind.Handshake or PrivilegedOperationKind.Status => Status(client, request),
                PrivilegedOperationKind.Cancel => Cancel(client, request),
                PrivilegedOperationKind.Terminal => Terminal(client, request, description),
                PrivilegedOperationKind.Launch => Launch(client, request, description),
                PrivilegedOperationKind.Run => Run(client, request, description),
                PrivilegedOperationKind.Service => Service(client, request, description),
                _ => PrivilegedResponse.For(request, PrivilegedStatus.Invalid, "Unsupported operation."),
            };

            if (request.Kind is not (PrivilegedOperationKind.Handshake or PrivilegedOperationKind.Status))
            {
                AuditLog.Write(request.Kind.ToString(), client, request.TaskId, description.Target,
                    response.Status.ToString(),
                    response.ExitCode is { } code ? "exit=" + code : response.Message);
            }

            return response;
        }
        catch (Exception ex)
        {
            _log("operation failed: " + ex);
            AuditLog.Write(request.Kind.ToString(), client, request.TaskId, description.Target, "Failed", ex.Message);
            return PrivilegedResponse.For(request, PrivilegedStatus.Failed, ex.Message);
        }
    }

    // ------------------------------------------------------------- identity

    private string? Authorize(ClientIdentity client)
    {
        if (string.IsNullOrWhiteSpace(_state.OwnerSid))
            return "This helper has no recorded owner. Reinstall it from PC Assistant.";

        if (!string.Equals(client.UserSid, _state.OwnerSid, StringComparison.OrdinalIgnoreCase))
        {
            return $"This privileged helper was installed for {_state.OwnerAccount} and refuses requests " +
                   $"from {(string.IsNullOrEmpty(client.UserName) ? "another account" : client.UserName)}.";
        }

        if (client.IntegrityLevel != 0 && client.IntegrityLevel < MediumIntegrity)
            return "Requests from low-integrity or sandboxed processes are refused.";

        if (client.SessionId == 0)
            return "Requests from session 0 are refused; privileged operations belong to an interactive desktop session.";

        // Defence in depth, not a boundary: on Windows a same-user, same-integrity process can
        // already act as its peers, so this raises the bar without pretending to be a wall.
        if (!string.IsNullOrWhiteSpace(_state.ClientExecutable) &&
            !string.Equals(client.ImagePath, _state.ClientExecutable, StringComparison.OrdinalIgnoreCase))
        {
            return "This helper was installed for " + _state.ClientExecutable +
                   ", but the request came from " + (client.ImagePath ?? "an unidentified process") +
                   ". Reinstall the privileged helper from the copy of PC Assistant you actually run.";
        }

        return null;
    }

    private string? Freshness(PrivilegedRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 64)
            return "Every request needs a request id of at most 64 characters.";

        var age = DateTimeOffset.UtcNow - request.IssuedAt;
        if (age > PrivilegedProtocol.MaxRequestAge || age < -PrivilegedProtocol.MaxRequestAge)
        {
            return $"This request is timestamped {request.IssuedAt:o}, which is more than " +
                   $"{PrivilegedProtocol.MaxRequestAge.TotalMinutes:0} minutes from the helper's clock. Refused as stale.";
        }

        if (!_seenRequests.TryAdd(request.RequestId, DateTimeOffset.UtcNow))
            return "This request id has already been used. A privileged request is never replayed.";

        if (_seenRequests.Count > MaxRememberedRequests)
        {
            var cutoff = DateTimeOffset.UtcNow - PrivilegedProtocol.MaxRequestAge - PrivilegedProtocol.MaxRequestAge;
            foreach (var entry in _seenRequests.Where(e => e.Value < cutoff).ToList())
                _seenRequests.TryRemove(entry.Key, out _);
        }

        return null;
    }

    // ------------------------------------------------------------ operations

    private PrivilegedResponse Status(ClientIdentity client, PrivilegedRequest request)
    {
        var available = SessionElevation.ElevationAvailable(client.SessionId, out var account, out var reason);
        return new PrivilegedResponse
        {
            RequestId = request.RequestId,
            Status = PrivilegedStatus.Ok,
            ElevationAvailable = available,
            OwnerAccount = string.IsNullOrEmpty(account) ? _state.OwnerAccount : account,
            SessionId = (int)client.SessionId,
            Message = available
                ? $"Privileged helper {HelperVersion} ready for {_state.OwnerAccount} in session {client.SessionId}. " +
                  "Approved operations run with your administrator token, not as SYSTEM."
                // Says why, not just that. Readiness is checked by acquiring a real elevated
                // token, so a failure here is the same failure an operation would hit.
                : "The helper is running, but no administrator token could be acquired for this " +
                  $"account, so nothing can be elevated. {reason}".TrimEnd(),
        };
    }

    private PrivilegedResponse Terminal(ClientIdentity client, PrivilegedRequest request, OperationDescription description)
    {
        var shell = (request.Shell ?? "powershell").Trim().ToLowerInvariant();
        if (!TryResolveShell(shell, out var executable, out var shellError))
            return PrivilegedResponse.For(request, PrivilegedStatus.Invalid, shellError!);

        var arguments = new List<string>();
        if (shell == "cmd")
        {
            if (!string.IsNullOrWhiteSpace(request.InitialCommand)) arguments.AddRange(["/K", request.InitialCommand!]);
        }
        else
        {
            arguments.Add("-NoExit");
            if (!string.IsNullOrWhiteSpace(request.InitialCommand)) arguments.AddRange(["-Command", request.InitialCommand!]);
        }

        return StartVisible(client, request, executable!, arguments, description);
    }

    private PrivilegedResponse Launch(ClientIdentity client, PrivilegedRequest request, OperationDescription description)
        => StartVisible(client, request, Path.GetFullPath(request.Executable!),
            request.Arguments ?? Array.Empty<string>(), description);

    private PrivilegedResponse StartVisible(
        ClientIdentity client, PrivilegedRequest request, string executable,
        IReadOnlyList<string> arguments, OperationDescription description)
    {
        if (!SessionElevation.TryAcquire(client.SessionId, out var acquired, out var error))
            return PrivilegedResponse.For(request, PrivilegedStatus.NotElevatable, error);

        using var context = acquired!;
        {
            var launched = ElevatedLauncher.Launch(
                context, executable, OperationValidator.BuildCommandLine(executable, arguments),
                request.WorkingDirectory, visible: true, suspended: false);

            launched.CloseHandles();

            // Deliberately not tracked for cancellation. A visible elevated window belongs to
            // the owner's desktop from the moment it appears; PC Assistant does not get to
            // close the terminal it was asked to open.
            return new PrivilegedResponse
            {
                RequestId = request.RequestId,
                Status = PrivilegedStatus.Ok,
                ProcessId = launched.ProcessId,
                Elevated = launched.Elevated,
                TokenUser = context.Account,
                SessionId = (int)context.SessionId,
                Message = $"Started {Path.GetFileName(executable)} (pid {launched.ProcessId}) as {context.Account} " +
                          $"in session {context.SessionId} using the {context.Source}. " +
                          (launched.Elevated
                              ? "Windows reports the process token as elevated."
                              : "WARNING: Windows does not report this process as elevated."),
            };
        }
    }

    private PrivilegedResponse Run(ClientIdentity client, PrivilegedRequest request, OperationDescription description)
    {
        if (_running.Count >= MaxConcurrentOperations)
        {
            return PrivilegedResponse.For(request, PrivilegedStatus.Invalid,
                $"{MaxConcurrentOperations} privileged commands are already running. Wait for one to finish.");
        }

        if (!SessionElevation.TryAcquire(client.SessionId, out var acquired, out var error))
            return PrivilegedResponse.For(request, PrivilegedStatus.NotElevatable, error);

        using var context = acquired!;
        {
            // The client names the operation so it can cancel it while it is still running.
            var operationId = request.OperationId ?? Guid.NewGuid().ToString("N");
            var timeout = request.TimeoutSeconds == 0 ? OperationValidator.DefaultTimeoutSeconds : request.TimeoutSeconds;

            using var runner = new ElevatedRunnerSession(context, operationId, _log);

            if (!_running.TryAdd(operationId, new RunningOperation(operationId, runner, client.UserSid,
                    description.Target, description.Interruptible, DateTimeOffset.Now)))
            {
                return PrivilegedResponse.For(request, PrivilegedStatus.Invalid,
                    "That operation id is already running. Every privileged command needs its own id.");
            }

            try
            {
                var result = runner.Execute(request with { TimeoutSeconds = timeout }, TimeSpan.FromSeconds(timeout));
                return result with
                {
                    RequestId = request.RequestId,
                    OperationId = operationId,
                    Elevated = true,
                    TokenUser = context.Account,
                    SessionId = (int)context.SessionId,
                };
            }
            finally
            {
                _running.TryRemove(operationId, out _);
            }
        }
    }

    private PrivilegedResponse Cancel(ClientIdentity client, PrivilegedRequest request)
    {
        if (!_running.TryGetValue(request.OperationId!, out var operation))
        {
            return PrivilegedResponse.For(request, PrivilegedStatus.Invalid,
                "No such running privileged operation. It may already have finished.");
        }

        if (!string.Equals(operation.ClientSid, client.UserSid, StringComparison.OrdinalIgnoreCase))
            return PrivilegedResponse.For(request, PrivilegedStatus.Denied, "That operation belongs to another account.");

        if (!operation.Interruptible)
        {
            return PrivilegedResponse.For(request, PrivilegedStatus.Denied,
                "This operation installs or reconfigures software. Terminating it could leave the system " +
                "half-configured, so the helper will not kill it. It is still running and will report its own result.");
        }

        if (!operation.Session.TryTerminate())
        {
            return PrivilegedResponse.For(request, PrivilegedStatus.Invalid,
                "That operation finished before it could be cancelled; its own result stands.");
        }

        AuditLog.Write("Cancel", client, request.TaskId, operation.Target, "Cancelled");

        return PrivilegedResponse.For(request, PrivilegedStatus.Ok,
            "Terminated that operation's own process tree. Nothing else was touched.");
    }

    private PrivilegedResponse Service(ClientIdentity client, PrivilegedRequest request, OperationDescription description)
    {
        if (!SessionElevation.TryAcquire(client.SessionId, out var acquired, out var error))
            return PrivilegedResponse.For(request, PrivilegedStatus.NotElevatable, error);

        using var context = acquired!;
        {
            var action = request.ServiceAction!.Trim().ToLowerInvariant();
            var name = request.ServiceName!.Trim();
            var stopwatch = Stopwatch.StartNew();

            // Impersonating the owner's elevated token means the Service Control Manager
            // applies the owner's rights, not the helper's SYSTEM rights. A service the owner
            // could not control from an elevated console cannot be controlled from here either.
            var message = WindowsIdentity.RunImpersonated(context.Token, () =>
            {
                using var controller = new ServiceController(name);
                var timeout = TimeSpan.FromSeconds(30);

                switch (action)
                {
                    case "query":
                        return $"{controller.DisplayName} ({name}) is {controller.Status}.";
                    case "start":
                        if (controller.Status != ServiceControllerStatus.Running)
                        {
                            controller.Start();
                            controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
                        }
                        break;
                    case "stop":
                        if (controller.Status != ServiceControllerStatus.Stopped)
                        {
                            controller.Stop();
                            controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                        }
                        break;
                    default:
                        if (controller.Status != ServiceControllerStatus.Stopped)
                        {
                            controller.Stop();
                            controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                        }
                        controller.Start();
                        controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
                        break;
                }

                controller.Refresh();
                return $"{controller.DisplayName} ({name}) is now {controller.Status}.";
            });

            return new PrivilegedResponse
            {
                RequestId = request.RequestId,
                Status = PrivilegedStatus.Ok,
                Message = message,
                DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                Elevated = true,
                TokenUser = context.Account,
                SessionId = (int)context.SessionId,
            };
        }
    }

    // --------------------------------------------------------------- helpers

    private static bool TryResolveShell(string shell, out string? executable, out string? error)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        executable = shell switch
        {
            "cmd" => Path.Combine(system, "cmd.exe"),
            "powershell" => Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "pwsh" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PowerShell", "7", "pwsh.exe"),
            _ => null,
        };

        if (executable is null)
        {
            error = "Unknown shell.";
            return false;
        }

        if (!File.Exists(executable))
        {
            error = shell == "pwsh"
                ? "PowerShell 7 (pwsh.exe) is not installed at " + executable + ". Use powershell or cmd."
                : "Could not find " + executable + ".";
            executable = null;
            return false;
        }

        error = null;
        return true;
    }

    private static string Describe(PrivilegedRequest request) => request.Kind switch
    {
        PrivilegedOperationKind.Terminal => request.Shell ?? "powershell",
        PrivilegedOperationKind.Run or PrivilegedOperationKind.Launch =>
            OperationValidator.Display(request.Executable ?? "-", request.Arguments ?? Array.Empty<string>()),
        PrivilegedOperationKind.Service => (request.ServiceName ?? "-") + " " + (request.ServiceAction ?? "-"),
        _ => "-",
    };

    private static string Serialize(PrivilegedResponse response)
        => JsonSerializer.Serialize(response, PrivilegedProtocol.Json);
}
