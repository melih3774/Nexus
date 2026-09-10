using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace PcCore.Windows;

public sealed class AutoStartOptions
{
    public const string SectionName = "PcCore:Startup";

    /// <summary>
    /// When true, PC Core makes sure it is registered to start at sign-in each time it launches.
    /// Set it to false while debugging so a dev run does not touch the owner's startup list —
    /// that is the escape hatch, and it is the only thing that governs registration.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

public sealed record AutoStartStatus(bool Registered, string? RegisteredCommand, string ExpectedCommand)
{
    public bool UpToDate => Registered
        && string.Equals(RegisteredCommand, ExpectedCommand, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Starts PC Core when the owner signs in, via the per-user <c>Run</c> key.
/// <para>
/// <b>Mechanism:</b> a single value under
/// <c>HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run</c> named <c>PcCore</c>,
/// whose data is the quoted path of the executable. Nothing else is written anywhere.
/// </para>
/// <para>
/// <b>Why this one.</b> It is per-user, so it needs no administrator rights and cannot affect
/// anyone else on the machine. It is also the location Windows itself surfaces in Task Manager's
/// Startup apps tab and in Settings, so the owner can see and disable it without going near this
/// application — which is precisely the property a startup entry should have.
/// </para>
/// <para>
/// <b>What this deliberately is not.</b> No service, no elevation, no scheduled task, no
/// watchdog, no re-registration after the owner removes it, no attempt to survive being killed,
/// and no hiding: PC Core runs as an ordinary process in the user's session and is visible in
/// Task Manager like anything else. If the owner deletes the value, PC Core stays gone until they
/// ask for it back.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAutoStart
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "PcCore";

    private readonly ILogger<WindowsAutoStart> _logger;

    public WindowsAutoStart(ILogger<WindowsAutoStart> logger) => _logger = logger;

    /// <summary>The command that should be registered: this process's executable, quoted.</summary>
    public static string ExpectedCommand()
    {
        var path = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
        return $"\"{path}\"";
    }

    public AutoStartStatus GetStatus()
    {
        var expected = ExpectedCommand();

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var current = key?.GetValue(ValueName) as string;

        return new AutoStartStatus(current is not null, current, expected);
    }

    /// <summary>Registers, or corrects the path if the executable has moved. Idempotent.</summary>
    public AutoStartStatus Register() => Register(null);

    /// <summary>
    /// Registers a specific executable rather than the running one.
    /// <para>
    /// The installer needs this. PC Core normally registers itself, which is right when it is
    /// running from where it lives — and wrong during development, where "where it is running from"
    /// is build output that a clean or a branch switch can delete. <c>--install-app</c> copies the
    /// build somewhere permanent and registers <em>that</em>, which is a different path from the one
    /// doing the registering.
    /// </para>
    /// </summary>
    /// <param name="executablePath">The executable to register, or null for this process.</param>
    public AutoStartStatus Register(string? executablePath)
    {
        var expected = executablePath is null ? ExpectedCommand() : $"\"{executablePath}\"";

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            _logger.LogError("Could not open the per-user Run key; auto-start was not registered.");
            return GetStatus();
        }

        var current = key.GetValue(ValueName) as string;

        if (string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Auto-start already registered as {Command}.", expected);
            return new AutoStartStatus(true, current, expected);
        }

        key.SetValue(ValueName, expected, RegistryValueKind.String);

        _logger.LogInformation(
            "Registered auto-start: HKCU\\{Key}\\{Value} = {Command}. Remove it from Task Manager's "
            + "Startup apps tab, or run with --uninstall-startup.",
            RunKeyPath,
            ValueName,
            expected);

        return new AutoStartStatus(true, expected, expected);
    }

    /// <summary>Removes the entry. Returns false if it was not there.</summary>
    public bool Unregister()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

        if (key?.GetValue(ValueName) is null)
        {
            return false;
        }

        key.DeleteValue(ValueName, throwOnMissingValue: false);
        _logger.LogInformation("Removed auto-start entry HKCU\\{Key}\\{Value}.", RunKeyPath, ValueName);
        return true;
    }
}
