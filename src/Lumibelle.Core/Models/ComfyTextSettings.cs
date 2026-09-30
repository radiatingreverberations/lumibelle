namespace lumibelle.Models;

// Observed by the model test's image probes, not a filename guess or a text benchmark result.
public enum ComfyVisionInput { Disabled, SingleImage, ImageBatch }

public sealed record ComfyTextModelSettings(int MaxOutputTokens, float Temperature)
{
    // Legacy manual choice, used only until the model is tested with capability detection.
    // Default omission keeps legacy settings and request snapshots unchanged.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public ComfyVisionInput VisionInput { get; init; }
    // Longest side of multi-image inspection canvases; null keeps the 1024-pixel default.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? BatchImageSide { get; init; }
    // False (the default) lets capture raise the reply limit when the prompt leaves room in GPU memory.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool FixedReplyLimit { get; init; }
}
