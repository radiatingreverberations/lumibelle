using System.Globalization;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public static partial class PromptComposer
{
    // The model writes prose, not H3 syntax. An action passage precedes each saved dialogue line;
    // the final passage follows the last line. A silent shot has one passage.
    public static JsonElement ResponseSchemaFor(Shot shot)
    {
        var text = new { type = "string", minLength = 1 };
        var sections = ProductionPolicy.Sections.ToDictionary(name => name, name => name == "detailed_description"
            ? (object)new { type = "array", minItems = shot.Dialogue.Count + 1, maxItems = shot.Dialogue.Count + 1,
                items = new { type = "string" }, description = "Action passages interleaved with the supplied dialogue insertion plan, in order. Do not repeat dialogue or write H3 tags." }
            : text);
        return JsonSerializer.SerializeToElement(new { anyOf = new object[] {
            new { type = "object", properties = new {
                sections = new { type = "object", properties = sections, required = ProductionPolicy.Sections, additionalProperties = false },
                referenceUsage = text }, required = new[] { "sections", "referenceUsage" }, additionalProperties = false },
            new { type = "object", properties = new { needsInput = text }, required = new[] { "needsInput" }, additionalProperties = false }
        } });
    }

    private static string[] SpeakerNames(Shot shot) => shot.Dialogue.Select(line => line.Speaker.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static int SpeakerNumber(string[] speakers, string speaker) => Array.FindIndex(speakers, name => name.Equals(speaker.Trim(), StringComparison.OrdinalIgnoreCase)) + 1;

    private static string DialogueInsertionPlan(Shot shot)
    {
        var speakers = SpeakerNames(shot);
        return "Dialogue insertion plan (Lumibelle inserts these saved lines; write only the surrounding action passages):\n" +
            JsonSerializer.Serialize(new {
                actionPassages = shot.Dialogue.Count + 1,
                insertions = shot.Dialogue.Select((line, index) => new {
                    afterPassage = index + 1, speaker = line.Speaker.Trim(), speakerLabel = $"(S{SpeakerNumber(speakers, line.Speaker)})",
                    language = line.Language, text = line.Text
                })
            }, Compact);
    }

    private static string StructuredInstructions(string instructions, Shot shot)
    {
        var text = instructions.ReplaceLineEndings("\n");
        text = ReplaceParagraph(text, "Return one complete JSON object", """
            Return one complete JSON object with exactly two fields: "sections" and "referenceUsage". "sections" is an object with the six required fields below. "referenceUsage" is a short string explaining each supplied reference's contribution and any important limitations. If an essential reference conflict cannot reasonably be resolved, return only {"needsInput":"one concise explanation"} instead.
            """);
        text = ReplaceParagraph(text, "The prompt contains these six labelled sections", """
            The sections object has exactly these fields: subject_definitions, summary, retention_analysis, detailed_description, overall_soundscape, non_diegetic_music. All are nonempty strings except detailed_description, which is an array of action-passage strings. Lumibelle adds the section headings and their order. Do not put headings inside the values. Give explicit sound and music directions, including silence or no music where intended.
            """);
        text = ReplaceParagraph(text, "Use dialogue exactly once and in order", $"""
            detailed_description contains exactly {shot.Dialogue.Count + 1} action passages, in order. The supplied dialogue insertion plan lists the exact dialogue Lumibelle inserts AFTER each numbered passage; the last passage describes the remaining action and ending. Empty passages are allowed for consecutive lines or a line that ends the shot. With no dialogue, write the complete action in the single passage. Use the supplied lines to stage speech, reactions and physical performance naturally, but do not copy, translate or paraphrase dialogue into any section. Do not write dialogue tags, language brackets or speaker prefixes in the action passages. Lumibelle renders the exact saved text, language and stable speaker number. In subject_definitions, include the authored speaker names and their supplied (S1), (S2) mappings. Keep ambience and physical sound in overall_soundscape, and music in non_diegetic_music.
            """);
        return text.Replace("Include [Shot 1] and explicitly state one continuous take and the generated duration. Do not add other shot labels or cut timestamps.",
                "Lumibelle adds the [Shot 1] label, one-continuous-take statement and exact generated duration. Do not repeat them or add other shot labels or cut timestamps.", StringComparison.Ordinal)
            .Replace("Before returning JSON, verify that all six headings are present exactly once and in order.",
                "Before returning JSON, complete all six section fields.", StringComparison.Ordinal)
            .Replace("State generatedDurationSeconds using digits followed by 'seconds'.", "Lumibelle renders generatedDurationSeconds from the captured frame count.", StringComparison.Ordinal);
    }

    private static string ReplaceParagraph(string text, string start, string replacement)
    {
        var begin = text.IndexOf(start, StringComparison.Ordinal);
        if (begin < 0) throw new WorkspaceStoreException("The structured composition instructions are unavailable.");
        var end = text.IndexOf("\n\n", begin, StringComparison.Ordinal);
        if (end < 0) end = text.Length;
        return text[..begin] + replacement + text[end..];
    }

    private static PromptCompositionResult ReadSectionedComposition(JsonElement root, JsonElement sections, Shot shot)
    {
        if (root.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "referenceUsage", "sections" }) is false ||
            sections.ValueKind != JsonValueKind.Object ||
            !sections.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(ProductionPolicy.Sections.Order()) ||
            !root.TryGetProperty("referenceUsage", out var usage) || usage.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(usage.GetString()))
            throw new WorkspaceStoreException("The response needs all six section fields and referenceUsage. Inspect it and retry; the accepted prompt is unchanged.");
        var rendered = new List<string>();
        foreach (var name in ProductionPolicy.Sections)
        {
            var value = sections.GetProperty(name);
            string body;
            if (name == "detailed_description") body = RenderActionPassages(value, shot);
            else
            {
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    throw new WorkspaceStoreException($"Complete the {name} section field before using this composition.");
                body = value.GetString()!.Trim();
            }
            rendered.Add(name + ":\n" + body);
        }
        return new(string.Join("\n\n", rendered), usage.GetString()!);
    }

    private static string RenderActionPassages(JsonElement value, Shot shot)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != shot.Dialogue.Count + 1 ||
            value.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String))
            throw new WorkspaceStoreException($"The composition needs {shot.Dialogue.Count + 1} action passages around the saved dialogue. Inspect it and retry.");
        if (shot.Duration is null) throw new WorkspaceStoreException("Set a shot duration before composing.");
        var duration = H3Policy.Seconds(shot.Duration.Value).ToString("0.###", CultureInfo.InvariantCulture);
        var passages = new List<string> { $"[Shot 1] One continuous camera take lasting {duration} seconds, without camera cuts or scene transitions." };
        var speakers = SpeakerNames(shot);
        var index = 0;
        foreach (var passage in value.EnumerateArray())
        {
            if (!string.IsNullOrWhiteSpace(passage.GetString())) passages.Add(passage.GetString()!.Trim());
            if (index < shot.Dialogue.Count)
            {
                var line = shot.Dialogue[index];
                passages.Add($"{line.Speaker.Trim()} (S{SpeakerNumber(speakers, line.Speaker)}) says <d>[{line.Language}] {line.Text}</d>");
            }
            index++;
        }
        return string.Join("\n\n", passages);
    }
}
