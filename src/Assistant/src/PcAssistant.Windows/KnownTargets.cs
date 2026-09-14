using System;
using System.Collections.Generic;
using System.Linq;

namespace PcAssistant.Windows;

/// <summary>
/// The things an owner names that have no Start Menu entry: Settings pages, shell folders and
/// the built-in management tools.
///
/// Every Settings entry is a documented <c>ms-settings:</c> URI. That matters because the
/// alternative — driving the Settings app's own UI, or invoking undocumented shell verbs — is
/// exactly the kind of brittle guesswork that breaks on the next Windows update. Folders go
/// through <c>shell:</c> known-folder names for the same reason: they survive a relocated
/// Downloads folder, which a hardcoded path does not.
/// </summary>
public static class KnownTargets
{
    /// <summary>Display name, URI, and what the owner gets. Aliases are separate rows on purpose, so search finds them.</summary>
    public static readonly IReadOnlyList<(string Name, string Uri, string Detail)> All = Build();

    public static string KindOf(string uri) =>
        uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ? "Windows Settings page"
        : uri.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? "Windows folder"
        : "Windows tool";

    private static IReadOnlyList<(string, string, string)> Build()
    {
        var items = new List<(string, string, string)>();

        void Settings(string name, string page, string detail) => items.Add((name, "ms-settings:" + page, detail));
        void Folder(string name, string folder, string detail) => items.Add((name, "shell:" + folder, detail));
        void Tool(string name, string command, string detail) => items.Add((name, command, detail));

        // --- Settings, all documented ms-settings pages -------------------
        Settings("Bluetooth settings", "bluetooth", "Bluetooth & devices, on the Bluetooth page.");
        Settings("Bluetooth and devices", "connecteddevices", "Bluetooth & devices overview, including paired devices.");
        Settings("Display settings", "display", "System > Display: resolution, scale and multiple displays.");
        Settings("Night light settings", "nightlight", "System > Display > Night light.");
        Settings("Graphics settings", "display-advancedgraphics", "System > Display > Graphics.");
        Settings("Sound settings", "sound", "System > Sound: output and input devices.");
        Settings("Volume mixer", "apps-volume", "System > Sound > Volume mixer, per-application levels.");
        Settings("Notification settings", "notifications", "System > Notifications.");
        Settings("Power and sleep settings", "powersleep", "System > Power: screen and sleep timeouts.");
        Settings("Battery settings", "batterysaver", "System > Power & battery, including battery saver.");
        Settings("Storage settings", "storage", "System > Storage: what is using disk space.");
        Settings("Multitasking settings", "multitasking", "System > Multitasking: snap layouts and desktops.");
        Settings("Clipboard settings", "clipboard", "System > Clipboard, including clipboard history.");
        Settings("Remote desktop settings", "remotedesktop", "System > Remote Desktop.");
        Settings("About this PC", "about", "System > About: device name, specifications and Windows edition.");
        Settings("Network settings", "network", "Network & internet overview.");
        Settings("Wi-Fi settings", "network-wifi", "Network & internet > Wi-Fi.");
        Settings("Ethernet settings", "network-ethernet", "Network & internet > Ethernet.");
        Settings("VPN settings", "network-vpn", "Network & internet > VPN.");
        Settings("Mobile hotspot settings", "network-mobilehotspot", "Network & internet > Mobile hotspot.");
        Settings("Airplane mode", "network-airplanemode", "Network & internet > Airplane mode.");
        Settings("Proxy settings", "network-proxy", "Network & internet > Proxy.");
        Settings("Advanced network settings", "network-advancedsettings", "Network & internet > Advanced network settings.");
        Settings("Apps and features", "appsfeatures", "Apps > Installed apps: what is installed and how to remove it.");
        Settings("Default apps", "defaultapps", "Apps > Default apps.");
        Settings("Startup apps", "startupapps", "Apps > Startup: what runs when Windows starts.");
        Settings("Optional features", "optionalfeatures", "Apps > Optional features.");
        Settings("Windows Update", "windowsupdate", "Windows Update.");
        Settings("Windows Update history", "windowsupdate-history", "Windows Update > Update history.");
        Settings("Windows Security", "windowsdefender", "Privacy & security > Windows Security.");
        Settings("Recovery options", "recovery", "System > Recovery.");
        Settings("Troubleshoot settings", "troubleshoot", "System > Troubleshoot.");
        Settings("Date and time settings", "dateandtime", "Time & language > Date & time.");
        Settings("Language settings", "regionlanguage", "Time & language > Language & region.");
        Settings("Personalization settings", "personalization", "Personalization.");
        Settings("Theme settings", "themes", "Personalization > Themes.");
        Settings("Taskbar settings", "taskbar", "Personalization > Taskbar.");
        Settings("Mouse settings", "mousetouchpad", "Bluetooth & devices > Mouse.");
        Settings("Touchpad settings", "devices-touchpad", "Bluetooth & devices > Touchpad.");
        Settings("Keyboard settings", "keyboard", "Time & language > Typing, keyboard behaviour.");
        Settings("Printers and scanners", "printers", "Bluetooth & devices > Printers & scanners.");
        Settings("Accounts", "yourinfo", "Accounts > Your info.");
        Settings("Sign-in options", "signinoptions", "Accounts > Sign-in options.");
        Settings("Privacy settings", "privacy", "Privacy & security.");
        Settings("Camera privacy", "privacy-webcam", "Privacy & security > Camera.");
        Settings("Microphone privacy", "privacy-microphone", "Privacy & security > Microphone.");
        Settings("Location privacy", "privacy-location", "Privacy & security > Location.");
        Settings("Accessibility settings", "easeofaccess", "Accessibility.");
        Settings("Developer settings", "developers", "System > For developers.");
        Settings("Backup settings", "backup", "Accounts > Windows backup.");

        // --- Shell folders, by known-folder name rather than by path ------
        Folder("Downloads folder", "Downloads", "Opens the owner's Downloads folder in File Explorer.");
        Folder("Documents folder", "Personal", "Opens the owner's Documents folder in File Explorer.");
        Folder("Desktop folder", "Desktop", "Opens the owner's Desktop folder in File Explorer.");
        Folder("Pictures folder", "My Pictures", "Opens the owner's Pictures folder.");
        Folder("Videos folder", "My Video", "Opens the owner's Videos folder.");
        Folder("Music folder", "My Music", "Opens the owner's Music folder.");
        Folder("This PC", "MyComputerFolder", "Opens This PC in File Explorer.");
        Folder("Recycle Bin", "RecycleBinFolder", "Opens the Recycle Bin. Nothing is deleted or restored by opening it.");
        Folder("Startup folder", "Startup", "Opens the per-user Startup folder.");
        Folder("Temporary files folder", "Local AppData", "Opens the owner's local application data folder.");
        Folder("Fonts folder", "Fonts", "Opens the installed fonts folder.");

        // --- Built-in tools -----------------------------------------------
        Tool("Task Manager", "taskmgr.exe", "Opens Task Manager. PC Assistant cannot type into it if Windows opens it elevated.");
        Tool("Device Manager", "devmgmt.msc", "Opens Device Manager.");
        Tool("Services", "services.msc", "Opens the Services console. Changing a service still needs an administrator operation.");
        Tool("Event Viewer", "eventvwr.msc", "Opens Event Viewer.");
        Tool("Disk Management", "diskmgmt.msc", "Opens Disk Management. Read it; do not change partitions without the owner.");
        Tool("Performance Monitor", "perfmon.msc", "Opens Performance Monitor.");
        Tool("Resource Monitor", "resmon.exe", "Opens Resource Monitor.");
        Tool("System Information", "msinfo32.exe", "Opens System Information.");
        Tool("Control Panel", "control.exe", "Opens the classic Control Panel.");
        Tool("Character Map", "charmap.exe", "Opens Character Map.");
        Tool("Calculator", "calc.exe", "Opens Calculator.");
        Tool("Notepad", "notepad.exe", "Opens Notepad.");
        Tool("Paint", "mspaint.exe", "Opens Paint.");
        Tool("Snipping Tool", "ms-screenclip:", "Starts a Windows screen capture. PC Assistant has its own capture tools; this is for the owner.");
        Tool("File Explorer", "explorer.exe", "Opens a File Explorer window.");
        Tool("Magnifier", "magnify.exe", "Opens Magnifier.");
        Tool("On-Screen Keyboard", "osk.exe", "Opens the on-screen keyboard.");
        Tool("Windows Security app", "windowsdefender:", "Opens the Windows Security application.");
        Tool("Disk Cleanup", "cleanmgr.exe", "Opens Disk Cleanup. It deletes nothing until the owner confirms in its own dialog.");

        return items;
    }

