using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.Assets;

public static partial class ReferenceReels
{
    // As for shots, the model supplies section prose. The application renders H3
    // headings, view numbering, generated duration and the captured dialogue.
    public static JsonElement ResponseSchemaFor(ReferenceReelDraft draft)
    {
        var text = new { type = "string", minLength = 1, maxLength = 100_000 };
        var sections = ProductionPolicy.Sections.ToDictionary(name => name, name => name == "detailed_description"
            ? (object)new { type = "array", minItems = 1, maxItems = 24, items = text,
                description = "Ordered camera shots. Use one entry for a continuous move, including its held viewpoints. Multiple entries mean cuts. No shot labels or dialogue." }
            : text);
        return JsonSerializer.SerializeToElement(new {
            type = "object", required = new[] { "sections", "useGuidance" }, additionalProperties = false,
            properties = new {
                sections = new { type = "object", properties = sections, required = ProductionPolicy.Sections, additionalProperties = false },
                useGuidance = new { type = "string", minLength = 1, maxLength = 20_000 }
            }
        });
    }

    private static void UseSectionedOutput(List<ChatMessage> messages, ReferenceReelDraft draft)
    {
        var contents = messages[0].Contents;
        for (var i = 0; i < contents.Count; i++)
        {
            if (contents[i] is not TextContent text) continue;
            var rules = Regex.Replace(text.Text,
                @"Return ordinary JSON[^.]*\. |(?:The prompt must have these six headings|Use six headings)[^.]*\. |Number views consecutively[^.]*\. |State the supplied generated duration exactly\. ", "");
            rules = rules.Replace("For speaking modes keep the exact language and line once as <d>[Language] line</d>, preceded on the same line by the character's exact name and (S1). ", "", StringComparison.Ordinal)
                .Replace("Number views consecutively starting with [Shot 1] in detailed_description. ", "", StringComparison.Ordinal);
            contents[i] = new TextContent(rules);
        }
        contents.Add(new TextContent("""
            Return exactly {"sections":{...},"useGuidance":"..."}. sections has exactly these six fields:
            subject_definitions, summary, retention_analysis, detailed_description, overall_soundscape, non_diegetic_music.
            All are nonempty prose strings except detailed_description, an array of 1 to 24 nonempty camera-shot descriptions in order.
            Use ONE array entry for a continuous camera move, keeping its pauses and changing viewpoints within that entry. Separate entries mean cuts.
            Lumibelle adds the six headings, consecutive [Shot N] labels and the exact total generated duration. Do not write those headings or shot labels in the values.
            Describe the supplied camera plan, timing, action and sound in the appropriate fields. Keep reference tags such as <Picture 1> exact.
            Lumibelle inserts the exact saved dialogue once, with its language and (S1) speaker label. Describe delivery and action but do not copy, translate, paraphrase or add spoken words or dialogue markup in any field.
            When speech is supplied, stage it as one continuous performance across any cuts, with no restart. Silent reels contain no speech.
            Use guidance describes intended later use, not verified output, and stays independent of reference numbering.
            """));
        messages[1].Contents.Add(new TextContent(JsonSerializer.Serialize(new {
            generatedSeconds = H3Policy.Seconds(draft.Duration),
            savedDialogue = Inputs(draft).Dialogue.Select(line => new { line.Speaker, line.Language, line.Text })
        }, AtomicJsonFile.Options)));
    }

    // Liberal like shot compositions: field names in any case or style, strings or lists, missing or extra fields.
    // Every adjustment is a review note, so the pair waits for its author; only a response without usable text is rejected.
    private static ReelPromptPair ReadSectionedPair(JsonElement root, JsonElement sections, ReferenceReelDraft draft)
    {
        List<string> notes = [];
        foreach (var property in root.EnumerateObject())
            if (!property.Name.Equals("sections", StringComparison.OrdinalIgnoreCase) && !property.Name.Equals("useGuidance", StringComparison.OrdinalIgnoreCase))
                notes.Add($"Ignored an unexpected response field, {property.Name}.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (sections.ValueKind == JsonValueKind.Object)
            foreach (var field in sections.EnumerateObject())
            {
                var name = PromptComposer.SectionName(field.Name);
                if (!ProductionPolicy.Sections.Contains(name)) { notes.Add($"Ignored an unexpected response field, {field.Name}."); continue; }
                if (fields.ContainsKey(name)) notes.Add($"The response repeated {name}; its last value is used.");
                fields[name] = field.Value;
            }
        var rendered = new List<string>();
        foreach (var name in ProductionPolicy.Sections)
        {
            string? body;
            if (name == "detailed_description")
            {
                var views = Views(fields.GetValueOrDefault(name), notes);
                if (views.Count == 0) { notes.Add("The response left out detailed_description. Describe the reel's views before using it."); continue; }
                var seconds = H3Policy.Seconds(draft.Duration).ToString("0.###", CultureInfo.InvariantCulture);
                body = $"Exactly {seconds} seconds total. " + (views.Count == 1 ? "One continuous camera take.\n\n" : "\n\n") +
                    string.Join("\n\n", views.Select((v, i) => $"[Shot {i + 1}] {v}"));
                foreach (var line in Inputs(draft).Dialogue)
                    body += $"\n\nThe character speaks once, continuously across any cuts without restarting. {line.Speaker.Trim()} (S1) says <d>[{line.Language}] {line.Text}</d>";
            }
            else body = fields.TryGetValue(name, out var value) ? PromptComposer.Text(value)?.Trim() : null;
            if (string.IsNullOrWhiteSpace(body)) { if (name != "detailed_description") notes.Add($"The response left out {name}. Add it if this reel needs it."); continue; }
            rendered.Add(name + ":\n" + body);
        }
        if (rendered.Count == 0) throw new JsonException();
        var guidance = PromptComposer.Field(root, "useGuidance") is { } g ? PromptComposer.Text(g)?.Trim() ?? "" : "";
        if (guidance.Length == 0) notes.Add("The response left out useGuidance. Explain how to use this reel.");
        return new(string.Join("\n\n", rendered), guidance) { Notes = notes.Count > 0 ? notes : null };
    }

    // The reel's views: a list as written, or one string as a single view; blank views dropped, at most 24.
    private static IReadOnlyList<string> Views(JsonElement value, List<string> notes)
    {
        var views = (value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(PromptComposer.Text) : [PromptComposer.Text(value)])
            .Select(v => v?.Trim()).ToArray();
        var usable = views.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
        if (value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Undefined or JsonValueKind.Null) && usable.Count > 0)
            notes.Add("detailed_description was one text instead of a list, so it is one view.");
        if (usable.Count < views.Length && value.ValueKind == JsonValueKind.Array) notes.Add("Dropped empty views from detailed_description.");
        if (usable.Count > 24) { notes.Add($"Kept the first 24 of {usable.Count} views."); usable = usable[..24]; }
        return usable;
    }
}
