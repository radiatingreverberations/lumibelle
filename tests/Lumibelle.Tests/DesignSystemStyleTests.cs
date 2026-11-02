using System.Text.RegularExpressions;
using lumibelle.Components.Layout;

namespace Lumibelle.Tests;

// The design system keeps colours, type sizes and radii in design-tokens.css. These tests keep feature
// styles on those tokens and keep MudBlazor's theme palette identical to the CSS palette.
public sealed partial class DesignSystemStyleTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Ui = Path.Combine(Root, "src", "Lumibelle.UI");
    private static readonly string Tokens = Path.Combine(Ui, "wwwroot", "design-tokens.css");

    [Fact]
    public void MudThemePaletteMatchesTheDesignTokens()
    {
        var css = File.ReadAllText(Tokens);
        var light = Declarations(Block(css, ":root"));
        var dark = Declarations(Block(css, "html[data-theme=\"dark\"]"));
        var theme = StudioTheme.Theme;
        void Same(Dictionary<string, string> tokens, string token, string? actual) =>
            Assert.True(string.Equals(tokens[token], actual, StringComparison.OrdinalIgnoreCase), $"{token}: CSS {tokens[token]}, theme {actual}");

        Same(light, "--lumi-accent", theme.PaletteLight.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(light, "--lumi-canvas", theme.PaletteLight.Background.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(light, "--lumi-surface", theme.PaletteLight.Surface.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(light, "--lumi-text", theme.PaletteLight.TextPrimary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(light, "--lumi-text-muted", theme.PaletteLight.TextSecondary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(light, "--lumi-border", theme.PaletteLight.Divider.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(light, "--lumi-control-border", theme.PaletteLight.LinesInputs.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(light, "--lumi-danger", theme.PaletteLight.Error.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(light, "--lumi-success", theme.PaletteLight.Success.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(light, "--lumi-success", theme.PaletteLight.Secondary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(light, "--lumi-warning", theme.PaletteLight.Warning.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(light, "--lumi-info", theme.PaletteLight.Info.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));

        Same(dark, "--lumi-accent", theme.PaletteDark.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(dark, "--lumi-on-accent", theme.PaletteDark.PrimaryContrastText.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(dark, "--lumi-canvas", theme.PaletteDark.Background.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(dark, "--lumi-surface", theme.PaletteDark.Surface.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(dark, "--lumi-text", theme.PaletteDark.TextPrimary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(dark, "--lumi-text-muted", theme.PaletteDark.TextSecondary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(dark, "--lumi-border", theme.PaletteDark.Divider.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(dark, "--lumi-control-border", theme.PaletteDark.LinesInputs.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(dark, "--lumi-danger", theme.PaletteDark.Error.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(dark, "--lumi-success", theme.PaletteDark.Success.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(dark, "--lumi-success", theme.PaletteDark.Secondary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(dark, "--lumi-warning", theme.PaletteDark.Warning.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Same(dark, "--lumi-info", theme.PaletteDark.Info.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex));
        Assert.Equal("6px", theme.LayoutProperties.DefaultBorderRadius);
        Assert.Equal("6px", light["--lumi-radius-control"]);
    }

    [Fact]
    public void FeatureStylesUseDesignTokens()
    {
        var problems = new List<string>();
        foreach (var file in StyleFiles())
        {
            var name = Path.GetRelativePath(Ui, file).Replace('\\', '/');
            var css = Comment().Replace(File.ReadAllText(file), "");
            foreach (var (property, value, line) in Properties(css))
            {
                var where = $"{name}:{line} {property}: {value}";
                if (HexColor().IsMatch(value)) problems.Add($"{where} — use a --lumi-* colour token");
                if (RgbColor().IsMatch(value)) problems.Add($"{where} — use a --lumi-* colour token (color-mix for tints)");
                if (value.Contains("--mud-palette-", StringComparison.Ordinal)) problems.Add($"{where} — use --lumi-* tokens, not MudBlazor palette variables");
                if (property == "font-size" && BadFontSize(value) is { } font) problems.Add($"{where} — {font}");
                if (property is "border-radius" or "border-top-left-radius" or "border-top-right-radius" or "border-bottom-left-radius" or "border-bottom-right-radius"
                    && !RadiusValue().IsMatch(value)) problems.Add($"{where} — use --lumi-radius-* tokens");
            }
        }
        Assert.True(problems.Count == 0, $"{problems.Count} style rules bypass the design tokens:\n" + string.Join("\n", problems));
    }

    private static string? BadFontSize(string value)
    {
        if (value.Contains("var(--lumi-font-size-", StringComparison.Ordinal) || value is "inherit" or "100%" or "1em") return null;
        if (Regex.Match(value, @"^(\d+(?:\.\d+)?)px$") is { Success: true } px)
            return double.Parse(px.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) < 13 ? "text must be at least 13px; use a --lumi-font-size-* token" : null;
        return "use a --lumi-font-size-* token";
    }

    private static IEnumerable<string> StyleFiles()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Ui, "wwwroot"), "*.css"))
            if (!file.Equals(Tokens, StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase)) yield return file;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Ui, "Components"), "*.razor.css", SearchOption.AllDirectories)) yield return file;
    }

    private static IEnumerable<(string Property, string Value, int Line)> Properties(string css)
    {
        foreach (Match m in Declaration().Matches(css))
            yield return (m.Groups[1].Value.Trim().ToLowerInvariant(), m.Groups[2].Value.Trim().Replace("!important", "").Trim(), css[..m.Index].Count(c => c == '\n') + 1);
    }

    private static string Block(string css, string selector)
    {
        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{selector} block not found");
        var open = css.IndexOf('{', start);
        return css[(open + 1)..css.IndexOf('}', open)];
    }

    private static Dictionary<string, string> Declarations(string block) =>
        Declaration().Matches(Comment().Replace(block, "")).ToDictionary(m => m.Groups[1].Value.Trim(), m => m.Groups[2].Value.Trim());

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "lumibelle.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)] private static partial Regex Comment();
    [GeneratedRegex(@"([a-zA-Z-]+)\s*:\s*([^;{}]+?)\s*(?=;|\})")] private static partial Regex Declaration();
    [GeneratedRegex(@"(?<![\w-])#[0-9a-fA-F]{3,8}\b")] private static partial Regex HexColor();
    [GeneratedRegex(@"\brgba?\(")] private static partial Regex RgbColor();
    [GeneratedRegex(@"^(?:(?:var\(--lumi-radius-[a-z]+\)|0|50%|inherit)\s*)+$")] private static partial Regex RadiusValue();
}
