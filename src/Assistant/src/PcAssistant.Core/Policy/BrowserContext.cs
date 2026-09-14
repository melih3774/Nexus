using System.Text.RegularExpressions;

namespace PcAssistant.Core.Policy;

/// <summary>
/// What a task has learned about the browser, so a click can be judged by what it hits.
///
/// Two facts are worth remembering across a turn, and only two:
///
/// * which tabs this task opened, so closing one of its own is ordinary work while closing
///   one the owner had open is not;
/// * what the labels on the current page's elements say, so clicking "Send" and clicking
///   "Show more" can be told apart.
///
/// Both are fed from tool results the task already produced. Nothing is scraped, stored on
/// disk, or kept beyond the task. Element labels are page content and therefore untrusted:
/// they are used to decide whether to <em>ask</em> the owner, never to decide to proceed
/// without asking on their own — an unknown label asks.
/// </summary>
public sealed partial class BrowserContext
{
    private const int MaxLabels = 500;
    private const int MaxTabs = 64;

    private readonly Dictionary<string, string> labels = new(StringComparer.Ordinal);
    private readonly HashSet<string> ownedTabs = new(StringComparer.Ordinal);
    private readonly object gate = new();

    [GeneratedRegex(@"\[?(ref_\d{1,6})\]?", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceRx();

    [GeneratedRegex(@"""?tab(?:Id|_id)""?\s*[:=]\s*""?([A-Za-z0-9_\-]{1,64})""?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TabIdRx();

    /// <summary>Records the tab a <c>tabs_create</c> produced, if its id can be found in the result.</summary>
    public void RememberCreatedTabs(string? toolResult)
    {
        if (string.IsNullOrEmpty(toolResult)) return;
        lock (gate)
        {
            foreach (Match match in TabIdRx().Matches(toolResult))
            {
                if (ownedTabs.Count >= MaxTabs) return;
                ownedTabs.Add(match.Groups[1].Value);
            }
        }
    }

    /// <summary>
    /// Records element labels from an accessibility read. The whole line is kept rather than a
    /// parsed role and name: the exact shape of that output is not this code's to depend on,
    /// and a line containing "Send" is enough to know the click deserves an owner.
    /// </summary>
    public void RememberLabels(string? toolResult)
    {
        if (string.IsNullOrEmpty(toolResult)) return;
        lock (gate)
        {
            foreach (var line in toolResult.Split('\n'))
            {
                if (labels.Count >= MaxLabels) return;
                var match = ReferenceRx().Match(line);
                if (!match.Success) continue;
                var text = line.Replace(match.Value, " ", StringComparison.Ordinal).Trim();
                if (text.Length > 160) text = text[..160];
                labels[match.Groups[1].Value] = text;
            }
        }
    }

    /// <summary>A navigation replaces the page, and every element reference with it.</summary>
    public void PageChanged()
    {
        lock (gate) { labels.Clear(); }
    }

    public string? LabelFor(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        lock (gate) { return labels.TryGetValue(reference, out var label) ? label : null; }
    }

    public bool OwnsTab(string? tabId)
    {
        if (string.IsNullOrEmpty(tabId)) return false;
        lock (gate) { return ownedTabs.Contains(tabId); }
    }

    public int KnownLabelCount { get { lock (gate) { return labels.Count; } } }
    public int OwnedTabCount { get { lock (gate) { return ownedTabs.Count; } } }
}
