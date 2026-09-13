using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

namespace PcCore.Update.Windows;

/// <summary>
/// Which directory a path actually opens, after every redirection Windows applies to this process:
/// its volume and 128-bit file id. Two paths with the same identity are the same directory.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DirectoryIdentity
{
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;

    /// <summary>Required to open a directory rather than a file.</summary>
    private const uint FileFlagBackupSemantics = 0x02000000;

    private const int FileIdInfoClass = 18;

    /// <summary>The identity of the directory at <paramref name="path"/>, or null if it cannot be opened.</summary>
    public static string? Of(string path)
    {
        using var handle = CreateFileW(path, FileReadAttributes, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);

        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, FileIdInfoClass, out var info, (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            return null;
        }

        return $"{info.VolumeSerialNumber:x16}:{info.FileIdHigh:x16}{info.FileIdLow:x16}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileIdInfo information, uint size);
}
