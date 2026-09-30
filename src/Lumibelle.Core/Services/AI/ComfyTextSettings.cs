using lumibelle.Models;

namespace lumibelle.Services.AI;

public static class ComfyTextSettings
{
    public static void Validate(AiSettings settings)
    {
        if (settings.ComfyTextModels is null)
            throw new lumibelle.Services.Story.WorkspaceStoreException("ComfyUI model settings are missing.");
        foreach (var (key, value) in settings.ComfyTextModels)
        {
            ValidateKey(key);
            if (value is null || value.MaxOutputTokens is < 1 or > 32768 || !float.IsFinite(value.Temperature) || value.Temperature is < .01f or > 2 || !Enum.IsDefined(value.VisionInput) ||
                value.BatchImageSide is { } side && !BatchImageSides.Contains(side))
                throw new lumibelle.Services.Story.WorkspaceStoreException("Model settings require 1–32,768 reply tokens, temperature 0.01–2, a supported image-input mode and an image size of 512, 768 or 1,024 pixels.");
        }
    }
    private static void ValidateKey(string key)
    {
        var parts = key.Split('\n');
        if (parts.Length != 3 || parts[0] != nameof(AiBackend.ComfyUI)) throw new lumibelle.Services.Story.WorkspaceStoreException("ComfyUI settings need an exact model and server identity.");
        var model = new TextModelReference(AiBackend.ComfyUI, parts[1], parts[1], parts[2]);
        TextModelPolicy.Validate(model);
        if (TextModelPolicy.Key(model) != key) throw new lumibelle.Services.Story.WorkspaceStoreException("The saved ComfyUI model identity is not normalized.");
    }
    public static readonly int[] BatchImageSides = [1024, 768, 512];
    public static int BatchImageSide(TextModelReference model, AiSettings settings) =>
        Resolve(model, settings).BatchImageSide ?? ComfyTextVision.BatchMaximumSide;
    public static ComfyTextModelSettings Resolve(TextModelReference model, AiSettings settings) =>
        settings.ComfyTextModels.GetValueOrDefault(TextModelPolicy.Key(model)) ?? new(settings.MaxOutputTokens, settings.Temperature);

    public static AiSettings Capture(TextModelReference model, AiSettings settings)
    {
        if (model.Backend != AiBackend.ComfyUI) return settings;
        var resolved = Resolve(model, settings);
        return settings with { MaxOutputTokens = resolved.MaxOutputTokens, Temperature = resolved.Temperature };
    }
}
