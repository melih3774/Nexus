using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using PcAssistant.Windows;

namespace PcAssistant.PcTools;

/// <summary>
/// The everyday PC controls an assistant is actually asked for: volume, battery, network,
/// what is in the Recycle Bin, opening an application or a Settings page, and telling the
/// owner something finished.
///
/// Everything that changes state here is small, visible and reversible by the owner in one
/// click. Shutdown, restart, sleep, emptying the Recycle Bin and any power-policy change are
/// deliberately absent: they are one-way, and an assistant that can do them by accident is
/// worse than one that has to ask the owner to do them.
///
/// There is deliberately no notification tool. A tray-balloon implementation was written and
/// measured on this machine — with and without a message pump, and with the icon addition
/// separated from the balloon — and Windows 11 26200 rendered nothing in any variant. The
/// reliable route is a registered AppUserModelID, which means installing a Start Menu shortcut:
/// persistent OS state, and therefore the owner's decision rather than a side effect. A tool
/// that reports "the owner has been notified" when nothing appeared is worse than no tool.
/// </summary>
[McpServerToolType]
public sealed class SystemTools(AudioService audio, AppLauncher apps)
{
    private static string Json(object value) => JsonSerializer.Serialize(value);

    [McpServerTool(Name = "audio_status", ReadOnly = true), Description(
        "Read master output volume, mute state and the name of the default playback device. This is the same endpoint " +
        "the owner's volume flyout controls, so it reflects what they actually hear. Changes nothing.")]
    public async Task<string> AudioStatus(CancellationToken cancellationToken) => Json(await audio.GetAsync(cancellationToken));

    [McpServerTool(Name = "audio_set"), Description(
        "Set master output volume as a percentage (0-100) and/or mute state, then report the level read back from the " +
        "device — which can differ from what was asked for, because some endpoints quantize steps. Give volumePercent, " +
        "muted, or both. Note the previous level first if the owner asked for a temporary change, so it can be restored.")]
    public async Task<string> AudioSet(CancellationToken cancellationToken, int? volumePercent = null, bool? muted = null)
    {
        if (volumePercent is null && muted is null)
            throw new ArgumentException("Give volumePercent, muted, or both.");
        if (volumePercent is { } percent) await audio.SetVolumeAsync(percent, cancellationToken);
        if (muted is { } mute) return Json(await audio.SetMuteAsync(mute, cancellationToken));
        return Json(await audio.GetAsync(cancellationToken));
    }

    [McpServerTool(Name = "power_status", ReadOnly = true), Description(
        "Read power source, battery state, battery percentage and estimated remaining time. Reports honestly when the " +
        "machine has no battery, and when Windows has no time estimate yet. Does not change power settings, sleep or " +
        "shut down: those need the owner.")]
    public string PowerStatus() => Json(SystemControls.Power());

    [McpServerTool(Name = "network_status", ReadOnly = true), Description(
        "Read network adapters, their state, local addresses, the Wi-Fi network name where applicable, and whether the " +
        "machine has a usable connection. Tunnels and virtual adapters that are up are listed separately so a second " +
        "address range is explained rather than confusing. This is adapter state, not a reachability test: a captive " +
        "portal or a DNS failure still looks connected. Changes nothing.")]
    public string NetworkStatus() => Json(SystemControls.Network());

    [McpServerTool(Name = "recycle_bin", ReadOnly = true), Description(
        "Read how many items are in the Recycle Bin and how much space they use, across all drives. Pass open=true to " +
        "show it to the owner in File Explorer. PC Assistant never empties the Recycle Bin and never restores from it.")]
    public string RecycleBin(bool open = false)
    {
        var state = SystemControls.RecycleBin();
        return open ? Json(new { state, opened = SystemControls.OpenRecycleBin() }) : Json(state);
    }

    [McpServerTool(Name = "installed_apps", ReadOnly = true), Description(
        "Find applications and Windows targets by name: Start Menu shortcuts, App Paths, packaged apps, documented " +
        "Settings pages, shell folders and built-in tools, plus installed software from the uninstall registry. Pass a " +
        "query — 'discord', 'vs code', 'bluetooth' — which is usually better than listing everything. Each entry has a " +
        "short-lived reference for open_app. Entries whose kind is 'Installed software' answer \"what is installed\" " +
        "but have no launch target and cannot be opened; do not invent one. Shells and script hosts are deliberately absent.")]
    public string InstalledApps(string? query = null, int limit = 40) => Json(apps.Discover(query, limit));

    [McpServerTool(Name = "open_app"), Description(
        "Open an application, Settings page, folder or Windows tool — by a reference from installed_apps, or directly by " +
        "name such as 'Discord', 'Task Manager' or 'Bluetooth settings'. A name that matches several things equally well " +
        "is reported with the candidates rather than guessed at. Waits for a real window and reports the one that " +
        "appeared, or says plainly that none did; a launch on its own is never reported as success. Shells and script " +
        "hosts are refused: use the shell tools for commands.")]
    public async Task<string> OpenApp(string application, CancellationToken cancellationToken)
        => Json(await apps.OpenAsync(application, cancellationToken));

    [McpServerTool(Name = "open_settings"), Description(
        "Open a Windows Settings page by name — 'bluetooth', 'display', 'windows update', 'sound', 'apps', 'storage', " +
        "'network', 'startup apps'. Uses documented ms-settings URIs, so this keeps working across Windows updates in a " +
        "way that driving the Settings UI would not. Opening a page changes nothing; the owner still makes the change.")]
    public async Task<string> OpenSettings(string page, CancellationToken cancellationToken)
    {
        var entry = KnownTargets.SettingsEntryFor(page)
            ?? throw new ArgumentException(
                $"No known Settings page matches '{page}'. Known pages include: " +
                string.Join(", ", KnownTargets.SettingsPageNames().Take(24)) + ".");
        return Json(await apps.OpenAsync(entry.Name, cancellationToken));
    }

    [McpServerTool(Name = "open_folder"), Description(
        "Open an existing local folder in File Explorer for the owner. Absolute local paths only; network paths are " +
        "excluded. Changes nothing in the folder. For a document use open_document; to open a folder in an editor use open_in_app.")]
    public async Task<string> OpenFolder(string path, CancellationToken cancellationToken)
        => Json(await apps.OpenFolderAsync(path, cancellationToken));

    [McpServerTool(Name = "open_in_app"), Description(
        "Open one existing local file or folder with a named application — 'open this folder in VS Code'. The application " +
        "comes from installed_apps and the argument is exactly one validated path: there is no second argument, no flag " +
        "and no shell. Waits for the window and reports what actually appeared.")]
    public async Task<string> OpenInApp(string application, string path, CancellationToken cancellationToken)
        => Json(await apps.OpenPathWithAsync(application, path, cancellationToken));
}
