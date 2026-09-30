using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Components.Pages;

public partial class AiSettingsPage
{
    private bool _providerDefaultsOpen;
    private sealed class ComfyTextDraft { public int Tokens { get; set; } public float Temperature { get; set; } public ComfyVisionInput VisionInput { get; set; } public int ImageSide { get; set; } }
    private readonly Dictionary<string, ComfyTextDraft> _comfyTextDrafts = new(StringComparer.Ordinal);
    private ComfyTextDraft TextDraft(TextModelReference model)
    {
        var key = TextModelPolicy.Key(model);
        if (!_comfyTextDrafts.TryGetValue(key, out var draft))
        {
            var value = ComfyTextSettings.Resolve(model, _settings!);
            _comfyTextDrafts[key] = draft = new() { Tokens = value.MaxOutputTokens, Temperature = value.Temperature, VisionInput = value.VisionInput,
                ImageSide = value.BatchImageSide ?? ComfyTextVision.BatchMaximumSide };
        }
        return draft;
    }
    private static string ImageSideLabel(TextModelReference model, int side) =>
        $"{side:N0} px · ≤{ComfyTextCapacity.ImageTokens(model.Model, side, side):N0} tokens";
    /// <summary>Turns the measured capacity into what fits at the drafted reply limit, or null before a capacity test.</summary>
    private string? CapacityText(TextModelReference model, ComfyTextDraft draft)
    {
        var benchmark = ComfyTextCapacity.Benchmark(model, _settings!);
        if (ComfyTextCapacity.FastTokens(benchmark) is not { } fast) return null;
        var prompt = fast - draft.Tokens;
        var perImage = ComfyTextCapacity.ImageTokens(model.Model, draft.ImageSide, draft.ImageSide);
        var perToken = benchmark!.BytesPerToken!.Value / 1024d;
        return prompt <= 0
            ? $"About {fast:N0} tokens of prompt and reply fit in GPU memory on this machine, less than this reply limit alone. Lower the reply limit; larger requests stream the model from system RAM and become very slow."
            : $"About {fast:N0} tokens of prompt and reply fit in GPU memory on this machine (~{perToken:N0} KB per token, measured {benchmark.MeasuredUtc.ToLocalTime():d}). " +
              $"At this reply limit that leaves about {prompt:N0} prompt tokens: roughly {prompt * 4:N0} characters of text, and each reference image uses up to ~{perImage:N0} of them. Larger requests become very slow.";
    }
    private string ComfyCapabilityText(TextModelReference model)
    {
        if (TextModelPolicy.Verification(model, _settings!)?.Capabilities is { } detected)
            return $"Detected by the latest model test: {VisionLabel(detected.Vision)}; " +
                (detected.SystemPrompt ? "instructions use the model's native system prompt." : "instructions are combined into the prompt because the model ignored a native system prompt.") +
                " Test again after changing the model file or ComfyUI.";
        var legacy = ComfyTextSettings.Resolve(model, _settings!).VisionInput;
        return "Image input and system-prompt support are detected when you test the model." +
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
            else models[TextModelPolicy.Key(model)] = new(draft.Tokens, draft.Temperature)
            { VisionInput = draft.VisionInput, BatchImageSide = draft.ImageSide == ComfyTextVision.BatchMaximumSide ? null : draft.ImageSide };
            return settings with { ComfyTextModels = models };
        }, reset ? "Model now uses ComfyUI defaults." : "Model generation settings saved.")) CancelTextDraft(model);
    }
}
