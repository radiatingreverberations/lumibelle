using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Components.Pages;

public partial class AiSettingsPage
{
    private bool _providerDefaultsOpen;
    private sealed class ComfyTextDraft { public int Tokens { get; set; } public float Temperature { get; set; } public ComfyVisionInput VisionInput { get; set; } }
    private readonly Dictionary<string, ComfyTextDraft> _comfyTextDrafts = new(StringComparer.Ordinal);
    private ComfyTextDraft TextDraft(TextModelReference model)
    {
        var key = TextModelPolicy.Key(model);
        if (!_comfyTextDrafts.TryGetValue(key, out var draft))
        {
            var value = ComfyTextSettings.Resolve(model, _settings!);
            _comfyTextDrafts[key] = draft = new() { Tokens = value.MaxOutputTokens, Temperature = value.Temperature, VisionInput = value.VisionInput };
        }
        return draft;
    }
    private string ComfyCapabilityText(TextModelReference model)
    {
        if (TextModelPolicy.Verification(model, _settings!)?.Capabilities is { } detected)
            return $"Detected by the latest model test: {VisionLabel(detected.Vision)}; " +
                (detected.SystemPrompt ? "instructions use the model's native system prompt." : "instructions are combined into the prompt because the model ignored a native system prompt.") +
                " Test again after changing the model file or ComfyUI.";
        var legacy = ComfyTextSettings.Resolve(model, _settings!).VisionInput;
        return "Image input and system-prompt support are detected by the model test (Details & test)." +
            (legacy == ComfyVisionInput.Disabled ? "" : $" Until then, the previously chosen {VisionLabel(legacy)} applies.");
    }
    private static string VisionLabel(ComfyVisionInput mode) => mode switch
    {
        ComfyVisionInput.ImageBatch => "multiple images",
        ComfyVisionInput.SingleImage => "single image only",
        _ => "text only, no image input"
    };
    private void CancelTextDraft(TextModelReference model) { _comfyTextDrafts.Remove(TextModelPolicy.Key(model)); }
    private async Task SaveTextDraftAsync(TextModelReference model, bool reset = false)
    {
        var draft = TextDraft(model);
        if (await SaveChangeAsync(settings =>
        {
            var models = new Dictionary<string, ComfyTextModelSettings>(settings.ComfyTextModels, StringComparer.Ordinal);
            if (reset) models.Remove(TextModelPolicy.Key(model));
            else models[TextModelPolicy.Key(model)] = new(draft.Tokens, draft.Temperature) { VisionInput = draft.VisionInput };
            return settings with { ComfyTextModels = models };
        }, reset ? "Model now uses ComfyUI defaults." : "Model generation settings saved.")) CancelTextDraft(model);
    }
}
