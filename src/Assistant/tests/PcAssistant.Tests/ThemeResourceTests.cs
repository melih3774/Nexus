using System.IO;
using System.Text.RegularExpressions;
using PcAssistant.Desktop;
using Xunit;

namespace PcAssistant.Tests;

/// <summary>
/// A theme change swaps the whole palette dictionary, so a screen only follows along if it
/// asks for its colours dynamically. These are the rules that keep a theme change from
/// half-applying — the failure mode is unreadable text, which no unit test on the palette
/// numbers alone can catch.
/// </summary>
public class ThemeResourceTests
{
    private static readonly string DesktopDirectory = FindDesktopProject();

    [Fact]
    public void PaletteColoursAreNeverReferencedWithStaticResource()
    {
        // StaticResource captures one theme's brush for the life of the window, and WPF
        // freezes that brush, so it can never be recoloured afterwards.
        var offenders = new List<string>();
        foreach (var file in XamlFiles())
            foreach (Match reference in Regex.Matches(File.ReadAllText(file), @"\{StaticResource\s+(\w+)\s*\}"))
                if (ThemePalette.Dark.ContainsKey(reference.Groups[1].Value))
                    offenders.Add($"{Path.GetFileName(file)}: {reference.Value}");

        Assert.Empty(offenders);
    }

    [Fact]
    public void EveryResourceReferenceResolves()
    {
        var known = KnownResourceKeys();
        var unresolved = new List<string>();
        foreach (var file in XamlFiles())
            foreach (Match reference in Regex.Matches(File.ReadAllText(file), @"\{(?:Static|Dynamic)Resource\s+(\w+)\s*\}"))
                if (!known.Contains(reference.Groups[1].Value))
                    unresolved.Add($"{Path.GetFileName(file)}: {reference.Value}");

        Assert.Empty(unresolved);
    }

    [Fact]
    public void NoPaletteNameIsShadowedByAnAppResourceOfItsOwn()
    {
        // App.xaml's own entries are searched before merged dictionaries. A style or template
        // sharing a palette name would win the lookup and the colour would never change.
        var appKeys = DeclaredKeys(Path.Combine(DesktopDirectory, "App.xaml"));
        var clashes = ThemePalette.Dark.Keys
            .Where(name => appKeys.Contains(name) || appKeys.Contains(name + "Color"))
            .ToList();

        Assert.Empty(clashes);
    }

    [Fact]
    public void LightAndDarkDefineTheSameColours()
    {
        Assert.Equal(ThemePalette.Dark.Keys.OrderBy(k => k), ThemePalette.Light.Keys.OrderBy(k => k));
    }

    private static IEnumerable<string> XamlFiles() => Directory.GetFiles(DesktopDirectory, "*.xaml");

    private static HashSet<string> KnownResourceKeys()
    {
        var keys = DeclaredKeys(Path.Combine(DesktopDirectory, "App.xaml"));
        foreach (var file in XamlFiles()) keys.UnionWith(DeclaredKeys(file));
        foreach (var name in ThemePalette.Dark.Keys)
        {
            keys.Add(name);
            keys.Add(name + "Color");
        }
        return keys;
    }

    private static HashSet<string> DeclaredKeys(string xamlFile) =>
        Regex.Matches(File.ReadAllText(xamlFile), @"x:Key=""(\w+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string FindDesktopProject()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PcAssistant.slnx")))
                return Path.Combine(dir.FullName, "src", "PcAssistant.Desktop");

        throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
