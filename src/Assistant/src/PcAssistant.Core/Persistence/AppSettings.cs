using System.Text.Json;
using System.Text.Json.Serialization;
using PcAssistant.Core.Model;

namespace PcAssistant.Core.Persistence;

/// <summary>
/// The Nexus bridge. Off until the owner turns it on; while off there is no pipe at all. Trust is one
/// executable path, set when the owner approves the Nexus that asked to connect.
/// </summary>
public sealed class NexusBridgeSettings
{
    public bool Enabled { get; set; }

    public string? TrustedClientExecutable { get; set; }
}

/// <summary>Owner preferences. Small, human-readable, and never contains credentials.</summary>
public sealed class AppSettings
{
    /// <summary>Directories the owner has allowed workspaces to live under.</summary>
    public List<string> WorkspaceRoots { get; set; } = new();

    /// <summary>Last-used mode, restored for the next new task.</summary>
    public AssistantMode LastMode { get; set; } = AssistantMode.Auto;

    /// <summary>Explicit path to claude.exe, when auto-discovery picks the wrong one.</summary>
    public string? ClaudeExecutableOverride { get; set; }
    public bool EnableChromeIntegration { get; set; } = true;
    public bool EnablePcTools { get; set; } = true;

    /// <summary>System, Dark, or Light. Invalid legacy values fall back to System.</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Minutes an approval card waits before it is treated as a denial.</summary>
    public int ApprovalTimeoutMinutes { get; set; } = 10;

    /// <summary>Cap for each task's raw provider log.</summary>
    public int RawLogMaxMegabytes { get; set; } = 8;

    /// <summary>Whether Nexus on this PC may reach PC Assistant, and which Nexus.</summary>
    public NexusBridgeSettings NexusBridge { get; set; } = new();

    [JsonIgnore]
    public string? FilePath { get; private set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
                if (loaded is not null)
                {
                    loaded.FilePath = path;
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // A corrupted settings file must not stop the app from starting.
        }

        return new AppSettings { FilePath = path };
    }

    public void Save()
    {
        if (FilePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception)
        {
            // Losing a preference is acceptable; crashing over it is not.
        }
    }
}
