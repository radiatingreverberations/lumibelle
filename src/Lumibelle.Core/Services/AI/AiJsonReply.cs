using System.Text;
using System.Text.Json;

namespace lumibelle.Services.AI;

/// <summary>
/// Local models sometimes reason aloud around a complete JSON answer, fenced or bare, and may keep going until the reply
/// limit cuts them off. Parsers that expect a reply made only of JSON use this as a fallback.
/// </summary>
internal static class AiJsonReply
{
    /// <summary>The latest complete top-level JSON object or array in the reply that <paramref name="read"/> accepts, or null.</summary>
    public static T? Latest<T>(string raw, Func<string, T?> read) where T : class
    {
        var values = TopLevelValues(raw);
        for (var i = values.Count - 1; i >= 0; i--)
            if (read(values[i]) is { } value) return value;
        return null;
    }

    // Values nested in another value are never candidates: an inner list such as "evidence": [] must not pass for an answer.
    private static List<string> TopLevelValues(string text)
    {
        var values = new List<string>();
        // Multi-byte UTF-8 sequences never contain '{' or '[' bytes, so scanning bytes finds the same starts as scanning characters.
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var start = 0; start < bytes.Length; start++)
        {
            if (bytes[start] is not ((byte)'{' or (byte)'[')) continue;
            var reader = new Utf8JsonReader(bytes.AsSpan(start), isFinalBlock: false, state: default);
            try
            {
                // A value the reply cuts off contains everything after its start, so nothing later is top level.
                if (!JsonDocument.TryParseValue(ref reader, out var document)) break;
                using (document) values.Add(document.RootElement.GetRawText());
                start += (int)reader.BytesConsumed - 1;
            }
            catch (JsonException) { /* Prose such as "[English]" is not a value; keep scanning. */ }
        }
        return values;
    }
}
