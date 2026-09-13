using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Files;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.Storage;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using Xunit;

namespace PcCore.Tests.Capabilities;

/// <summary>
/// Files as the phone uses them: browsing with a way up, ranged downloads that notice a file changing
/// under them, and uploads that land whole or not at all - against real directories, real ACLs and
/// real file locks.
/// </summary>
public sealed class FileTransferTests : IDisposable
{
    private static readonly CallerDescriptor Phone = new("tailscale-remote", "c1", ActorType.Device, "phone-1");
    private static readonly CallerDescriptor OtherPhone = new("tailscale-remote", "c2", ActorType.Device, "phone-2");

    private readonly TempDirectory _root = new("transfer");
    private readonly TempDirectory _outside = new("transfer-outside");
    private readonly TempStateDirectory _state = new("transfer-state");
    private readonly AdvanceableTimeProvider _clock = new();
    private readonly PathAuthorizer _authorizer;
    private readonly FileProtections _protections;
    private readonly FileUploadService _uploads;

    public FileTransferTests()
    {
        _authorizer = TestSecurity.AuthorizerFor(_root.Path);
        _protections = new FileProtections([_root.CreateDirectory("NexusState")]);
        _uploads = NewUploadService();
    }

    public void Dispose()
    {
        _uploads.Dispose();
        _root.Dispose();
        _outside.Dispose();
        _state.Dispose();
    }

    private FileUploadService NewUploadService() => new(
        _authorizer, new FilesOptions { UploadIdleMinutes = 10 }, TestLog.For<FileUploadService>(), _protections, _state.Directory, _clock);

    private static CapabilityContext As(CallerDescriptor caller) => new("test", caller);

    private async Task<T> Invoke<T>(ICapabilityAction action, object parameters, CallerDescriptor? caller = null) =>
        (T)(await action.InvokeAsync(parameters, As(caller ?? Phone), CancellationToken.None))!;

    private Task<FileChunk> Read(string path, long offset, int maxBytes, long? expectedSize = null, DateTimeOffset? expectedModified = null) =>
        Invoke<FileChunk>(new ReadFileAction(_authorizer, _protections), new ReadFileParameters
        {
            Path = path,
            Offset = offset,
            MaxBytes = maxBytes,
            ExpectedSizeBytes = expectedSize,
            ExpectedModifiedUtc = expectedModified,
        });

    // --- Browsing -------------------------------------------------------------------------------

    [Fact]
    public async Task AListingNamesItsParentAndARootHasNone()
    {
        var nested = _root.CreateDirectory(Path.Combine("a", "b"));

        var inner = await Invoke<ListFilesResult>(new ListFilesAction(_authorizer), new ListFilesParameters { Path = nested });
        Assert.Equal(Path.Combine(_root.Path, "a"), inner.Parent);

        var top = await Invoke<ListFilesResult>(new ListFilesAction(_authorizer), new ListFilesParameters { Path = _root.Path });
        Assert.Null(top.Parent);
    }

    [Fact]
    public async Task AFolderWindowsRefusesToListIsAccessDeniedNotEmpty()
    {
        var locked = _root.CreateDirectory("locked");
        _root.CreateFile(Path.Combine("locked", "inside.txt"));

        using var _ = Deny(locked, FileSystemRights.ListDirectory);

        var refused = await Assert.ThrowsAsync<CapabilityException>(() =>
            Invoke<ListFilesResult>(new ListFilesAction(_authorizer), new ListFilesParameters { Path = locked }));

        Assert.Equal(ErrorCodes.AccessDenied, refused.Code);
    }

    [Fact]
    public async Task NexussOwnStateIsRefusedEvenInsideTheBoundary()
    {
        var state = _protections.Paths.Single();
        var secret = Path.Combine(state, "primary-phone.json");
        File.WriteAllText(secret, "{}");

        foreach (var attempt in new Func<Task>[]
                 {
                     () => Invoke<ListFilesResult>(new ListFilesAction(_authorizer, _protections), new ListFilesParameters { Path = state }),
                     () => Invoke<StatResult>(new StatFileAction(_authorizer, _protections), new StatParameters { Path = secret }),
                     () => Read(secret, 0, 10),
                     () => Task.FromResult(_uploads.Begin(new BeginUploadParameters { Directory = state, Name = "x.json", SizeBytes = 1 }, Phone)),
                 })
        {
            var refused = await Assert.ThrowsAsync<CapabilityException>(attempt);
            Assert.Equal(ErrorCodes.PathNotAllowed, refused.Code);
        }
    }

