using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using PcAssistant.Core.Approvals;
using PcAssistant.Core.Persistence;
using PcAssistant.Elevation;

namespace PcAssistant.Core.Elevation;

/// <summary>One probe's result, written to the report file and asserted on by the test suite.</summary>
public sealed record VerificationFinding(
    string Name,
    string Outcome,
    string Detail,
    IReadOnlyDictionary<string, string> Facts);

public sealed record VerificationReport
{
    public DateTimeOffset StartedAt { get; init; }
    public double DurationSeconds { get; init; }

    /// <summary>The image path Windows reports for this process on the helper's pipe.</summary>
    public string ClientImagePath { get; init; } = string.Empty;
    public int ClientProcessId { get; init; }
    public bool ClientElevated { get; init; }
    public int SessionId { get; init; }
    public string ClientSid { get; init; } = string.Empty;

    /// <summary>Sampled throughout the run: a UAC consent prompt would have shown up here.</summary>
    public bool ConsentPromptSeen { get; init; }

    public string? Error { get; init; }
    public IReadOnlyList<VerificationFinding> Findings { get; init; } = [];
}

/// <summary>
/// Live verification of the privileged boundary, run from inside the trusted PC Assistant
/// executable itself.
///
/// This exists because of a real measurement, not a hypothetical one. The helper identifies
/// its caller from the connection — owner SID, integrity, session, and the image path
/// recorded at install time — so a request from an xUnit test host is refused before it is
/// even parsed, exactly as it should be. That refusal proves the boundary works; it proves
/// nothing about elevation. The only way to exercise
///
///     PC Assistant  →  named pipe  →  LocalSystem service  →  owner's elevated token
///
/// is for the connection to originate from PC Assistant. So it does: the test suite starts
/// this executable, this class performs the probes through the ordinary
/// <see cref="PrivilegedClient"/> and <see cref="PrivilegedOperationService"/>, and the
/// results go to a file the suite reads back and asserts on.
///
/// What this deliberately does not do is take instructions. Every probe is a compile-time
/// constant: the command line names probes from a closed set and nothing else. No
/// executable, argument, service name or shell command can be passed through this mode, so
/// the trusted client cannot be turned into a confused deputy that runs someone else's
/// command with the owner's administrator token. The operations below are whoami, a cmd
/// exit code, a ping, a read-only service query, a stop of the helper's own service that
/// must be refused, and one visible elevated PowerShell that prints its groups and exits by
/// itself.
///
/// Nothing here relaxes a single check the helper makes. It changes who is calling, not what
/// the callee accepts.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PrivilegedVerification
{
    public const string Switch = "--verify-privileged";

    private static readonly string[] AllProbes =
    [
        "unelevated", "status", "elevated-token", "owner-not-system", "exit-code",
        "timeout", "cancel", "service-query", "self-protection",
        "approval-deny", "approval-allow", "elevated-window",
    ];

    /// <summary>
    /// Recognises <c>--verify-privileged &lt;probes&gt; &lt;report path&gt;</c> and, when present,
    /// runs it and reports the exit code. Returns false for an ordinary application start.
    /// </summary>
    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        var index = Array.FindIndex(args, a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;

        if (index + 2 >= args.Length)
        {
            exitCode = 2;
            return true;
        }

        exitCode = RunAsync(args[index + 1], args[index + 2]).GetAwaiter().GetResult();
        return true;
    }

    public static async Task<int> RunAsync(string probeList, string reportPath)
    {
        var requested = probeList
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .ToList();

        if (requested is ["all"]) requested = [.. AllProbes];

        var unknown = requested.Where(p => !AllProbes.Contains(p)).ToList();
        var error = unknown.Count == 0 ? null : "Unknown probe(s): " + string.Join(", ", unknown);

        var started = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        using var consentWatch = new ConsentWatcher();
        var findings = new List<VerificationFinding>();

        if (error is null)
        {
            foreach (var probe in requested)
            {
                try
                {
                    findings.Add(await RunProbeAsync(probe).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    findings.Add(new VerificationFinding(probe, "Fail",
                        ex.GetType().Name + ": " + ex.Message, Facts()));
                }
            }
        }

        using var identity = WindowsIdentity.GetCurrent();
        var report = new VerificationReport
        {
            StartedAt = started,
            DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
            ClientImagePath = Environment.ProcessPath ?? string.Empty,
            ClientProcessId = Environment.ProcessId,
            ClientElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
            SessionId = Process.GetCurrentProcess().SessionId,
            ClientSid = identity.User?.Value ?? string.Empty,
            ConsentPromptSeen = consentWatch.Seen,
            Error = error,
            Findings = findings,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions(PrivilegedProtocol.Json) { WriteIndented = true }),
            new UTF8Encoding(false));

        return error is null && findings.All(f => f.Outcome == "Pass") ? 0 : 1;
    }

    // ------------------------------------------------------------------ probes

    private static Task<VerificationFinding> RunProbeAsync(string probe) => probe switch
    {
        "unelevated" => Task.FromResult(Unelevated()),
        "status" => StatusAsync(),
        "elevated-token" => ElevatedTokenAsync(),
        "owner-not-system" => OwnerNotSystemAsync(),
        "exit-code" => ExitCodeAsync(),
        "timeout" => TimeoutAsync(),
        "cancel" => CancelAsync(),
        "service-query" => ServiceQueryAsync(),
        "self-protection" => SelfProtectionAsync(),
        "approval-deny" => ApprovalAsync(allow: false),
        "approval-allow" => ApprovalAsync(allow: true),
        "elevated-window" => ElevatedWindowAsync(),
        _ => throw new InvalidOperationException("Unknown probe " + probe),
    };

    /// <summary>PC Assistant must be doing all of this without being elevated itself.</summary>
    private static VerificationFinding Unelevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        return new VerificationFinding("unelevated", elevated ? "Fail" : "Pass",
            elevated
                ? "PC Assistant is running elevated. Every measurement below would be meaningless."
                : "PC Assistant is running unelevated, as intended.",
            Facts(("elevated", elevated), ("imagePath", Environment.ProcessPath ?? ""),
                  ("processId", Environment.ProcessId), ("account", identity.Name)));
    }

    private static async Task<VerificationFinding> StatusAsync()
    {
        var status = await new PrivilegedClient().GetStatusAsync().ConfigureAwait(false);
        var ready = status.Availability == HelperAvailability.Ready;

        return new VerificationFinding("status", ready ? "Pass" : "Fail", status.Detail,
            Facts(("availability", status.Availability), ("displayText", status.DisplayText),
                  ("helperVersion", status.HelperVersion ?? ""), ("ownerAccount", status.OwnerAccount ?? ""),
                  ("sessionId", status.SessionId?.ToString() ?? "")));
    }

    /// <summary>
    /// S-1-16-12288 is the high mandatory integrity level and S-1-5-32-544 is
    /// BUILTIN\Administrators enabled rather than deny-only. An unelevated token would show
    /// S-1-16-8192 instead. This is Windows' own account of the token, not ours.
    /// </summary>
    private static async Task<VerificationFinding> ElevatedTokenAsync()
    {
        var response = await SendAsync(new PrivilegedRequest
        {
            Kind = PrivilegedOperationKind.Run,
            Executable = System32("whoami.exe"),
            Arguments = ["/groups"],
            Reason = "Live verification: prove the token really is elevated.",
            TimeoutSeconds = 60,
        }).ConfigureAwait(false);

        var output = response.StandardOutput ?? string.Empty;
        var high = output.Contains("S-1-16-12288", StringComparison.Ordinal);
        var administrators = output.Contains("S-1-5-32-544", StringComparison.Ordinal);
        var medium = output.Contains("S-1-16-8192", StringComparison.Ordinal);
        var mySession = Process.GetCurrentProcess().SessionId;

        var pass = response.Status == PrivilegedStatus.Ok && response.ExitCode == 0 &&
                   response.Elevated == true && high && administrators && !medium &&
                   response.SessionId == mySession && response.SessionId != 0;

        return new VerificationFinding("elevated-token", pass ? "Pass" : "Fail", response.Message,
            Facts(("status", response.Status), ("exitCode", response.ExitCode?.ToString() ?? ""),
                  ("elevated", response.Elevated?.ToString() ?? ""), ("tokenUser", response.TokenUser ?? ""),
                  ("sessionId", response.SessionId?.ToString() ?? ""), ("clientSessionId", mySession),
                  ("highIntegrity_S-1-16-12288", high), ("administrators_S-1-5-32-544", administrators),
                  ("mediumIntegrity_S-1-16-8192", medium)));
    }

    private static async Task<VerificationFinding> OwnerNotSystemAsync()
    {
        var response = await SendAsync(new PrivilegedRequest
        {
            Kind = PrivilegedOperationKind.Run,
            Executable = System32("whoami.exe"),
            Reason = "Live verification: prove the identity is the owner, not SYSTEM.",
            TimeoutSeconds = 60,
        }).ConfigureAwait(false);

        var identity = (response.StandardOutput ?? string.Empty).Trim();
        var isSystem = identity.Contains("nt authority\\system", StringComparison.OrdinalIgnoreCase);
        var isOwner = identity.Split('\\').LastOrDefault()?
            .Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase) == true;

        return new VerificationFinding("owner-not-system",
            response.Status == PrivilegedStatus.Ok && !isSystem && isOwner ? "Pass" : "Fail",
            "The elevated command ran as " + identity + ".",
            Facts(("status", response.Status), ("identity", identity),
                  ("expectedUser", Environment.UserName), ("ranAsSystem", isSystem)));
    }

    /// <summary>"The operation succeeded" and "the command succeeded" are different facts.</summary>
    private static async Task<VerificationFinding> ExitCodeAsync()
    {
        var response = await SendAsync(new PrivilegedRequest
        {
            Kind = PrivilegedOperationKind.Run,
            Executable = System32("cmd.exe"),
            Arguments = ["/c", "exit 7"],
            Reason = "Live verification: check exit-code reporting.",
            TimeoutSeconds = 60,
        }).ConfigureAwait(false);

        return new VerificationFinding("exit-code",
            response.Status == PrivilegedStatus.Ok && response.ExitCode == 7 ? "Pass" : "Fail",
            "Privileged operation status " + response.Status + ", command exit code " + response.ExitCode + ".",
            Facts(("operationStatus", response.Status), ("commandExitCode", response.ExitCode?.ToString() ?? "")));
    }

    private static async Task<VerificationFinding> TimeoutAsync()
    {
        var response = await SendAsync(new PrivilegedRequest
        {
            Kind = PrivilegedOperationKind.Run,
            Executable = System32("ping.exe"),
            Arguments = ["-n", "60", "127.0.0.1"],
            Reason = "Live verification: a command that overruns its approved budget.",
            TimeoutSeconds = 3,
        }).ConfigureAwait(false);

        return new VerificationFinding("timeout",
            response.Status == PrivilegedStatus.TimedOut ? "Pass" : "Fail", response.Message,
            Facts(("status", response.Status), ("durationMs", response.DurationMilliseconds)));
    }

    /// <summary>
    /// Cancellation must terminate the operation's own process tree and nothing else. The
    /// bystander is a process of this client's own, started before the operation and checked
    /// after it — if cancellation reached beyond the job it was given, this is what notices.
    /// </summary>
    private static async Task<VerificationFinding> CancelAsync()
    {
        using var bystander = Process.Start(new ProcessStartInfo(System32("ping.exe"), "-n 40 127.0.0.1")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;

        try
        {
            var client = new PrivilegedClient();
            var operationId = Guid.NewGuid().ToString("N");

            var running = client.SendAsync(new PrivilegedRequest
            {
                Kind = PrivilegedOperationKind.Run,
                Executable = System32("ping.exe"),
                Arguments = ["-n", "40", "127.0.0.1"],
                Reason = "Live verification: a long-running harmless command to cancel.",
                TimeoutSeconds = 120,
                OperationId = operationId,
            });

            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

            var cancel = await client.SendAsync(new PrivilegedRequest
            {
                Kind = PrivilegedOperationKind.Cancel,
                OperationId = operationId,
                Reason = "Live verification: cancellation.",
            }).ConfigureAwait(false);

            var result = await running.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

            bystander.Refresh();
            var bystanderAlive = !bystander.HasExited;

            return new VerificationFinding("cancel",
                cancel.Status == PrivilegedStatus.Ok && result.Status == PrivilegedStatus.Cancelled && bystanderAlive
                    ? "Pass" : "Fail",
                cancel.Message,
                Facts(("cancelStatus", cancel.Status), ("operationStatus", result.Status),
                      ("bystanderPid", bystander.Id), ("bystanderAliveAfterCancel", bystanderAlive)));
        }
        finally
        {
            try { bystander.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
        }
    }

    private static async Task<VerificationFinding> ServiceQueryAsync()
    {
        var response = await SendAsync(new PrivilegedRequest
        {
            Kind = PrivilegedOperationKind.Service,
            ServiceName = PrivilegedProtocol.ServiceName,
            ServiceAction = "query",
            Reason = "Live verification: read a service's state without changing it.",
        }).ConfigureAwait(false);

        var after = QueryServiceState();

        return new VerificationFinding("service-query",
            response.Status == PrivilegedStatus.Ok &&
            response.Message.Contains("Running", StringComparison.OrdinalIgnoreCase) &&
            after == ServiceControllerStatus.Running ? "Pass" : "Fail",
            response.Message,
            Facts(("status", response.Status), ("stateAfterQuery", after?.ToString() ?? "unknown")));
    }

    /// <summary>
    /// The helper must refuse to stop itself, and it must do so on its own account rather
    /// than because the client's copy of the validator got there first. So this one is sent
    /// as a raw line — from the trusted client, over the real pipe, carrying a well-formed
    /// request that the client-side convenience check would otherwise have blocked.
    /// </summary>
    private static async Task<VerificationFinding> SelfProtectionAsync()
    {
        var raw = JsonSerializer.Serialize(new PrivilegedRequest
        {
            Kind = PrivilegedOperationKind.Service,
            ServiceName = PrivilegedProtocol.ServiceName,
            ServiceAction = "stop",
            Reason = "Live verification: the helper must refuse to stop itself.",
        }, PrivilegedProtocol.Json);

        var reply = await RawExchangeAsync(raw).ConfigureAwait(false);
        var response = string.IsNullOrWhiteSpace(reply)
            ? null
            : JsonSerializer.Deserialize<PrivilegedResponse>(reply, PrivilegedProtocol.Json);

        var after = QueryServiceState();
        var status = await new PrivilegedClient().GetStatusAsync().ConfigureAwait(false);

        return new VerificationFinding("self-protection",
            response?.Status == PrivilegedStatus.Invalid &&
            after == ServiceControllerStatus.Running &&
            status.Availability == HelperAvailability.Ready ? "Pass" : "Fail",
            response?.Message ?? "The helper sent no readable answer.",
            Facts(("status", response?.Status.ToString() ?? "none"),
                  ("serviceStateAfter", after?.ToString() ?? "unknown"),
                  ("helperStillReady", status.Availability == HelperAvailability.Ready)));
    }

    /// <summary>
    /// The approval path itself, through the shipped <see cref="PrivilegedOperationService"/>
    /// and the shipped <see cref="ApprovalBroker"/>. The owner's click is the only thing
    /// stood in for. A denial's load-bearing property is that the helper never hears about
    /// the request at all, and that is checked against the helper's own audit log rather than
    /// against our side of the conversation.
    /// </summary>
    private static async Task<VerificationFinding> ApprovalAsync(bool allow)
    {
        var name = allow ? "approval-allow" : "approval-deny";
        var database = Path.Combine(Path.GetTempPath(), "PcAssistant", "verify-" + Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);

        using var broker = new ApprovalBroker(TimeSpan.FromMinutes(1));
        var store = new TaskStore(database);

        var shown = 0;
        string? card = null;
        broker.ApprovalRequested += (_, request) =>
        {
            Interlocked.Increment(ref shown);
            card = request.Action + " | " + request.Target + " | " + request.Privilege;
            broker.Resolve(request.Id, allow);
        };

        var auditBefore = AuditLineCount();

        var service = new PrivilegedOperationService(broker, store);
        var outcome = await service.RequestAsync(
            new PrivilegedRequestContext(Guid.NewGuid(), "Live verification"),
            new PrivilegedRequest
            {
                Kind = PrivilegedOperationKind.Run,
                Executable = System32("whoami.exe"),
                Reason = "Live verification of the approval path.",
                TimeoutSeconds = 60,
            }).ConfigureAwait(false);

        var auditAfter = AuditLineCount();
        var helperSawIt = auditAfter > auditBefore;

        store.Dispose();
        try { File.Delete(database); } catch (Exception) { /* a temp file, and it is small */ }

        var pass = shown == 1 && (allow
            ? outcome.Succeeded && outcome.Response?.Elevated == true && outcome.Response.ExitCode == 0 && helperSawIt
            : !outcome.Ran && outcome.Status == PrivilegedStatus.Denied && outcome.Response is null && !helperSawIt);

        return new VerificationFinding(name, pass ? "Pass" : "Fail", outcome.Message,
            Facts(("approvalCardsShown", shown), ("approvalCard", card ?? ""),
                  ("ran", outcome.Ran), ("status", outcome.Status),
                  ("exitCode", outcome.Response?.ExitCode?.ToString() ?? ""),
                  ("elevated", outcome.Response?.Elevated?.ToString() ?? ""),
                  ("helperAuditLinesBefore", auditBefore), ("helperAuditLinesAfter", auditAfter),
                  ("helperSawTheRequest", helperSawIt)));
    }

    /// <summary>
    /// One visible elevated console, so the unelevated side can measure what Windows lets it
    /// do with an elevated window. It prints its own groups and exits by itself after thirty
    /// seconds; PC Assistant deliberately cannot close it.
    /// </summary>
    private static async Task<VerificationFinding> ElevatedWindowAsync()
    {
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        var response = await SendAsync(new PrivilegedRequest
        {
            Kind = PrivilegedOperationKind.Launch,
            Executable = powershell,
            Arguments = ["-NoLogo", "-NoProfile", "-Command",
                         "$Host.UI.RawUI.WindowTitle='PcAssistantElevatedProbe'; whoami /groups; Start-Sleep -Seconds 30"],
            Reason = "Live verification: measure the elevated-window interaction boundary.",
        }).ConfigureAwait(false);

        return new VerificationFinding("elevated-window",
            response.Status == PrivilegedStatus.Ok && response.Elevated == true && response.ProcessId is > 0
                ? "Pass" : "Fail",
            response.Message,
            Facts(("status", response.Status), ("processId", response.ProcessId?.ToString() ?? ""),
                  ("elevated", response.Elevated?.ToString() ?? ""), ("tokenUser", response.TokenUser ?? "")));
    }

    // ----------------------------------------------------------------- plumbing

    private static Task<PrivilegedResponse> SendAsync(PrivilegedRequest request)
        => new PrivilegedClient().SendAsync(request);

    private static string System32(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);

    private static ServiceControllerStatus? QueryServiceState()
    {
        try
        {
            using var controller = new ServiceController(PrivilegedProtocol.ServiceName);
            return controller.Status;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The helper's own log, inside its administrators-only install directory. Readable by
    /// the owner, writable only by the service — which is what makes it usable as evidence
    /// that a denied operation never crossed the boundary.
    /// </summary>
    private static int AuditLineCount()
    {
        try
        {
            if (!File.Exists(HelperPaths.AuditLogFile)) return 0;
            using var stream = new FileStream(HelperPaths.AuditLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var count = 0;
            while (reader.ReadLine() is not null) count++;
            return count;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static async Task<string> RawExchangeAsync(string line)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", PrivilegedProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000).ConfigureAwait(false);

        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        await writer.WriteLineAsync(line).ConfigureAwait(false);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false) ?? string.Empty;
    }

    private static IReadOnlyDictionary<string, string> Facts(params (string Key, object? Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value?.ToString() ?? string.Empty, StringComparer.Ordinal);

    /// <summary>
    /// Samples the Windows consent process for the length of the run. If any approved helper
    /// operation had triggered UAC, consent.exe would have appeared here — which is the whole
    /// point of installing the service once instead of elevating every time.
    /// </summary>
    private sealed class ConsentWatcher : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _seen;

        public ConsentWatcher() => _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    if (Process.GetProcessesByName("consent").Length > 0) Interlocked.Exchange(ref _seen, 1);
                }
                catch (Exception) { /* enumeration is best effort */ }

                try { await Task.Delay(250, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        });

        public bool Seen => Volatile.Read(ref _seen) == 1;

        public void Dispose()
        {
            _stop.Cancel();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { /* shutting down */ }
            _stop.Dispose();
        }
    }
}
