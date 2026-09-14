using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PcAssistant.ClaudeCode;

/// <summary>
/// Builds the per-task files handed to Claude Code: the settings overlay that installs
/// PC Assistant's hooks, and the behaviour text loaded from the PC Assistant skill.
/// </summary>
public sealed class ClaudeRuntimeFiles
{
    private readonly string _runtimeDirectory;
    private readonly string _hookHostPath;
    private readonly string? _pcToolsPath;

    public ClaudeRuntimeFiles(string runtimeDirectory, string? hookHostPath = null, string? pcToolsPath = null)
    {
        _runtimeDirectory = runtimeDirectory;
        Directory.CreateDirectory(_runtimeDirectory);
        _hookHostPath = hookHostPath ?? DefaultHookHostPath();
        _pcToolsPath = pcToolsPath;
    }

    public string HookHostPath => _hookHostPath;
    public bool HookHostExists => File.Exists(_hookHostPath);

    private static string DefaultHookHostPath()
    {
        var baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, "PcAssistant.HookHost.exe");
    }

    /// <summary>
    /// Writes the settings file passed via <c>--settings</c>.
    ///
    /// PreToolUse is the enforcement point: it is the only hook that can synchronously
    /// stop an operation before it happens. PostToolUse and Stop are observational and
    /// give PC Assistant authoritative tool completion/failure signals.
    /// </summary>
    public string WriteSettings(Guid taskId, int approvalTimeoutMinutes)
    {
        // Give the hook helper longer than the approval window so the owner, not the
        // hook timeout, is what decides.
        var hookTimeoutSeconds = Math.Max(60, (approvalTimeoutMinutes + 2) * 60);

        var settings = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = HookEntry("PreToolUse", hookTimeoutSeconds),
                ["PostToolUse"] = HookEntry("PostToolUse", 30),
                ["Stop"] = HookEntry("Stop", 30),
            },
        };

        var path = Path.Combine(_runtimeDirectory, $"settings-{taskId:N}.json");
        File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        return path;
    }

    public string PcToolsPath => _pcToolsPath ?? Path.Combine(AppContext.BaseDirectory, "PcTools", "PcAssistant.PcTools.exe");

    public string WriteMcpSettings(Guid taskId)
    {
        if (!File.Exists(PcToolsPath))
            throw new FileNotFoundException("PC tools are missing. Build the Desktop project to deploy its helpers.", PcToolsPath);
        var path = Path.Combine(_runtimeDirectory, $"mcp-{taskId:N}.json");
        var config = new { mcpServers = new Dictionary<string, object>
        {
            ["pc-assistant"] = new { type = "stdio", command = PcToolsPath, args = Array.Empty<string>() },
        } };
        File.WriteAllText(path, JsonSerializer.Serialize(config), new UTF8Encoding(false));
        return path;
    }

    private JsonArray HookEntry(string eventName, int timeoutSeconds) => new()
    {
        new JsonObject
        {
            ["matcher"] = "*",
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = Quote(_hookHostPath) + " " + eventName,
                    ["timeout"] = timeoutSeconds,
                },
            },
        },
    };

    private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// The PC Assistant behaviour text.
    ///
    /// It lives as a real SKILL.md so it is versioned in one place, but it is applied via
    /// --append-system-prompt because PC Assistant's behaviour must be active on every
    /// turn. Skills are model-invoked on demand, which is the wrong trigger for
    /// always-on conduct, and dropping a skill file into the owner's project directories
    /// would be invasive.
    /// </summary>
    public static string LoadSkillBehaviour()
    {
        var text = ReadSkillFile();
        if (text is null) return FallbackBehaviour;

        // Strip the YAML frontmatter; the body is the behaviour contract.
        if (text.StartsWith("---", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                var after = text.IndexOf('\n', end + 1);
                if (after > 0) text = text[(after + 1)..];
            }
        }

        return text.Trim();
    }

    private static string? ReadSkillFile()
    {
        foreach (var candidate in SkillFileCandidates())
        {
            try
            {
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private static IEnumerable<string> SkillFileCandidates()
    {
        const string relative = @".claude\skills\pc-assistant\SKILL.md";

        // Deployed next to the application.
        yield return Path.Combine(AppContext.BaseDirectory, "PcAssistantSkill.md");

        // Running from the repository during development: walk up to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            yield return Path.Combine(dir.FullName, relative);

        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrEmpty(assemblyDir))
            yield return Path.Combine(assemblyDir, relative);
    }

    private const string FallbackBehaviour = """
        You are running inside PC Assistant, the owner's personal Windows desktop assistant.
        Keep user-facing updates concise and do not narrate routine tool calls.
        Ask questions only when the answer changes what you do, and ask them in conversation.
        Discussion and execution are separate: do not create or scaffold a project just
        because an idea was discussed. Never commit, push, or otherwise change Git state
        unless explicitly asked. Never claim an operation succeeded when it did not.
        """;
}
