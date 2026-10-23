using Microsoft.AspNetCore.Components;

namespace lumibelle.Services.AI;

public static class AiSettingsNavigation
{
    public static string Link(NavigationManager navigation, string? tab = null, Guid? projectId = null, string? returnTo = null)
    {
        var current = navigation.ToAbsoluteUri(navigation.Uri);
        if (current.AbsolutePath.Equals("/settings/ai", StringComparison.OrdinalIgnoreCase))
            return tab is null ? current.PathAndQuery + current.Fragment
                : navigation.GetUriWithQueryParameters(new Dictionary<string, object?> { ["tab"] = tab });

        var origin = ProjectReturn(current.PathAndQuery + current.Fragment)
            ?? LegacyReturn(projectId, returnTo);
        var query = new Dictionary<string, string?>
        {
            ["tab"] = tab,
            ["returnUrl"] = origin?.Url
        };
        var encoded = string.Join("&", query.Where(p => p.Value is not null).Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value!)));
        return "/settings/ai" + (encoded.Length == 0 ? "" : "?" + encoded);
    }

    public static (string Url, string Label) Return(string? returnUrl, Guid? projectId, string? returnTo) =>
        ProjectReturn(returnUrl) ?? LegacyReturn(projectId, returnTo) ?? ("/", "All projects");

    private static (string Url, string Label)? ProjectReturn(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("/projects/", StringComparison.OrdinalIgnoreCase)
            || url.Any(char.IsControl) || url.Contains('\\')) return null;
        var path = url.Split('?', '#')[0];
        if (path.Contains("//")) return null;
        path = path.TrimEnd('/');
        var parts = path.Split('/');
        if (parts.Length is < 3 or > 4 || string.IsNullOrWhiteSpace(parts[2]) || !ValidProjectKey(parts[2])) return null;
        var label = parts.Length == 3 ? "project" : parts[3].ToLowerInvariant() switch
        {
            "source" => "source", "script" => "script", "assets" => "assets", "shots" => "shots", "production" => "production", "cut" => "cut",
            "settings" => "project settings", _ => null
        };
        return label is null ? null : (url, $"Back to {label}");
    }

    private static bool ValidProjectKey(string value)
    {
        try { value = Uri.UnescapeDataString(value); } catch (UriFormatException) { return false; }
        if (Guid.TryParseExact(value, "D", out var id)) return id != Guid.Empty;
        return value.Length > 0 && value != "." && value != ".." && value.EnumerateRunes().All(c => System.Text.Rune.IsLetterOrDigit(c) || c.Value == '-');
    }

    private static (string Url, string Label)? LegacyReturn(Guid? projectId, string? returnTo)
    {
        if (projectId is not { } id || id == Guid.Empty) return null;
        var section = returnTo?.ToLowerInvariant() switch
        {
            "overview" => "", "assets" => "/assets", "shots" => "/shots", "production" => "/production", "cut" => "/cut",
            "settings" => "/settings", _ => "/script"
        };
        return ProjectReturn($"/projects/{id:D}{section}");
    }
}
