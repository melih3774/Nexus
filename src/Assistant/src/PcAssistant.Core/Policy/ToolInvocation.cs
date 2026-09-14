using System.Text.Json;

namespace PcAssistant.Core.Policy;

/// <summary>A single tool call as reported by Claude Code, normalized for classification.</summary>
public sealed record ToolInvocation(
    string ToolName,
    IReadOnlyDictionary<string, JsonElement> Input,
    string WorkingDirectory)
{
    public string? GetString(params string[] keys)
    {
        foreach (var k in keys)
        {
            if (Input.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    /// <summary>The command text for shell tools, or null for non-shell tools.</summary>
    public string? Command => GetString("command", "script");

    /// <summary>The primary path argument for file tools, or null.</summary>
    public string? FilePath => GetString("file_path", "path", "notebook_path", "filePath");

    public static ToolInvocation FromJson(string toolName, JsonElement input, string workingDirectory)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (input.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in input.EnumerateObject()) dict[p.Name] = p.Value.Clone();
        }
        return new ToolInvocation(toolName, dict, workingDirectory);
    }
}
