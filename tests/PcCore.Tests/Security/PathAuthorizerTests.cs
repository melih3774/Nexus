using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Security;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using PcCore.Windows;
using Xunit;

namespace PcCore.Tests.Security;

/// <summary>
/// The filesystem boundary is the most safety-critical component in v0.1, so it gets the largest
/// suite. Everything here runs against real scratch directories, including a real NTFS junction.
/// </summary>
public sealed class PathAuthorizerTests : IDisposable
{
    private readonly TempDirectory _root = new("root");
    private readonly TempDirectory _outside = new("outside");
    private readonly PathAuthorizer _authorizer;

    public PathAuthorizerTests()
    {
        _authorizer = TestSecurity.AuthorizerFor(_root.Path);
    }

    public void Dispose()
    {
        _root.Dispose();
        _outside.Dispose();
    }

    // --- Allowed ------------------------------------------------------------------------------

    [Fact]
    public void Allows_TheRootItself()
    {
        var result = _authorizer.Authorize(_root.Path);

        Assert.True(result.Allowed);
        Assert.Equal(_root.Path, result.RealPath);
    }

    [Fact]
    public void Allows_TheRootWithATrailingSeparator()
    {
        Assert.True(_authorizer.Authorize(_root.Path + Path.DirectorySeparatorChar).Allowed);
    }

    [Fact]
    public void Allows_ANestedDirectory()
    {
        var nested = _root.CreateDirectory(Path.Combine("a", "b", "c"));

        Assert.True(_authorizer.Authorize(nested).Allowed);
    }

    [Fact]
    public void Allows_ANestedFile()
    {
        var file = _root.CreateFile(Path.Combine("docs", "notes.txt"));

        Assert.True(_authorizer.Authorize(file).Allowed);
    }

    [Fact]
    public void Allows_APathThatDoesNotExistYetButIsInsideTheRoot()
    {
        // files.stat must be able to answer not_found rather than path_not_allowed for a
        // legitimately-scoped path that simply is not there.
        var missing = _root.Combine("no", "such", "file.txt");

        Assert.True(_authorizer.Authorize(missing).Allowed);
    }

    [Fact]
    public void Allows_MixedCase()
    {
        // Windows filesystems are case-insensitive by default, so containment must be too.
        Assert.True(_authorizer.Authorize(_root.Path.ToUpperInvariant()).Allowed);
        Assert.True(_authorizer.Authorize(_root.Path.ToLowerInvariant()).Allowed);
    }

    [Fact]
    public void Allows_TraversalThatStaysInsideTheRoot()
    {
        _root.CreateDirectory("a");
        _root.CreateDirectory("b");

        // a\..\b normalises to b, which is inside. Traversal is not banned; escaping is.
        var path = Path.Combine(_root.Path, "a", "..", "b");

        var result = _authorizer.Authorize(path);

        Assert.True(result.Allowed);
        Assert.Equal(Path.Combine(_root.Path, "b"), result.RealPath);
    }

    [Fact]
    public void Allows_ForwardSlashSeparators()
    {
        _root.CreateDirectory("a");

        Assert.True(_authorizer.Authorize(_root.Path.Replace('\\', '/') + "/a").Allowed);
    }

    [Fact]
    public void Allows_APathInsideAnyOfSeveralRoots()
    {
        using var second = new TempDirectory("second");
        var authorizer = TestSecurity.AuthorizerFor(_root.Path, second.Path);

        Assert.True(authorizer.Authorize(_root.Path).Allowed);
        Assert.True(authorizer.Authorize(second.Path).Allowed);
    }

    // --- Denied: traversal and shape ----------------------------------------------------------

    [Fact]
    public void Denies_TraversalThatEscapesTheRoot()
    {
        var escape = Path.Combine(_root.Path, "..", "..", "..", "Windows");

        AssertDenied(_authorizer.Authorize(escape));
    }

    [Fact]
    public void Denies_DeeplyNestedTraversalBackOut()
    {
        _root.CreateDirectory(Path.Combine("a", "b"));
        var escape = Path.Combine(_root.Path, "a", "b", "..", "..", "..", "..");

        AssertDenied(_authorizer.Authorize(escape));
    }

