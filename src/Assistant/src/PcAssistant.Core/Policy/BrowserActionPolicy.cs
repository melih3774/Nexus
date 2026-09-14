using System.Text.RegularExpressions;

namespace PcAssistant.Core.Policy;

/// <summary>
/// What a browser action would actually do, rather than what tool it used.
///
/// The previous rule was "anything that is not obviously a read asks". That is safe and
/// nearly useless: following a link, scrolling a page and typing a search query all cost the
/// owner an approval, so real browsing in AUTO meant a stream of interruptions, and an owner
/// who is interrupted twenty times stops reading the cards.
///
/// So actions are separated by consequence instead:
///
/// * Reading a page, scrolling it, taking a screenshot — free.
/// * Navigating to an http(s) address, opening a tab, following a link, typing a search
///   query, focusing a field — ordinary work, allowed in AUTO.
/// * Sending, posting, publishing, submitting, buying, paying, booking, deleting, accepting
///   terms, changing an account, uploading a file, running script in the page — always the
///   owner's, in both modes.
///
/// Where the target cannot be identified — a click on bare coordinates, or on an element this
/// task has never read — the answer is to ask. Unknown is treated as consequential, so the
/// looser policy never rests on a guess.
/// </summary>
public static partial class BrowserActionPolicy
{
    public const string Prefix = "mcp__claude-in-chrome__";