    [Fact]
    public async Task RootsDescribeTheBoundaryAndOnlyPlacesInsideIt()
    {
        var inside = _root.CreateDirectory("Profile");
        var roots = await Invoke<FileRootsResult>(
            new ListFileRootsAction(_authorizer, new FixedKnownFolders(new() { ["userprofile"] = inside, ["desktop"] = _outside.Path })),
            new NoParameters());

        var root = Assert.Single(roots.Roots);
        Assert.Equal(_root.Path, root.Path);
        Assert.Equal("folder", root.Kind);

        // The desktop is outside this boundary, so it is not offered as a place.
        var place = Assert.Single(roots.Places);
        Assert.Equal(("Home", inside), (place.Name, place.Path));
    }

    [Fact]
    public void TheDrivesTokenBecomesEveryLocalDrive()
    {
        var roots = AllowedRoots.Create(
            [AllowedRoots.DrivesToken],
            new PcCore.Windows.Win32RealPathResolver(),
            new PortableKnownFolderResolver(),
            TestLog.Null);

        var system = Path.GetPathRoot(Environment.SystemDirectory)!;

        Assert.Contains(roots.Roots, r => string.Equals(r, system, StringComparison.OrdinalIgnoreCase));
        Assert.All(roots.Roots, r => Assert.Equal(Path.GetPathRoot(r), r));
    }

    // --- Downloads ------------------------------------------------------------------------------

