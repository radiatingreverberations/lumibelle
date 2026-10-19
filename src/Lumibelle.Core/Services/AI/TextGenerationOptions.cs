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
    public static ChatOptions Captured(AiTextJobRequest request) => request.Version == 1
        ? new() { Temperature = request.Temperature, MaxOutputTokens = request.Settings.MaxOutputTokens, Seed = request.Seed }
        : Create(request.Model.Backend, request.Settings, request.Temperature, request.Seed, request.Version >= 3 ? request.Model : null);
}
