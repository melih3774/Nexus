using System.Text;
using System.Text.Json;
using PcAssistant.Core.Model;

namespace PcAssistant.ClaudeCode;

public sealed record NormalizedTurn
{
    public string? SessionId { get; init; }
    public string? FinalText { get; init; }
    public bool? IsError { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Translates Claude Code's <c>--output-format stream-json</c> lines into PC Assistant's
/// own event model. Raw provider JSON never reaches the UI.
///
/// Every method here is defensive: a malformed or unexpected line produces nothing at
/// worst, and never throws.
/// </summary>
public sealed class StreamEventNormalizer
{
    private readonly Guid _taskId;
    private readonly Dictionary<string, string> _toolNamesById = new(StringComparer.Ordinal);
    private readonly Dictionary<int, TextBlock> _streamBlocks = new();
    private string? _streamMessageId;

    private sealed class TextBlock
    {
        public Guid Id { get; } = Guid.NewGuid();
        public StringBuilder Text { get; } = new();
        public string? CompletedText { get; set; }
        public int TextIndex { get; set; } = -1;
    }

    public StreamEventNormalizer(Guid taskId) => _taskId = taskId;

    /// <summary>
    /// Called with each tool result before it is clipped for the UI. The browser policy needs
    /// the whole accessibility read to know what the page's elements are labelled, and the
    /// four-thousand-character transcript version would lose most of them.
    /// </summary>
    public Action<string, string?>? ToolResultObserver { get; set; }

    /// <summary>Accumulated turn-level facts (session id, final text, error).</summary>
    public NormalizedTurn Turn { get; private set; } = new();

    /// <summary>Text blocks Claude produced this turn, in order.</summary>
    public IReadOnlyList<string> AssistantTexts => _assistantTexts;
    private readonly List<string> _assistantTexts = new();

    public IEnumerable<AssistantEvent> Consume(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) yield break;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // Not JSON: it is still in the raw log, which is where diagnostics belong.
            yield break;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) yield break;

            var type = GetString(root, "type");
            var sessionId = GetString(root, "session_id");
            if (!string.IsNullOrEmpty(sessionId) && Turn.SessionId is null)
                Turn = Turn with { SessionId = sessionId };

            switch (type)
            {
                case "system":
                    foreach (var e in FromSystem(root)) yield return e;
                    break;

                case "assistant":
                    foreach (var e in FromAssistant(root)) yield return e;
                    break;

                case "stream_event":
                    foreach (var e in FromStream(root)) yield return e;
                    break;

                case "user":
                    foreach (var e in FromUser(root)) yield return e;
                    break;

                case "result":
                    foreach (var e in FromResult(root)) yield return e;
                    break;
            }
        }
    }

    private IEnumerable<AssistantEvent> FromStream(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.Object)
            yield break;

        var type = GetString(evt, "type");
        if (type == "message_start")
        {
            _streamBlocks.Clear();
            _streamMessageId = evt.TryGetProperty("message", out var message)
                ? GetString(message, "id") : null;
            yield break;
        }

        if (!evt.TryGetProperty("index", out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var index) || index < 0) yield break;

        if (type == "content_block_start")
        {
            if (!evt.TryGetProperty("content_block", out var block) || GetString(block, "type") != "text")
                yield break;
            var state = new TextBlock();
            _streamBlocks[index] = state;
            var text = GetString(block, "text");
            if (!string.IsNullOrEmpty(text))
            {
                state.Text.Append(text);
                yield return TextEvent(state, AssistantEventKind.AssistantMessageDelta, text);
            }
        }
        else if (type == "content_block_delta")
        {
            if (!evt.TryGetProperty("delta", out var delta) || GetString(delta, "type") != "text_delta")
                yield break;
            var text = GetString(delta, "text");
            if (string.IsNullOrEmpty(text)) yield break;
            // A dropped or malformed start must not discard otherwise valid text.
            if (!_streamBlocks.TryGetValue(index, out var state))
                _streamBlocks[index] = state = new TextBlock();
            if (state.CompletedText is not null) yield break;
            state.Text.Append(text);
            yield return TextEvent(state, AssistantEventKind.AssistantMessageDelta, text);
        }
        else if (type == "content_block_stop" && _streamBlocks.TryGetValue(index, out var state))
        {
            var text = state.Text.ToString();
            if (!string.IsNullOrWhiteSpace(text) && state.CompletedText is null)
            {
                RememberText(state, text);
                yield return TextEvent(state, AssistantEventKind.AssistantMessage, text);
            }
        }
    }

    private void RememberText(TextBlock state, string text)
    {
        state.CompletedText = text;
        if (state.TextIndex < 0)
        {
            state.TextIndex = _assistantTexts.Count;
            _assistantTexts.Add(text);
        }
        else _assistantTexts[state.TextIndex] = text;
    }

    private AssistantEvent TextEvent(TextBlock state, AssistantEventKind kind, string text)
        => new() { Id = state.Id, TaskId = _taskId, Kind = kind, Title = "Claude", Detail = text };

    private IEnumerable<AssistantEvent> FromSystem(JsonElement root)
    {
        var subtype = GetString(root, "subtype");

        if (subtype == "init")
        {
            var model = GetString(root, "model");
            yield return Event(AssistantEventKind.Progress,
                model is null ? "Claude session started" : $"Claude session started ({model})");
        }
    }

    private IEnumerable<AssistantEvent> FromAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            yield break;

        // An authentication or API failure arrives as a synthetic assistant message.
        var apiError = GetString(root, "error");
        if (!string.IsNullOrEmpty(apiError)) Turn = Turn with { IsError = true, ErrorMessage = apiError };

        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            yield break;

        var messageId = GetString(message, "id");
        var matchesStream = _streamMessageId is null || messageId == _streamMessageId;
        var matchedBlocks = new HashSet<Guid>();
        var index = -1;
        foreach (var block in content.EnumerateArray())
        {
            index++;
            if (block.ValueKind != JsonValueKind.Object) continue;

            switch (GetString(block, "type"))
            {
                case "text":
                {
                    var text = GetString(block, "text");
                    if (string.IsNullOrWhiteSpace(text)) break;

                    var streamed = matchesStream ? MatchStreamBlock(index, text!, matchedBlocks) : null;
                    if (streamed is not null)
                    {
                        matchedBlocks.Add(streamed.Id);
                        if (streamed.CompletedText == text) break;
                        RememberText(streamed, text!);
                        yield return TextEvent(streamed, AssistantEventKind.AssistantMessage, text!);
                    }
                    else
                    {
                        _assistantTexts.Add(text!);
                        yield return Event(AssistantEventKind.AssistantMessage, Summarize(text!), text);
                    }
                    break;
                }

                case "tool_use":
                {
                    var name = GetString(block, "name") ?? "tool";
                    var id = GetString(block, "id");
                    if (id is not null) _toolNamesById[id] = name;

                    var detail = block.TryGetProperty("input", out var input)
                        ? DescribeInput(name, input)
                        : null;

                    yield return Event(AssistantEventKind.ToolStarted,
                        detail is null ? name : $"{name}: {detail}", detail, name);
                    break;
                }
            }
        }
    }

    private TextBlock? MatchStreamBlock(int index, string text, HashSet<Guid> matched)
    {
        bool Matches(TextBlock candidate) => !matched.Contains(candidate.Id) &&
            (candidate.CompletedText == text || (candidate.CompletedText is null &&
                text.StartsWith(candidate.Text.ToString(), StringComparison.Ordinal)));
        var unfinished = _streamBlocks.Values.FirstOrDefault(candidate => candidate.CompletedText is null && Matches(candidate));
        if (unfinished is not null) return unfinished;
        if (_streamBlocks.TryGetValue(index, out var indexed) && Matches(indexed)) return indexed;
        // Some CLI versions emit a completed assistant envelope for each content block.
        // Its array index starts again at zero even when thinking preceded the text.
        return _streamBlocks.Values.FirstOrDefault(Matches);
    }

    private IEnumerable<AssistantEvent> FromUser(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            yield break;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (GetString(block, "type") != "tool_result") continue;

            var id = GetString(block, "tool_use_id");
            var toolName = id is not null && _toolNamesById.TryGetValue(id, out var n) ? n : "tool";

            var isError = block.TryGetProperty("is_error", out var errFlag) &&
                          errFlag.ValueKind == JsonValueKind.True;

            var text = ExtractResultText(block);

            if (ToolResultObserver is { } observe)
            {
                try { observe(toolName, ExtractResultText(block, FullResultLimit)); }
                catch (Exception) { /* An observer must never break the transcript. */ }
            }

            yield return isError
                ? Event(AssistantEventKind.ToolFailed, $"{toolName} failed", text, toolName)
                : Event(AssistantEventKind.ToolCompleted, toolName, text, toolName);
        }
    }

    private IEnumerable<AssistantEvent> FromResult(JsonElement root)
    {
        var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
        var text = GetString(root, "result");

        Turn = Turn with
        {
            IsError = isError || Turn.IsError == true,
            FinalText = string.IsNullOrWhiteSpace(text) ? Turn.FinalText : text,
            ErrorMessage = isError && !string.IsNullOrWhiteSpace(text) ? text : Turn.ErrorMessage,
        };

        yield break;
    }

    /// <summary>Bounded even for observers: a page read is large, but it is not unbounded.</summary>
    private const int FullResultLimit = 200_000;

    private static string? ExtractResultText(JsonElement block, int limit = 4000)
    {
        if (!block.TryGetProperty("content", out var content)) return null;

        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return Clip(content.GetString(), limit);

            case JsonValueKind.Array:
            {
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && GetString(item, "type") == "text")
                        return Clip(GetString(item, "text"), limit);
                }
                return null;
            }

            default:
                return null;
        }
    }

    /// <summary>Short human-readable description of what a tool was asked to do.</summary>
    private static string? DescribeInput(string toolName, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;

        // Ordered by how much the value tells the owner. The PC and browser tools carry their
        // subject in different fields, and without these the progress list showed a bare tool
        // name for every window capture, application launch and control search.
        foreach (var key in new[]
                 {
                     "command", "file_path", "path", "pattern", "query", "url", "prompt",
                     "application", "page", "name", "reference", "text", "value", "action",
                 })
        {
            if (input.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return Summarize(text!, 120);
            }
        }

        // Numeric subjects are worth showing when there is no text one: a window handle at
        // least distinguishes two captures of different windows.
        foreach (var key in new[] { "hwnd", "volumePercent" })
        {
            if (input.TryGetProperty(key, out var number) && number.ValueKind == JsonValueKind.Number)
                return key + " " + number;
        }
        return null;
    }

    private static string Summarize(string text, int max = 100)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        while (flat.Contains("  ", StringComparison.Ordinal)) flat = flat.Replace("  ", " ");
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }

    private static string? Clip(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length <= max ? s : s[..max] + "\n… (truncated)";
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private AssistantEvent Event(AssistantEventKind kind, string title, string? detail = null, string? tool = null)
        => new() { TaskId = _taskId, Kind = kind, Title = title, Detail = detail, ToolName = tool };
}
