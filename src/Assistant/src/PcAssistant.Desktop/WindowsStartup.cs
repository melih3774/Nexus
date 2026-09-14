using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace PcAssistant.Desktop;

/// <summary>Whether PC Assistant starts with Windows, and why it can or cannot.</summary>
public sealed record StartupState(bool Enabled, bool Available, string Summary, string Detail);

/// <summary>
/// The ordinary per-user Run key, and nothing more.
///
/// Off by default, visible in the app, and removable from Windows' own Startup apps page as
/// well as from here — that last part matters: an owner who forgets this setting exists should
/// still be able to find and remove it where they would look for anything else. No service, no
/// scheduled task, no admin rights, and no second copy of anything.
///
/// A registry value is easy to write and easy to get wrong in a way nobody notices. So the
/// value is always written as the current executable's full path, and reading it back compares
/// paths rather than trusting the name: a stale entry pointing at a moved or deleted build is
/// reported as not enabled, because that is what it effectively is.
/// </summary>
public static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The name the owner will see in Windows' Startup apps list.</summary>
    public const string ValueName = "PC Assistant";

    public static StartupState Read()
    {
        var executable = Executable();
        if (executable is null)
            return new(false, false, "Unavailable in this build",
                "PC Assistant is running from a host process rather than as PcAssistant.Desktop.exe, which happens with " +
                "'dotnet run'. Starting that with Windows would launch the wrong program, so the option is disabled. " +
                "Build the app and run the executable to use it.");

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var value = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(value))
                return new(false, true, "Disabled",
                    "PC Assistant does not start with Windows. Nothing is installed and nothing runs in the background.");

            var recorded = value.Trim().Trim('"');
            if (!PathsMatch(recorded, executable))
                return new(false, true, "Disabled (a stale entry points elsewhere)",
                    $"Windows has a startup entry named '{ValueName}' pointing at {recorded}, which is not this build. " +
                    "Enabling will replace it; disabling will remove it.");

            return new(true, true, "Enabled",
                $"Windows starts {executable} when you sign in. Remove it here, or from Settings > Apps > Startup.");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new(false, false, "Unavailable", "The per-user startup key could not be read: " + ex.Message);
        }
    }

    /// <summary>Turns the setting on or off. Returns the state read back afterwards, never the state asked for.</summary>
    public static StartupState Set(bool enabled)
    {
        var executable = Executable();
        if (executable is null) return Read();

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                ?? throw new IOException("The per-user Run key could not be opened for writing.");

            if (enabled) key.SetValue(ValueName, "\"" + executable + "\"", RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new(false, false, "Could not be changed", ex.Message);
        }

        return Read();
    }

    /// <summary>
    /// The real executable, or null when PC Assistant is being hosted by something else. Only a
    /// file actually named PcAssistant.Desktop.exe counts, so a development run through dotnet
    /// can never register dotnet.exe to start with Windows.
    /// </summary>
    private static string? Executable()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            return Path.GetFileName(path).Equals("PcAssistant.Desktop.exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(path)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool PathsMatch(string recorded, string executable)
    {
        try { return string.Equals(Path.GetFullPath(recorded), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }
}
