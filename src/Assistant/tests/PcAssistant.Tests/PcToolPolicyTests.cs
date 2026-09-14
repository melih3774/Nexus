using System.Text.Json;
using PcAssistant.Core.Approvals;
using PcAssistant.Core.Model;
using PcAssistant.Core.Policy;
using Xunit;

namespace PcAssistant.Tests;

public class PcToolPolicyTests
{
    private static ToolInvocation Call(string tool, object input) =>
        ToolInvocation.FromJson(tool, JsonSerializer.SerializeToElement(input), @"C:\projects\demo");

    [Theory]
    [InlineData("windows", RiskCategory.Read)]
    [InlineData("screenshot", RiskCategory.Read)]
    [InlineData("recent_files", RiskCategory.Read)]
    [InlineData("clipboard_write", RiskCategory.DesktopAction)]
    [InlineData("input", RiskCategory.InteractiveInput)]
    [InlineData("not_known", RiskCategory.CommandHighImpact)]
    public void NativeToolsHaveExplicitPolicy(string name, RiskCategory expected) =>
        Assert.Equal(expected, new RiskClassifier().Classify(Call(PcToolPolicy.Prefix + name, new { })).Category);

    [Fact]
    public void UnknownMcpServerCannotBorrowNativeToolPolicy() =>
        Assert.Equal(RiskCategory.CommandHighImpact, new RiskClassifier().Classify(Call("mcp__other__windows", new { })).Category);

    [Fact]
    public void BrowserObservationIsFreeButScriptAndClickAreReviewed()
    {
        foreach (var name in new[] { "tabs_context_mcp", "read_page", "get_page_text" })
            Assert.Equal(RiskCategory.Read, new RiskClassifier().Classify(Call("mcp__claude-in-chrome__" + name, new { })).Category);
        foreach (var name in new[] { "javascript_tool", "computer", "form_input", "navigate" })
            Assert.Equal(PolicyDecision.Ask, ApprovalPolicy.Evaluate(AssistantMode.Auto,
                new RiskClassifier().Classify(Call("mcp__claude-in-chrome__" + name, new { action = "left_click" })).Category));
    }

    [Fact]
    public void TextEntryWithoutSubmissionIsRoutineButClickAndCloseAreReviewed()
    {
        Assert.Equal(RiskCategory.DesktopAction, new RiskClassifier().Classify(Call(PcToolPolicy.Prefix + "input", new { action = "typeText", text = "hello" })).Category);
        Assert.Equal(RiskCategory.InteractiveInput, new RiskClassifier().Classify(Call(PcToolPolicy.Prefix + "input", new { action = "click", x = 1, y = 2 })).Category);
        Assert.Equal(RiskCategory.InteractiveInput, new RiskClassifier().Classify(Call(PcToolPolicy.Prefix + "window_action", new { action = "Close", hwnd = 1 })).Category);
    }

    [Fact]
    public void OwnerReadRootsDoNotGrantWrites()
    {
        var classifier = new RiskClassifier { AdditionalReadRoots = new[] { @"C:\Users\Owner\Downloads" } };
        var args = new { file_path = @"C:\Users\Owner\Downloads\report.pdf" };
        Assert.Equal(RiskCategory.Read, classifier.Classify(Call("Read", args)).Category);
        Assert.Equal(RiskCategory.OutsideWorkspace, classifier.Classify(Call("Write", args)).Category);
    }

    [Fact]
    public async Task TaskGrantIsScopedAndStopRevokesIt()
    {
        using var broker = new ApprovalBroker();
        var request = new ApprovalRequest { TaskId = Guid.NewGuid(), ToolName = "Edit", Action = "Edit",
            Target = "a.txt", Reason = "test", ScopeKey = "workspace-edits:demo" };
        var pending = broker.RequestAsync(request, default);
        Assert.True(broker.ResolveForTask(request.Id));
        Assert.Equal(ApprovalOutcome.Allowed, await pending);
        Assert.Equal(ApprovalOutcome.Allowed, await broker.RequestAsync(request with { Id = Guid.NewGuid() }, default));
        var different = request with { Id = Guid.NewGuid(), ScopeKey = "other" };
        var other = broker.RequestAsync(different, default);
        Assert.False(other.IsCompleted);
        broker.Resolve(different.Id, false);
        Assert.Equal(ApprovalOutcome.Denied, await other);
        broker.CancelTask(request.TaskId);
        var again = request with { Id = Guid.NewGuid() };
        var revoked = broker.RequestAsync(again, default);
        Assert.False(revoked.IsCompleted);
        broker.Resolve(again.Id, false);
        Assert.Equal(ApprovalOutcome.Denied, await revoked);
    }
}
