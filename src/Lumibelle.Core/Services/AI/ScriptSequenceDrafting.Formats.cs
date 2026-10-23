using System.Text.Json;
using System.Text.Json.Nodes;

namespace lumibelle.Services.AI;

public static partial class ScriptSequenceDrafting
{
    public const string DraftGrammar = """
        root ::= "{" ws "\"scenes\"" colon "[" ws scene (comma scene)* ws "]" ws "}"
        scene ::= "{" ws "\"heading\"" colon string comma "\"beatIds\"" colon strings comma "\"blocks\"" colon "[" ws block (comma block)* ws "]" ws "}"
        block ::= "{" ws "\"kind\"" colon kind comma "\"spans\"" colon "[" ws span (comma span)* ws "]" ws "}"
        kind ::= "\"Action\"" | "\"Character\"" | "\"Dialogue\"" | "\"Parenthetical\"" | "\"Transition\""
        span ::= "{" ws "\"text\"" colon string (comma "\"bold\"" colon boolean)? (comma "\"italic\"" colon boolean)? ws "}"
        boolean ::= "true" | "false"
        strings ::= "[" ws (string (comma string)*)? ws "]"
        """ + "\n" + ScriptScenePlanning.JsonSeparators + "\n" + ScriptScenePlanning.JsonStringGrammar;

    public static string ReviewGrammarFor(ScriptScenePlan plan)
    {
        var ids = RequiredBeats(plan).Select(b => b.Id).ToArray();
        var sequence = string.Join(" comma ", ids.Select((_, i) => $"beat-{i + 1}"));
        var rules = ids.Select((id, i) => $"beat-{i + 1} ::= \"{{\" ws \"\\\"id\\\"\" colon {JsonSerializer.Serialize(JsonSerializer.Serialize(id))} beat-fields");
        return "root ::= \"{\" ws \"\\\"beats\\\"\" colon \"[\" ws " + sequence + " ws \"]\" comma \"\\\"findings\\\"\" colon \"[\" ws (finding (comma finding)*)? ws \"]\" ws \"}\"\n" +
            string.Join("\n", rules) + "\n" + ReviewGrammarBody;
    }

    private const string ReviewGrammarBody = """
        beat-fields ::= comma "\"covered\"" colon boolean comma "\"reason\"" colon string comma "\"evidence\"" colon references ws "}"
        finding ::= "{" ws "\"category\"" colon category comma "\"severity\"" colon severity comma "\"detail\"" colon string comma "\"evidence\"" colon references comma "\"sourceEvidence\"" colon "[" ws (source (comma source)*)? ws "]" ws "}"
        references ::= "[" ws (reference (comma reference){0,5})? ws "]"
        reference ::= "{" ws "\"scene\"" colon positive comma "\"block\"" colon integer comma "\"quote\"" colon string ws "}"
        source ::= "{" ws "\"section\"" colon positive comma "\"quote\"" colon string ws "}"
        category ::= "\"coverage\"" | "\"ending\"" | "\"source_facts\"" | "\"knowledge\"" | "\"continuity\"" | "\"duplication\"" | "\"screenplay\"" | "\"scope\""
        severity ::= "\"repair\"" | "\"note\""
        positive ::= [1-9] [0-9]*
        integer ::= "0" | positive
        boolean ::= "true" | "false"
        """ + "\n" + ScriptScenePlanning.JsonSeparators + "\n" + ScriptScenePlanning.JsonStringGrammar;

    public static JsonElement DraftSchema { get; } = Schema("""
        {"type":"object","properties":{"scenes":{"type":"array","minItems":1,"maxItems":64,"items":{"type":"object","properties":{
          "heading":{"type":"string","minLength":1,"maxLength":180},
          "beatIds":{"type":"array","items":{"type":"string"}},
          "blocks":{"type":"array","minItems":1,"items":{"type":"object","properties":{
            "kind":{"type":"string","enum":["Action","Character","Dialogue","Parenthetical","Transition"]},
            "spans":{"type":"array","minItems":1,"items":{"type":"object","properties":{"text":{"type":"string"},"bold":{"type":"boolean"},"italic":{"type":"boolean"}},"required":["text"],"additionalProperties":false}}
          },"required":["kind","spans"],"additionalProperties":false}}
        },"required":["heading","beatIds","blocks"],"additionalProperties":false}}},"required":["scenes"],"additionalProperties":false}
        """);

    public static JsonElement ReviewSchema { get; } = Schema("""
        {"type":"object","$defs":{
          "reference":{"type":"object","properties":{"scene":{"type":"integer","minimum":1},"block":{"type":"integer","minimum":0},"quote":{"type":"string","minLength":1,"maxLength":400}},"required":["scene","block","quote"],"additionalProperties":false},
          "source":{"type":"object","properties":{"section":{"type":"integer","minimum":1},"quote":{"type":"string","minLength":1,"maxLength":800}},"required":["section","quote"],"additionalProperties":false}
        },"properties":{
          "beats":{"type":"array","minItems":1,"maxItems":33,"items":{"type":"object","properties":{
            "id":{"type":"string"},"covered":{"type":"boolean"},"reason":{"type":"string"},"evidence":{"type":"array","maxItems":6,"items":{"$ref":"#/$defs/reference"}}
          },"required":["id","covered","reason","evidence"],"additionalProperties":false}},
          "findings":{"type":"array","maxItems":32,"items":{"type":"object","properties":{
            "category":{"type":"string","enum":["coverage","ending","source_facts","knowledge","continuity","duplication","screenplay","scope"]},
            "severity":{"type":"string","enum":["repair","note"]},"detail":{"type":"string"},
            "evidence":{"type":"array","minItems":1,"maxItems":6,"items":{"$ref":"#/$defs/reference"}},
            "sourceEvidence":{"type":"array","maxItems":6,"items":{"$ref":"#/$defs/source"}}
          },"required":["category","severity","detail","evidence","sourceEvidence"],"additionalProperties":false}}
        },"required":["beats","findings"],"additionalProperties":false}
        """);

    public static JsonElement ReviewSchemaFor(ScriptScenePlan plan)
    {
        var schema = JsonNode.Parse(ReviewSchema.GetRawText())!;
        var beats = schema["properties"]!["beats"]!;
        var ids = RequiredBeats(plan).Select(b => b.Id).ToArray();
        beats["minItems"] = ids.Length; beats["maxItems"] = ids.Length;
        beats["items"]!["properties"]!["id"]!["enum"] = JsonSerializer.SerializeToNode(ids);
        return JsonSerializer.SerializeToElement(schema);
    }

    private static JsonElement Schema(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
}
