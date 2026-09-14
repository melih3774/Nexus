using PcAssistant.Core.Workspaces;
using Xunit;

namespace PcAssistant.Tests;

public class PathScopeTests
{
    [Fact]
    public void CanonicalizeRemovesParentSegments()
    {
        var result = PathScope.Canonicalize(@"C:\projects\demo\..\demo\src");
        Assert.Equal(@"C:\projects\demo\src", result);
    }

    [Fact]
    public void CanonicalizeStripsTrailingSeparator()
        => Assert.Equal(@"C:\projects\demo", PathScope.Canonicalize(@"C:\projects\demo\"));

    [Fact]
    public void CanonicalizeRejectsEmpty()
    {
        Assert.Null(PathScope.Canonicalize(null));
        Assert.Null(PathScope.Canonicalize("   "));
    }

    [Fact]
    public void IsInsideAcceptsTheRootItself()
        => Assert.True(PathScope.IsInside(@"C:\projects", @"C:\projects"));

    [Fact]
    public void IsInsideAcceptsDescendants()
        => Assert.True(PathScope.IsInside(@"C:\projects", @"C:\projects\demo\src\Main.cs"));

    [Fact]
    public void IsInsideRejectsSiblingWithSharedPrefix()
        => Assert.False(PathScope.IsInside(@"C:\projects", @"C:\projects-secret\x.txt"));

    [Fact]
    public void IsInsideRejectsTraversalOut()
        => Assert.False(PathScope.IsInside(@"C:\projects\demo", @"C:\projects\demo\..\..\Windows"));

    [Fact]
    public void SystemDirectoriesAreProtected()
    {
        Assert.True(PathScope.IsProtectedSystemLocation(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.True(PathScope.IsProtectedSystemLocation(
            Environment.GetFolderPath(Environment.SpecialFolder.System)));
        Assert.True(PathScope.IsProtectedSystemLocation(@"C:\"));
    }
}

public class WorkspaceServiceTests
{
    private static (WorkspaceService Service, string Root, List<string> Roots) Build()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcassistant-ws", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var roots = new List<string> { root };
        return (new WorkspaceService(() => roots), root, roots);
    }

    [Fact]
    public void WorkspaceInsideARootIsAccepted()
    {
        var (service, root, _) = Build();
        var project = Path.Combine(root, "demo");
        Directory.CreateDirectory(project);

        var result = service.ValidateWorkspace(project);

        Assert.True(result.Ok);
        Assert.Equal(project, result.CanonicalPath);
    }

    [Fact]
    public void WorkspaceOutsideEveryRootIsRejected()
    {
        var (service, _, _) = Build();
        var outside = Path.Combine(Path.GetTempPath(), "pcassistant-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);

        var result = service.ValidateWorkspace(outside);

        Assert.False(result.Ok);
        Assert.Contains("not inside", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TraversalOutOfARootIsRejected()
    {
        var (service, root, _) = Build();
        var escape = Path.Combine(root, "..", "..", "Windows");

        Assert.False(service.ValidateWorkspace(escape).Ok);
    }

    [Fact]
    public void SystemDirectoryIsNeverAWorkspace()
    {
        var (service, _, roots) = Build();
        roots.Add(@"C:\");

        Assert.False(service.ValidateWorkspace(
            Environment.GetFolderPath(Environment.SpecialFolder.System)).Ok);
    }

    [Fact]
    public void SystemDirectoryIsNeverARoot()
        => Assert.False(Build().Service.ValidateRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)).Ok);

    [Fact]
    public void CreateWorkspaceMakesTheFolderUnderTheRoot()
    {
        var (service, root, _) = Build();

        var result = service.CreateWorkspace(root, "new-project");

        Assert.True(result.Ok);
        Assert.True(Directory.Exists(result.CanonicalPath!));
        Assert.True(PathScope.IsInside(root, result.CanonicalPath));
    }

    [Fact]
    public void CreateWorkspaceRejectsTraversalInTheName()
    {
        var (service, root, _) = Build();

        var result = service.CreateWorkspace(root, @"..\escaped");

        Assert.False(result.Ok);
    }

    [Fact]
    public void CreateWorkspaceRejectsDuplicates()
    {
        var (service, root, _) = Build();
        service.CreateWorkspace(root, "dupe");

        Assert.False(service.CreateWorkspace(root, "dupe").Ok);
    }

    [Fact]
    public void EnumerateListsProjectsUnderRoots()
    {
        var (service, root, _) = Build();
        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        Directory.CreateDirectory(Path.Combine(root, "beta"));
        Directory.CreateDirectory(Path.Combine(root, ".hidden"));

        var found = service.EnumerateWorkspaces().Select(Path.GetFileName).ToList();

        Assert.Contains("alpha", found);
        Assert.Contains("beta", found);
        Assert.DoesNotContain(".hidden", found);
    }
}
