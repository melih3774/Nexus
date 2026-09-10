using System.Runtime.Versioning;

using PcCore.Capabilities.Abstractions;
using PcCore.Windows;
using PcCore.Windows.Shell;

namespace PcCore.Host;

/// <summary>
/// Gives Nexus an ordinary Windows presence: a stable install location, a Start Menu entry, and an
/// auto-start pointing at something permanent.
/// <para>
/// <b>The problem this exists to solve is the auto-start path.</b> PC Core registers itself to run
/// at sign-in using its own executable path, and during development that path is
/// <c>src\PcCore.Host\bin\Release\...</c> — a directory that a <c>git clean</c>, a branch switch or
/// a rebuild can empty. An auto-start entry pointing into build output is one that silently stops
/// working, and the owner finds out when their PC stops answering the phone.
/// </para>
/// <para>
/// So there is an install step. It copies the build to <c>%LOCALAPPDATA%\Programs\Nexus</c>, creates
/// the Start Menu shortcut there, and re-points auto-start at the installed copy. Per-user
/// throughout: no administrator rights, no service, no <c>Program Files</c>, nothing a standard user
/// cannot undo. The privileged Secure Desktop helper is the one component that needs more, and it
/// has its own installer for exactly that reason.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NexusShellInstaller
{
    /// <summary>The name shown in Start, and the shortcut's file name.</summary>
    private const string ShortcutName = ProductInfo.Name;

    /// <summary>
    /// Where an installed Nexus lives.
    /// <para>
    /// <c>%LOCALAPPDATA%\Programs</c> is the per-user equivalent of Program Files and the
    /// documented home for an application installed without elevation. Not Roaming: this is a
    /// machine-specific binary, and roaming it to another PC would carry a Windows executable
    /// somewhere it was never meant to run.
    /// </para>
    /// </summary>
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        ProductInfo.Name);

    public static string InstalledExecutable => Path.Combine(InstallDirectory, "PcCore.Host.exe");

    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;

        var command = args.FirstOrDefault(a =>
            a is "--install-app" or "--uninstall-app" or "--shell-status");

        if (command is null)
        {
            return false;
        }

        exitCode = command switch
        {
            "--install-app" => Install(),
            "--uninstall-app" => Uninstall(),
            _ => Describe(),
        };

        return true;
    }

    private static int Describe()
    {
        var autoStart = new WindowsAutoStart(NullLoggerFor.AutoStart).GetStatus();
        var shortcut = WindowsShortcut.TargetOf(ShortcutName);

        Console.WriteLine();
        Console.WriteLine($"{ProductInfo.Display} — Windows integration");
        Console.WriteLine();
        Console.WriteLine($"Running from : {Environment.ProcessPath}");
        Console.WriteLine($"Install dir  : {InstallDirectory}");
        Console.WriteLine($"Installed    : {File.Exists(InstalledExecutable)}");
        Console.WriteLine($"Start Menu   : {shortcut ?? "(not present)"}");
        Console.WriteLine($"Auto-start   : {autoStart.RegisteredCommand ?? "(not registered)"}");
        Console.WriteLine();

        if (autoStart.RegisteredCommand is { } command && command.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  Auto-start points into build output, which a rebuild or a clean can remove.");
            Console.WriteLine("  Run --install-app to move it to the installed copy.");
            Console.WriteLine();
        }

        return File.Exists(InstalledExecutable) ? 0 : 2;
    }

    private static int Install()
    {
        var source = AppContext.BaseDirectory;

        try
        {
            var staged = Stage(source, InstallDirectory);

            Console.WriteLine($"Installed to : {staged}");

            var shortcut = WindowsShortcut.Create(
                ShortcutName,
                staged,
                $"{ProductInfo.Display} — remote control and security for this PC");

            Console.WriteLine($"Start Menu   : {shortcut}");

            // Auto-start is re-pointed at the installed copy, which is the entire reason for the
            // install directory: the registered command must outlive the build it came from.
            var autoStart = new WindowsAutoStart(NullLoggerFor.AutoStart);
            autoStart.Register(staged);

            Console.WriteLine($"Auto-start   : \"{staged}\"");
            Console.WriteLine();
            Console.WriteLine($"  {ProductInfo.Name} will appear in Start and Search, and will run at sign-in.");
            Console.WriteLine("  Start it now from Start, or run the installed copy directly.");

            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Nexus could not be installed: {ex.Message}");

            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(source),
                    Path.TrimEndingDirectorySeparator(InstallDirectory),
                    StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            Console.Error.WriteLine("Close any running Nexus and try again.");
            return 1;
        }
    }

    private static int Uninstall()
    {
        var removedShortcut = WindowsShortcut.Remove(ShortcutName);
        Console.WriteLine(removedShortcut ? "Start Menu shortcut removed." : "The Start Menu shortcut could not be removed.");

        new WindowsAutoStart(NullLoggerFor.AutoStart).Unregister();
        Console.WriteLine("Auto-start removed.");

        // The installed files are deliberately left alone. Removing the directory a running process
        // is executing from is the kind of thing that half-succeeds, and the owner asked to remove
        // Nexus from Start and from sign-in, not to delete a folder they may have installed from.
        if (Directory.Exists(InstallDirectory))
        {
            Console.WriteLine();
            Console.WriteLine($"  The installed copy is still at {InstallDirectory}.");
            Console.WriteLine("  Delete that folder to remove it entirely.");
        }

        return 0;
    }

    /// <summary>
    /// Copies the build output to the install directory and returns the installed executable.
    /// <para>
    /// The whole directory, recursively: a framework-dependent .NET application is an executable
    /// plus its runtime configuration, its dependencies, <c>wwwroot</c>, and — importantly — the
    /// input broker, which PC Core requires to sit beside it.
    /// </para>
    /// </summary>
    private static string Stage(string source, string destination)
    {
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(source),
                Path.TrimEndingDirectorySeparator(destination),
                StringComparison.OrdinalIgnoreCase))
        {
            // Already running from the install directory; there is nothing to copy.
            return InstalledExecutable;
        }

        CopyDirectory(new DirectoryInfo(source), new DirectoryInfo(destination));
        return InstalledExecutable;
    }

    private static void CopyDirectory(DirectoryInfo source, DirectoryInfo destination)
    {
        destination.Create();

        foreach (var file in source.EnumerateFiles())
        {
            file.CopyTo(Path.Combine(destination.FullName, file.Name), overwrite: true);
        }

        foreach (var directory in source.EnumerateDirectories())
        {
            CopyDirectory(directory, new DirectoryInfo(Path.Combine(destination.FullName, directory.Name)));
        }
    }
}

/// <summary>
/// Loggers for the two places install commands need one before the host exists.
/// <para>
/// These commands run and exit without building a service provider, so there is no logger factory to
/// ask. They print to the console instead, which is where the owner is looking.
/// </para>
/// </summary>
internal static class NullLoggerFor
{
    public static Microsoft.Extensions.Logging.ILogger<WindowsAutoStart> AutoStart { get; } =
        Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowsAutoStart>.Instance;
}
