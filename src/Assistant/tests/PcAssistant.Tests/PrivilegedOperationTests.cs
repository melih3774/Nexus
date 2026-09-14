using System.Collections.Generic;
using System.Text.Json;
using System.Linq;
using PcAssistant.Core.Approvals;
using PcAssistant.Core.Elevation;
using PcAssistant.Core.Model;
using PcAssistant.Core.Persistence;
using PcAssistant.Core.Policy;
using PcAssistant.Elevation;
using Xunit;

namespace PcAssistant.Tests;

/// <summary>
/// The privileged boundary's rules, proven without an installed service.
///
/// The two that matter most are the approval semantics: a denial must mean nothing was ever
/// sent to the helper, and an allow must mean exactly the approved operation was sent and
/// nothing else. Both are asserted against a recording client, so they are statements about
/// what crossed the boundary rather than about what the code intended.
/// </summary>
public sealed class PrivilegedOperationTests
{
    // ------------------------------------------------------- recording client

    private sealed class RecordingClient(HelperStatus? status = null, PrivilegedResponse? response = null)
        : IPrivilegedClient
    {
        public List<PrivilegedRequest> Sent { get; } = [];
        public HelperStatus Status { get; set; } = status ??
            new HelperStatus(HelperAvailability.Ready, "test helper", "1.0", "TEST\\owner", 1);

        public PrivilegedResponse Response { get; set; } = response ?? new PrivilegedResponse
        {
            Status = PrivilegedStatus.Ok, Message = "ran", ExitCode = 0, Elevated = true,
        };

        public Task<HelperStatus> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Status);