    [Fact]
    public async Task RangesReassembleTheFileExactlyAndTheHashAgrees()
    {
        var bytes = RandomNumberGenerator.GetBytes(2_500_123);
        var path = _root.Combine("big.bin");
        File.WriteAllBytes(path, bytes);

        using var assembled = new MemoryStream();
        long offset = 0;
        FileChunk chunk;

        do
        {
            chunk = await Read(path, offset, 1024 * 1024);
            Assert.Equal(offset, chunk.Offset);
            Assert.Equal(bytes.Length, chunk.SizeBytes);

            var data = Convert.FromBase64String(chunk.Data!);
            assembled.Write(data);
            offset += data.Length;
        }
        while (!chunk.EndOfFile);

        Assert.Equal(3, (int)Math.Ceiling(bytes.Length / (double)(1024 * 1024)));
        Assert.Equal(bytes, assembled.ToArray());

        var hash = await Invoke<FileHashResult>(new HashFileAction(_authorizer), new HashFileParameters { Path = path });
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), hash.Sha256);
    }

    [Fact]
    public async Task AnEmptyFileIsOneEmptyFinalRange()
    {
        var path = _root.CreateFile("empty.txt", string.Empty);

        var chunk = await Read(path, 0, 1024);

        Assert.True(chunk.EndOfFile);
        Assert.Equal(0, chunk.ChunkBytes);
    }

    [Fact]
    public async Task AFileThatChangesBetweenRangesIsRefusedRatherThanStitched()
    {
        var path = _root.CreateFile("log.txt", new string('a', 1000));
        var first = await Read(path, 0, 100);

        File.AppendAllText(path, "more");

        var changed = await Assert.ThrowsAsync<CapabilityException>(() =>
            Read(path, 100, 100, first.SizeBytes, first.ModifiedUtc));

        Assert.Equal(ErrorCodes.FileChanged, changed.Code);
    }

    [Fact]
    public async Task ReadingAMissingFileAFolderOrAPathOutsideSaysWhichItIs()
    {
        Assert.Equal(ErrorCodes.NotFound, (await Assert.ThrowsAsync<CapabilityException>(() => Read(_root.Combine("gone.txt"), 0, 10))).Code);
        Assert.Equal(ErrorCodes.InvalidParameters, (await Assert.ThrowsAsync<CapabilityException>(() => Read(_root.Path, 0, 10))).Code);
        Assert.Equal(ErrorCodes.PathNotAllowed, (await Assert.ThrowsAsync<CapabilityException>(() => Read(_outside.CreateFile("s.txt"), 0, 10))).Code);
    }

    [Fact]
    public async Task AFileAnotherProgramHoldsExclusivelyIsInUse()
    {
        var path = _root.CreateFile("held.txt", "held");

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var refused = await Assert.ThrowsAsync<CapabilityException>(() => Read(path, 0, 10));
            Assert.Equal(ErrorCodes.FileInUse, refused.Code);
        }
    }

    [Fact]
    public async Task AFileThisAccountMayNotReadIsAccessDenied()
    {
        var path = _root.CreateFile("private.txt", "private");

        using var _ = Deny(path, FileSystemRights.ReadData);

        var refused = await Assert.ThrowsAsync<CapabilityException>(() => Read(path, 0, 10));
        Assert.Equal(ErrorCodes.AccessDenied, refused.Code);
    }

    // --- Uploads --------------------------------------------------------------------------------

    [Fact]
    public async Task AnUploadArrivesInChunksAndAppearsOnlyWhenFinished()
    {
        var bytes = RandomNumberGenerator.GetBytes(FileUploadService.MaxChunkBytes * 2 + 17);
        var target = _root.Combine("photo.jpg");

        var state = _uploads.Begin(Begin("photo.jpg", bytes.Length), Phone);
        Assert.Equal(target, state.Path);
        Assert.Equal(0, state.ReceivedBytes);

        await SendAll(state.UploadId, bytes, from: 0);

        // Complete, but not yet checked: nothing under the real name.
        Assert.False(File.Exists(target));

        var done = await _uploads.FinishAsync(new FinishUploadParameters { UploadId = state.UploadId, Sha256 = Sha(bytes) }, Phone, TestContext.Current.CancellationToken);

        Assert.Equal(bytes, File.ReadAllBytes(target));
        Assert.Equal(bytes.Length, done.SizeBytes);
        Assert.Empty(PartFiles());
        Assert.False(File.GetAttributes(target).HasFlag(FileAttributes.Hidden));
        Assert.Equal(0, _uploads.ActiveCount);
    }

    [Fact]
    public async Task ADigestThatDoesNotMatchKeepsNothing()
    {
        var bytes = RandomNumberGenerator.GetBytes(4096);
        var state = _uploads.Begin(Begin("doc.pdf", bytes.Length), Phone);
        await SendAll(state.UploadId, bytes, from: 0);

        var refused = await Assert.ThrowsAsync<CapabilityException>(async () =>
            await _uploads.FinishAsync(new FinishUploadParameters { UploadId = state.UploadId, Sha256 = new string('0', 64) }, Phone, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.ChecksumMismatch, refused.Code);
        Assert.False(File.Exists(_root.Combine("doc.pdf")));
        Assert.Empty(PartFiles());
    }

    [Fact]
    public async Task ChunksMustArriveInOrderAndWithinTheSize()
    {
        var state = _uploads.Begin(Begin("a.bin", 10), Phone);

        var gap = await Assert.ThrowsAsync<CapabilityException>(async () => await Chunk(state.UploadId, 5, new byte[5]));
        Assert.Equal(ErrorCodes.InvalidParameters, gap.Code);

        await Chunk(state.UploadId, 0, new byte[8]);

        var overflow = await Assert.ThrowsAsync<CapabilityException>(async () => await Chunk(state.UploadId, 8, new byte[5]));
        Assert.Equal(ErrorCodes.InvalidParameters, overflow.Code);

        var early = await Assert.ThrowsAsync<CapabilityException>(async () =>
            await _uploads.FinishAsync(new FinishUploadParameters { UploadId = state.UploadId }, Phone, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.InvalidParameters, early.Code);
    }

    [Fact]
    public async Task BeginningTheSameUploadAgainResumesFromWhatArrived()
    {
        var bytes = RandomNumberGenerator.GetBytes(3000);
        var first = _uploads.Begin(Begin("video.mp4", bytes.Length), Phone);
        await Chunk(first.UploadId, 0, bytes[..1000]);

        // The connection dropped; the phone comes back and asks again.
        var resumed = _uploads.Begin(Begin("video.mp4", bytes.Length), Phone);

        Assert.True(resumed.Resumed);
        Assert.Equal(first.UploadId, resumed.UploadId);
        Assert.Equal(1000, resumed.ReceivedBytes);

        await SendAll(resumed.UploadId, bytes, from: 1000);
        await _uploads.FinishAsync(new FinishUploadParameters { UploadId = resumed.UploadId, Sha256 = Sha(bytes) }, Phone, TestContext.Current.CancellationToken);

        Assert.Equal(bytes, File.ReadAllBytes(_root.Combine("video.mp4")));
    }

    [Fact]
    public async Task CancellingDeletesThePartialFile()
    {
        var state = _uploads.Begin(Begin("big.iso", 1_000_000), Phone);
        await Chunk(state.UploadId, 0, new byte[1000]);
        Assert.Single(PartFiles());

        Assert.True(_uploads.Cancel(new CancelUploadParameters { UploadId = state.UploadId }, Phone).Cancelled);

        Assert.Empty(PartFiles());
        Assert.Equal(ErrorCodes.NotFound, (await Assert.ThrowsAsync<CapabilityException>(async () => await Chunk(state.UploadId, 1000, new byte[1]))).Code);
    }

    [Fact]
    public async Task AnotherCallerCannotTouchSomeoneElsesUpload()
    {
        var state = _uploads.Begin(Begin("mine.txt", 4), Phone);

        var refused = await Assert.ThrowsAsync<CapabilityException>(async () =>
            await _uploads.WriteAsync(new UploadChunkParameters { UploadId = state.UploadId, Offset = 0, Data = Convert.ToBase64String(new byte[4]) }, OtherPhone, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.NotFound, refused.Code);
        Assert.False(_uploads.Cancel(new CancelUploadParameters { UploadId = state.UploadId }, OtherPhone).Cancelled);
        Assert.Single(PartFiles());
    }

    [Fact]
    public async Task AnAbandonedUploadIsDeletedAfterTheIdleInterval()
    {
        var state = _uploads.Begin(Begin("stale.bin", 100), Phone);
        await Chunk(state.UploadId, 0, new byte[10]);

        _clock.Advance(TimeSpan.FromMinutes(9));
        _uploads.Sweep();
        Assert.Single(PartFiles());

        _clock.Advance(TimeSpan.FromMinutes(2));
        _uploads.Sweep();

        Assert.Empty(PartFiles());
        Assert.Equal(0, _uploads.ActiveCount);
    }

    [Fact]
    public async Task StoppingDeletesPartialUploadsAndACrashIsCleanedUpAtNextStart()
    {
        var stopping = NewUploadService();
        stopping.Begin(Begin("one.bin", 100), Phone);
        stopping.Dispose();
        Assert.Empty(PartFiles());

        // A crash: nothing ran, so the journal still names a partial file - and, whatever it says, it
        // is never a reason to delete a file that is not one.
        var leftover = _root.CreateFile("two.bin.0badf00d" + FileNames.PartSuffix, "partial");
        var innocent = _root.CreateFile("keep.txt", "not an upload");

        new AtomicJsonFileStore<FileUploadService.UploadJournal>(_state.Directory, "uploads.json", TestLog.Null)
            .Save(new FileUploadService.UploadJournal { PartFiles = [leftover, innocent] });

        using var restarted = NewUploadService();

        Assert.Empty(PartFiles());
        Assert.True(File.Exists(innocent));

        await Task.CompletedTask;
    }

    [Fact]
    public async Task AnExistingFileIsReplacedOnlyWhenAskedTo()
    {
        var target = _root.CreateFile("notes.txt", "old");

        var refused = Assert.Throws<CapabilityException>(() => _uploads.Begin(Begin("notes.txt", 3), Phone));
        Assert.Equal(ErrorCodes.AlreadyExists, refused.Code);
        Assert.Empty(PartFiles());

        var bytes = "new"u8.ToArray();
        var state = _uploads.Begin(Begin("notes.txt", 3) with { Overwrite = true }, Phone);
        await SendAll(state.UploadId, bytes, from: 0);
        await _uploads.FinishAsync(new FinishUploadParameters { UploadId = state.UploadId, Sha256 = Sha(bytes) }, Phone, TestContext.Current.CancellationToken);

        Assert.Equal("new", File.ReadAllText(target));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a\\b.txt")]
    [InlineData("a/b.txt")]
    [InlineData("C:evil.txt")]
    [InlineData("notes.txt:stream")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("sneaky.nexus-upload")]
    public void ANameThatIsNotExactlyOneFileNameIsRefused(string name)
    {
        var refused = Assert.Throws<CapabilityException>(() => _uploads.Begin(Begin(name, 1), Phone));

        Assert.Equal(ErrorCodes.InvalidParameters, refused.Code);
        Assert.DoesNotContain(Directory.GetFileSystemEntries(_root.Path), e => !e.EndsWith("NexusState", StringComparison.Ordinal));
    }

    [Fact]
    public void UploadingOutsideTheBoundaryOrIntoAFolderWindowsRefusesSaysSo()
    {
        var outside = Assert.Throws<CapabilityException>(() =>
            _uploads.Begin(new BeginUploadParameters { Directory = _outside.Path, Name = "x.txt", SizeBytes = 1 }, Phone));
        Assert.Equal(ErrorCodes.PathNotAllowed, outside.Code);

        var readOnly = _root.CreateDirectory("readonly");
        using var _ = Deny(readOnly, FileSystemRights.CreateFiles);

        var denied = Assert.Throws<CapabilityException>(() =>
            _uploads.Begin(new BeginUploadParameters { Directory = readOnly, Name = "x.txt", SizeBytes = 1 }, Phone));
        Assert.Equal(ErrorCodes.AccessDenied, denied.Code);
    }

    // --- Helpers --------------------------------------------------------------------------------

    private BeginUploadParameters Begin(string name, long size) => new() { Directory = _root.Path, Name = name, SizeBytes = size };

    private ValueTask<UploadChunkResult> Chunk(string id, long offset, byte[] data) =>
        _uploads.WriteAsync(new UploadChunkParameters { UploadId = id, Offset = offset, Data = Convert.ToBase64String(data) }, Phone, TestContext.Current.CancellationToken);

    private async Task SendAll(string id, byte[] bytes, long from)
    {
        for (var offset = from; offset < bytes.Length; offset += FileUploadService.MaxChunkBytes)
        {
            var slice = bytes[(int)offset..(int)Math.Min(bytes.Length, offset + FileUploadService.MaxChunkBytes)];
            var result = await Chunk(id, offset, slice);
            Assert.Equal(offset + slice.Length, result.ReceivedBytes);
        }
    }

    private string[] PartFiles() => Directory.GetFiles(_root.Path, "*" + FileNames.PartSuffix, SearchOption.AllDirectories);

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Adds a deny entry for this account, and takes it away again on dispose so the temp directory can be deleted.</summary>
    private static IDisposable Deny(string path, FileSystemRights rights)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, rights, AccessControlType.Deny);

        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(rule);
            info.SetAccessControl(acl);

            return new Restore(() =>
            {
                var restored = info.GetAccessControl();
                restored.RemoveAccessRule(rule);
                info.SetAccessControl(restored);
            });
        }

        var file = new FileInfo(path);
        var fileAcl = file.GetAccessControl();
        fileAcl.AddAccessRule(rule);
        file.SetAccessControl(fileAcl);

        return new Restore(() =>
        {
            var restored = file.GetAccessControl();
            restored.RemoveAccessRule(rule);
            file.SetAccessControl(restored);
        });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    private sealed class FixedKnownFolders(Dictionary<string, string> folders) : IKnownFolderResolver
    {
        public string? Resolve(string token) => folders.GetValueOrDefault(token.ToLowerInvariant());
    }
}
