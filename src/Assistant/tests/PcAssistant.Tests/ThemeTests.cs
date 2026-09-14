using PcAssistant.Desktop;
using Xunit;

namespace PcAssistant.Tests;

public class ThemeTests
{
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void PrimaryAndSecondaryTextRemainReadableAcrossControlSurfaces(AppTheme theme)
    {
        var p = ThemePalette.For(theme);
        foreach (var surface in new[] { "Bg", "BgPanel", "BgRaised", "BgInput", "ControlHover", "AccentSoft" })
        {
            Assert.True(Contrast(p["Fg"], p[surface]) >= 4.5, $"{theme} Fg on {surface}");
            Assert.True(Contrast(p["FgMuted"], p[surface]) >= 3.0, $"{theme} FgMuted on {surface}");
        }
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void AccentButtonsAndStatusColorsHaveReadableContrast(AppTheme theme)
    {
        var p = ThemePalette.For(theme);
        Assert.True(Contrast("#FFFFFF", p["Accent"]) >= 4.5, $"{theme} accent button");
        Assert.True(Contrast("#FFFFFF", p["AccentHover"]) >= 4.5, $"{theme} accent button hover");
        Assert.True(Contrast("#FFFFFF", p["AccentPressed"]) >= 4.5, $"{theme} accent button pressed");
        Assert.True(Contrast(p["Danger"], p["Bg"]) >= 4.5, $"{theme} danger on background");
        Assert.True(Contrast(p["Ok"], p["Bg"]) >= 4.5, $"{theme} success on background");
    }

    private static double Contrast(string a, string b)
    {
        static double Luminance(string hex)
        {
            var rgb = new[] { 1, 3, 5 }.Select(i => Convert.ToInt32(hex.Substring(i, 2), 16) / 255d)
                .Select(c => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4)).ToArray();
            return 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2];
        }
        var x = Luminance(a); var y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}
