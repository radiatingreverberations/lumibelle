using System.Text.Json;
using lumibelle.Models;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace lumibelle.Services.AI;

internal static class TextGenerationOptions
{
    internal const string ReasoningEffortKey = "lumibelle.reasoning_effort";

    public static string OutputLimitAdvice(AiBackend backend) => backend == AiBackend.ComfyUI
        ? "Increase the ComfyUI output limit in AI settings or shorten the request."
        : "Shorten the request, review the profile's output/thinking limits, or choose a model with a larger output limit.";

    // Unset hosted controls remain omitted. Never turn ComfyUI fallback settings into
    // implicit OpenRouter overrides. The selected profile is a captured value object.
    public static ChatOptions Create(AiBackend backend, AiSettings settings, float? temperature = null, long? seed = null,
        TextModelReference? selection = null)
    {
        if (selection is not null)
        {
            TextModelPolicy.Validate(selection);
            if (selection.Backend != backend) throw new AiGenerationException("The profile and request providers do not match.");
            selection = TextModelPolicy.WithDefaultEffort(selection, settings);
            selection = VllmTextSettings.Apply(selection, settings);
        }
        var result = new ChatOptions
        {
            Temperature = selection?.Temperature ?? (backend == AiBackend.ComfyUI ? temperature ?? settings.Temperature : null),
            MaxOutputTokens = selection?.MaxOutputTokens ?? (backend == AiBackend.ComfyUI ? settings.MaxOutputTokens : null),
            Seed = seed
        };
        if (backend is AiBackend.Codex or AiBackend.ClaudeCode && selection is not null)
        {
            // Presence with a null value explicitly means the model's default, rather
            // than the global subscription effort. Profiles do not inherit that effort.
            result.AdditionalProperties = new() { [ReasoningEffortKey] = selection.ReasoningEffort };
        }
        if (backend == AiBackend.OpenRouter && selection is { } model &&
            (model.ReasoningEffort is not null || model.ReasoningMaxTokens is not null))
        {
            result.RawRepresentationFactory = _ => OpenRouterOptions(model);
        }
        return result;
    }

    private static ChatCompletionOptions OpenRouterOptions(TextModelReference model)
    {
        // OpenRouter's unified reasoning object is not the OpenAI-only reasoning_effort
        // property. Use the SDK extension mechanism instead of reflection or a new client.
        var result = new ChatCompletionOptions();
#pragma warning disable SCME0001 // JsonPatch is the SDK's documented additional-properties API.
        // "none" is our saved off selection, not an ON effort. Send the explicit
        // toggle so providers need not advertise "none" in their effort-level list.
        if (model.ReasoningEffort == "none") result.Patch.Set("$.reasoning.enabled"u8, false);
        else if (model.ReasoningEffort is { } effort) result.Patch.Set("$.reasoning.effort"u8, effort);
        if (model.ReasoningMaxTokens is { } budget) result.Patch.Set("$.reasoning.max_tokens"u8, budget);
#pragma warning restore SCME0001
        return result;
    }

    // Version 1 snapshots predate provider-default semantics; keep their replay behavior.
    public static ChatOptions Captured(AiTextJobRequest request)
    {
        var options = request.Version == 1
            ? new ChatOptions { Temperature = request.Temperature, MaxOutputTokens = request.Settings.MaxOutputTokens, Seed = request.Seed }
            : Create(request.Model.Backend, request.Settings, request.Temperature, request.Seed, request.Version >= 3 ? request.Model : null);
        if (request.ResponseGrammar is { } grammar)
            options.RawRepresentationFactory = _ => VllmGrammarOptions(grammar);
        else if (request.ResponseSchema is { } schema)
        {
            var standard = request.Model.Backend == AiBackend.Vllm && OpenAiConnections.Find(request.Settings, request.Model.VllmUrl) is { } connection &&
                OpenAiConnections.Kind(connection) == OpenAiServerKind.Standard;
            if (standard)
                options.RawRepresentationFactory = _ => OpenAiSchemaOptions(schema);
            else if (request.Kind is AiJobKind.PromptComposition or AiJobKind.ShotPlanning && request.Model.Backend == AiBackend.Vllm)
                options.RawRepresentationFactory = _ => VllmSchemaOptions(schema);
            else options.ResponseFormat = Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema(schema, "lumibelle_response");
        }
        return options;
    }

    private static ChatCompletionOptions OpenAiSchemaOptions(JsonElement schema)
    {
        var options = new ChatCompletionOptions();
#pragma warning disable SCME0001
        // Preserve bounds, enums and required fields instead of letting the adapter simplify the captured schema.
        options.Patch.Set("$.response_format"u8, BinaryData.FromString(JsonSerializer.Serialize(new {
            type = "json_schema", json_schema = new { name = "lumibelle_response", strict = true, schema }
        })));
#pragma warning restore SCME0001
        return options;
    }

    private static ChatCompletionOptions VllmSchemaOptions(JsonElement schema)
    {
        var options = new ChatCompletionOptions();
#pragma warning disable SCME0001
        // The generic OpenAI adapter rewrites minItems/maxItems/minLength into descriptions.
        // vLLM supports these constraints; preserve the captured bounds and required fields.
        options.Patch.Set("$.structured_outputs.json"u8, BinaryData.FromString(schema.GetRawText()));
#pragma warning restore SCME0001
        return options;
    }

    private static ChatCompletionOptions VllmGrammarOptions(string grammar)
    {
        var options = new ChatCompletionOptions();
#pragma warning disable SCME0001
        options.Patch.Set("$.structured_outputs.grammar"u8, grammar);
#pragma warning restore SCME0001
        return options;
    }
}
