using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.AI;

public static class AiTextResults
{
    public static AiTextJobResult Parse(AiTextJobRequest request, string raw, string? finishReason)
    {
        var result = new AiTextJobResult(raw, true, finishReason);
        if (string.IsNullOrWhiteSpace(raw)) return result with { Error = "The model returned an empty response. Inspect and retry; nothing has been applied." };
        if (finishReason is not null && finishReason != ChatFinishReason.Stop.ToString() || request.Model.Backend is AiBackend.OpenRouter or AiBackend.Codex or AiBackend.ClaudeCode or AiBackend.Vllm && finishReason is null)
            return result with { Error = "The response did not finish normally. Inspect it and retry; incomplete output cannot be applied." };
        var parsed = Content(request, result, raw, finishReason);
        if (parsed.Error is null || WholeJson(raw)) return parsed;
        // Local models sometimes reason aloud around a complete JSON answer. The saved raw reply is unchanged for review.
        return AiJsonReply.Latest(raw, json => Content(request, result, json, finishReason) is { Error: null } answer ? answer : null) ?? parsed;
    }

    private static bool WholeJson(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal) && text.Length > 6)
            text = text[3..^3].TrimStart() is var body && body.StartsWith("json", StringComparison.OrdinalIgnoreCase) ? body[4..] : body;
        try { using var _ = JsonDocument.Parse(text); return true; }
        catch (JsonException) { return false; }
    }

    private static AiTextJobResult Content(AiTextJobRequest request, AiTextJobResult result, string raw, string? finishReason)
    {
        switch (request.Kind)
        {
            case AiJobKind.AssetReelEvaluation:
                try { return Value(result, AssetReelEvaluator.Parse(raw, request.Payload<AssetReelEvaluationRequest>())); }
                catch (WorkspaceStoreException e) { return result with { Error = e.Message }; }
            case AiJobKind.AssetImageEvaluation:
                try { return Value(result, AssetImageEvaluator.Parse(raw, request.Payload<AssetImageEvaluationRequest>())); }
                catch (WorkspaceStoreException e) { return result with { Error = e.Message }; }
            case AiJobKind.ScriptAssistant:
                var script = request.Payload<ScriptAssistantRequest>();
                if (script.Run.EditFormat == 1)
                {
                    try
                    {
                        var edits = ScriptEdits.Parse(raw, script.Run.Target);
                        return Value(result, new AssistantUpdate(Blocks: ScriptEdits.Materialize(script.Run.Target, edits), Complete: true, Edits: edits));
                    }
                    catch (WorkspaceStoreException e) { return Value(result, new AssistantUpdate(ValidationError: e.Message, Complete: true), e.Message); }
                }
                ScreenplayJsonResult? parsed;
                if (request.Profile == ScriptSequenceDrafting.Profile) {
                    try { parsed = new(ScriptSequenceDrafting.Parse(raw).Blocks, null); }
                    catch (WorkspaceStoreException e) { parsed = new(null, e.Message); }
                }
                else parsed = script.Run.Operation == WritingOperation.Discuss ? null : ScreenplayJson.Parse(raw);
                var blocks = parsed?.Blocks;
                var error = parsed is { Blocks: null } ? ScreenplayJson.Rejection(parsed.Error) : null;
                if (error is null && blocks is not null && script.Run.SourceScene is not null) error = ScriptAdaptation.DraftError(blocks);
                return Value(result, new AssistantUpdate(Blocks: error is null ? blocks : null, ValidationError: error, Complete: true, NormalizationNotes: parsed?.NormalizationNotes), error);
            case AiJobKind.AssetExtraction:
                var extraction = AssetExtractor.Inspect(raw, request.Payload<AssetExtractionRequest>());
                return Value(result, extraction, extraction.ValidationError);
            case AiJobKind.ShotPlanning:
                var shots = ShotPlanner.Parse(raw, request.Payload<ShotPlanningRequest>());
                return Value(result, shots, shots.Error);
            case AiJobKind.AssetPromptEnhancement:
            case AiJobKind.PromptEnhancement:
                var enhancement = request.Payload<PromptEnhancementRequest>();
                var prompt = PromptEnhancer.ParseForContext(raw, enhancement.Context, allowPlainText: finishReason == ChatFinishReason.Stop.ToString());
                if (prompt is null) return result with { Error = "The response could not be read as a complete prompt. Inspect it and retry; your prompt is unchanged." };
                if (prompt.Kind == PromptEnhancementKind.Prompt && enhancement.Context.ProtectedTriggers.Any(t => !prompt.Text.Contains(t, StringComparison.Ordinal)))
                    return result with { Error = "The suggestion removed an explicit LoRA trigger. Retry; your original prompt is unchanged." };
                return Value(result, prompt);
            case AiJobKind.ReelComposition:
                try {
                    var pair = ReferenceReels.ParsePair(raw);
                    try { ReferenceReels.ValidatePair(pair, request.Payload<ReelCompositionRequest>().Draft); }
                    catch (WorkspaceStoreException e) { return Value(result, pair, e.Message); }
                    return Value(result, pair);
                }
                catch (WorkspaceStoreException e) { return result with { Error = e.Message }; }
            case AiJobKind.ShotTranslation:
                try { return Value(result, lumibelle.Services.Production.ShotDubbing.Parse(raw, request.Payload<ShotDubRequest>())); }
                catch (WorkspaceStoreException e) { return result with { Error = e.Message }; }
            case AiJobKind.AssetPicking:
                try { return Value(result, lumibelle.Services.Production.AssetPicker.Parse(raw, request.Payload<AssetPickRequest>())); }
                catch (WorkspaceStoreException e) { return result with { Error = e.Message }; }
            case AiJobKind.ReelViewSuggestion:
                try { return Value(result, ReelViewSuggestions.Parse(raw, request.Payload<ReelViewSuggestionRequest>())); }
                catch (WorkspaceStoreException e) { return result with { Error = e.Message }; }
            case AiJobKind.SceneEnvironmentSelection:
                try { return Value(result, DraftingEnvironments.ParseSelection(raw, request.Payload<SceneEnvironmentSelectionRequest>())); }
                catch (WorkspaceStoreException e) { return result with { Error = e.Message }; }
            case AiJobKind.EnvironmentViewSelection:
                try { return Value(result, EnvironmentViews.Parse(raw, request.Payload<EnvironmentViewRequest>())); }
                catch (WorkspaceStoreException e) { return result with { Error = e.Message }; }
            case AiJobKind.PromptComposition:
                try { return Value(result, lumibelle.Services.Production.PromptComposer.Parse(raw, request.Payload<PromptCompositionRequest>())); }
                catch (WorkspaceStoreException e) { return result with { Error = e.Message }; }
            case AiJobKind.SourceAnalysis:
                try { return Value(result, SourceAnalysis.Parse(raw, request.Payload<SourceAnalysisRequest>())); }
                catch (WorkspaceStoreException e) { return result with { Error = e.Message }; }
            case AiJobKind.Guidance:
                var guidance = PromptEnhancer.Parse(raw);
                return guidance is null || guidance.Text.Length > 12000
                    ? result with { Error = "No complete guidance suggestion was returned. Inspect the response and retry; your guidance is unchanged." }
                    : Value(result, guidance);
            default: throw new WorkspaceStoreException("This operation does not produce a text-assistance result.");
        }
    }
    private static AiTextJobResult Value<T>(AiTextJobResult result, T value, string? error = null) => result with
    { Value = JsonSerializer.SerializeToElement(value, AtomicJsonFile.Options), Error = error };
}
