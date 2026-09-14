namespace PcAssistant.Core.Workspaces;

public sealed record WorkspaceValidation(bool Ok, string? CanonicalPath, string? Error)
{
    public static WorkspaceValidation Fail(string reason) => new(false, null, reason);
    public static WorkspaceValidation Success(string path) => new(true, path, null);
}

/// <summary>
/// PC Assistant has no arbitrary filesystem scope. A directory may only become a task's
/// workspace if it canonicalizes to a real directory underneath one of the owner's
/// configured roots, and is not a protected Windows location.
/// </summary>
public sealed class WorkspaceService
{
    private readonly Func<IReadOnlyList<string>> _rootsProvider;

    public WorkspaceService(Func<IReadOnlyList<string>> rootsProvider) => _rootsProvider = rootsProvider;

    public IReadOnlyList<string> Roots => _rootsProvider();

    /// <summary>Validates a candidate root the owner is trying to add.</summary>
    public WorkspaceValidation ValidateRoot(string? candidate)
    {
        var canonical = PathScope.Canonicalize(candidate);
        if (canonical is null) return WorkspaceValidation.Fail("That is not a usable path.");
        if (!Directory.Exists(canonical)) return WorkspaceValidation.Fail("That directory does not exist.");
        if (PathScope.IsProtectedSystemLocation(canonical))
            return WorkspaceValidation.Fail("Windows and system directories cannot be used as project roots.");

        return WorkspaceValidation.Success(canonical);
    }

    /// <summary>Validates a workspace the owner is trying to select for a task.</summary>
    public WorkspaceValidation ValidateWorkspace(string? candidate)
    {
        var canonical = PathScope.Canonicalize(candidate);
        if (canonical is null) return WorkspaceValidation.Fail("That is not a usable path.");
        if (!Directory.Exists(canonical)) return WorkspaceValidation.Fail("That directory does not exist.");
        if (PathScope.IsProtectedSystemLocation(canonical))
            return WorkspaceValidation.Fail("Windows and system directories cannot be used as workspaces.");

        var roots = Roots;
        if (roots.Count == 0)
            return WorkspaceValidation.Fail("Add a project root first, then pick a workspace inside it.");

        foreach (var root in roots)
        {
            if (PathScope.IsInside(root, canonical)) return WorkspaceValidation.Success(canonical);
        }

        return WorkspaceValidation.Fail("That directory is not inside any configured project root.");
    }

    /// <summary>Lists immediate child directories of every configured root.</summary>
    public IReadOnlyList<string> EnumerateWorkspaces()
    {
        var result = new List<string>();
        foreach (var root in Roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith('.')) continue;
                    result.Add(dir);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return result.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Creates a new project folder under an allowed root and returns its canonical path.</summary>
    public WorkspaceValidation CreateWorkspace(string root, string folderName)
    {
        var rootCheck = ValidateRoot(root);
        if (!rootCheck.Ok) return rootCheck;

        if (string.IsNullOrWhiteSpace(folderName))
            return WorkspaceValidation.Fail("Give the project a folder name.");

        folderName = folderName.Trim();
        if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return WorkspaceValidation.Fail("That folder name contains characters Windows does not allow.");
        if (folderName is "." or "..")
            return WorkspaceValidation.Fail("That folder name is not allowed.");

        var target = Path.Combine(rootCheck.CanonicalPath!, folderName);

        // Re-validate after combining: this rejects names that traverse out of the root.
        var canonical = PathScope.Canonicalize(target);
        if (canonical is null || !PathScope.IsInside(rootCheck.CanonicalPath!, canonical))
            return WorkspaceValidation.Fail("That name would create the folder outside the project root.");

        if (Directory.Exists(canonical))
            return WorkspaceValidation.Fail("A folder with that name already exists.");

        try
        {
            Directory.CreateDirectory(canonical);
        }
        catch (Exception ex)
        {
            return WorkspaceValidation.Fail("Could not create the folder: " + ex.Message);
        }

        return WorkspaceValidation.Success(canonical);
    }
}
