using System.Text.Json;

namespace lumibelle.Services.AI;

// Read the installed schema rather than guessing capabilities from version strings.
internal static class ComfyNodeCatalog
{
    public static bool Input(JsonElement catalog, string node, string name, out JsonElement spec)
    {
        spec = default;
        if (catalog.ValueKind != JsonValueKind.Object || !catalog.TryGetProperty(node, out var info) ||
            !info.TryGetProperty("input", out var inputs)) return false;
        foreach (var group in new[] { "required", "optional" })
            if (inputs.TryGetProperty(group, out var fields) && fields.TryGetProperty(name, out spec)) return true;
        return false;
    }

    public static IReadOnlyList<string> Choices(JsonElement catalog, string node, string name)
    {
        if (!Input(catalog, node, name, out var spec) || spec.ValueKind != JsonValueKind.Array || spec.GetArrayLength() == 0) return [];
        var values = spec[0];
        if (values.ValueKind != JsonValueKind.Array && spec.GetArrayLength() > 1 && spec[1].ValueKind == JsonValueKind.Object &&
            spec[1].TryGetProperty("options", out var options)) values = options;
        return values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray() : [];
    }

    public static bool Output(JsonElement catalog, string node, string type) => catalog.TryGetProperty(node, out var info) &&
        info.TryGetProperty("output", out var outputs) && outputs.ValueKind == JsonValueKind.Array &&
        outputs.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == type);

    public static bool OutputAt(JsonElement catalog, string node, int index, string type) => catalog.TryGetProperty(node, out var info) &&
        info.TryGetProperty("output", out var outputs) && outputs.ValueKind == JsonValueKind.Array && outputs.GetArrayLength() > index &&
        outputs[index].ValueKind == JsonValueKind.String && outputs[index].GetString() == type;

    public static bool Choice(JsonElement catalog, string node, string input, string choice) =>
        Input(catalog, node, input, out var spec) && Contains(spec, choice);

    public static bool References(JsonElement catalog, string node, int count, string prefix = "image_", int first = 0)
    {
        if (Enumerable.Range(first, count).All(i => Input(catalog, node, $"images.{prefix}{i}", out var spec) && Contains(spec, "IMAGE"))) return true;
        if (!Input(catalog, node, "images", out var grow) || grow.ValueKind != JsonValueKind.Array || grow.GetArrayLength() < 2 ||
            grow[1].ValueKind != JsonValueKind.Object || !grow[1].TryGetProperty("template", out var template) || !Contains(template, "IMAGE")) return false;
        if (template.TryGetProperty("names", out var names)) return Enumerable.Range(first, count).All(i => Contains(names, prefix + i));
        return first == 0 && template.TryGetProperty("prefix", out var declared) && declared.GetString() == prefix &&
            template.TryGetProperty("max", out var max) && max.TryGetInt32(out var maximum) && maximum >= count;
    }

    private static bool Contains(JsonElement value, string text, int depth = 0) => depth < 16 && (value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() == text,
        JsonValueKind.Object => value.EnumerateObject().Any(p => Contains(p.Value, text, depth + 1)),
        JsonValueKind.Array => value.EnumerateArray().Any(v => Contains(v, text, depth + 1)),
        _ => false
    });
}
