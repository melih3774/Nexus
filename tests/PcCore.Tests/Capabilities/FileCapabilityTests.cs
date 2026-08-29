using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Files;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using Xunit;

namespace PcCore.Tests.Capabilities;

public sealed class FileCapabilityTests : IDisposable
{
    private readonly TempDirectory _root = new("files");
    private readonly TempDirectory _outside = new("files-outside");
    private readonly ListFilesAction _list;
    private readonly StatFileAction _stat;

    public FileCapabilityTests()
    {
        var authorizer = TestSecurity.AuthorizerFor(_root.Path);
        _list = new ListFilesAction(authorizer);
        _stat = new StatFileAction(authorizer);
    }

    public void Dispose()
    {
        _root.Dispose();
        _outside.Dispose();
    }

    private static readonly CapabilityContext Context = new("test", CallerDescriptor.Internal);

    private ValueTask<object?> Invoke(ICapabilityAction action, object parameters) =>
        action.InvokeAsync(parameters, Context, CancellationToken.None);

    private async Task<ListFilesResult> List(string? path, bool includeHidden = false) =>
        (ListFilesResult)(await Invoke(_list, new ListFilesParameters { Path = path, IncludeHidden = includeHidden }))!;

    private async Task<StatResult> Stat(string? path) =>
        (StatResult)(await Invoke(_stat, new StatParameters { Path = path }))!;

    // --- files.list ---------------------------------------------------------------------------

