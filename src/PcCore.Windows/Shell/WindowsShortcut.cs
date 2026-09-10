using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace PcCore.Windows.Shell;

/// <summary>
/// Creates and removes Start Menu shortcuts.
/// <para>
/// <b>Why a shortcut is the whole of "appears in Windows Search".</b> Start and Search index the
/// Start Menu folder; a <c>.lnk</c> in the per-user Programs directory is what makes an application
/// findable by typing its name, and there is nothing else to register. No installer, no
/// <c>Uninstall</c> registry key, no AppX package, and no administrator rights — the per-user folder
/// is writable by its owner, which is exactly the privilege level the rest of Nexus runs at.
/// </para>
/// <para>
/// Through COM rather than a script. <c>IShellLink</c> is the documented interface and
/// <c>WScript.Shell</c> would mean launching a scripting host to write a file this can write
/// directly.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsShortcut
{
    /// <summary>
    /// The per-user Start Menu programs folder, which is what Search indexes for one account.
    /// </summary>
    public static string ProgramsFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.Programs);

    /// <summary>Where a shortcut with this name would live.</summary>
    public static string PathFor(string name) => Path.Combine(ProgramsFolder, name + ".lnk");

    /// <summary>
    /// Writes a shortcut, replacing any existing one with the same name.
    /// </summary>
    /// <param name="name">Shown in Start; also the file name.</param>
    /// <param name="target">The executable to launch.</param>
    /// <param name="description">The tooltip, and part of what Search matches on.</param>
    /// <param name="arguments">Command line for the target, if any.</param>
    public static string Create(
        string name,
        string target,
        string description,
        string? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        Directory.CreateDirectory(ProgramsFolder);

        var path = PathFor(name);
        var link = (IShellLinkW)new ShellLink();

        try
        {
            link.SetPath(target);
            link.SetDescription(Truncate(description, 255));
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? string.Empty);

            if (!string.IsNullOrWhiteSpace(arguments))
            {
                link.SetArguments(arguments);
            }

            // The executable's own icon, which is the one Windows would show anyway.
            link.SetIconLocation(target, 0);

            ((IPersistFile)link).Save(path, fRemember: true);
            return path;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>Removes the shortcut. Idempotent: a missing one is already the desired state.</summary>
    public static bool Remove(string name)
    {
        var path = PathFor(name);

        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>
    /// What the shortcut currently points at, or null when there is none.
    /// <para>
    /// Read back rather than assumed, so a status command can say "installed, but pointing at a
    /// build directory that no longer exists" — which is the failure this whole area is meant to
    /// stop happening quietly.
    /// </para>
    /// </summary>
    public static string? TargetOf(string name)
    {
        var path = PathFor(name);

        if (!File.Exists(path))
        {
            return null;
        }

        var link = (IShellLinkW)new ShellLink();

        try
        {
            ((IPersistFile)link).Load(path, 0);

            var buffer = new char[260];
            link.GetPath(buffer, buffer.Length, IntPtr.Zero, 0);

            var text = new string(buffer).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [Out][MarshalAs(UnmanagedType.LPArray)] char[] file,
            int maxPath,
            IntPtr findData,
            uint flags);

        void GetIDList(out IntPtr idList);

        void SetIDList(IntPtr idList);

        void GetDescription([Out][MarshalAs(UnmanagedType.LPArray)] char[] name, int maxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([Out][MarshalAs(UnmanagedType.LPArray)] char[] dir, int maxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

        void GetArguments([Out][MarshalAs(UnmanagedType.LPArray)] char[] args, int maxPath);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCmd);

        void SetShowCmd(int showCmd);

        void GetIconLocation(
            [Out][MarshalAs(UnmanagedType.LPArray)] char[] iconPath,
            int iconPathLength,
            out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relative, uint reserved);

        void Resolve(IntPtr hwnd, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