    [Fact]
    public void Denies_ASiblingDirectoryWithTheRootAsAStringPrefix()
    {
        // The reason containment compares path segments and not string prefixes:
        // "<root>2" starts with "<root>" but is a different directory.
        var sibling = _root.Path + "2";
        Directory.CreateDirectory(sibling);

        try
        {
            AssertDenied(_authorizer.Authorize(sibling));
            AssertDenied(_authorizer.Authorize(Path.Combine(sibling, "secret.txt")));
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public void Denies_ASiblingFileWithTheRootAsAStringPrefix()
    {
        var siblingFile = _root.Path + ".txt";
        File.WriteAllText(siblingFile, "secret");

        try
        {
            AssertDenied(_authorizer.Authorize(siblingFile));
        }
        finally
        {
            File.Delete(siblingFile);
        }
    }

    [Theory]
    [InlineData(@"..\relative")]
    [InlineData(@"relative\path")]
    [InlineData("file.txt")]
    [InlineData(@"\rooted-but-not-qualified")]
    [InlineData("C:relative-to-drive")]
    public void Denies_PathsThatAreNotFullyQualified(string path) => AssertDenied(_authorizer.Authorize(path));

    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData("//server/share")]
    public void Denies_UncPaths(string path) => AssertDenied(_authorizer.Authorize(path));

    [Theory]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1")]
    [InlineData("//?/C:/Windows")]
    public void Denies_DeviceAndExtendedNamespacePaths(string path) => AssertDenied(_authorizer.Authorize(path));

    [Fact]
    public void Denies_AlternateDataStreamSpecifiers()
    {
        // "notes.txt:hidden" is a separate NTFS stream on the same file. A boundary that only
        // looks at directory names would never notice it.
        _root.CreateFile("notes.txt");

        AssertDenied(_authorizer.Authorize(Path.Combine(_root.Path, "notes.txt:hidden")));
        AssertDenied(_authorizer.Authorize(Path.Combine(_root.Path, "notes.txt:$DATA")));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("?")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("|")]
    [InlineData("\"")]
    public void Denies_WildcardsAndReservedCharacters(string bad) =>
        AssertDenied(_authorizer.Authorize(Path.Combine(_root.Path, "a" + bad + "b")));

    [Fact]
    public void Denies_EmbeddedNullAndControlCharacters()
    {
        AssertDenied(_authorizer.Authorize(_root.Path + "\0evil"));
        AssertDenied(_authorizer.Authorize(_root.Path + "\u0001"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Denies_EmptyPathsAsAParameterError(string? path)
    {
        var result = _authorizer.Authorize(path);

        Assert.False(result.Allowed);
        // An empty path is a caller mistake, not a boundary violation.
        Assert.Equal(ErrorCodes.InvalidParameters, result.ErrorCode);
    }

    [Fact]
    public void Denies_EverythingWhenNoRootsAreConfigured()
    {
        // Fail closed: no roots must mean "nothing", never "everything".
        var authorizer = TestSecurity.EmptyAuthorizer();

        AssertDenied(authorizer.Authorize(_root.Path));
        AssertDenied(authorizer.Authorize(@"C:\"));
    }

    [Fact]
    public void Denies_APathOutsideTheRootThatExists()
    {
        AssertDenied(_authorizer.Authorize(_outside.Path));
        AssertDenied(_authorizer.Authorize(@"C:\Windows\System32"));
    }

    // --- Denied: junctions --------------------------------------------------------------------

    [Fact]
    public void Denies_AJunctionInsideTheRootThatPointsOutside()
    {
        // The headline case for real-path resolution. Lexically "<root>\escape\file.txt" is
        // inside the root; in reality it is somewhere else entirely.
        var link = _root.Combine("escape");

        Assert.SkipUnless(TempDirectory.TryCreateJunction(link, _outside.Path), "junction creation unavailable");

        _outside.CreateFile("secret.txt", "top secret");

        AssertDenied(_authorizer.Authorize(link));
        AssertDenied(_authorizer.Authorize(Path.Combine(link, "secret.txt")));

        // Also for a file that does not exist under the junction: the ancestor walk must still
        // judge it by the junction's real target.
        AssertDenied(_authorizer.Authorize(Path.Combine(link, "does-not-exist.txt")));
    }

    [Fact]
    public void Allows_AJunctionInsideTheRootThatPointsInside()
    {
        var target = _root.CreateDirectory("real-target");
        var link = _root.Combine("inner-link");

        Assert.SkipUnless(TempDirectory.TryCreateJunction(link, target), "junction creation unavailable");

        var result = _authorizer.Authorize(Path.Combine(link, "file.txt"));

        Assert.True(result.Allowed);
        // Resolution rewrites the path to the junction's target, which is what the capability uses.
        Assert.Equal(Path.Combine(target, "file.txt"), result.RealPath);
    }

    [Fact]
    public void Denies_ARootReachedThroughAJunctionFromOutside()
    {
        // The inverse direction: a junction outside the boundary pointing at the root resolves
        // into the root, so it is correctly allowed rather than denied. Documented as a test so
        // the behaviour is deliberate rather than accidental.
        var link = _outside.Combine("into-root");

        Assert.SkipUnless(TempDirectory.TryCreateJunction(link, _root.Path), "junction creation unavailable");

        var result = _authorizer.Authorize(link);

        Assert.True(result.Allowed);
        Assert.Equal(_root.Path, result.RealPath);
    }

    // --- Denial messages ----------------------------------------------------------------------

    [Fact]
    public void DenialMessagesDoNotDiscloseTheResolvedLocation()
    {
        // The error text must not confirm what exists outside the boundary.
        var result = _authorizer.Authorize(@"C:\Windows\System32\config\SAM");

        Assert.False(result.Allowed);
        Assert.DoesNotContain("Windows", result.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SAM", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureAllowed_ThrowsTheMatchingProtocolError()
    {
        var exception = Assert.Throws<CapabilityException>(
            () => _authorizer.Authorize(@"C:\Windows").EnsureAllowed());

        Assert.Equal(ErrorCodes.PathNotAllowed, exception.Code);
    }

    private static void AssertDenied(PathAuthorizationResult result)
    {
        Assert.False(result.Allowed);
        Assert.Null(result.RealPath);
        Assert.Equal(ErrorCodes.PathNotAllowed, result.ErrorCode);
    }
}

public sealed class PathContainmentTests
{
    [Theory]
    [InlineData(@"C:\Root", @"C:\Root")]
    [InlineData(@"C:\Root\a", @"C:\Root")]
    [InlineData(@"C:\Root\a\b\c", @"C:\Root")]
    [InlineData(@"C:\ROOT\a", @"C:\root")]
    [InlineData(@"C:\Root\a\", @"C:\Root\")]
    public void Within(string candidate, string root) => Assert.True(PathContainment.IsWithin(candidate, root));

    [Theory]
    [InlineData(@"C:\Root2", @"C:\Root")]
    [InlineData(@"C:\Root2\a", @"C:\Root")]
    [InlineData(@"C:\Root.txt", @"C:\Root")]
    [InlineData(@"C:\Rootless", @"C:\Root")]
    [InlineData(@"C:\", @"C:\Root")]
    [InlineData(@"D:\Root", @"C:\Root")]
    [InlineData(@"C:\Other\Root", @"C:\Root")]
    [InlineData("", @"C:\Root")]
    public void NotWithin(string candidate, string root) => Assert.False(PathContainment.IsWithin(candidate, root));
}

public sealed class RealPathResolverTests : IDisposable
{
    private readonly TempDirectory _temp = new("resolver");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ResolvesAnExistingDirectory()
    {
        var resolver = new Win32RealPathResolver();

        Assert.Equal(_temp.Path, resolver.Resolve(_temp.Path));
    }

    [Fact]
    public void ResolvesAnExistingFile()
    {
        var file = _temp.CreateFile("a.txt");
        var resolver = new Win32RealPathResolver();

        Assert.Equal(file, resolver.Resolve(file));
    }

    [Fact]
    public void ResolvesAMissingPathThroughItsDeepestExistingAncestor()
    {
        var resolver = new Win32RealPathResolver();
        var missing = Path.Combine(_temp.Path, "nope", "still-nope.txt");

        Assert.Equal(missing, resolver.Resolve(missing));
    }

    [Fact]
    public void ResolvesEightDotThreeShortNames()
    {
        // "C:\PROGRA~1" and "C:\Program Files" are the same directory. A lexical comparison
        // cannot tell, which is precisely why resolution asks the OS.
        var resolver = new Win32RealPathResolver();

        var resolved = resolver.Resolve(@"C:\PROGRA~1");

        Assert.SkipWhen(resolved is null, "no 8.3 alias on this volume");
        Assert.Equal(@"C:\Program Files", resolved, ignoreCase: true);
    }

    [Fact]
    public void ResolvesThroughAJunction()
    {
        using var target = new TempDirectory("junction-target");
        var link = _temp.Combine("link");

        Assert.SkipUnless(TempDirectory.TryCreateJunction(link, target.Path), "junction creation unavailable");

        var resolver = new Win32RealPathResolver();

        Assert.Equal(target.Path, resolver.Resolve(link));
        Assert.Equal(Path.Combine(target.Path, "x.txt"), resolver.Resolve(Path.Combine(link, "x.txt")));
    }

    [Fact]
    public void ReturnsNullForRelativeInput()
    {
        Assert.Null(new Win32RealPathResolver().Resolve(@"..\relative"));
        Assert.Null(new PortableRealPathResolver().Resolve(@"..\relative"));
    }

    [Fact]
    public void PortableResolverFollowsJunctionsToo()
    {
        using var target = new TempDirectory("portable-target");
        var link = _temp.Combine("plink");

        Assert.SkipUnless(TempDirectory.TryCreateJunction(link, target.Path), "junction creation unavailable");

        Assert.Equal(target.Path, new PortableRealPathResolver().Resolve(link));
    }

    [Fact]
    public void StripsTheExtendedLengthPrefix()
    {
        Assert.Equal(@"C:\Windows", Win32RealPathResolver.StripExtendedPrefix(@"\\?\C:\Windows"));
        Assert.Equal(@"\\server\share", Win32RealPathResolver.StripExtendedPrefix(@"\\?\UNC\server\share"));
        Assert.Equal(@"C:\Windows", Win32RealPathResolver.StripExtendedPrefix(@"C:\Windows"));
    }
}

public sealed class AllowedRootsTests : IDisposable
{
    private readonly TempDirectory _a = new("roots-a");
    private readonly TempDirectory _b = new("roots-b");

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    [Fact]
    public void ResolvesConfiguredAbsoluteRoots()
    {
        var roots = Create(_a.Path, _b.Path);

        Assert.Equal(2, roots.Roots.Count);
        Assert.Contains(_a.Path, roots.Roots);
    }

    [Fact]
    public void DropsRootsThatDoNotExist()
    {
        var roots = Create(_a.Path, @"C:\this\does\not\exist\anywhere");

        Assert.Single(roots.Roots);
        Assert.Equal(_a.Path, roots.Roots[0]);
    }

    [Fact]
    public void DropsRelativeAndUnknownTokens()
    {
        var roots = Create("relative\\path", "{NoSuchFolder}", "  ");

        Assert.True(roots.IsEmpty);
    }

    [Fact]
    public void CollapsesARootThatIsNestedInsideAnother()
    {
        var nested = _a.CreateDirectory("nested");

        // Keeping both would make the boundary confusing without widening it. The outer wins.
        Assert.Single(Create(_a.Path, nested).Roots);
        Assert.Single(Create(nested, _a.Path).Roots);
        Assert.Equal(_a.Path, Create(nested, _a.Path).Roots[0]);
    }

    [Fact]
    public void ResolvesWellKnownFolderTokens()
    {
        var roots = Create("{UserProfile}");

        Assert.Single(roots.Roots);
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            roots.Roots[0],
            ignoreCase: true);
    }

    [Fact]
    public void ResolvesTheDownloadsKnownFolder()
    {
        // Downloads has no Environment.SpecialFolder, so this exercises the Windows override.
        var resolved = new WindowsKnownFolderResolver().Resolve("downloads");

        Assert.False(string.IsNullOrWhiteSpace(resolved));
        Assert.True(Path.IsPathFullyQualified(resolved!));
    }

    private static AllowedRoots Create(params string[] configured) =>
        AllowedRoots.Create(
            configured,
            new Win32RealPathResolver(),
            new WindowsKnownFolderResolver(),
            TestLog.For<AllowedRoots>());
}
