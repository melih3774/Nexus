using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcCore.Windows.Interop;

/// <summary>
/// The complete set of Win32 entry points PC Core v0.1 uses. Keeping them in one file makes the
/// platform surface auditable: if it is not here, PC Core does not call it.
/// </summary>
internal static unsafe partial class NativeMethods
{
    private const string Kernel32 = "kernel32.dll";
    private const string Shell32 = "shell32.dll";
    private const string User32 = "user32.dll";
    private const string Ole32 = "ole32.dll";

    // --- CreateFileW ---------------------------------------------------------------------------

    /// <summary>No access rights requested. Enough to query a path, and requires no read permission.</summary>
    internal const uint AccessNone = 0;

    /// <summary>FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE — never block another process.</summary>
    internal const uint ShareAll = 0x00000001 | 0x00000002 | 0x00000004;

    internal const uint OpenExisting = 3;

    /// <summary>Required to obtain a handle to a <em>directory</em> rather than a file.</summary>
    internal const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary>
    /// FILE_NAME_NORMALIZED | VOLUME_NAME_DOS. Both are zero, but naming them documents that we
    /// ask for the normalised, drive-letter form rather than a volume GUID path.
    /// </summary>
    internal const uint FileNameNormalizedVolumeNameDos = 0x0;

    [LibraryImport(Kernel32, EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    /// <summary>
    /// Asks the OS for the true path behind an open handle. This is what resolves symlinks,
    /// junctions, mount points and 8.3 short names — none of which lexical normalisation can see.
    /// </summary>
    [LibraryImport(Kernel32, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    internal static partial uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        char* lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    // --- Known folders -------------------------------------------------------------------------

    /// <summary>FOLDERID_Downloads. Has no <c>Environment.SpecialFolder</c> equivalent.</summary>
    internal static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>KF_FLAG_DONT_VERIFY — return the configured path even if it is offline.</summary>
    internal const uint KnownFolderDontVerify = 0x00004000;

    [LibraryImport(Shell32, EntryPoint = "SHGetKnownFolderPath")]
    internal static partial int SHGetKnownFolderPath(Guid* rfid, uint dwFlags, nint hToken, out nint ppszPath);

    [LibraryImport(Ole32, EntryPoint = "CoTaskMemFree")]
    internal static partial void CoTaskMemFree(nint pv);

    // --- DPI awareness -------------------------------------------------------------------------

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.</summary>
    internal static readonly nint DpiAwarenessContextPerMonitorAwareV2 = -4;

    [LibraryImport(User32, EntryPoint = "SetProcessDpiAwarenessContext", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDpiAwarenessContext(nint value);

    // --- Display geometry ----------------------------------------------------------------------

    internal const int SmCxScreen = 0;
    internal const int SmCyScreen = 1;
    internal const int SmXVirtualScreen = 76;
    internal const int SmYVirtualScreen = 77;
    internal const int SmCxVirtualScreen = 78;
    internal const int SmCyVirtualScreen = 79;

    [LibraryImport(User32, EntryPoint = "GetSystemMetrics", SetLastError = true)]
    internal static partial int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [LibraryImport(User32, EntryPoint = "EnumDisplayMonitors")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayMonitors(
        nint hdc,
        nint lprcClip,
        delegate* unmanaged<nint, nint, Rect*, nint, int> lpfnEnum,
        nint dwData);

    // --- Elevation check -----------------------------------------------------------------------

    internal const uint TokenQuery = 0x0008;

    /// <summary>TokenElevation.</summary>
    internal const int TokenElevationClass = 20;

    [LibraryImport(Kernel32, EntryPoint = "GetCurrentProcess")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        out uint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [LibraryImport(Kernel32, EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}
