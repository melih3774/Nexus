using System.Text.Json;
using PcAssistant.Core.Model;
using PcAssistant.Core.Policy;
using Xunit;

namespace PcAssistant.Tests;

/// <summary>
/// The browser policy's whole point is that consequence, not tool name, decides. These tests
/// are written as the two halves of that claim: ordinary browsing must stop interrupting the
/// owner in AUTO, and the things that reach other people must still stop, every time.
/// </summary>
public sealed class BrowserPolicyTests
{
    private const string Chrome = "mcp__claude-in-chrome__";

    private static ToolInvocation Call(string tool, object arguments) =>
        ToolInvocation.FromJson(Chrome + tool,
            JsonSerializer.SerializeToElement(arguments), @"C:\workspace");

    private static RiskCategory Classify(string tool, object arguments, BrowserContext? context = null) =>
        BrowserActionPolicy.Classify(Call(tool, arguments), context)
        ?? throw new Xunit.Sdk.XunitException("A claude-in-chrome tool must be classified.");

    private static PolicyDecision InAuto(string tool, object arguments, BrowserContext? context = null) =>
        ApprovalPolicy.Evaluate(AssistantMode.Auto, Classify(tool, arguments, context));

    [Fact]
    public void ReadingAPageIsFreeInBothModes()
    {
        foreach (var (tool, arguments) in new (string, object)[]
                 {
                     ("read_page", new { filter = "interactive" }),
                     ("get_page_text", new { max_chars = 5000 }),
                     ("find", new { query = "Save" }),
                     ("tabs_context_mcp", new { }),
                     ("read_console_messages", new { onlyErrors = true }),
                     ("read_network_requests", new { limit = 20 }),
                     ("computer", new { action = "screenshot" }),
                     ("computer", new { action = "scroll", coordinate = new[] { 400, 400 }, scroll_direction = "down" }),
                     ("computer", new { action = "hover", ref_ = "ref_3" }),
                 })
        {
            Assert.Equal(RiskCategory.Read, Classify(tool, arguments));
            Assert.Equal(PolicyDecision.Allow, ApprovalPolicy.Evaluate(AssistantMode.Ask, Classify(tool, arguments)));
        }
    }

    [Fact]
    public void OrdinaryBrowsingProceedsInAutoWithoutAskingTheOwner()
    {
        var context = new BrowserContext();
        context.RememberLabels("""
            - link "Read more about the release" [ref_4]
            - button "Show more results" [ref_7]
            - textbox "Search the docs" [ref_9]
            """);

        Assert.Equal(PolicyDecision.Allow, InAuto("navigate", new { url = "https://learn.microsoft.com/windows" }));
        Assert.Equal(PolicyDecision.Allow, InAuto("navigate", new { url = "back" }));
        Assert.Equal(PolicyDecision.Allow, InAuto("navigate", new { url = "forward" }));
        Assert.Equal(PolicyDecision.Allow, InAuto("tabs_create_mcp", new { foreground = false }));
        Assert.Equal(PolicyDecision.Allow, InAuto("computer", new { action = "left_click", @ref = "ref_4" }, context));
        Assert.Equal(PolicyDecision.Allow, InAuto("computer", new { action = "left_click", @ref = "ref_7" }, context));
        Assert.Equal(PolicyDecision.Allow, InAuto("computer", new { action = "type", text = "windows graphics capture", @ref = "ref_9" }, context));
        Assert.Equal(PolicyDecision.Allow, InAuto("form_input", new { @ref = "ref_9", value = "docs search" }, context));
        Assert.Equal(PolicyDecision.Allow, InAuto("computer", new { action = "key", text = "PageDown" }));
        Assert.Equal(PolicyDecision.Allow, InAuto("resize_window", new { preset = "mobile" }));
    }

    [Fact]
    public void ActionsThatReachOtherPeopleAlwaysAskEvenInAuto()
    {
        var context = new BrowserContext();
        context.RememberLabels("""
            - button "Send message" [ref_1]
            - button "Post reply" [ref_2]
            - button "Buy now" [ref_3]
            - button "Delete account" [ref_4]
            - button "I agree to the terms" [ref_5]
            - button "Gönder" [ref_6]
            - button "Satın al" [ref_7]
            - button "Sil" [ref_8]
            """);

        foreach (var reference in new[] { "ref_1", "ref_2", "ref_3", "ref_4", "ref_5", "ref_6", "ref_7", "ref_8" })
            Assert.Equal(PolicyDecision.Ask,
                InAuto("computer", new { action = "left_click", @ref = reference }, context));

        // Enter commits whatever form is focused.
        Assert.Equal(PolicyDecision.Ask, InAuto("computer", new { action = "key", text = "Return" }));
        Assert.Equal(PolicyDecision.Ask, InAuto("computer", new { action = "key", text = "ctrl+Enter" }));

        // Script in the page acts as the signed-in owner.
        Assert.Equal(PolicyDecision.Ask, InAuto("javascript_tool", new { action = "javascript_exec", text = "fetch('/pay')" }));

        // Files leaving the machine, and non-http navigation.
        Assert.Equal(PolicyDecision.Ask, InAuto("file_upload", new { path = @"C:\secret.pdf" }));
        Assert.Equal(PolicyDecision.Ask, InAuto("upload_image", new { path = @"C:\a.png" }));
        Assert.Equal(PolicyDecision.Ask, InAuto("navigate", new { url = "javascript:alert(1)" }));
        Assert.Equal(PolicyDecision.Ask, InAuto("navigate", new { url = "file:///C:/Windows" }));
        Assert.Equal(PolicyDecision.Ask, InAuto("navigate", new { url = "https://shop.example.com/checkout/confirm" }));

        // An unrecognized browser tool is consequential until someone classifies it.
        Assert.Equal(PolicyDecision.Ask, InAuto("some_future_tool", new { }));
    }