    /// <summary>
    /// Resolves a spoken page name to a catalogue entry. Returns the display <em>name</em>
    /// alongside the URI because that is what discovery matches on: handing the raw
    /// "ms-settings:..." string back to a name lookup finds nothing.
    /// </summary>
    public static (string Name, string Uri)? SettingsEntryFor(string page)
    {
        var uri = SettingsUriFor(page);
        if (uri is null) return null;
        var entry = All.First(t => t.Uri.Equals(uri, StringComparison.OrdinalIgnoreCase));
        return (entry.Name, entry.Uri);
    }

    /// <summary>Reverse lookup for a settings page name, used by the settings-specific tool.</summary>
    public static string? SettingsUriFor(string page)
    {
        var wanted = page.Trim();
        if (wanted.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) wanted = wanted["ms-settings:".Length..];

        var direct = All.FirstOrDefault(t =>
            t.Uri.Equals("ms-settings:" + wanted, StringComparison.OrdinalIgnoreCase));
        if (direct.Uri is not null) return direct.Uri;

        var byName = All.Where(t => t.Uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) &&
                                    t.Name.Contains(page.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Name.Length)
            .Select(t => t.Uri)
            .FirstOrDefault();
        return byName;
    }

    /// <summary>Every settings page name this table knows, for an error message that actually helps.</summary>
    public static IEnumerable<string> SettingsPageNames() => All
        .Where(t => t.Uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        .Select(t => t.Name);
}
