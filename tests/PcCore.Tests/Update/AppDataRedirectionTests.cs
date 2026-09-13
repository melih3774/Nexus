using PcCore.Tests.TestSupport;
using PcCore.Update;
using PcCore.Update.Windows;
using Xunit;

namespace PcCore.Tests.Update;

/// <summary>
/// Telling apart the Nexus folder every PC Core shares from a packaged app's private copy of it.
/// <para>
/// The reported failure: Nexus was first run from a terminal inside a packaged app, before
/// <c>%LOCALAPPDATA%\Nexus</c> existed, so Windows created it as that app's private copy. Everything
/// later started from the app - the publish script, the updater, the PC Core the updater restarted -
/// kept using the copy; Nexus started from the Start Menu used the real folder. An update was published,
/// installed and recorded in the copy, and closing Nexus and opening it from the Start Menu brought back
/// the pairing, release store and update state from before it.
/// </para>
/// <para>
/// Windows shows a process inside the app the copy <em>at the real folder's path</em>. A junction from
/// that path to the copy is the same thing on disk, which is how these tests stand in for it.
/// </para>
/// </summary>
public sealed class AppDataRedirectionTests : IDisposable
{
    private const string Package = "Claude_test0123456789";

    private readonly TempDirectory _root = new("appdata");

    public void Dispose() => _root.Dispose();

    /// <summary>%LOCALAPPDATA% as the Start Menu, sign-in and Explorer see it.</summary>
    private string Real => _root.Combine("real");

    /// <summary>The packaged app's private copy of a folder.</summary>
    private string CopyOf(string folder) => Path.Combine(Real, "Packages", Package, "LocalCache", "Local", folder);

    [Fact]
    public void WithNoPackagedAppHoldingACopyThereIsNothingToReport()
    {
        _root.CreateDirectory(Path.Combine("real", UpdatePaths.StateFolder));
        _root.CreateDirectory(Path.Combine("real", "Packages", "Other_app", "LocalCache", "Local", "SomethingElse"));

        Assert.Null(AppDataRedirection.Find(Real, UpdatePaths.StateFolder, DirectoryIdentity.Of));
    }

    [Fact]
    public void PcCoreStartedNormallyIsToldThatAPackagedAppHoldsASecondCopy()
    {
        _root.CreateDirectory(Path.Combine("real", UpdatePaths.StateFolder));
        Directory.CreateDirectory(CopyOf(UpdatePaths.StateFolder));

        var found = AppDataRedirection.Find(Real, UpdatePaths.StateFolder, DirectoryIdentity.Of);

        Assert.NotNull(found);
        Assert.False(found.UsedByThisProcess);
        Assert.Equal(Package, found.Package);
        Assert.Equal(CopyOf(UpdatePaths.StateFolder), found.PrivateCopy);
        Assert.Contains(found.PrivateCopy, found.Describe(), StringComparison.Ordinal);

        // Started normally, nothing is refused: this is the folder every PC Core should be using.
        Assert.Null(AppDataRedirection.FirstUsedByThisProcess(Real, [UpdatePaths.StateFolder, UpdatePaths.InstallFolder], DirectoryIdentity.Of));
    }

    [Fact]
    public void AProcessStartedInsideThePackagedAppIsFoundToBeUsingTheCopy()
    {
        var inside = InsideView(copied: UpdatePaths.StateFolder);

        var found = AppDataRedirection.FirstUsedByThisProcess(inside, [UpdatePaths.StateFolder, UpdatePaths.InstallFolder], DirectoryIdentity.Of);

        Assert.NotNull(found);
        Assert.True(found.UsedByThisProcess);
        Assert.Equal(Package, found.Package);

        // Which is why it must not run: everything the updater would write lands in the copy.
        Assert.Equal(
            DirectoryIdentity.Of(Directory.CreateDirectory(Path.Combine(CopyOf(UpdatePaths.StateFolder), "update")).FullName),
            DirectoryIdentity.Of(UpdatePaths.ForLocalAppData(inside).WorkDirectory));
    }

    [Fact]
    public void WhereThePackagedAppHoldsNoCopyNothingIsRefused()
    {
        // Measured on Windows: where the real folder exists and the app holds no copy, its processes
        // write straight through. The copy is the problem, not where Nexus was started from.
        _root.CreateDirectory(Path.Combine("real", UpdatePaths.StateFolder));
        _root.CreateDirectory(Path.Combine("real", "Packages", Package, "LocalCache", "Local"));

        Assert.Null(AppDataRedirection.FirstUsedByThisProcess(Real, [UpdatePaths.StateFolder, UpdatePaths.InstallFolder], DirectoryIdentity.Of));
    }

    [Fact]
    public void TheUpdaterIsAlsoRefusedWhenOnlyTheInstallFolderIsACopy()
    {
        // The same mechanism on the install: an update would replace files the Start Menu never runs.
        var inside = InsideView(copied: UpdatePaths.InstallFolder);

        var found = AppDataRedirection.FirstUsedByThisProcess(inside, [UpdatePaths.StateFolder, UpdatePaths.InstallFolder], DirectoryIdentity.Of);

        Assert.NotNull(found);
        Assert.EndsWith(UpdatePaths.InstallFolder, found.Folder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// %LOCALAPPDATA% as a process inside the packaged app sees it: <paramref name="copied"/> opens as
    /// the app's copy, and the rest of the folders are the real ones.
    /// </summary>
    private string InsideView(string copied)
    {
        var copy = Directory.CreateDirectory(CopyOf(copied)).FullName;
        _root.CreateDirectory(Path.Combine("real", UpdatePaths.StateFolder));
        _root.CreateDirectory(Path.Combine("real", UpdatePaths.InstallFolder));

        var inside = _root.CreateDirectory("inside");
        var link = Path.Combine(inside, copied);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);

        Assert.SkipUnless(TempDirectory.TryCreateJunction(Path.Combine(inside, "Packages"), Path.Combine(Real, "Packages")), "junction creation unavailable");
        Assert.SkipUnless(TempDirectory.TryCreateJunction(link, copy), "junction creation unavailable");

        return inside;
    }
}
