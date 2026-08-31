using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace VvCash.Tests;

/// <summary>Экраны кухни и табло должны выглядеть продолжением кассы, а не
/// чужой админкой. Без этого теста первый же правленый в Colors.axaml цвет
/// молча оставляет обе веб-страницы на старом значении: ни одна сборка не
/// смотрит одновременно на XAML-палитру приложения и на CSS-палитру
/// страниц — они расходятся тихо.</summary>
public class WebThemeTest
{
    /// <summary>Ключ Colors.axaml → CSS custom property. Минимум из задачи:
    /// primary, primary dark, primary light, background, text primary,
    /// text secondary, text muted, success, danger, border.</summary>
    private static readonly Dictionary<string, string> Mapping = new()
    {
        ["PrimaryColor"] = "--primary",
        ["PrimaryDarkColor"] = "--primary-dark",
        ["PrimaryLightColor"] = "--primary-light",
        ["BackgroundColor"] = "--background",
        ["TextPrimary"] = "--text-primary",
        ["TextSecondary"] = "--text-secondary",
        ["TextMuted"] = "--text-muted",
        ["SuccessColor"] = "--success",
        ["DangerColor"] = "--danger",
        ["BorderDarkColor"] = "--border"
    };

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    [Fact]
    public void TheWebPaletteMatchesTheApplicationPalette()
    {
        var colors = File.ReadAllText(Path.Combine(Root(), "src", "VvCash", "Assets", "Styles", "Colors.axaml"));
        var theme = File.ReadAllText(Path.Combine(Root(), "src", "VvCash", "Assets", "Web", "theme.css"));

        foreach (var (key, variable) in Mapping)
        {
            var expected = Regex.Match(colors, $"<Color x:Key=\"{key}\">(#[0-9a-fA-F]{{6}})</Color>").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(expected), $"{key} исчез из Colors.axaml");

            var actual = Regex.Match(theme, $@"{variable}:\s*(#[0-9a-fA-F]{{6}})\s*;").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(actual), $"{variable} исчез из theme.css");
            Assert.Equal(expected.ToLowerInvariant(), actual.ToLowerInvariant());
        }
    }

    /// <summary>Радиусы скруглений — из Controls.axaml, а не выдуманы заново.
    /// Без этого теста theme.css может тихо сползти на значения, которых в
    /// приложении не существует, и карточки на экранах перестанут читаться как
    /// та же касса.</summary>
    [Fact]
    public void TheCornerRadiiComeFromTheApplicationControls()
    {
        var controls = File.ReadAllText(Path.Combine(Root(), "src", "VvCash", "Assets", "Styles", "Controls.axaml"));
        var theme = File.ReadAllText(Path.Combine(Root(), "src", "VvCash", "Assets", "Web", "theme.css"));

        var usedInApp = new HashSet<string>(Regex.Matches(controls, @"CornerRadius""\s+Value=""(\d+)""")
            .Select(m => m.Groups[1].Value));
        var usedInTheme = Regex.Matches(theme, @"--radius-[sml]:\s*(\d+)px\s*;")
            .Select(m => m.Groups[1].Value);

        foreach (var radius in usedInTheme)
        {
            Assert.Contains(radius, usedInApp);
        }
    }

    /// <summary>Шрифт не должен зависеть от сети: касса-точка бывает офлайн, и
    /// экран обязан подняться в любом случае. @font-face с url(http...) или
    /// ссылка на fonts.googleapis.com — та самая тихая поломка, которую видно
    /// только на точке без интернета.</summary>
    [Fact]
    public void TheFontIsNotFetchedFromTheNetwork()
    {
        var theme = File.ReadAllText(Path.Combine(Root(), "src", "VvCash", "Assets", "Web", "theme.css"));

        Assert.DoesNotContain("@font-face", theme);
        Assert.DoesNotContain("googleapis", theme);
        Assert.DoesNotContain("http://", theme);
        Assert.DoesNotContain("https://", theme);
        Assert.Contains("Inter", theme);
    }
}
