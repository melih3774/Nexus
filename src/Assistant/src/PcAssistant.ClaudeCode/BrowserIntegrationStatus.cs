using System.Text.Json;

namespace PcAssistant.ClaudeCode;

/// <summary>
/// Installation evidence only. The Claude MCP server can report connected while its
/// browser extension is disconnected, so this snapshot deliberately never claims Ready.
/// Does not inspect browser preferences, sessions, cookies, or authentication stores.
/// </summary>
public sealed record BrowserIntegrationStatus(
    bool ExtensionInstalled,
    string Summary,
    string Detail,
    IReadOnlyList<string> InstalledProfiles)
{
    public const string ExtensionId = "fcoeoabgfenejglbffodgkkbkcdhcgfn";
    public const string SetupUrl = "https://claude.ai/chrome";
    public const string DocumentationUrl = "https://code.claude.com/docs/en/chrome";

    /// <summary>
    /// Checks only known Chrome/Edge profile extension manifests. The optional root
    /// supports portable diagnostics and tests without touching a real browser profile.
    /// </summary>
    public static BrowserIntegrationStatus Inspect(string? localAppDataRoot = null)
    {
        var root = localAppDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var found = new List<string>();
        var incomplete = false;
        if (!string.IsNullOrWhiteSpace(root))
        {
            foreach (var (browser, relativePath) in new[]
                     {
                         ("Chrome", Path.Combine("Google", "Chrome", "User Data")),
                         ("Edge", Path.Combine("Microsoft", "Edge", "User Data")),
                     })
            {
                foreach (var profile in ReadDirectories(Path.Combine(root, relativePath), 64, ref incomplete))
                {
                    var profileName = Path.GetFileName(profile);
                    if (profileName != "Default" && !profileName.StartsWith("Profile ", StringComparison.Ordinal))
                        continue;

                    var extensionRoot = Path.Combine(profile, "Extensions", ExtensionId);
                    Version? newest = null;
                    foreach (var versionDirectory in ReadDirectories(extensionRoot, 32, ref incomplete))
                    {
                        var version = ReadManifestVersion(Path.Combine(versionDirectory, "manifest.json"), ref incomplete);
                        if (version is not null && (newest is null || version > newest)) newest = version;
                    }

                    if (newest is not null) found.Add($"{browser} / {profileName} ({newest})");
                }
            }
        }

        var installed = found.Count > 0;
        var summary = installed ? "Extension installed; connection unverified" : "Browser extension not detected";
        var detail = installed
            ? $"Found {string.Join(", ", found)}. Open that browser profile, enable Claude in Chrome, " +
              "and sign in with the same Claude account. Run Claude Code with --chrome, then use /chrome to reconnect " +
              "or select the browser. Restart the browser if official setup asks. A successful browser tool call verifies the connection."
            : "Install the official Claude in Chrome extension in Chrome or Edge, enable it, and sign in with the same " +
              "Claude account. Then run Claude Code with --chrome and complete /chrome setup. " +
              "An unrecognized profile location can also cause this result.";

        if (incomplete) detail += " Some installation files could not be inspected; the result may be incomplete.";
        detail += $" Setup: {SetupUrl} Help: {DocumentationUrl}";
        return new BrowserIntegrationStatus(installed, summary, detail, found.AsReadOnly());
    }

    private static string[] ReadDirectories(string path, int limit, ref bool incomplete)
    {
        try
        {
            if (!Directory.Exists(path)) return [];
            var directories = Directory.EnumerateDirectories(path).Take(limit + 1).ToArray();
            if (directories.Length > limit) incomplete = true;
            return directories.Take(limit).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            incomplete = true;
            return [];
        }
    }

    private static Version? ReadManifestVersion(string path, ref bool incomplete)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var file = File.OpenRead(path);
            if (file.Length > 64 * 1024)
            {
                incomplete = true;
                return null;
            }
            using var manifest = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 16 });
            if (manifest.RootElement.ValueKind == JsonValueKind.Object &&
                manifest.RootElement.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String &&
                Version.TryParse(version.GetString(), out var parsed)) return parsed;
            incomplete = true;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        {
            incomplete = true;
            return null;
        }
    }
}