    [Fact]
    public void AClickWithNoIdentifiableTargetAsks()
    {
        // Bare coordinates carry no evidence of what is under them.
        Assert.Equal(PolicyDecision.Ask, InAuto("computer", new { action = "left_click", coordinate = new[] { 500, 300 } }));

        // A reference this task has never read is equally unknown.
        Assert.Equal(PolicyDecision.Ask, InAuto("computer", new { action = "left_click", @ref = "ref_99" }, new BrowserContext()));

        // And with no context at all, every click asks — the old behaviour, and the safe direction.
        Assert.Equal(PolicyDecision.Ask, InAuto("computer", new { action = "left_click", @ref = "ref_4" }));
    }

    [Fact]
    public void CredentialsAndCardNumbersAreNeverTypedWithoutTheOwner()
    {
        var context = new BrowserContext();
        context.RememberLabels("""
            - textbox "Password" [ref_1]
            - textbox "Şifre" [ref_2]
            - textbox "One-time code" [ref_3]
            - textbox "Comment" [ref_4]
            """);

        Assert.Equal(PolicyDecision.Ask, InAuto("form_input", new { @ref = "ref_1", value = "anything" }, context));
        Assert.Equal(PolicyDecision.Ask, InAuto("form_input", new { @ref = "ref_2", value = "anything" }, context));
        Assert.Equal(PolicyDecision.Ask, InAuto("form_input", new { @ref = "ref_3", value = "123456" }, context));

        // Card-number-shaped values ask whatever field they are going into.
        Assert.Equal(PolicyDecision.Ask, InAuto("form_input", new { @ref = "ref_4", value = "4111 1111 1111 1111" }, context));
        Assert.Equal(PolicyDecision.Ask, InAuto("computer", new { action = "type", text = "4111111111111111" }, context));

        // An ordinary comment is ordinary.
        Assert.Equal(PolicyDecision.Allow, InAuto("form_input", new { @ref = "ref_4", value = "looks good to me" }, context));
    }

    [Fact]
    public void ATaskMayCloseItsOwnTabsButNotTheOwnersExistingOnes()
    {
        var context = new BrowserContext();
        context.RememberCreatedTabs("""{"tabId":"tab-created-by-task","url":"https://example.test"}""");

        Assert.True(context.OwnsTab("tab-created-by-task"));
        Assert.Equal(PolicyDecision.Allow, InAuto("tabs_close_mcp", new { tabId = "tab-created-by-task" }, context));

        // A tab this task did not open is the owner's work.
        Assert.Equal(PolicyDecision.Ask, InAuto("tabs_close_mcp", new { tabId = "tab-the-owner-had-open" }, context));
        Assert.Equal(PolicyDecision.Ask, InAuto("tabs_close_mcp", new { tabId = "anything" }, new BrowserContext()));
        Assert.Equal(PolicyDecision.Ask, InAuto("tabs_close_mcp", new { }, context));
    }

    [Fact]
    public void NavigatingForgetsTheOldPagesLabelsSoAStaleClickCannotInheritThem()
    {
        var context = new BrowserContext();
        context.RememberLabels("""- link "Harmless article" [ref_2]""");
        Assert.Equal(PolicyDecision.Allow, InAuto("computer", new { action = "left_click", @ref = "ref_2" }, context));

        context.PageChanged();

        Assert.Null(context.LabelFor("ref_2"));
        Assert.Equal(PolicyDecision.Ask, InAuto("computer", new { action = "left_click", @ref = "ref_2" }, context));
    }

    [Fact]
    public void LabelAndTabMemoryStaysBounded()
    {
        var context = new BrowserContext();
        for (var i = 0; i < 900; i++) context.RememberLabels($"- button \"Item {i}\" [ref_{i}]");
        Assert.InRange(context.KnownLabelCount, 1, 500);

        for (var i = 0; i < 200; i++) context.RememberCreatedTabs($$"""{"tabId":"tab-{{i}}"}""");
        Assert.InRange(context.OwnedTabCount, 1, 64);
    }

    [Fact]
    public void ApprovalCardsSayWhichConsequenceTriggeredThem()
    {
        Assert.Contains("script inside the page",
            BrowserActionPolicy.Explain(Call("javascript_tool", new { text = "x" }), RiskCategory.InteractiveInput)!);
        Assert.Contains("sends a file",
            BrowserActionPolicy.Explain(Call("file_upload", new { path = "x" }), RiskCategory.InteractiveInput)!);
        Assert.Contains("did not open",
            BrowserActionPolicy.Explain(Call("tabs_close_mcp", new { tabId = "x" }), RiskCategory.InteractiveInput)!);

        // Nothing extra is said about actions that were allowed anyway.
        Assert.Null(BrowserActionPolicy.Explain(Call("read_page", new { }), RiskCategory.Read));
    }

    [Fact]
    public void NonBrowserToolsAreLeftToTheOrdinaryClassifier()
    {
        Assert.Null(BrowserActionPolicy.Classify(
            ToolInvocation.FromJson("Bash", JsonSerializer.SerializeToElement(new { command = "ls" }), @"C:\w"), null));
        Assert.Null(BrowserActionPolicy.Classify(
            ToolInvocation.FromJson("mcp__pc-assistant__capture_window",
                JsonSerializer.SerializeToElement(new { hwnd = 1 }), @"C:\w"), null));
    }
}