        public Task<PrivilegedResponse> SendAsync(PrivilegedRequest request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult(Response with { RequestId = request.RequestId });
        }
    }

    private static (PrivilegedOperationService Service, RecordingClient Client, TaskStore Store, ApprovalBroker Broker)
        Build(HelperStatus? status = null, PrivilegedResponse? response = null)
    {
        var store = new TaskStore(Path.Combine(Path.GetTempPath(), "pcassistant-tests", Guid.NewGuid().ToString("N"), "t.db"));
        var broker = new ApprovalBroker(TimeSpan.FromSeconds(10));
        var client = new RecordingClient(status, response);
        return (new PrivilegedOperationService(broker, store, client), client, store, broker);
    }

    private static PrivilegedRequest HarmlessRun() => new()
    {
        Kind = PrivilegedOperationKind.Run,
        Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe"),
        Arguments = ["/groups"],
        Reason = "Prove the token is elevated.",
        TimeoutSeconds = 30,
    };

    /// <summary>
    /// Subscribes before starting the request. Subscribing afterwards races the approval
    /// event, which is raised synchronously inside RequestAsync.
    /// </summary>
    private static async Task<(Task<PrivilegedOutcome> Pending, ApprovalRequest Approval)> StartAsync(
        ApprovalBroker broker, Func<Task<PrivilegedOutcome>> start)
    {
        var completion = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, ApprovalRequest request) => completion.TrySetResult(request);

        broker.ApprovalRequested += Handler;
        try
        {
            var pending = start();
            return (pending, await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            broker.ApprovalRequested -= Handler;
        }
    }

    // ------------------------------------------------------ approval semantics

    [Fact]
    public async Task OwnerDenialMeansNothingIsEverSentToThePrivilegedHelper()
    {
        var (service, client, store, broker) = Build();
        using (store)
        using (broker)
        {
            var (pending, approval) = await StartAsync(broker,
                () => service.RequestAsync(new PrivilegedRequestContext(Guid.NewGuid(), "Deny test"), HarmlessRun()));

            Assert.True(broker.Resolve(approval.Id, allow: false));

            var outcome = await pending;

            Assert.False(outcome.Ran);
            Assert.Empty(client.Sent);
            Assert.Contains("denied", outcome.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("nothing ran", outcome.Message, StringComparison.OrdinalIgnoreCase);

            var history = service.RecentHistory();
            Assert.Equal("Denied", history[0].Approval);
            Assert.Equal("Not run", history[0].Result);
        }
    }

    [Fact]
    public async Task OwnerApprovalSendsExactlyTheApprovedOperationAndNothingElse()
    {
        var (service, client, store, broker) = Build();
        using (store)
        using (broker)
        {
            var request = HarmlessRun();
            var (pending, approval) = await StartAsync(broker,
                () => service.RequestAsync(new PrivilegedRequestContext(Guid.NewGuid(), "Allow test"), request));

            Assert.True(broker.Resolve(approval.Id, allow: true));

            var outcome = await pending;

            Assert.True(outcome.Succeeded);
            var sent = Assert.Single(client.Sent);
            Assert.Equal(PrivilegedOperationKind.Run, sent.Kind);
            Assert.Equal(request.Executable, sent.Executable);
            Assert.Equal(request.Arguments, sent.Arguments);
            Assert.Equal(30, sent.TimeoutSeconds);

            // The operation id is chosen before sending, so cancellation has something to name.
            Assert.False(string.IsNullOrWhiteSpace(sent.OperationId));

            Assert.Equal("Allowed", service.RecentHistory()[0].Approval);
            Assert.Equal(0, service.RecentHistory()[0].ExitCode);
        }
    }

    [Fact]
    public async Task NoAnswerIsTreatedAsDenialAndStillSendsNothing()
    {
        var store = new TaskStore(Path.Combine(Path.GetTempPath(), "pcassistant-tests", Guid.NewGuid().ToString("N"), "t.db"));
        using var broker = new ApprovalBroker(TimeSpan.FromMilliseconds(250));
        using (store)
        {
            var client = new RecordingClient();
            var service = new PrivilegedOperationService(broker, store, client);

            var outcome = await service.RequestAsync(new PrivilegedRequestContext(Guid.NewGuid(), "Timeout test"), HarmlessRun());

            Assert.False(outcome.Ran);
            Assert.Empty(client.Sent);
            Assert.Equal("No answer", service.RecentHistory()[0].Approval);
        }
    }

    [Fact]
    public async Task AnUnavailableHelperNeverAsksTheOwner()
    {
        var (service, client, store, broker) = Build(
            new HelperStatus(HelperAvailability.NotInstalled, "nothing installed"));
        using (store)
        using (broker)
        {
            var asked = false;
            broker.ApprovalRequested += (_, _) => asked = true;

            var outcome = await service.RequestAsync(new PrivilegedRequestContext(Guid.NewGuid(), "No helper"), HarmlessRun());

            // Asking about something that cannot run is exactly how approval loops start.
            Assert.False(asked);
            Assert.False(outcome.Ran);
            Assert.Empty(client.Sent);
            Assert.Equal("Not asked", service.RecentHistory()[0].Approval);
        }
    }

    [Fact]
    public async Task AnInvalidRequestIsRefusedBeforeTheOwnerIsAsked()
    {
        var (service, client, store, broker) = Build();
        using (store)
        using (broker)
        {
            var asked = false;
            broker.ApprovalRequested += (_, _) => asked = true;

            var outcome = await service.RequestAsync(new PrivilegedRequestContext(Guid.NewGuid(), "Bad request"),
                HarmlessRun() with { Executable = @"C:\does\not\exist.exe" });

            Assert.False(asked);
            Assert.False(outcome.Ran);
            Assert.Empty(client.Sent);
        }
    }

    [Fact]
    public async Task ApprovalCardCarriesPrivilegeConsequenceReasonAndTask()
    {
        var (service, _, store, broker) = Build();
        using (store)
        using (broker)
        {
            var (pending, approval) = await StartAsync(broker, () => service.RequestAsync(
                new PrivilegedRequestContext(Guid.NewGuid(), "Set up the toolchain"), HarmlessRun()));

            Assert.Equal(RiskCategory.Elevation, approval.Category);
            Assert.Contains("ADMINISTRATOR OPERATION", approval.Action, StringComparison.Ordinal);
            Assert.Contains("whoami.exe", approval.Target, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/groups", approval.Target, StringComparison.Ordinal);
            Assert.Contains("Administrator", approval.Privilege!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("elevated token", approval.Privilege!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Not SYSTEM", approval.Privilege!, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(approval.Consequence));
            Assert.Equal("Prove the token is elevated.", approval.Justification);
            Assert.Equal("Set up the toolchain", approval.TaskTitle);

            // The load-bearing negative: no scoped grant is ever offered for elevation.
            Assert.Null(approval.ScopeKey);
            Assert.Null(approval.ScopeDescription);

            broker.Resolve(approval.Id, allow: false);
            await pending;
        }
    }

    [Fact]
    public async Task ElevationNeverEarnsATaskWideGrantSoASecondOperationAsksAgain()
    {
        var (service, client, store, broker) = Build();
        using (store)
        using (broker)
        {
            var taskId = Guid.NewGuid();
            var asks = 0;
            broker.ApprovalRequested += (_, request) =>
            {
                asks++;
                // "Allow for this task" must not even be offered, let alone taken.
                Assert.Null(request.ScopeKey);
                Assert.False(broker.ResolveForTask(request.Id));
                broker.Resolve(request.Id, allow: true);
            };

            await service.RequestAsync(new PrivilegedRequestContext(taskId, "Twice"), HarmlessRun());
            await service.RequestAsync(new PrivilegedRequestContext(taskId, "Twice"), HarmlessRun());

            Assert.Equal(2, asks);
            Assert.Equal(2, client.Sent.Count);
        }
    }

    [Fact]
    public async Task CancellingBeforeTheAnswerRunsNothing()
    {
        var (service, client, store, broker) = Build();
        using (store)
        using (broker)
        {
            using var cancellation = new CancellationTokenSource();
            var (pending, _) = await StartAsync(broker, () => service.RequestAsync(
                new PrivilegedRequestContext(Guid.NewGuid(), "Stopped"), HarmlessRun(), cancellation.Token));

            await cancellation.CancelAsync();

            var outcome = await pending;
            Assert.False(outcome.Ran);
            Assert.Empty(client.Sent);
        }
    }

    [Fact]
    public async Task AVersionMismatchIsReportedAsSuchAndRunsNothing()
    {
        var (service, client, store, broker) = Build(
            new HelperStatus(HelperAvailability.VersionMismatch, "helper speaks protocol 0"));
        using (store)
        using (broker)
        {
            var outcome = await service.RequestAsync(new PrivilegedRequestContext(Guid.NewGuid(), "Mismatch"), HarmlessRun());

            Assert.False(outcome.Ran);
            Assert.Empty(client.Sent);
            Assert.Contains("version mismatch", outcome.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task StatusIsReadableWithoutAskingTheOwnerForAnything()
    {
        var (service, client, store, broker) = Build();
        using (store)
        using (broker)
        {
            var asked = false;
            broker.ApprovalRequested += (_, _) => asked = true;

            var text = await service.DescribeStatusAsync();

            Assert.False(asked);
            Assert.Empty(client.Sent);
            Assert.Contains("Ready", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // -------------------------------------------------------- policy placement

    [Fact]
    public void ElevationAsksInBothModesAndNeverBecomesAutomatic()
    {
        Assert.Equal(PolicyDecision.Ask, ApprovalPolicy.Evaluate(AssistantMode.Auto, RiskCategory.Elevation));
        Assert.Equal(PolicyDecision.Ask, ApprovalPolicy.Evaluate(AssistantMode.Ask, RiskCategory.Elevation));
    }

    [Fact]
    public void TheElevatedToolIsKnownClassifiedAsElevationAndUngrantable()
    {
        var call = ToolInvocation.FromJson("mcp__pc-assistant__elevated_request", JsonSerializer.SerializeToElement(new { operation = "run" }), @"C:\work");

        Assert.True(PcToolPolicy.IsKnown(call.ToolName));
        var assessment = PcToolPolicy.Classify(call);
        Assert.NotNull(assessment);
        Assert.Equal(RiskCategory.Elevation, assessment!.Category);

        var scope = PcToolPolicy.Scope(call, assessment.Category);
        Assert.Null(scope.Key);
    }

    [Fact]
    public void ReadingElevationStatusIsNotItselfPrivileged()
    {
        var call = ToolInvocation.FromJson("mcp__pc-assistant__elevated_status", JsonSerializer.SerializeToElement(new { }), @"C:\work");
        var assessment = PcToolPolicy.Classify(call);

        Assert.Equal(RiskCategory.Read, assessment!.Category);
        Assert.Equal(PolicyDecision.Allow, ApprovalPolicy.Evaluate(AssistantMode.Auto, assessment.Category));
    }

    // ------------------------------------------------------------- this process

    [Fact]
    public void PcAssistantItselfIsNotElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);

        // The whole design exists so this stays false. If a change ever makes PC Assistant
        // run elevated, every guarantee in the privileged path is weaker than it claims.
        Assert.False(principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
    }
}
