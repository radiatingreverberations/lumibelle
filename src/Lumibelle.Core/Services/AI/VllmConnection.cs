using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public static class VllmConnection
{
    public static void ValidateUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new WorkspaceStoreException("Enter an OpenAI-compatible HTTP or HTTPS server URL without credentials, a query, or a fragment.");
    }

    public static string NormalizeUrl(string url)
    {
        ValidateUrl(url);
        var uri = new Uri(url.Trim());
        var normalized = uri.AbsoluteUri.TrimEnd('/');
        return uri.AbsolutePath.Trim('/') == "" ? normalized + "/v1" : normalized;
    }
}
