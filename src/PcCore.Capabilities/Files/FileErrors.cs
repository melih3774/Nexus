using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Security;
using PcCore.Protocol;

namespace PcCore.Capabilities.Files;

/// <summary>
/// Turns what Windows said into something the phone can act on.
/// <para>
/// Every file operation fails for one of a handful of reasons the owner can do something about -
/// the file is gone, Windows will not let this user at it, another program has it open, the disk is
/// full - and one message for all of them ("the file could not be read") leaves them guessing which.
/// The messages name the reason and never the path: a caller already knows what it asked for, and
/// an exception's own text can carry more than that.
/// </para>
/// </summary>
internal static class FileErrors
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorWriteProtect = 19;
    private const int ErrorNotReady = 21;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorFileExists = 80;
    private const int ErrorDiskFull = 112;
    private const int ErrorAlreadyExists = 183;

    /// <summary>The Win32 error inside an <see cref="IOException"/>'s HRESULT, when it carries one.</summary>
    public static int? Win32Code(Exception exception) =>
        (exception.HResult & 0xFFFF0000) == 0x80070000 ? exception.HResult & 0xFFFF : null;

    /// <summary>The protocol error for a filesystem failure. <paramref name="doing"/> finishes "Windows refused ...".</summary>
    public static CapabilityException From(Exception exception, string doing = "that") => exception switch
    {
        CapabilityException capability => capability,

        FileNotFoundException or DirectoryNotFoundException =>
            new CapabilityException(ErrorCodes.NotFound, "It no longer exists on the PC."),

        UnauthorizedAccessException =>
            new CapabilityException(ErrorCodes.AccessDenied, $"Windows refused {doing}: this account is not allowed."),

        IOException io => Win32Code(io) switch
        {
            ErrorFileNotFound or ErrorPathNotFound =>
                new CapabilityException(ErrorCodes.NotFound, "It no longer exists on the PC."),

            ErrorAccessDenied or ErrorWriteProtect =>
                new CapabilityException(ErrorCodes.AccessDenied, $"Windows refused {doing}: this account is not allowed."),

            ErrorSharingViolation or ErrorLockViolation =>
                new CapabilityException(ErrorCodes.FileInUse, "Another program on the PC has it open."),

            ErrorHandleDiskFull or ErrorDiskFull =>
                new CapabilityException(ErrorCodes.DiskFull, "The PC's disk is full."),

            ErrorFileExists or ErrorAlreadyExists =>
                new CapabilityException(ErrorCodes.AlreadyExists, "Something with that name is already there."),

            ErrorNotReady =>
                new CapabilityException(ErrorCodes.NotFound, "That drive is not ready."),

            _ => new CapabilityException(ErrorCodes.AccessDenied, $"Windows could not do {doing}."),
        },

        _ => throw new ArgumentException("not a filesystem failure", nameof(exception), exception),
    };

    public static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;
}

/// <summary>
/// Places inside the filesystem boundary that Files still does not reach: Nexus's own state and
/// installation.
/// <para>
/// Pairing, owner keys, security evidence and the release store live under <c>%LOCALAPPDATA%\Nexus</c>.
/// The README promises that evidence is deleted only at the PC behind Windows Hello and that a
/// replacement owner key is accepted only after the owner clears the old one at the PC. A file browser
/// that could overwrite those files would quietly be a second way round both rules, so these folders
/// are refused whole, for reading as well as writing - there is nothing in them a phone needs as a
/// file, and each has its own capability where it needs one.
/// </para>
/// </summary>
public sealed class FileProtections
{
    public static readonly FileProtections None = new([]);

    public FileProtections(IEnumerable<string> protectedRealPaths) =>
        Paths = [.. protectedRealPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.TrimEndingDirectorySeparator)];

    public IReadOnlyList<string> Paths { get; }

    public bool IsProtected(string realPath) =>
        Paths.Any(root => PathContainment.IsWithin(realPath, root));

    /// <summary>Throws the same refusal the boundary gives, so a protected folder reads as outside it.</summary>
    public string EnsureReachable(string realPath) => IsProtected(realPath)
        ? throw new CapabilityException(ErrorCodes.PathNotAllowed, "Nexus keeps its own state there; it is not reachable through Files.")
        : realPath;
}
