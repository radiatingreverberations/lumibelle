using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public static class VllmTextSettings
{
    public static VllmTextModelSettings Resolve(TextModelReference model, AiSettings settings) =>
        settings.VllmTextModels.GetValueOrDefault(TextModelPolicy.Key(model)) ?? new();

    // Capture effective values in the request model so later edits cannot change queued work.
    public static TextModelReference Apply(TextModelReference model, AiSettings settings)
    {
        if (model.Backend != AiBackend.Vllm) return model;
        var defaults = Resolve(model, settings);
        return model with { Temperature = model.Temperature ?? defaults.Temperature,
            MaxOutputTokens = model.MaxOutputTokens ?? defaults.MaxOutputTokens };
    }

    public static void Validate(AiSettings settings)
    {
        if (settings.VllmTextModels is null) throw new WorkspaceStoreException("vLLM model settings are missing.");
        foreach (var (key, value) in settings.VllmTextModels)
        {
            var parts = key.Split('\n');
            if (parts.Length != 3 || parts[0] != nameof(AiBackend.Vllm) || value is null)
                throw new WorkspaceStoreException("vLLM settings need an exact model and server identity.");
            var model = new TextModelReference(AiBackend.Vllm, parts[1], parts[1])
                { VllmUrl = parts[2], Temperature = value.Temperature, MaxOutputTokens = value.MaxOutputTokens };
            TextModelPolicy.Validate(model);
            if (TextModelPolicy.Key(TextModelPolicy.Normalize(model)) != key)
                throw new WorkspaceStoreException("The saved vLLM model identity is not normalized.");
        }
    }
}
