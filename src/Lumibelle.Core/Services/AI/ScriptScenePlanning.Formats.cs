using System.Text.Json;

namespace lumibelle.Services.AI;

public static partial class ScriptScenePlanning
{
    // The vLLM JSON-schema backend permits arbitrary separator whitespace, which
    // some small models repeat indefinitely. Allow normal, bounded indentation
    // without changing whitespace or Unicode inside the actual prose strings.
    public const string ScreenplayGrammar = """
        root ::= "[" ws block (comma block)* ws "]"
        block ::= "{" ws "\"kind\"" colon kind comma "\"spans\"" colon "[" ws span (comma span)* ws "]" ws "}"
        kind ::= "\"Scene\"" | "\"Action\"" | "\"Character\"" | "\"Dialogue\"" | "\"Parenthetical\"" | "\"Transition\""
        span ::= "{" ws "\"text\"" colon string ws "}"
        """ + "\n" + JsonSeparators + "\n" + JsonStringGrammar;
    public const string PlanningGrammar = """
        root ::= "{" ws "\"beats\"" colon "[" ws string (comma string)* ws "]" comma "\"continuity\"" colon strings comma "\"withhold\"" colon strings comma "\"ending\"" colon string comma "\"timeCue\"" colon string comma "\"details\"" colon strings comma "\"evidence\"" colon "[" ws (evidence (comma evidence)*)? ws "]" ws "}"
        strings ::= "[" ws (string (comma string)*)? ws "]"
        evidence ::= "{" ws "\"section\"" colon [1-9] [0-9]* comma "\"quote\"" colon string ws "}"
        """ + "\n" + JsonSeparators + "\n" + JsonStringGrammar;
    internal const string JsonSeparators = """
        comma ::= ws "," ws
        colon ::= ws ":" ws
        ws ::= [ \t\n\r]{0,16}
        """;
    internal const string JsonStringGrammar = """
        string ::= "\"" ([^"\\\x00-\x1F] | "\\" (["\\/bfnrt] | "u" [a-fA-F0-9]{4}))* "\""
        """;

    public static JsonElement ScreenplaySchema { get; } = ReadSchema("""
        {"type":"array","minItems":1,"items":{"type":"object","properties":{
          "kind":{"type":"string","enum":["Scene","Action","Character","Dialogue","Parenthetical","Transition"]},
          "spans":{"type":"array","minItems":1,"items":{"type":"object","properties":{
            "text":{"type":"string"},"bold":{"type":"boolean"},"italic":{"type":"boolean"}
          },"required":["text"],"additionalProperties":false}}
        },"required":["kind","spans"],"additionalProperties":false}}
        """);
    public static JsonElement PlanningSchema { get; } = ReadSchema("""
        {"type":"object","properties":{
          "beats":{"type":"array","minItems":1,"maxItems":32,"items":{"type":"string"}},
          "continuity":{"type":"array","items":{"type":"string"}},
          "withhold":{"type":"array","items":{"type":"string"}},
          "ending":{"type":"string"},"timeCue":{"type":"string"},
          "details":{"type":"array","items":{"type":"string"}},
          "evidence":{"type":"array","maxItems":12,"items":{"type":"object","properties":{
            "section":{"type":"integer","minimum":1},"quote":{"type":"string"}
          },"required":["section","quote"],"additionalProperties":false}}
        },"required":["beats","continuity","withhold","ending","timeCue","details","evidence"],"additionalProperties":false}
        """);
    private static JsonElement ReadSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
