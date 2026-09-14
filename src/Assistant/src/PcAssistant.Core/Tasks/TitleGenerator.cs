using System.Globalization;
using System.Text;

namespace PcAssistant.Core.Tasks;

/// <summary>
/// Titles are derived from the owner's first message. This is deliberately deterministic —
/// no extra model call is made just to name a task.
/// </summary>
public static class TitleGenerator
{
    private const int MaxLength = 60;

    private static readonly string[] LeadingNoise =
    {
        "please ", "can you ", "could you ", "i want to ", "i want you to ", "i need to ",
        "i would like to ", "help me ", "let's ", "lets ", "hey ", "hi ", "ok ", "okay ",
    };

    public static string FromPrompt(string? prompt, string fallback = "New task")
    {
        if (string.IsNullOrWhiteSpace(prompt)) return fallback;

        // First sentence or first line, whichever comes first.
        var text = prompt.Trim().ReplaceLineEndings("\n");
        var cut = text.IndexOfAny(new[] { '\n', '.', '?', '!' });
        if (cut > 12) text = text[..cut];
        text = text.Trim();

        if (text.Length == 0) text = prompt.Trim();

        // Drop conversational lead-ins so the title starts at the actual subject.
        var lowered = text.ToLowerInvariant();
        foreach (var noise in LeadingNoise)
        {
            if (lowered.StartsWith(noise, StringComparison.Ordinal))
            {
                text = text[noise.Length..].TrimStart();
                lowered = text.ToLowerInvariant();
            }
        }

        text = CollapseWhitespace(text);
        if (text.Length == 0) return fallback;

        if (text.Length > MaxLength)
        {
            var window = text[..MaxLength];
            var lastSpace = window.LastIndexOf(' ');
            text = (lastSpace > 20 ? window[..lastSpace] : window).TrimEnd() + "…";
        }

        return Capitalize(text);
    }

    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        var lastWasSpace = false;
        foreach (var ch in s)
        {
            var c = char.IsWhiteSpace(ch) ? ' ' : ch;
            if (c == ' ')
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static string Capitalize(string s)
    {
        if (s.Length == 0) return s;
        var first = char.ToUpper(s[0], CultureInfo.CurrentCulture);
        return first + s[1..];
    }

    /// <summary>Title for a task created via "Continue as New Task".</summary>
    public static string ForContinuation(string previousTitle)
    {
        var baseTitle = previousTitle;

        // Avoid "(continued) (continued)" when branching repeatedly.
        const string suffix = " (continued)";
        if (baseTitle.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return baseTitle;

        if (baseTitle.Length + suffix.Length > MaxLength)
            baseTitle = baseTitle[..(MaxLength - suffix.Length - 1)].TrimEnd() + "…";

        return baseTitle + suffix;
    }
}
