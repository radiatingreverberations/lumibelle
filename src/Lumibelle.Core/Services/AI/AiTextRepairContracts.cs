using System.Globalization;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

// Deliberately build a small whitelist. Never serialize the original request,
// Settings, LoRA catalog, scene history, reference buffers or original messages
// into a provider message. Task retains that local context for the normal parser.
public static class AiTextRepairContracts
{
    private static object Text(int maximum = 100_000) => new { type = "string", minLength = 1, maxLength = maximum };
    private static object Pair(string second, int maximum) => new {
        type = "object", required = new[] { "prompt", second }, additionalProperties = false,
        properties = new Dictionary<string, object> { ["prompt"] = Text(), [second] = Text(maximum) }
    };
    private static object Suggestion(int maximum) => new {
        type = "object", required = new[] { "kind", "text" }, additionalProperties = false,
        properties = new Dictionary<string, object> {
            ["kind"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "Prompt", "NeedsInput", "NeedsSetup" } },
            ["text"] = Text(maximum)
        }
    };
    private const string H3Rules = """
        The prompt is a string with exactly these colon-terminated headings once each, in order:
        subject_definitions, summary, retention_analysis, detailed_description, overall_soundscape, non_diegetic_music.
        Complete every section. Write 'No non-diegetic music.' when no music is intended.
        Refer to every selected Picture, Video and Audio using its supplied exact label. Do not invent or renumber references.
        Define every <Subject N> in subject_definitions before using it. Reference-role notes are author directions, not fresh visual evidence.
        Keep every supplied dialogue line exactly once, in its original order and language: NAME (S1) says: <d>[Language] exact text</d>.
        Use the supplied stable speaker number (S1), (S2), etc immediately before each dialogue on the SAME line.
        Map each Audio label to its assigned NAME (S#) on the SAME line in subject_definitions. Reference recordings do not supply target words.
        State generatedDuration with digits followed by 'seconds'. Do not change the requested duration or other generation settings.
        A sparse RefMod occupies a Video label; its still previews are not additional Picture labels. Audio is separate.
        """;

    public static JsonElement Capture(AiTextJobRequest request)
    {
        object contract = request.Kind switch
        {
            AiJobKind.PromptComposition => ShotContract(request.Payload<PromptCompositionRequest>()),
            AiJobKind.ReelComposition => ReelContract(request.Payload<ReelCompositionRequest>()),
            AiJobKind.PromptEnhancement => Enhancement(request.Payload<PromptEnhancementRequest>()),
            AiJobKind.Guidance => new {
                outputSchema = Suggestion(12_000),
                rules = "Return kind and text. Preserve the existing guidance; fix only its format. Put no NEEDS_INPUT/NEEDS_SETUP prefix inside text. Do not invent visual observations or change the target field.",
                facts = new { field = request.Payload<GuidanceRequest>().Context.Label }
            },
            AiJobKind.ScriptAssistant => ScriptContract(request.Payload<ScriptAssistantRequest>()),
            _ => throw new WorkspaceStoreException("There is no text-only repair contract for this operation.")
        };
        return JsonSerializer.SerializeToElement(contract, AtomicJsonFile.Options);
    }

    private static object ShotContract(PromptCompositionRequest request) => new {
        outputSchema = Pair("referenceUsage", 100_000),
        rules = H3Rules + "\nThis is one continuous take, labelled [Shot 1]. State 'one continuous take' explicitly. Do not add internal cuts or other [Shot N] labels. Preserve the existing opening composition and continuity choices.",
        facts = Facts(request.Shot, false)
    };
    private static object ReelContract(ReelCompositionRequest request) => new {
        outputSchema = Pair("useGuidance", 20_000),
        rules = H3Rules + "\nCuts are allowed. Number views consecutively from [Shot 1] inside detailed_description. Use guidance describes intended later reference use, not verified generated output, and must stay independent of Picture/Video numbering.",
        facts = new {
            shot = Facts(ReferenceReels.Inputs(request.Draft), true),
            voiceMode = request.Draft.VoiceMode.ToString(),
            request.Draft.VoiceDescription,
            requestedViews = ReferenceReels.Views(request.Draft)
        }
    };
    private static object Facts(Shot shot, bool allowCuts)
    {
        var refs = ResolvedReferences.For(shot);
        var speakers = shot.Dialogue.Select(d => d.Speaker.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string? SpeakerLabel(string? speaker)
        {
            var index = Array.FindIndex(speakers, s => string.Equals(s, speaker?.Trim(), StringComparison.OrdinalIgnoreCase));
            return index < 0 ? null : "(S" + (index + 1).ToString(CultureInfo.InvariantCulture) + ")";
        }
        return new {
            generatedDuration = H3Policy.Seconds(shot.Duration!.Value).ToString("0.###", CultureInfo.InvariantCulture) + " seconds",
            allowCuts, shot.Aspect, shot.Atmosphere, shot.Music,
            dialogue = shot.Dialogue.Select(d => new { d.Speaker, label = SpeakerLabel(d.Speaker), d.Language, d.Text }),
            pictures = refs.Pictures.Select(p => new { label = $"<Picture {p.Number}>", name = p.Image?.Name ?? p.Continuity?.Name ?? p.Reel?.Name }),
            videos = refs.Videos.Select(v => new { label = $"<Video {v.Number}>", v.Reel.Name, representation = v.Reel.EffectiveVisuals.ToString() }),
            audio = ReferenceVideos.AudioMappings(shot).Select(a => new { label = $"<Audio {a.Item1}>", speaker = a.Item2, speakerLabel = SpeakerLabel(a.Item2) })
        };
    }
    private static object Enhancement(PromptEnhancementRequest request) => new {
        outputSchema = Suggestion(100_000),
        rules = "Return kind and text. A Prompt is a complete English paragraph; preserve quoted visible lettering in its original language and every explicit trigger exactly. Do not add triggers or invent missing reference details. Put no NEEDS_INPUT/NEEDS_SETUP prefix inside JSON text.",
        facts = new { explicitTriggers = request.Context.ProtectedTriggers,
            workflow = request.Context.Workflow.Label(), request.Context.IsEdit, request.Context.AspectRatio,
            referenceCount = request.Context.References.Count }
    };
    private static object FocusedSchema(object block)
    {
        object Operation(string kind, string[] required, params string[] fields)
        {
            var properties = new Dictionary<string, object> { ["kind"] = new Dictionary<string, object> { ["const"] = kind } };
            foreach (var field in fields) properties[field] = field switch {
                "blocks" => new { type = "array", items = block },
                "side" => new Dictionary<string, object> { ["enum"] = new[] { "before", "after" } },
                "startOffset" or "endOffset" => new { type = "integer", minimum = 0 },
                _ => new { type = "string", format = "uuid" }
            };
            return new { type = "object", required, additionalProperties = false, properties };
        }
        return new { type = "object", required = new[] { "version", "operations" }, additionalProperties = false,
            properties = new {
                version = new Dictionary<string, object> { ["type"] = "integer", ["const"] = 1 },
                operations = new { type = "array", maxItems = 1000, items = new { oneOf = new[] {
                    Operation("replace", ["kind", "startId", "blocks"], "startId", "endId", "startOffset", "endOffset", "blocks"),
                    Operation("insert", ["kind", "anchorId", "side", "blocks"], "anchorId", "side", "blocks"),
                    Operation("delete", ["kind", "startId"], "startId", "endId"),
                    Operation("move", ["kind", "startId", "anchorId", "side"], "startId", "endId", "anchorId", "side")
                } } }
            } };
    }
    private static object ScriptContract(ScriptAssistantRequest request)
    {
        var block = new {
            type = "object", required = new[] { "kind", "spans" }, additionalProperties = false,
            properties = new Dictionary<string, object> {
                ["kind"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "Act", "Scene", "Action", "Character", "Dialogue", "Parenthetical", "Transition" } },
                ["spans"] = new { type = "array", items = new { type = "object", required = new[] { "text" }, additionalProperties = false,
                    properties = new { text = new { type = "string" }, bold = new { type = "boolean" }, italic = new { type = "boolean" } } } }
            }
        };
        if (request.Run.EditFormat == 1)
            return new {
                outputSchema = FocusedSchema(block),
                blockSchema = block, rules = ScriptEdits.Instructions,
                facts = new { target = request.Run.Target,
                    preservation = "Preserve the proposed edits and their text. Correct their representation; do not redo the author request or silently discard conflicting edits." }
            };
        return new {
            outputSchema = new { type = "array", items = block },
            rules = "Return the complete proposed screenplay block array. Do not give new blocks IDs. Repair structure and syntax without rewriting the proposed screenplay or changing its language. Retain all proposed blocks.",
            facts = new { target = request.Run.Target, operation = request.Run.Operation.ToString() }
        };
    }
}
