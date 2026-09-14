using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using PcAssistant.Core.Persistence;

namespace PcAssistant.Desktop;

public enum AppTheme { System, Dark, Light }

public static class ThemePalette
{
    public static IReadOnlyDictionary<string, string> For(AppTheme theme)
        => theme == AppTheme.Light ? Light : Dark;

    public static readonly IReadOnlyDictionary<string, string> Dark = new Dictionary<string, string>
    {
        ["Bg"] = "#15161A", ["BgPanel"] = "#1C1E24", ["BgRaised"] = "#242731", ["BgInput"] = "#101217",
        ["Border"] = "#343944", ["Fg"] = "#F1F3F5", ["FgMuted"] = "#A5ABB6", ["FgDisabled"] = "#737A86",
        ["Accent"] = "#A9472C", ["AccentHover"] = "#B65335", ["AccentPressed"] = "#963E27", ["AccentSoft"] = "#3A2A24",
        ["ControlHover"] = "#2C303A", ["ControlPressed"] = "#353A46", ["SelectionHover"] = "#292C35",
        ["OwnerBg"] = "#34241F", ["OwnerBorder"] = "#704631", ["SystemBg"] = "#2A1F20", ["SystemBorder"] = "#714044",
        ["ApprovalBg"] = "#2A2318", ["Ok"] = "#65B789", ["Warn"] = "#E0AA4A", ["Danger"] = "#E17A72",
        ["ScrollTrack"] = "#17191E", ["ScrollThumb"] = "#4C5360",
    };

    public static readonly IReadOnlyDictionary<string, string> Light = new Dictionary<string, string>
    {
        ["Bg"] = "#F4F5F7", ["BgPanel"] = "#FFFFFF", ["BgRaised"] = "#F0F1F3", ["BgInput"] = "#FFFFFF",
        ["Border"] = "#CDD1D8", ["Fg"] = "#20242B", ["FgMuted"] = "#626A76", ["FgDisabled"] = "#8B929C",
        ["Accent"] = "#B85031", ["AccentHover"] = "#A44328", ["AccentPressed"] = "#8D3822", ["AccentSoft"] = "#F5E1D9",
        ["ControlHover"] = "#E7E9ED", ["ControlPressed"] = "#DBDEE4", ["SelectionHover"] = "#ECEEF1",
        ["OwnerBg"] = "#F8E8E1", ["OwnerBorder"] = "#DDAA97", ["SystemBg"] = "#F8E9EA", ["SystemBorder"] = "#DBA4A8",
        ["ApprovalBg"] = "#FFF6DE", ["Ok"] = "#2D7E50", ["Warn"] = "#926600", ["Danger"] = "#A93D37",
        ["ScrollTrack"] = "#E8EAED", ["ScrollThumb"] = "#A5ABB5",
    };
}

internal static class ThemeManager
{
    private static AppSettings? settings;
    public static AppTheme Preference { get; private set; } = AppTheme.System;
    public static AppTheme EffectiveTheme { get; private set; } = AppTheme.Dark;
    public static string DisplayName => Preference == AppTheme.System ? $"Theme: System ({EffectiveTheme})" : $"Theme: {Preference}";
    public static event EventHandler? Changed;

    public static void Initialize(AppSettings appSettings)
    {
        settings = appSettings;
        Preference = Enum.TryParse<AppTheme>(appSettings.Theme, true, out var parsed) ? parsed : AppTheme.System;
        ApplyPreference();
    }

    public static void Cycle()
    {
        Preference = Preference switch { AppTheme.System => AppTheme.Dark, AppTheme.Dark => AppTheme.Light, _ => AppTheme.System };
        if (settings is not null) { settings.Theme = Preference.ToString(); settings.Save(); }
        ApplyPreference();
    }

    public static void RefreshSystemTheme()
    {
        if (Preference == AppTheme.System) ApplyPreference();
    }

    /// <summary>
    /// The palette lives in one merged dictionary that is swapped wholesale. WPF freezes a
    /// brush once a loaded window has used it, so recolouring a brush in place silently stops
    /// working after the first screen appears; replacing the dictionary instead makes every
    /// DynamicResource reference re-resolve, which is why the XAML never uses StaticResource
    /// for a palette colour.
    /// </summary>
    private static ResourceDictionary? applied;

    private static void ApplyPreference()
    {
        EffectiveTheme = Preference == AppTheme.System ? ReadSystemTheme() : Preference;

        var next = new ResourceDictionary();
        foreach (var (name, value) in ThemePalette.For(EffectiveTheme))
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            next[name + "Color"] = color;
            next[name] = brush;
        }

        // Added before the old one is removed: later dictionaries are searched first, so no
        // lookup ever falls through to a window with no palette at all.
        var merged = Application.Current.Resources.MergedDictionaries;
        merged.Add(next);
        if (applied is not null) merged.Remove(applied);
        applied = next;

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static AppTheme ReadSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0 ? AppTheme.Light : AppTheme.Dark;
        }
        catch { return AppTheme.Dark; }
    }
}
