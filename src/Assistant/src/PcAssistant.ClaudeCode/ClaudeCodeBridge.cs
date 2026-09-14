using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using PcAssistant.Core;
using PcAssistant.Core.Approvals;
using PcAssistant.Core.Bridge;
using PcAssistant.Core.Elevation;
using PcAssistant.Core.Logging;
using PcAssistant.Core.Model;
using PcAssistant.Core.Persistence;
using PcAssistant.Core.Policy;
using PcAssistant.Elevation;

namespace PcAssistant.ClaudeCode;

/// <summary>
/// The only component that knows how to talk to the Claude Code CLI.
///
/// Integration shape (Claude Code 2.1.247):
///   claude -p --output-format stream-json --verbose --include-hook-events
///          --session-id &lt;uuid&gt; | --resume &lt;uuid&gt;
///          --permission-mode &lt;mode&gt; --settings &lt;hooks.json&gt;
///          --append-system-prompt &lt;PC Assistant behaviour&gt;
///   with the prompt written to stdin and the workspace as the process working directory.
///
/// One process per turn. Follow-ups resume the same Claude session id, which is what
/// keeps a task's conversation continuous across turns and across app restarts.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClaudeCodeBridge : IClaudeCodeBridge
{
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly IApprovalBroker _approvals;
    private readonly Func<Guid, AssistantMode> _modeLookup;
    private readonly ClaudeRuntimeFiles _runtimeFiles;
    private readonly string _behaviour;

    /// <summary>
    /// Present when PC Assistant can perform administrator operations. Null keeps the whole
    /// privileged path unreachable rather than half-wired, which is what the tests without a
    /// database rely on.
    /// </summary>
    private readonly PrivilegedOperationService? _privileged;

    /// <summary>
    /// What each task has learned about the browser — the tabs it opened, and the labels on
    /// the page it is looking at. Per task rather than per turn, because a tab opened in one
    /// turn is still that task's to close in the next one.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, BrowserContext> _browsers = new();

    /// <summary>
    /// Base permission mode handed to Claude Code. PC Assistant's PreToolUse hook is the
    /// real gate: it returns an explicit allow for permitted operations, so this only
    /// matters if a tool call ever bypasses the hook — in which case strict is what we
    /// want. PC Assistant never passes a permission-skipping mode.
    /// </summary>
    private string BasePermissionMode =>
        "manual";

    public ClaudeCodeBridge(
        AppPaths paths,
        AppSettings settings,
        IApprovalBroker approvals,
        Func<Guid, AssistantMode> modeLookup,
        string? hookHostPath = null,
        string? pcToolsPath = null,
        PrivilegedOperationService? privileged = null)
    {
        _paths = paths;
        _settings = settings;
        _approvals = approvals;
        _modeLookup = modeLookup;
        _runtimeFiles = new ClaudeRuntimeFiles(paths.Runtime, hookHostPath, pcToolsPath);
        _behaviour = ClaudeRuntimeFiles.LoadSkillBehaviour();
        _privileged = privileged;
    }

    /// <summary>Updated whenever a real turn reveals the true auth state.</summary>
    public ClaudeStatus? LastObservedStatus { get; private set; }

    private static readonly TimeSpan LoginRefusalMemory = TimeSpan.FromMinutes(2);
    private DateTimeOffset _loginRefusedAt = DateTimeOffset.MinValue;

    public event EventHandler<ClaudeStatus>? StatusChanged;

    // ---------------------------------------------------------------- status

    public async Task<ClaudeStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var install = ClaudeCodeLocator.Locate(_settings.ClaudeExecutableOverride);
        if (install is null)
            return Publish(new ClaudeStatus(ClaudeAvailability.NotInstalled,
                Detail: "No claude executable found on PATH or in the Claude desktop bundle."));

        string? version;
        try
        {
            var (exit, stdout, stderr) = await RunShortAsync(
                install.ExecutablePath, new[] { "--version" }, cancellationToken).ConfigureAwait(false);

            if (exit != 0)
                return Publish(new ClaudeStatus(ClaudeAvailability.FailedToStart, null, install.ExecutablePath,
                    string.IsNullOrWhiteSpace(stderr) ? stdout : stderr));

            version = stdout.Trim();
        }
        catch (Exception ex)
        {
            return Publish(new ClaudeStatus(ClaudeAvailability.FailedToStart, null, install.ExecutablePath, ex.Message));
        }

        try
        {
            var (exit, stdout, _) = await RunShortAsync(
                install.ExecutablePath, new[] { "auth", "status" }, cancellationToken).ConfigureAwait(false);

            var loggedIn = exit == 0 && LooksLoggedIn(stdout);
            if (!loggedIn)
                return Publish(new ClaudeStatus(ClaudeAvailability.AuthenticationRequired, version,
                    install.ExecutablePath, "Run: claude auth login"));
        }
        catch (Exception ex)
        {
            return Publish(new ClaudeStatus(ClaudeAvailability.FailedToStart, version, install.ExecutablePath, ex.Message));
        }

        // A previous turn may have proven that the stored login cannot actually be used. Believed for a
        // while rather than forever: the owner fixes that at the PC without restarting PC Assistant, and a
        // phone that is refused new work while this says "not ready" would otherwise stay refused. If the
        // login is still unusable, the next turn proves it again.
        if (LastObservedStatus?.Availability == ClaudeAvailability.AuthenticationRequired &&
            DateTimeOffset.UtcNow - _loginRefusedAt < LoginRefusalMemory)
            return LastObservedStatus with { Version = version, ExecutablePath = install.ExecutablePath };

        return Publish(new ClaudeStatus(ClaudeAvailability.Ready, version, install.ExecutablePath, install.Source));
    }

    private static bool LooksLoggedIn(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("loggedIn", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return json.Contains("loggedIn", StringComparison.OrdinalIgnoreCase) &&
                   json.Contains("true", StringComparison.OrdinalIgnoreCase);
        }
    }

    private ClaudeStatus Publish(ClaudeStatus status)
    {
        LastObservedStatus = status;
        StatusChanged?.Invoke(this, status);
        return status;
    }

    // ------------------------------------------------------------------ run

    public async Task<BridgeTurnResult> RunTurnAsync(
        BridgeTurnRequest request,
        Action<AssistantEvent> onEvent,
        CancellationToken cancellationToken)
    {
        var install = ClaudeCodeLocator.Locate(_settings.ClaudeExecutableOverride);
        if (install is null)
        {
            return new BridgeTurnResult(false, false, null, null,
                "Claude Code is not installed, or PC Assistant could not find claude.exe. " +
                "Set the executable path in settings if it lives somewhere unusual.");
        }

        Directory.CreateDirectory(request.WorkingDirectory);

        using var rawLog = new RawLogWriter(
            _paths.RawLogFor(request.TaskId),
            Math.Max(1, _settings.RawLogMaxMegabytes) * 1024L * 1024L);

        var browser = BrowserFor(request.TaskId);
        var normalizer = new StreamEventNormalizer(request.TaskId)
        {
            // Element labels and created tab ids come back in tool results, and they are what
            // let a click be judged by what it hits rather than by the tool that made it.
            ToolResultObserver = (tool, text) =>
            {
                if (!tool.StartsWith(BrowserActionPolicy.Prefix, StringComparison.Ordinal)) return;
                var name = tool[BrowserActionPolicy.Prefix.Length..];
                if (name is "read_page" or "find" or "get_page_text") browser.RememberLabels(text);
                else if (name == "tabs_create_mcp") browser.RememberCreatedTabs(text);
            },
        };
        var settingsFile = _runtimeFiles.WriteSettings(request.TaskId, _settings.ApprovalTimeoutMinutes);

        if (!_runtimeFiles.HookHostExists)
        {
            rawLog.Write("pcassistant",
                "WARNING: hook helper not found at " + _runtimeFiles.HookHostPath +
                " - approvals cannot be enforced, refusing to run.");

            return new BridgeTurnResult(false, false, null, null,
                "PC Assistant's hook helper (PcAssistant.HookHost.exe) is missing, so approvals " +
                "could not be enforced. The turn was not started.");
        }

        await using var pipeServer = new ApprovalPipeServer(
            HookProtocol.PipeNameFor(request.TaskId),
            (hook, ct) => HandleHookAsync(request, hook, onEvent, rawLog, ct),
            diagnostic => rawLog.Write("pcassistant", diagnostic));

        pipeServer.Start();

        using var job = new ProcessJob("pcassistant-task-" + request.TaskId.ToString("N"));
        using var process = new Process();

        var args = BuildArguments(request, settingsFile);
        ConfigureStartInfo(process.StartInfo, install.ExecutablePath, request, args, pipeServer.PipeName);

        rawLog.Write("pcassistant", $"exe: {install.ExecutablePath}");
        rawLog.Write("pcassistant", $"cwd: {request.WorkingDirectory}");
        rawLog.Write("pcassistant", $"mode: {request.Mode}");
        rawLog.Write("pcassistant", "args: " + string.Join(' ', args));

        try
        {
            if (!process.Start())
                return new BridgeTurnResult(false, false, null, null, "Claude Code failed to start.");
        }
        catch (Exception ex)
        {
            Publish(new ClaudeStatus(ClaudeAvailability.FailedToStart, null, install.ExecutablePath, ex.Message));
            return new BridgeTurnResult(false, false, null, null, "Claude Code failed to start: " + ex.Message);
        }

        // Own the process tree before it can spawn anything, so Stop is complete.
        if (!job.Assign(process))
            rawLog.Write("pcassistant", "WARNING: could not assign the Claude process to the job object.");

        var cancelled = false;
        await using var cancelReg = cancellationToken.Register(() =>
        {
            cancelled = true;
            rawLog.Write("pcassistant", "cancellation requested - terminating owned process tree");
            _approvals.CancelTask(request.TaskId);
            try { job.TerminateAll(); } catch (Exception ex) { rawLog.Write("pcassistant", "terminate failed: " + ex.Message); }
        });

        try
        {
            await WritePromptAsync(process, request.Prompt, rawLog).ConfigureAwait(false);

            var stderrTask = PumpStderrAsync(process, rawLog);
            await PumpStdoutAsync(process, normalizer, onEvent, rawLog).ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            rawLog.Write("pcassistant", "stream error: " + ex.Message);
        }

        var exitCode = SafeExitCode(process);
        rawLog.Write("pcassistant", $"exit code: {exitCode}");

        var turn = normalizer.Turn;
        var sessionId = turn.SessionId ?? request.SessionId;

        if (cancelled || cancellationToken.IsCancellationRequested)
            return new BridgeTurnResult(false, true, sessionId, null, null);

        var finalText = turn.FinalText ?? normalizer.AssistantTexts.LastOrDefault();

        if (turn.IsError == true || exitCode != 0)
        {
            var error = turn.ErrorMessage
                        ?? (string.IsNullOrWhiteSpace(finalText) ? null : finalText)
                        ?? $"Claude Code exited with code {exitCode}.";

            if (LooksLikeAuthFailure(error))
            {
                _loginRefusedAt = DateTimeOffset.UtcNow;
                Publish(new ClaudeStatus(ClaudeAvailability.AuthenticationRequired,
                    LastObservedStatus?.Version, install.ExecutablePath, DescribeAuthFailure(error)));
            }

            return new BridgeTurnResult(false, false, sessionId, finalText, error);
        }

        return new BridgeTurnResult(true, false, sessionId, finalText, null);
    }

    /// <summary>
    /// The browser knowledge for a task, kept across its turns and bounded across tasks.
    /// Forgetting one only costs extra approvals, so the cap discards rather than grows.
    /// </summary>
    private BrowserContext BrowserFor(Guid taskId)
    {
        if (_browsers.Count > 32)
        {
            foreach (var stale in _browsers.Keys.Where(id => id != taskId).ToArray())
                _browsers.TryRemove(stale, out _);
        }
        return _browsers.GetOrAdd(taskId, _ => new BrowserContext());
    }

    public static bool LooksLikeAuthFailure(string? message) =>
        message is not null &&
        (message.Contains("authenticate", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("OAuth", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("log in", StringComparison.OrdinalIgnoreCase) ||
         IsSubscriptionRefusal(message));

    /// <summary>
    /// Signed in, and refused anyway. Measured on this machine with Claude Code 2.1.270: <c>claude auth
    /// status</c> reported a logged-in Pro account while every turn came back 403 with "Your organization
    /// has disabled Claude subscription access for Claude Code". The status check alone called that
    /// Ready, and a phone was offered a task that could only fail.
    /// </summary>
    private static bool IsSubscriptionRefusal(string message) =>
        message.Contains("subscription access", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the owner is told. A refusal like the one above is not fixed by logging in again.</summary>
    public static string DescribeAuthFailure(string? message) =>
        message is not null && IsSubscriptionRefusal(message)
            ? "Claude Code is signed in but refused: " + message.ReplaceLineEndings(" ").Trim()
            : "Claude Code could not use the stored login. Run: claude auth login";

    private List<string> BuildArguments(BridgeTurnRequest request, string settingsFile)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--include-hook-events",
            "--include-partial-messages",
            "--permission-mode", BasePermissionMode,
            "--settings", settingsFile,
        };

        if (_settings.EnableChromeIntegration) args.Add("--chrome");
        else args.Add("--no-chrome");
        if (_settings.EnablePcTools)
        {
            args.Add("--strict-mcp-config");
            args.Add("--mcp-config");
            args.Add(_runtimeFiles.WriteMcpSettings(request.TaskId));
        }

        // Continuity: the first turn creates the session, every follow-up resumes it.
        if (request.Resume)
        {
            args.Add("--resume");
            args.Add(request.SessionId);
        }
        else
        {
            args.Add("--session-id");
            args.Add(request.SessionId);
        }

        if (!string.IsNullOrWhiteSpace(_behaviour))
        {
            args.Add("--append-system-prompt");
            args.Add(_behaviour + Environment.NewLine + ModeReminder(request.Mode));
        }

        return args;
    }

    private static string ModeReminder(AssistantMode mode) => mode == AssistantMode.Auto
        ? "\nThis task is in AUTO mode: ordinary work in the workspace proceeds; high-impact operations still stop for the owner."
        : "\nThis task is in ASK mode: reading and inspecting is free, but every file change and every command is put to the owner first. Expect approvals and keep them few and meaningful.";

    private void ConfigureStartInfo(
        ProcessStartInfo psi, string exePath, BridgeTurnRequest request,
        IEnumerable<string> args, string pipeName)
    {
        psi.FileName = exePath;
        psi.WorkingDirectory = request.WorkingDirectory;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;          // no terminal window is ever shown
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);

        foreach (var a in args) psi.ArgumentList.Add(a);

        psi.Environment[HookProtocol.PipeEnvironmentVariable] = pipeName;

        // Inherited by the PC tools server Claude Code starts, so its input tools can refuse
        // to operate PC Assistant's own windows — where the approval cards live.
        psi.Environment["PCASSISTANT_OWNER_PID"] = Environment.ProcessId.ToString();

        // If PC Assistant was itself started from inside a Claude Code session, these
        // would make the child think it is a nested session of that host.
        foreach (var key in new[]
                 {
                     "CLAUDECODE", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_HOST_SESSION_ID",
                     "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_MESSAGING_SOCKET",
                     "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_PID", "CLAUDE_CODE_ENTRYPOINT",
                 })
        {
            psi.Environment.Remove(key);
        }
    }

    private static async Task WritePromptAsync(Process process, string prompt, RawLogWriter rawLog)
    {
        // The prompt goes over stdin so its length and quoting never touch the command line.
        try
        {
            await process.StandardInput.WriteAsync(prompt).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            try { process.StandardInput.Close(); }
            catch (Exception ex) { rawLog.Write("pcassistant", "stdin close: " + ex.Message); }
        }
    }

    private static async Task PumpStdoutAsync(
        Process process, StreamEventNormalizer normalizer, Action<AssistantEvent> onEvent, RawLogWriter rawLog)
    {
        while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            rawLog.Write("stdout", line);

            IEnumerable<AssistantEvent> events;
            try
            {
                events = normalizer.Consume(line).ToList();
            }
            catch (Exception ex)
            {
                // Malformed provider output degrades to raw logging, never a crash.
                rawLog.Write("pcassistant", "normalizer error: " + ex.Message);
                continue;
            }

            foreach (var e in events)
            {
                try { onEvent(e); }
                catch (Exception ex) { rawLog.Write("pcassistant", "event sink error: " + ex.Message); }
            }
        }
    }

    private static async Task PumpStderrAsync(Process process, RawLogWriter rawLog)
    {
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            rawLog.Write("stderr", line);
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : -1; }
        catch (InvalidOperationException) { return -1; }
    }

    // ---------------------------------------------------------------- hooks

    private async Task<HookReply> HandleHookAsync(
        BridgeTurnRequest request,
        HookRequest hook,
        Action<AssistantEvent> onEvent,
        RawLogWriter rawLog,
        CancellationToken cancellationToken)
    {
        rawLog.Write("hook", $"{hook.HookEventName} {hook.ToolName}");

        // Administrator operations are decided and performed by PC Assistant itself, so they
        // arrive on their own event and return a result rather than a bare allow/deny.
        if (string.Equals(hook.HookEventName, HookProtocol.ElevatedStatusEvent, StringComparison.Ordinal))
            return await HandleElevatedStatusAsync(cancellationToken).ConfigureAwait(false);

        if (string.Equals(hook.HookEventName, HookProtocol.ElevatedEvent, StringComparison.Ordinal))
            return await HandleElevatedAsync(request, hook, onEvent, rawLog, cancellationToken).ConfigureAwait(false);

        var nativeGate = string.Equals(hook.HookEventName, "PcToolUse", StringComparison.Ordinal);
        if (!nativeGate && !string.Equals(hook.HookEventName, "PreToolUse", StringComparison.OrdinalIgnoreCase))
            return HookReply.Allow();

        // The local server calls the same broker immediately before the native operation.
        // Avoid asking twice for a single action; unknown tools never use this path.
        if (!nativeGate && _settings.EnablePcTools && PcToolPolicy.IsKnown(hook.ToolName))
            return HookReply.Allow("The PC tools server enforces this action through the approval broker.");
        if (nativeGate && !PcToolPolicy.IsKnown(hook.ToolName))
            return HookReply.Deny("Unrecognized PC tool.");

        var mode = _modeLookup(request.TaskId);

        RiskAssessment assessment;
        try
        {
            var classifier = new RiskClassifier
            {
                AdditionalAllowedRoots = new[] { _paths.Runtime, Path.GetTempPath() },
                AdditionalReadRoots = OwnerReadRoots(),
                Browser = BrowserFor(request.TaskId),
            };

            var invocation = ToolInvocation.FromJson(
                hook.ToolName,
                hook.ToolInput,
                request.WorkingDirectory);

            assessment = classifier.Classify(invocation);
        }
        catch (Exception ex)
        {
            // An unclassifiable call is treated as high impact, so it asks rather than runs.
            rawLog.Write("pcassistant", "classifier error: " + ex.Message);
            assessment = new RiskAssessment(RiskCategory.CommandHighImpact, hook.ToolName, hook.ToolName);
        }

        // A navigation replaces the page, so every element label learned from the old one is
        // now a description of something that is gone. Cleared after classification, which for
        // navigate does not use labels anyway.
        if (hook.ToolName == BrowserActionPolicy.Prefix + "navigate" &&
            _browsers.TryGetValue(request.TaskId, out var navigating)) navigating.PageChanged();

        var decision = ApprovalPolicy.Evaluate(mode, assessment.Category);
        rawLog.Write("hook", $"policy: {mode}/{assessment.Category} -> {decision}");

        if (decision == PolicyDecision.Allow)
        {
            GrantForeground(nativeGate, hook, rawLog);
            return HookReply.Allow($"{mode} mode allows {assessment.Category}.");
        }

        var scope = PcToolPolicy.Scope(ToolInvocation.FromJson(hook.ToolName, hook.ToolInput, request.WorkingDirectory), assessment.Category);
        var approvalRequest = new ApprovalRequest
        {
            TaskId = request.TaskId,
            ToolName = hook.ToolName,
            Action = DescribeAction(hook.ToolName, assessment.Category),
            Target = assessment.Target,
            Reason = BrowserActionPolicy.Explain(
                         ToolInvocation.FromJson(hook.ToolName, hook.ToolInput, request.WorkingDirectory), assessment.Category)
                     ?? ApprovalPolicy.ExplainAsk(mode, assessment.Category),
            Category = assessment.Category,
            ScopeKey = scope.Key,
            ScopeDescription = scope.Description,
        };

        onEvent(new AssistantEvent
        {
            TaskId = request.TaskId,
            Kind = AssistantEventKind.ApprovalRequired,
            Title = approvalRequest.Action + ": " + assessment.Target,
            Detail = approvalRequest.Reason,
            ToolName = hook.ToolName,
        });

        var outcome = await _approvals.RequestAsync(approvalRequest, cancellationToken).ConfigureAwait(false);

        onEvent(new AssistantEvent
        {
            TaskId = request.TaskId,
            Kind = AssistantEventKind.ApprovalResolved,
            Title = outcome switch
            {
                ApprovalOutcome.Allowed => "Allowed: " + assessment.Target,
                ApprovalOutcome.Denied => "Denied: " + assessment.Target,
                ApprovalOutcome.TimedOut => "No answer, denied: " + assessment.Target,
                _ => "Cancelled: " + assessment.Target,
            },
            ToolName = hook.ToolName,
        });

        rawLog.Write("hook", $"approval {approvalRequest.Id} -> {outcome}");

        if (outcome == ApprovalOutcome.Allowed) GrantForeground(nativeGate, hook, rawLog);

        return outcome == ApprovalOutcome.Allowed
            ? HookReply.Allow("Approved by the owner.")
            : HookReply.Deny(outcome switch
            {
                ApprovalOutcome.Denied => "The owner denied this operation. Do not retry it or work around it; explain what you wanted to do instead.",
                ApprovalOutcome.TimedOut => "The owner did not respond, so this was not run.",
                _ => "The task was stopped.",
            });
    }

    // ------------------------------------------------------- privileged path

    private async Task<HookReply> HandleElevatedStatusAsync(CancellationToken cancellationToken)
    {
        if (_privileged is null)
            return HookReply.Deny("Administrator operations are not wired up in this PC Assistant instance.");

        var text = await _privileged.DescribeStatusAsync(cancellationToken).ConfigureAwait(false);
        return new HookReply { Decision = "allow", Reason = text, Result = text };
    }

    /// <summary>
    /// The one place an administrator operation is authorized. The PC tools process only
    /// carries the request and the answer; it holds no privileged policy, exactly like the
    /// hook helper.
    /// </summary>
    private async Task<HookReply> HandleElevatedAsync(
        BridgeTurnRequest request, HookRequest hook, Action<AssistantEvent> onEvent,
        RawLogWriter rawLog, CancellationToken cancellationToken)
    {
        if (_privileged is null)
            return HookReply.Deny("Administrator operations are not available in this PC Assistant instance.");

        PrivilegedRequest? operation;
        try
        {
            operation = string.IsNullOrWhiteSpace(hook.Payload)
                ? null
                : JsonSerializer.Deserialize<PrivilegedRequest>(hook.Payload, PrivilegedProtocol.Json);
        }
        catch (JsonException ex)
        {
            rawLog.Write("pcassistant", "elevated payload parse error: " + ex.Message);
            operation = null;
        }

        if (operation is null)
            return HookReply.Deny("PC Assistant could not read that administrator request.");

        var context = new PrivilegedRequestContext(request.TaskId, request.TaskTitle, onEvent);
        var outcome = await _privileged.RequestAsync(context, operation, cancellationToken).ConfigureAwait(false);

        rawLog.Write("hook", $"elevated {operation.Kind} -> {outcome.Status}");

        return new HookReply
        {
            Decision = outcome.Succeeded ? "allow" : "deny",
            Reason = outcome.Message,
            Result = JsonSerializer.Serialize(new
            {
                ran = outcome.Ran,
                status = outcome.Status.ToString(),
                message = outcome.Message,
                processId = outcome.Response?.ProcessId,
                exitCode = outcome.Response?.ExitCode,
                elevated = outcome.Response?.Elevated,
                tokenUser = outcome.Response?.TokenUser,
                sessionId = outcome.Response?.SessionId,
                operationId = outcome.Response?.OperationId,
                stdout = outcome.Response?.StandardOutput,
                stderr = outcome.Response?.StandardError,
                durationMs = outcome.Response?.DurationMilliseconds,
            }, PrivilegedProtocol.Json),
        };
    }

    /// <summary>
    /// Handing the PC tools process the foreground right as the approval is granted. It has to happen here,
    /// while the owner's click still makes this the foreground process, and before the reply unblocks the
    /// waiting tool call.
    /// </summary>
    private static void GrantForeground(bool nativeGate, HookRequest hook, RawLogWriter rawLog)
    {
        if (!nativeGate || hook.ProcessId <= 0) return;
        var granted = ForegroundHandover.GrantTo(hook.ProcessId);
        rawLog.Write("hook", $"foreground privilege -> pid {hook.ProcessId}: {(granted ? "granted" : "refused")}");
    }

    private static string DescribeAction(string toolName, RiskCategory category) => category switch
    {
        RiskCategory.WorkspaceWrite => "Claude wants to modify",
        RiskCategory.OutsideWorkspace => "Claude wants to touch a path outside the workspace",
        RiskCategory.DestructiveGit => "Claude wants to run a Git operation",
        RiskCategory.SystemChange => "Claude wants to change system configuration",
        RiskCategory.Elevation => "Claude wants administrator rights",
        RiskCategory.ProcessControl => "Claude wants to control a process",
        RiskCategory.CommandHighImpact => "Claude wants to run a high-impact command",
        RiskCategory.CommandSafe => "Claude wants to run",
        RiskCategory.DesktopAction => "Claude wants to act on the desktop",
        RiskCategory.InteractiveInput => "Claude wants to interact with an application",
        _ => "Claude wants to use " + toolName,
    };

    // -------------------------------------------------------------- helpers

    private static string[] OwnerReadRoots() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
    ];

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunShortAsync(
        string exe, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = exe;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        foreach (var a in args) process.StartInfo.ArgumentList.Add(a);

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException("claude did not respond within 60 seconds.");
        }

        return (process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
    }
}
