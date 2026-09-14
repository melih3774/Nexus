using PcAssistant.Core.Approvals;
using PcAssistant.Core.Policy;
using Xunit;

namespace PcAssistant.Tests;

public class ApprovalBrokerTests
{
    private static ApprovalRequest Request(Guid? taskId = null, string target = "src/Main.cs") => new()
    {
        TaskId = taskId ?? Guid.NewGuid(),
        ToolName = "Edit",
        Action = "Claude wants to modify",
        Target = target,
        Reason = "ASK mode",
        Category = RiskCategory.WorkspaceWrite,
    };

    [Fact]
    public async Task AllowResolvesTheWaiter()
    {
        using var broker = new ApprovalBroker();
        var request = Request();

        var pending = broker.RequestAsync(request, CancellationToken.None);
        Assert.True(broker.Resolve(request.Id, allow: true));

        Assert.Equal(ApprovalOutcome.Allowed, await pending);
    }

    [Fact]
    public async Task DenyResolvesTheWaiter()
    {
        using var broker = new ApprovalBroker();
        var request = Request();

        var pending = broker.RequestAsync(request, CancellationToken.None);
        broker.Resolve(request.Id, allow: false);

        Assert.Equal(ApprovalOutcome.Denied, await pending);
    }

    [Fact]
    public async Task TimeoutIsTreatedAsDenial()
    {
        using var broker = new ApprovalBroker(TimeSpan.FromMilliseconds(80));

        Assert.Equal(ApprovalOutcome.TimedOut,
            await broker.RequestAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task CancellingTheTaskResolvesItsPendingApprovals()
    {
        using var broker = new ApprovalBroker();
        var taskId = Guid.NewGuid();

        var pending = broker.RequestAsync(Request(taskId), CancellationToken.None);
        broker.CancelTask(taskId);

        Assert.Equal(ApprovalOutcome.Cancelled, await pending);
    }

    [Fact]
    public async Task CancellingTheTokenResolvesTheWaiter()
    {
        using var broker = new ApprovalBroker();
        using var cts = new CancellationTokenSource();

        var pending = broker.RequestAsync(Request(), cts.Token);
        await cts.CancelAsync();

        Assert.Equal(ApprovalOutcome.Cancelled, await pending);
    }

    [Fact]
    public async Task ShutdownResolvesEverythingWaiting()
    {
        var broker = new ApprovalBroker();
        var a = broker.RequestAsync(Request(), CancellationToken.None);
        var b = broker.RequestAsync(Request(target: "other.cs"), CancellationToken.None);

        broker.CancelAll();

        Assert.Equal(ApprovalOutcome.Cancelled, await a);
        Assert.Equal(ApprovalOutcome.Cancelled, await b);
        broker.Dispose();
    }

    [Fact]
    public async Task IdenticalPendingRequestsCollapseOntoOneCard()
    {
        using var broker = new ApprovalBroker();
        var taskId = Guid.NewGuid();

        var cardCount = 0;
        broker.ApprovalRequested += (_, _) => Interlocked.Increment(ref cardCount);

        var first = Request(taskId);
        var pendingA = broker.RequestAsync(first, CancellationToken.None);
        var pendingB = broker.RequestAsync(Request(taskId), CancellationToken.None);

        Assert.Equal(1, cardCount);
        Assert.Single(broker.Pending);

        broker.Resolve(first.Id, allow: true);

        Assert.Equal(ApprovalOutcome.Allowed, await pendingA);
        Assert.Equal(ApprovalOutcome.Allowed, await pendingB);
    }

    [Fact]
    public async Task DifferentTargetsDoNotCollapse()
    {
        using var broker = new ApprovalBroker();
        var taskId = Guid.NewGuid();

        var a = Request(taskId, "one.cs");
        var b = Request(taskId, "two.cs");

        var pendingA = broker.RequestAsync(a, CancellationToken.None);
        var pendingB = broker.RequestAsync(b, CancellationToken.None);

        Assert.Equal(2, broker.Pending.Count);

        broker.Resolve(a.Id, true);
        broker.Resolve(b.Id, false);

        Assert.Equal(ApprovalOutcome.Allowed, await pendingA);
        Assert.Equal(ApprovalOutcome.Denied, await pendingB);
    }

    [Fact]
    public void ResolvingAnUnknownRequestIsHarmless()
    {
        using var broker = new ApprovalBroker();
        Assert.False(broker.Resolve(Guid.NewGuid(), allow: true));
    }

    [Fact]
    public async Task ResolvedRequestsRaiseTheResolvedEventOnce()
    {
        using var broker = new ApprovalBroker();
        var request = Request();
        var resolutions = new List<ApprovalResolution>();
        broker.ApprovalResolved += (_, r) => resolutions.Add(r);

        var pending = broker.RequestAsync(request, CancellationToken.None);
        broker.Resolve(request.Id, true);
        broker.Resolve(request.Id, false);

        await pending;

        Assert.Single(resolutions);
        Assert.Equal(ApprovalOutcome.Allowed, resolutions[0].Outcome);
    }
}
