using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using lumibelle.Models;

namespace lumibelle.Services.Story;

public sealed record ScreenplayJsonResult(List<ScriptBlock>? Blocks, string? Error, IReadOnlyList<string>? NormalizationNotes = null);

public static class ScreenplayJson
{
    public static ScreenplayJsonResult Parse(string output)
    {
        var json = output;
        var lines = json.Split('\n');
        var fences = Enumerable.Range(0, lines.Length)
            .Where(i => lines[i].Trim().StartsWith("```", StringComparison.Ordinal)).ToArray();
        var lineOffset = 0;
        if (fences.Length > 0)
        {
            if (fences.Length != 2) return new(null, "Expected one complete JSON code block with an opening and closing fence.");
            var opening = lines[fences[0]].Trim();
            if (opening != "```" && !opening.Equals("```json", StringComparison.OrdinalIgnoreCase) || lines[fences[1]].Trim() != "```")
                return new(null, "Use a single JSON code block, or paste the JSON array without code fences.");
            lineOffset = fences[0] + 1;
            json = string.Join('\n', lines[lineOffset..fences[1]]);
        }
        try
        {
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Array) return new(null, "Expected a JSON array of screenplay blocks: [{\"kind\":\"Action\",\"spans\":[{\"text\":\"A kettle whistles.\"}]}].");
            if (parsed.RootElement.GetArrayLength() == 0) return new(null, "The screenplay array is empty. Add at least one block.");
            var speakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in parsed.RootElement.EnumerateArray()) {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String) continue;
                var cue = VoiceCue(k.GetString()!);
                if (cue is not null) speakers.Add(cue);
                if (k.GetString()!.Equals("Character", StringComparison.OrdinalIgnoreCase) && item.TryGetProperty("spans", out var s) && s.ValueKind == JsonValueKind.Array) {
                    var name = string.Concat(s.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String).Select(x => x.GetProperty("text").GetString()));
                    if (Name(name)) speakers.Add(VoiceCue(name) ?? name.Trim());
                }
            }
            List<ScriptBlock> blocks = [];
            var inputNumber = 0; var normalizedSpeakers = 0;
            foreach (var block in parsed.RootElement.EnumerateArray())
            {
                var label = $"Block {++inputNumber}";
                if (block.ValueKind != JsonValueKind.Object) return new(null, $"{label}: expected an object with \"kind\" and \"spans\".");
                if (!block.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
                    return new(null, $"{label}: \"kind\" must be Act, Scene, Action, Character, Dialogue, Parenthetical, or Transition.");
                var standardKind = Enum.TryParse<ScriptBlockKind>(kind.GetString(), true, out var value) && Enum.IsDefined(value);
                var speaker = kind.GetString()!.Trim();
                if (!standardKind && VoiceCue(speaker) is null && (!Name(speaker) || !speakers.Contains(speaker)))
                    return new(null, $"{label}: \"kind\" must be Act, Scene, Action, Character, Dialogue, Parenthetical, or Transition.");
                if (!block.TryGetProperty("spans", out var spans) || spans.ValueKind != JsonValueKind.Array)
                    return new(null, $"{label}: missing or invalid \"spans\". Expected an array of text spans.");
                List<ScriptSpan> content = [];
                foreach (var span in spans.EnumerateArray())
                {
                    var spanLabel = $"{label}, span {content.Count + 1}";
                    if (span.ValueKind != JsonValueKind.Object || !span.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                        return new(null, $"{spanLabel}: missing or invalid \"text\". Expected a string.");
                    foreach (var mark in new[] { "bold", "italic" })
                        if (span.TryGetProperty(mark, out var flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            return new(null, $"{spanLabel}: \"{mark}\" must be true or false.");
                    content.Add(new(text.GetString()!, span.TryGetProperty("bold", out var bold) && bold.GetBoolean(),
                        span.TryGetProperty("italic", out var italic) && italic.GetBoolean()));
                }
                if (string.IsNullOrWhiteSpace(string.Concat(content.Select(s => s.Text)))) return new(null, $"{label}: text must not be empty.");
                if (!standardKind) {
                    blocks.Add(ScriptBlock.Create(ScriptBlockKind.Character, speaker.ToUpperInvariant()));
                    blocks.Add(new() { Kind = ScriptBlockKind.Dialogue, Spans = content }); normalizedSpeakers++;
                } else blocks.Add(new() { Kind = value, Spans = content });
            }
            ScriptStructure.ValidateBlocks(blocks);
            return new(blocks, null, normalizedSpeakers == 0 ? null : [$"Converted {normalizedSpeakers} speaker-labelled blocks into Character and Dialogue blocks. The original response is preserved."]);
        }
        catch (JsonException e)
        {
            var line = (int)(e.LineNumber ?? 0);
            var bytes = Encoding.UTF8.GetBytes(json.Split('\n').ElementAtOrDefault(line) ?? "");
            var column = Encoding.UTF8.GetCharCount(bytes.AsSpan(0, (int)Math.Min(e.BytePositionInLine ?? 0, bytes.Length))) + 1;
            var reason = e.Message.Split(" LineNumber:")[0].TrimEnd(' ', '|');
            return new(null, $"Invalid JSON at line {line + lineOffset + 1}, column {column}: {reason}");
        }
        catch (WorkspaceStoreException e) { return new(null, e.Message); }
    }

    private static bool Name(string text) => text.Length is > 0 and <= 80 && Regex.IsMatch(text, @"^[\p{L}][\p{L}\p{M}\d .'’-]*(?:\s*\((?:V\.?O\.?|O\.?S\.?)\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string? VoiceCue(string text)
    {
        if (!Name(text)) return null;
        var index = text.LastIndexOf('(');
        return index > 0 ? text[..index].Trim() : null;
    }

    public static string Rejection(string? detail) => $"{detail ?? "The response was not a valid screenplay."} Your script is unchanged. You can paste corrected JSON and review it again.";
}
