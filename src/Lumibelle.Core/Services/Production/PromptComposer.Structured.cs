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
            }, Compact) +
            // Last in the request, where models without enforced schemas most reliably follow it.
            $"\nReturn detailed_description as a JSON array of exactly {shot.Dialogue.Count + 1} {(shot.Dialogue.Count == 0 ? "string" : "strings")}, never one string: {PassageShape(shot)}";
    }

    // The expected array for this shot, so the model sees where each saved line falls.
    private static string PassageShape(Shot shot)
    {
        var lines = shot.Dialogue.Select((line, index) => $"line {index + 1} ({line.Speaker.Trim()})").ToArray();
        if (lines.Length == 0) return """["the complete action of the shot"]""";
        var passages = Enumerable.Range(0, lines.Length + 1).Select(i =>
            i == 0 ? $"action before {lines[0]}" : i == lines.Length ? $"action after {lines[^1]}, to the end of the shot" : $"action between {lines[i - 1]} and {lines[i]}");
        return JsonSerializer.Serialize(passages, Compact);
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
            detailed_description contains exactly {shot.Dialogue.Count + 1} action passages, in order, as a JSON array of {shot.Dialogue.Count + 1} {(shot.Dialogue.Count == 0 ? "string" : "strings")}. It is never a single string, even when the action reads as one continuous paragraph. For this shot: "detailed_description": {PassageShape(shot)}. The supplied dialogue insertion plan lists the exact dialogue Lumibelle inserts AFTER each numbered passage; the last passage describes the remaining action and ending. An empty string is allowed only between consecutive lines, or as the last passage when a line ends the shot; never add extra passages. With no dialogue, write the complete action as the array's single string. Use the supplied lines to stage speech, reactions and physical performance naturally, but do not copy, translate or paraphrase dialogue into any section. Do not write dialogue tags, language brackets or speaker prefixes in the action passages. Lumibelle renders the exact saved text, language and stable speaker number. In subject_definitions, include the authored speaker names and their supplied (S1), (S2) mappings. Keep ambience and physical sound in overall_soundscape, and music in non_diegetic_music.
            """);
        return text.Replace("Include [Shot 1] and explicitly state one continuous take and the generated duration. Do not add other shot labels or cut timestamps.",
                "Lumibelle adds the [Shot 1] label, one-continuous-take statement and exact generated duration. Do not repeat them or add other shot labels or cut timestamps.", StringComparison.Ordinal)
            .Replace("Before returning JSON, verify that all six headings are present exactly once and in order.",
                "Before returning JSON, complete all six section fields and check that detailed_description is a JSON array with exactly the required number of strings.", StringComparison.Ordinal)
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

    // The rendered H3 prompt is input to another model, not a strict template. Usable responses are accepted
    // with review notes for anything adjusted; only a response with no usable section text is rejected.
    private static PromptCompositionResult ReadSectionedComposition(JsonElement root, JsonElement sections, Shot shot)
    {
        List<string> notes = [];
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (sections.ValueKind == JsonValueKind.Object)
            foreach (var field in sections.EnumerateObject())
            {
                var name = SectionName(field.Name);
                if (!ProductionPolicy.Sections.Contains(name)) { notes.Add($"Ignored an unexpected response field, {field.Name}."); continue; }
                if (fields.ContainsKey(name)) notes.Add($"The response repeated {name}; its last value is used.");
                fields[name] = field.Value;
            }
        var rendered = new List<string>();
        foreach (var name in ProductionPolicy.Sections)
        {
            var value = fields.GetValueOrDefault(name);
            if (name == "detailed_description") { rendered.Add(name + ":\n" + RenderActionPassages(value, shot, notes)); continue; }
            var body = Text(value)?.Trim();
            if (string.IsNullOrWhiteSpace(body) && name is "overall_soundscape" or "non_diegetic_music" &&
                (name == "overall_soundscape" ? shot.Atmosphere : shot.Music) is { Length: > 0 } direction && !string.IsNullOrWhiteSpace(direction))
            {
                body = direction.Trim(); notes.Add($"The response left out {name}, so the shot's own sound direction was used.");
            }
            if (string.IsNullOrWhiteSpace(body)) { notes.Add($"The response left out {name}. Add it if this shot needs it."); continue; }
            rendered.Add(name + ":\n" + body);
        }
        if (!fields.Values.Any(v => !string.IsNullOrWhiteSpace(Text(v))))
            throw new WorkspaceStoreException("The response has no usable prompt sections. Inspect it and retry; the accepted prompt is unchanged.");
        var usage = Field(root, "referenceUsage") is { } explanation ? Text(explanation)?.Trim() ?? "" : "";
        return new(string.Join("\n\n", rendered), usage) { Notes = notes };
    }

    // detailedDescription, Detailed-Description and "detailed description" all name detailed_description.
    internal static string SectionName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.Trim(), "(?<=[a-z0-9])(?=[A-Z])", "_").ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    // A top-level response field in any letter case.
    internal static JsonElement? Field(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in root.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    // Text from whatever shape a model used: strings as written, lists and objects joined by line.
    internal static string? Text(JsonElement value) => value.ValueKind switch {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Array => string.Join("\n", value.EnumerateArray().Select(Text).Where(t => !string.IsNullOrWhiteSpace(t))),
        JsonValueKind.Object => string.Join("\n", value.EnumerateObject().Select(p => Text(p.Value)).Where(t => !string.IsNullOrWhiteSpace(t))),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        _ => null
    };

    // Saved dialogue is always rendered exactly and in order. When the passages do not match its slots,
    // extra passages join the last one and missing ones leave the remaining lines after the last passage.
    private static string RenderActionPassages(JsonElement value, Shot shot, List<string> notes)
    {
        if (shot.Duration is null) throw new WorkspaceStoreException("Set a shot duration before composing.");
        var expected = shot.Dialogue.Count + 1;
        var texts = value.ValueKind switch {
            JsonValueKind.Array => value.EnumerateArray().Select(p => Text(p) ?? "").ToList(),
            JsonValueKind.Undefined or JsonValueKind.Null => [],
            _ => [Text(value) ?? ""]
        };
        if (value.ValueKind is not (JsonValueKind.Array or JsonValueKind.String or JsonValueKind.Undefined or JsonValueKind.Null))
            notes.Add("detailed_description was not a list of passages; its text is used as one passage.");
        // Trailing empty passages carry nothing and cannot shift dialogue, so extras there are dropped.
        while (texts.Count > expected && string.IsNullOrWhiteSpace(texts[^1])) texts.RemoveAt(texts.Count - 1);
        if (texts.Count > expected)
        {
            if (shot.Dialogue.Count > 0)
                notes.Add($"The response gave {Plural(texts.Count, "action passage")} for {expected} slots; the extra ones were joined into the last passage. Check where the dialogue falls.");
            texts = [.. texts.Take(expected - 1), string.Join("\n\n", texts.Skip(expected - 1).Where(t => !string.IsNullOrWhiteSpace(t)))];
        }
        else if (texts.Count < expected && shot.Dialogue.Count > 0)
        {
            notes.Add(texts.Count <= 1
                ? "The response gave one block of action, so the saved dialogue follows it. Check where each line falls, or move the lines in the prompt."
                : $"The response gave {Plural(texts.Count, "action passage")} for {expected} slots, so the remaining dialogue follows the last passage. Check where each line falls.");
        }
        while (texts.Count < expected) texts.Add("");
        if (texts.All(string.IsNullOrWhiteSpace)) notes.Add("The response left out detailed_description. Describe the action before generating.");
        var duration = H3Policy.Seconds(shot.Duration.Value).ToString("0.###", CultureInfo.InvariantCulture);
        var passages = new List<string> { $"[Shot 1] One continuous camera take lasting {duration} seconds, without camera cuts or scene transitions." };
        var speakers = SpeakerNames(shot);
        var index = 0;
        foreach (var passage in texts)
        {
            if (!string.IsNullOrWhiteSpace(passage)) passages.Add(passage.Trim());
            if (index < shot.Dialogue.Count)
            {
                var line = shot.Dialogue[index];
                passages.Add($"{line.Speaker.Trim()} (S{SpeakerNumber(speakers, line.Speaker)}) says <d>[{line.Language}] {line.Text}</d>");
            }
            index++;
        }
        return string.Join("\n\n", passages);
    }

    private static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
