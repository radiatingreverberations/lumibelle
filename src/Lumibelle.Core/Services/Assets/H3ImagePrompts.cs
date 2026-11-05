using System.Globalization;
using System.Text.RegularExpressions;

namespace lumibelle.Services.Assets;

// The image studio names inputs "Image 1"; the H3 encoder expects "<Picture 1>" in the same order.
// Metadata keeps the author's wording, so only the submitted graph carries the compiled form.
public static partial class H3ImagePrompts
{
    public static string Compile(string prompt, int references) => Reference().Replace(prompt, match =>
    {
        // Quoted text is matched first and returned as written: it may be visible lettering.
        var number = match.Groups["angle"].Success ? match.Groups["angle"] : match.Groups["plain"];
        return number.Success && int.TryParse(number.Value, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= references
            ? $"<Picture {n}>" : match.Value;
    });

    [GeneratedRegex("""
        "(?:\\.|[^"\\])*"|“[^”]*”|‘[^’]*’|(?<![\p{L}\p{N}_])'[^'\r\n]*'(?![\p{L}\p{N}_])|<image\s?(?<angle>\d+)>|(?<![\p{L}\p{N}_<])image\s?(?<plain>\d+)(?![\p{L}\p{N}_])
        """, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Reference();
}