    private static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal)
    {
        "tabs_context_mcp", "read_page", "get_page_text", "find", "read_console_messages",
        "read_network_requests", "shortcuts_list", "list_connected_browsers",
    };

    /// <summary>Pointer and view actions that observe without changing anything.</summary>
    private static readonly HashSet<string> ObservingActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "screenshot", "zoom", "scroll", "scroll_to", "hover", "wait",
    };

    /// <summary>Ordinary page interaction: it moves around a page, it does not commit anything.</summary>
    private static readonly HashSet<string> NavigatingActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "left_click", "double_click", "triple_click", "type", "left_click_drag", "right_click",
    };

    /// <summary>
    /// Words that mean an action leaves the page and reaches someone else — a message, an
    /// order, a payment, a deletion. English and Turkish, because this machine's Windows and
    /// this owner's browsing are both Turkish, and a policy that only reads English labels
    /// would quietly fall back to allowing the exact clicks it exists to catch.
    /// </summary>
    [GeneratedRegex(
        @"\b(send|post|publish|submit|tweet|reply|comment|share|buy|purchase|checkout|pay|payment|order|" +
        @"subscribe|unsubscribe|donate|book|reserve|confirm|delete|remove|erase|discard|destroy|" +
        @"accept|agree|consent|authorize|authorise|grant|allow\s+access|upload|transfer|withdraw|deposit|" +
        @"sign\s*out|log\s*out|deactivate|close\s+account|change\s+password|reset\s+password)\b" +
        @"|(gönder|yayınla|paylaş|satın\s*al|öde|ödeme|sipariş|abone|onayla|onay|sil|kaldır|kabul\s*et|" +
        @"yetki\s*ver|yükle|aktar|çekim|rezervasyon|ayır|çıkış\s*yap|hesab[ıi]\s*kapat|şifre.{0,12}değiştir)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsequentialRx();

    /// <summary>A value that should never be typed by an assistant, whatever field it is going into.</summary>
    [GeneratedRegex(
        @"(?:\d[ \-]?){13,19}" +                                  // card-number shaped
        @"|\b(?:cvv|cvc|otp|one[- ]time|passcode|password|passwd|secret|token|api[_ -]?key|seed\s*phrase|" +
        @"recovery\s*code|parola|şifre|güvenlik\s*kodu|kart\s*numaras)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveValueRx();

    /// <summary>Keys that commit whatever is in front of them.</summary>
    private static readonly HashSet<string> SubmittingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Return", "Enter", "NumpadEnter", "KP_Enter",
    };

    /// <summary>
    /// Classifies one browser tool call. Returns null when the tool is not a browser tool.
    /// </summary>
    public static RiskCategory? Classify(ToolInvocation call, BrowserContext? context)
    {
        if (!call.ToolName.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var tool = call.ToolName[Prefix.Length..];

        if (ReadTools.Contains(tool)) return RiskCategory.Read;

        return tool switch
        {
            // Arbitrary script in the page's own origin can do anything the page can, including
            // acting as the signed-in owner. There is no reading of it that makes it routine.
            "javascript_tool" => RiskCategory.InteractiveInput,

            // Sending a file out of the machine is not a browsing action.
            "file_upload" or "upload_image" => RiskCategory.InteractiveInput,

            // A saved shortcut is opaque here; what it does is not visible in its name.
            "shortcuts_execute" => RiskCategory.InteractiveInput,

            "navigate" => ClassifyNavigate(call),
            "tabs_create_mcp" => RiskCategory.DesktopAction,
            "tabs_close_mcp" => ClassifyClose(call, context),
            "resize_window" or "select_browser" or "switch_browser" => RiskCategory.DesktopAction,
            "form_input" => ClassifyValue(call.GetString("value"), call.GetString("ref"), context),
            "computer" => ClassifyComputer(call, context),

            // A browser tool nobody has classified yet is treated as consequential.
            _ => RiskCategory.InteractiveInput,
        };
    }

    private static RiskCategory ClassifyNavigate(ToolInvocation call)
    {
        var url = call.GetString("url");
        if (string.IsNullOrWhiteSpace(url)) return RiskCategory.InteractiveInput;

        // Moving through history is as consequential as scrolling.
        if (url.Equals("back", StringComparison.OrdinalIgnoreCase) ||
            url.Equals("forward", StringComparison.OrdinalIgnoreCase)) return RiskCategory.DesktopAction;

        var candidate = url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return RiskCategory.InteractiveInput;

        // javascript:, file:, and custom protocol handlers are not ordinary navigation.
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return RiskCategory.InteractiveInput;

        // An address is a GET, but some of them are the confirmation step itself.
        return ConsequentialRx().IsMatch(uri.PathAndQuery) ? RiskCategory.InteractiveInput : RiskCategory.DesktopAction;
    }

    /// <summary>
    /// Closing a tab this task opened is tidying up after itself. Closing one that was already
    /// there is throwing away the owner's work, so it asks — including when the tab cannot be
    /// identified at all.
    /// </summary>
    private static RiskCategory ClassifyClose(ToolInvocation call, BrowserContext? context) =>
        context is not null && context.OwnsTab(call.GetString("tabId", "tab_id"))
            ? RiskCategory.DesktopAction
            : RiskCategory.InteractiveInput;

    private static RiskCategory ClassifyComputer(ToolInvocation call, BrowserContext? context)
    {
        var action = call.GetString("action");
        if (action is null) return RiskCategory.InteractiveInput;
        if (ObservingActions.Contains(action)) return RiskCategory.Read;

        if (action.Equals("key", StringComparison.OrdinalIgnoreCase))
        {
            var text = call.GetString("text") ?? "";
            // Enter commits the form under the cursor; the rest are navigation.
            var submits = text.Split([' ', '+'], StringSplitOptions.RemoveEmptyEntries).Any(SubmittingKeys.Contains);
            return submits ? RiskCategory.InteractiveInput : RiskCategory.DesktopAction;
        }

        if (action.Equals("type", StringComparison.OrdinalIgnoreCase))
            return ClassifyValue(call.GetString("text"), call.GetString("ref"), context);

        if (!NavigatingActions.Contains(action)) return RiskCategory.InteractiveInput;

        // A click is judged by what it lands on. With a ref this task has read, the label is
        // known; with bare coordinates it is not, and unknown asks.
        var label = context?.LabelFor(call.GetString("ref"));
        if (label is null) return RiskCategory.InteractiveInput;
        return ConsequentialRx().IsMatch(label) ? RiskCategory.InteractiveInput : RiskCategory.DesktopAction;
    }

    /// <summary>
    /// Typing is ordinary — a search box, a filter, a form field — until either the value or
    /// the field it is going into looks like a credential or a card.
    ///
    /// Only the credential test is applied to the field's label, never the consequence test. A
    /// textbox labelled "Comment" is where a comment is written; posting it is a separate
    /// action, and that one does ask. Treating the field as consequential would put an approval
    /// in front of every message the owner asked to be drafted, and then another one to send it.
    /// </summary>
    private static RiskCategory ClassifyValue(string? value, string? reference, BrowserContext? context)
    {
        if (value is null) return RiskCategory.InteractiveInput;
        if (SensitiveValueRx().IsMatch(value)) return RiskCategory.InteractiveInput;

        var label = context?.LabelFor(reference);
        if (label is not null && SensitiveValueRx().IsMatch(label)) return RiskCategory.InteractiveInput;

        return RiskCategory.DesktopAction;
    }

    /// <summary>Owner-facing reason, so an approval card says which consequence triggered it.</summary>
    public static string? Explain(ToolInvocation call, RiskCategory category)
    {
        if (category != RiskCategory.InteractiveInput || !call.ToolName.StartsWith(Prefix, StringComparison.Ordinal))
            return null;

        var tool = call.ToolName[Prefix.Length..];
        return tool switch
        {
            "javascript_tool" => "This runs script inside the page, with everything the signed-in page can do.",
            "file_upload" or "upload_image" => "This sends a file from your PC to a website.",
            "tabs_close_mcp" => "This closes a browser tab this task did not open.",
            "navigate" => "This address looks like a confirmation or account step rather than ordinary browsing.",
            "form_input" or "computer" => "This looks like it sends, submits, pays for, deletes or agrees to something, " +
                                          "or types a credential. Ordinary browsing does not ask.",
            _ => "This browser action can have effects outside your PC.",
        };
    }
}