    [Fact]
    public async Task List_ReturnsFilesAndDirectories()
    {
        _root.CreateFile("alpha.txt", "hello");
        _root.CreateFile("beta.bin", "0123456789");
        _root.CreateDirectory("sub");

        var result = await List(_root.Path);

        Assert.Equal(3, result.EntryCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(_root.Path, result.Path);

        // Directories sort first, then names case-insensitively.
        Assert.Equal(["sub", "alpha.txt", "beta.bin"], result.Entries.Select(e => e.Name));

        var alpha = result.Entries.Single(e => e.Name == "alpha.txt");
        Assert.Equal(FileEntryType.File, alpha.Type);
        Assert.Equal(5, alpha.SizeBytes);
        Assert.NotNull(alpha.ModifiedUtc);
        Assert.NotNull(alpha.CreatedUtc);
        Assert.False(alpha.IsLink);

        var sub = result.Entries.Single(e => e.Name == "sub");
        Assert.Equal(FileEntryType.Directory, sub.Type);
        // Directory sizes are meaningless without recursion, so they are omitted rather than faked.
        Assert.Null(sub.SizeBytes);
    }

    [Fact]
    public async Task List_ReturnsAnEmptyListForAnEmptyDirectory()
    {
        var empty = _root.CreateDirectory("empty");

        var result = await List(empty);

        Assert.Equal(0, result.EntryCount);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task List_HidesHiddenEntriesByDefault()
    {
        var hidden = _root.CreateFile("secret.txt");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        _root.CreateFile("visible.txt");

        var withoutHidden = await List(_root.Path);
        Assert.Equal(["visible.txt"], withoutHidden.Entries.Select(e => e.Name));

        var withHidden = await List(_root.Path, includeHidden: true);
        Assert.Equal(2, withHidden.EntryCount);
        Assert.True(withHidden.Entries.Single(e => e.Name == "secret.txt").IsHidden);
    }

    [Fact]
    public async Task List_ReportsReadOnlyFiles()
    {
        var file = _root.CreateFile("locked.txt");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        try
        {
            var result = await List(_root.Path);
            Assert.True(result.Entries.Single().IsReadOnly);
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task List_MarksJunctionsAsLinksAndDoesNotFollowThem()
    {
        var link = _root.Combine("link");
        Assert.SkipUnless(TempDirectory.TryCreateJunction(link, _outside.Path), "junction creation unavailable");

        _outside.CreateFile("outside-secret.txt");

        var result = await List(_root.Path);

        var entry = Assert.Single(result.Entries);
        Assert.Equal("link", entry.Name);
        Assert.True(entry.IsLink);

        // The listing must not walk into the junction, and must never disclose its target.
        Assert.DoesNotContain(result.Entries, e => e.Name == "outside-secret.txt");
    }

    [Fact]
    public async Task List_RejectsAPathOutsideTheRoots()
    {
        var exception = await Assert.ThrowsAsync<CapabilityException>(() => List(_outside.Path));

        Assert.Equal(ErrorCodes.PathNotAllowed, exception.Code);
    }

    [Fact]
    public async Task List_RejectsTraversalOutOfTheRoots()
    {
        var exception = await Assert.ThrowsAsync<CapabilityException>(
            () => List(Path.Combine(_root.Path, "..", "..", "Windows")));

        Assert.Equal(ErrorCodes.PathNotAllowed, exception.Code);
    }

    [Fact]
    public async Task List_ReturnsNotFoundForAMissingDirectory()
    {
        var exception = await Assert.ThrowsAsync<CapabilityException>(
            () => List(_root.Combine("no-such-directory")));

        Assert.Equal(ErrorCodes.NotFound, exception.Code);
    }

    [Fact]
    public async Task List_ReturnsInvalidParametersWhenGivenAFile()
    {
        var file = _root.CreateFile("a-file.txt");

        var exception = await Assert.ThrowsAsync<CapabilityException>(() => List(file));

        Assert.Equal(ErrorCodes.InvalidParameters, exception.Code);
    }

    [Fact]
    public async Task List_ReturnsInvalidParametersForAnEmptyPath()
    {
        var exception = await Assert.ThrowsAsync<CapabilityException>(() => List(null));

        Assert.Equal(ErrorCodes.InvalidParameters, exception.Code);
    }

    // --- files.stat ---------------------------------------------------------------------------

    [Fact]
    public async Task Stat_DescribesAFile()
    {
        var file = _root.CreateFile("notes.txt", "0123456789");

        var result = await Stat(file);

        Assert.Equal(file, result.Path);
        Assert.Equal("notes.txt", result.Entry.Name);
        Assert.Equal(FileEntryType.File, result.Entry.Type);
        Assert.Equal(10, result.Entry.SizeBytes);
        Assert.NotNull(result.Entry.ModifiedUtc);
        Assert.NotNull(result.AccessedUtc);
    }

    [Fact]
    public async Task Stat_DescribesADirectory()
    {
        var directory = _root.CreateDirectory("folder");

        var result = await Stat(directory);

        Assert.Equal("folder", result.Entry.Name);
        Assert.Equal(FileEntryType.Directory, result.Entry.Type);
        Assert.Null(result.Entry.SizeBytes);
    }

    [Fact]
    public async Task Stat_DescribesTheRootItself()
    {
        var result = await Stat(_root.Path);

        Assert.Equal(FileEntryType.Directory, result.Entry.Type);
    }

    [Fact]
    public async Task Stat_ReturnsNotFoundForAMissingPath()
    {
        var exception = await Assert.ThrowsAsync<CapabilityException>(
            () => Stat(_root.Combine("ghost.txt")));

        Assert.Equal(ErrorCodes.NotFound, exception.Code);
    }

    [Fact]
    public async Task Stat_RejectsAPathOutsideTheRoots()
    {
        var outsideFile = _outside.CreateFile("secret.txt");

        var exception = await Assert.ThrowsAsync<CapabilityException>(() => Stat(outsideFile));

        // path_not_allowed, not not_found: the boundary must not confirm what exists outside it.
        Assert.Equal(ErrorCodes.PathNotAllowed, exception.Code);
    }

    [Fact]
    public async Task Stat_RejectsAnAlternateDataStream()
    {
        var file = _root.CreateFile("host.txt", "visible");

        var exception = await Assert.ThrowsAsync<CapabilityException>(() => Stat(file + ":hidden"));

        Assert.Equal(ErrorCodes.PathNotAllowed, exception.Code);
    }

    // --- Contract -----------------------------------------------------------------------------

    [Fact]
    public void ActionsDeclareTheirNamesAndParameterTypes()
    {
        Assert.Equal("files.list", _list.Name);
        Assert.Equal(typeof(ListFilesParameters), ((ICapabilityAction)_list).ParameterType);

        Assert.Equal("files.stat", _stat.Name);
        Assert.Equal(typeof(StatParameters), ((ICapabilityAction)_stat).ParameterType);
    }

    [Fact]
    public async Task ActionsRejectMistypedParameters()
    {
        var exception = await Assert.ThrowsAsync<CapabilityException>(
            async () => await Invoke(_list, new StatParameters { Path = _root.Path }));

        Assert.Equal(ErrorCodes.InvalidParameters, exception.Code);
    }
}
