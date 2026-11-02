using System.Buffers;
using System.Text;
using System.Text.Json;

namespace lumibelle.Services.Storage;

/// <summary>A top-level array of a stored document whose elements are kept as one row each.</summary>
/// <param name="Name">The array's JSON property name in the document.</param>
/// <param name="Key">The element property that identifies a row; null, missing or duplicate keys fall back to positions.</param>
/// <param name="Detached">Large element properties kept in separate rows and loaded only on request.</param>
internal sealed record DocumentList(string Name, string? Key = "id", IReadOnlyList<DetachedProperty>? Detached = null)
{
    public string DetachedList => Name + ".detached";
    public bool HasDetached => Detached is { Count: > 0 };

    /// <summary>The JSON object that stands in for the detached properties of a summary element.</summary>
    public string EmptyDetached => !HasDetached ? "{}" :
        "{" + string.Join(",", Detached!.Select(p => JsonSerializer.Serialize(p.Name) + ":" + p.EmptyJson)) + "}";
}

/// <param name="Name">The element's JSON property name.</param>
/// <param name="EmptyJson">The value it has in a summary, such as <c>[]</c> or <c>null</c>.</param>
internal sealed record DetachedProperty(string Name, string EmptyJson);

internal sealed record DocumentRow(string Id, string Payload);

/// <summary>A document split into a header (all other properties) and rows.</summary>
internal sealed class DecomposedDocument
{
    public required string Header { get; init; }
    /// <summary>Lists that were arrays in the document, in document order. Absent or null lists stay as they were.</summary>
    public required IReadOnlyList<string> Lists { get; init; }
    public required Dictionary<string, List<DocumentRow>> Rows { get; init; }
    /// <summary>Detached properties per list and element id, only for elements that carry them.</summary>
    public required Dictionary<string, Dictionary<string, string>> Detached { get; init; }
}

internal static class DocumentJson
{
    public static DecomposedDocument Decompose(object document, IReadOnlyList<DocumentList> lists)
    {
        var bytes = document is JsonElement element
            ? JsonSerializer.SerializeToUtf8Bytes(element, ProjectJson.Compact)
            : JsonSerializer.SerializeToUtf8Bytes(document, document.GetType(), ProjectJson.Compact);
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("A stored document must be a JSON object.");
        var header = new ArrayBufferWriter<byte>();
        List<string> present = [];
        var rows = lists.ToDictionary(l => l.Name, _ => new List<DocumentRow>(), StringComparer.Ordinal);
        var detached = lists.Where(l => l.HasDetached).ToDictionary(l => l.Name, _ => new Dictionary<string, string>(StringComparer.Ordinal), StringComparer.Ordinal);
        using (var writer = new Utf8JsonWriter(header))
        {
            writer.WriteStartObject();
            foreach (var property in json.RootElement.EnumerateObject())
            {
                var list = lists.FirstOrDefault(l => l.Name == property.Name);
                if (list is null || property.Value.ValueKind != JsonValueKind.Array) { property.WriteTo(writer); continue; }
                present.Add(list.Name);
                var elements = property.Value.EnumerateArray().ToList();
                var keys = Keys(elements, list.Key);
                for (var i = 0; i < elements.Count; i++)
                {
                    if (!list.HasDetached || elements[i].ValueKind != JsonValueKind.Object) { rows[list.Name].Add(new(keys[i], elements[i].GetRawText())); continue; }
                    var (main, extra) = Split(elements[i], list.Detached!);
                    rows[list.Name].Add(new(keys[i], main));
                    if (extra is not null) detached[list.Name][keys[i]] = extra;
                }
            }
            writer.WriteEndObject();
        }
        return new() { Header = Encoding.UTF8.GetString(header.WrittenSpan), Lists = present, Rows = rows, Detached = detached };
    }

    /// <summary>Rebuilds the document's JSON from its header and the elements of each present list.</summary>
    public static byte[] Compose(string header, IReadOnlyList<string> lists, Func<string, IEnumerable<string>> elements, int capacity = 0)
    {
        var buffer = new ArrayBufferWriter<byte>(Math.Max(capacity, 256));
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true }))
        using (var parsed = JsonDocument.Parse(header))
        {
            writer.WriteStartObject();
            foreach (var property in parsed.RootElement.EnumerateObject()) property.WriteTo(writer);
            foreach (var list in lists)
            {
                writer.WritePropertyName(list);
                writer.WriteStartArray();
                foreach (var element in elements(list)) writer.WriteRawValue(element, skipInputValidation: true);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Adds an object's properties to an element object, both compact JSON.</summary>
    public static string Merge(string element, string extra) =>
        extra.Length <= 2 ? element : element.Length <= 2 ? extra : string.Concat(element.AsSpan(0, element.Length - 1), ",", extra.AsSpan(1));

    public static string Hash(string payload) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    private static string[] Keys(List<JsonElement> elements, string? key)
    {
        var keys = new string[elements.Count];
        if (key is not null)
        {
            HashSet<string> seen = new(StringComparer.Ordinal);
            var usable = true;
            for (var i = 0; i < elements.Count && usable; i++)
            {
                var value = elements[i].ValueKind == JsonValueKind.Object && elements[i].TryGetProperty(key, out var k)
                    ? k.ValueKind switch { JsonValueKind.String => k.GetString(), JsonValueKind.Number => k.GetRawText(), _ => null } : null;
                // Ids never start with '#', so positional keys can't collide with natural ones.
                usable = !string.IsNullOrEmpty(value) && !value.StartsWith('#') && seen.Add(value);
                if (usable) keys[i] = value!;
            }
            if (usable) return keys;
        }
        for (var i = 0; i < elements.Count; i++) keys[i] = "#" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        return keys;
    }

    private static (string Main, string? Extra) Split(JsonElement element, IReadOnlyList<DetachedProperty> detached)
    {
        var main = new ArrayBufferWriter<byte>();
        var extra = new ArrayBufferWriter<byte>();
        var carries = false;
        using (var mainWriter = new Utf8JsonWriter(main))
        using (var extraWriter = new Utf8JsonWriter(extra))
        {
            mainWriter.WriteStartObject(); extraWriter.WriteStartObject();
            foreach (var property in element.EnumerateObject())
            {
                if (!detached.Any(d => d.Name == property.Name)) { property.WriteTo(mainWriter); continue; }
                property.WriteTo(extraWriter);
                // Empty values are what a summary carries; they mean "unchanged" when written back.
                carries |= property.Value.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => false,
                    JsonValueKind.Array => property.Value.GetArrayLength() > 0,
                    _ => true
                };
            }
            mainWriter.WriteEndObject(); extraWriter.WriteEndObject();
        }
        return (Encoding.UTF8.GetString(main.WrittenSpan), carries ? Encoding.UTF8.GetString(extra.WrittenSpan) : null);
    }
}
