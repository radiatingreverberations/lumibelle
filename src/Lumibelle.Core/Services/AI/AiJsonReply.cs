using System.Text;
using System.Text.Json;

namespace lumibelle.Services.AI;

/// <summary>
/// Local models sometimes reason aloud around a complete JSON answer, fenced or bare, and may keep going until the reply
/// limit cuts them off. Parsers that expect a reply made only of JSON use this as a fallback.
/// </summary>
internal static class AiJsonReply
{
    /// <summary>The latest complete JSON object in the reply that <paramref name="read"/> accepts, or null.</summary>
    public static T? Latest<T>(string raw, Func<string, T?> read) where T : class
    {
        // Scanning backwards finds the latest answer first; a nested object that is not an answer is skipped for the object around it.
        for (var start = raw.LastIndexOf('{'); start >= 0; start = start == 0 ? -1 : raw.LastIndexOf('{', start - 1))
            if (CompleteObject(raw, start) is { } json && read(json) is { } value) return value;
        return null;
    }

    private static string? CompleteObject(string text, int start)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text[start..]), new JsonReaderOptions { AllowMultipleValues = true });
        try
        {
            if (!JsonDocument.TryParseValue(ref reader, out var document)) return null;
            using (document) return document.RootElement.GetRawText();
        }
        catch (JsonException) { return null; }
    }
}
