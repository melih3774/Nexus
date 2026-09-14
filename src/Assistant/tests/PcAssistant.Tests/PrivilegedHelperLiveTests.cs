using System.Linq;
using PcAssistant.Core.Elevation;
using PcAssistant.Elevation;
using Xunit;

namespace PcAssistant.Tests;

/// <summary>
/// Runs the trusted client's verification pass once, so a dozen assertions cost one set of
/// elevated operations rather than a dozen.
/// </summary>
public sealed class LiveVerificationFixture : IAsyncLifetime
{
    private const string Probes =
        "unelevated,status,elevated-token,owner-not-system,exit-code,timeout,cancel," +
        "service-query,self-protection,approval-deny,approval-allow";

    public VerificationReport? Report { get; private set; }

    /// <summary>Why the pass could not run, if it could not. Reported as a test failure, never swallowed.</summary>
    public string? Error { get; private set; }

    public async Task InitializeAsync()
    {
        if (!LiveHelper.Enabled) return;

        Error = LiveHelper.WhyUnavailable();
        if (Error is not null) return;

        try
        {
            Report = await LiveHelper.RunAsync(Probes, "privileged-live-verification.json", TimeSpan.FromMinutes(3));
        }
        catch (Exception ex)
        {
            Error = ex.GetType().Name + ": " + ex.Message;
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// The privileged boundary against the real installed service, over the real named pipe,
/// driven by the real trusted client.
///
/// Opt-in, because it requires a helper the owner has deliberately installed:
///
///     $env:PCASSISTANT_ELEVATION_LIVE = "1"; dotnet test --filter PrivilegedHelperLiveTests
///
/// Two boundaries are tested here and they must not be confused. The first is that the
/// installed service refuses an arbitrary local process — including this test host — which
/// is asserted directly below. The second is that the production path really does produce an
/// elevated token in the owner's own session; that cannot be observed from a process the
/// helper distrusts, so it is measured inside the trusted client and read back from its
/// report.
///
/// Protocol-level hostility — malformed JSON, oversize lines, replays, stale clocks, unknown
/// operations — belongs to <see cref="PrivilegedBoundaryServerTests"/>, which hosts the real
/// server and the real handler on a test pipe and can legitimately inject anything it likes.
/// Bending the production protocol so that an untrusted process could inject raw bytes into
/// the SYSTEM service would be a worse trade than not testing it here.
///
/// Everything below is read-only or self-disposing. Nothing installs software, changes a
/// system setting, or mutates a service.
/// </summary>
public sealed class PrivilegedHelperLiveTests : IClassFixture<LiveVerificationFixture>
{
    private readonly LiveVerificationFixture _fixture;

    public PrivilegedHelperLiveTests(LiveVerificationFixture fixture) => _fixture = fixture;

    // ------------------------------------- the installed service's own front door

    [SkippableFact]
    public async Task TheInstalledServiceRefusesAnArbitraryLocalProcessEvenAsTheOwner()
    {
        Skip.IfNot(LiveHelper.Enabled, "Set PCASSISTANT_ELEVATION_LIVE=1 with the helper installed.");
        var why = LiveHelper.WhyUnavailable();
        Skip.If(why is not null, why ?? string.Empty);

        // This test host runs as the owner, at medium integrity, in the interactive session:
        // every identity check passes except the one that matters here.
        var response = await new PrivilegedClient("live-tests").SendAsync(new PrivilegedRequest
        {
            Kind = PrivilegedOperationKind.Status,
        });

        Assert.Equal(PrivilegedStatus.Denied, response.Status);
        Assert.Contains(HelperPaths.ReadState()!.ClientExecutable, response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("testhost", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void TheHelperTrustsOnlyTheExecutableItWasInstalledFor()
    {
        Skip.IfNot(LiveHelper.Enabled, "Set PCASSISTANT_ELEVATION_LIVE=1 with the helper installed.");
        var why = LiveHelper.WhyUnavailable();
        Skip.If(why is not null, why ?? string.Empty);

        var state = HelperPaths.ReadState()!;
        var report = Report();

        // The client the report came from is the client the service was installed for. If
        // these ever drift, every assertion below would be measuring the wrong process.
        Assert.Equal(state.ClientExecutable, report.ClientImagePath, ignoreCase: true);
        Assert.Equal(state.OwnerSid, report.ClientSid, ignoreCase: true);
    }

    // ------------------------------------------------------------------ readiness

    [SkippableFact]
    public void TheInstalledHelperReportsReadyForTheRealClient()
    {
        var status = Finding("status");

        Assert.Equal("Pass", status.Outcome);
        Assert.Equal("Ready", status.Facts["availability"]);
        Assert.False(string.IsNullOrWhiteSpace(status.Facts["ownerAccount"]));
        Assert.NotEqual("0", status.Facts["sessionId"]);
    }

    [SkippableFact]
    public void PcAssistantItselfStaysUnelevated()
    {
        var unelevated = Finding("unelevated");

        Assert.Equal("Pass", unelevated.Outcome);
        Assert.Equal("False", unelevated.Facts["elevated"]);
        Assert.False(Report().ClientElevated);
    }

    [SkippableFact]
    public void NoUacPromptAppearsForOrdinaryApprovedOperations()
    {
        Skip.IfNot(LiveHelper.Enabled, "Set PCASSISTANT_ELEVATION_LIVE=1 with the helper installed.");
        var why = LiveHelper.WhyUnavailable();
        Skip.If(why is not null, why ?? string.Empty);

        // The install was the one and only elevation prompt. Everything the helper does
        // afterwards happens without the Secure Desktop, which is what this samples.
        Assert.False(Report().ConsentPromptSeen);
    }

    // --------------------------------------------------- the elevated token itself

    [SkippableFact]
    public void AnApprovedCommandRunsWithARealElevatedTokenInThisSession()
    {
        var token = Finding("elevated-token");

        Assert.Equal("Pass", token.Outcome);
        Assert.Equal("Ok", token.Facts["status"]);
        Assert.Equal("0", token.Facts["exitCode"]);
        Assert.Equal("True", token.Facts["elevated"]);

        // S-1-16-12288 is the high mandatory integrity level. An unelevated token would show
        // S-1-16-8192, and BUILTIN\Administrators would be a deny-only group.
        Assert.Equal("True", token.Facts["highIntegrity_S-1-16-12288"]);
        Assert.Equal("True", token.Facts["administrators_S-1-5-32-544"]);
        Assert.Equal("False", token.Facts["mediumIntegrity_S-1-16-8192"]);

        // ...and it belongs to the owner's interactive session, not session 0.
        Assert.NotEqual("0", token.Facts["sessionId"]);
        Assert.Equal(token.Facts["clientSessionId"], token.Facts["sessionId"]);
    }

    [SkippableFact]
    public void TheCommandRunsAsTheOwnerNotAsSystem()
    {
        var owner = Finding("owner-not-system");

        Assert.Equal("Pass", owner.Outcome);
        Assert.Equal("False", owner.Facts["ranAsSystem"]);
        Assert.Equal(owner.Facts["expectedUser"], owner.Facts["identity"].Split('\\').Last(), ignoreCase: true);
    }

    [SkippableFact]
    public void ANonZeroExitCodeIsReportedHonestlyRatherThanAsFailure()
    {
        var exit = Finding("exit-code");

        // The operation succeeded; the command failed. Those are different facts.
        Assert.Equal("Pass", exit.Outcome);
        Assert.Equal("Ok", exit.Facts["operationStatus"]);
        Assert.Equal("7", exit.Facts["commandExitCode"]);
    }

    // ------------------------------------------------------- budget and cancellation

    [SkippableFact]
    public void ACommandThatOverrunsItsApprovedBudgetIsTerminatedAndReportedAsTimedOut()
    {
        var timeout = Finding("timeout");

        Assert.Equal("Pass", timeout.Outcome);
        Assert.Equal("TimedOut", timeout.Facts["status"]);
    }

    [SkippableFact]
    public void CancellingAnOperationKillsOnlyThatOperationsProcessTree()
    {
        var cancel = Finding("cancel");

        Assert.Equal("Pass", cancel.Outcome);
        Assert.Equal("Ok", cancel.Facts["cancelStatus"]);
        Assert.Equal("Cancelled", cancel.Facts["operationStatus"]);

        // The load-bearing assertion: nothing else was touched.
        Assert.Equal("True", cancel.Facts["bystanderAliveAfterCancel"]);
    }

    [SkippableFact]
    public async Task CancellingAnUnknownOperationIsRefusedRatherThanGuessed()
    {
        Skip.IfNot(LiveHelper.Enabled, "Set PCASSISTANT_ELEVATION_LIVE=1 with the helper installed.");
        var why = LiveHelper.WhyUnavailable();
        Skip.If(why is not null, why ?? string.Empty);

        // Refused for two independent reasons — this host is not the trusted client, and the
        // id names nothing — and neither of them is "guessed at a running operation".
        var response = await new PrivilegedClient("live-tests").SendAsync(new PrivilegedRequest
        {
            Kind = PrivilegedOperationKind.Cancel,
            OperationId = Guid.NewGuid().ToString("N"),
            Reason = "Unknown id.",
        });

        Assert.NotEqual(PrivilegedStatus.Ok, response.Status);
    }

    // --------------------------------------------------------- service operations

    [SkippableFact]
    public void AServiceCanBeQueriedWithoutChangingIt()
    {
        var query = Finding("service-query");

        Assert.Equal("Pass", query.Outcome);
        Assert.Equal("Ok", query.Facts["status"]);
        Assert.Equal("Running", query.Facts["stateAfterQuery"]);
    }

    [SkippableFact]
    public void TheHelperRefusesToStopItselfAndKeepsRunning()
    {
        var self = Finding("self-protection");

        // Sent raw from the trusted client, so this is the helper's own refusal rather than
        // the client-side copy of the validator getting there first.
        Assert.Equal("Pass", self.Outcome);
        Assert.Equal("Invalid", self.Facts["status"]);
        Assert.Equal("Running", self.Facts["serviceStateAfter"]);
        Assert.Equal("True", self.Facts["helperStillReady"]);
    }

    // ------------------------------------------------------------ the approval path

    [SkippableFact]
    public void DenyingAnAdministratorOperationSendsNothingAndRunsNothing()
    {
        var deny = Finding("approval-deny");

        Assert.Equal("Pass", deny.Outcome);
        Assert.Equal("1", deny.Facts["approvalCardsShown"]);
        Assert.Equal("False", deny.Facts["ran"]);
        Assert.Equal("Denied", deny.Facts["status"]);

        // Proven from the helper's own audit log, not from our side of the conversation:
        // a denial means the privileged boundary never heard about the request at all.
        Assert.Equal("False", deny.Facts["helperSawTheRequest"]);
        Assert.Equal(deny.Facts["helperAuditLinesBefore"], deny.Facts["helperAuditLinesAfter"]);
    }

    [SkippableFact]
    public void ApprovingAnAdministratorOperationRunsExactlyThatOperation()
    {
        var allow = Finding("approval-allow");

        Assert.Equal("Pass", allow.Outcome);
        Assert.Equal("1", allow.Facts["approvalCardsShown"]);
        Assert.Equal("True", allow.Facts["ran"]);
        Assert.Equal("Ok", allow.Facts["status"]);
        Assert.Equal("0", allow.Facts["exitCode"]);
        Assert.Equal("True", allow.Facts["elevated"]);
        Assert.Equal("True", allow.Facts["helperSawTheRequest"]);

        // The owner sees what they are approving: privilege and target, not just a command.
        Assert.Contains("ADMINISTRATOR OPERATION", allow.Facts["approvalCard"], StringComparison.Ordinal);
        Assert.Contains("whoami", allow.Facts["approvalCard"], StringComparison.OrdinalIgnoreCase);
    }

    // ----------------------------------------------------------------- helpers

    private VerificationReport Report()
    {
        Skip.IfNot(LiveHelper.Enabled, "Set PCASSISTANT_ELEVATION_LIVE=1 with the helper installed.");
        Assert.Null(_fixture.Error);
        Assert.NotNull(_fixture.Report);
        return _fixture.Report!;
    }

    private VerificationFinding Finding(string name)
    {
        var report = Report();
        Assert.Null(report.Error);

        var finding = report.Findings.FirstOrDefault(f => f.Name == name);
        Assert.True(finding is not null, "The trusted client reported no '" + name + "' probe.");

        // Failures are far easier to read with the client's own account attached.
        Assert.True(finding!.Outcome == "Pass",
            name + " → " + finding.Outcome + ": " + finding.Detail + Environment.NewLine +
            string.Join(Environment.NewLine, finding.Facts.Select(f => "  " + f.Key + " = " + f.Value)));

        return finding;
    }
}
