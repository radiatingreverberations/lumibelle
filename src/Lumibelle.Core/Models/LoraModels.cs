namespace lumibelle.Models;

// Keep the existing image assignment names and numeric values in saved records.
public enum LoraWorkflow { Krea2, Flux2Klein9bKv, MiniMaxH3Ref2VA, QwenImage21, MossImage, Flux3ImageKlein, Fibo2, ZImage }
public static class LoraWorkflows
{
    public static LoraWorkflow LoraWorkflow(this ImageWorkflow workflow) => workflow switch
    {
        ImageWorkflow.Krea2 => Models.LoraWorkflow.Krea2,
        ImageWorkflow.QwenImage21 => Models.LoraWorkflow.QwenImage21,
        ImageWorkflow.MossImage => Models.LoraWorkflow.MossImage,
        ImageWorkflow.Flux3ImageKlein => Models.LoraWorkflow.Flux3ImageKlein,
        ImageWorkflow.Fibo2 => Models.LoraWorkflow.Fibo2,
        ImageWorkflow.ZImage => Models.LoraWorkflow.ZImage,
        ImageWorkflow.Flux2Klein9bKv => Models.LoraWorkflow.Flux2Klein9bKv,
        _ => throw new ArgumentOutOfRangeException(nameof(workflow))
    };
    public static string Label(this LoraWorkflow workflow) => workflow switch
    {
        Models.LoraWorkflow.Krea2 => "Krea 2",
        Models.LoraWorkflow.QwenImage21 => "Qwen Image 2.1",
        Models.LoraWorkflow.MossImage => "MOSS-Image",
        Models.LoraWorkflow.Flux3ImageKlein => "FLUX 3 Image Klein",
        Models.LoraWorkflow.Fibo2 => "Fibo 2.0",
        Models.LoraWorkflow.ZImage => "Z-Image",
        Models.LoraWorkflow.Flux2Klein9bKv => "FLUX.2 Klein 9B KV",
        Models.LoraWorkflow.MiniMaxH3Ref2VA => "MiniMax H3 Ref2VA",
        _ => throw new ArgumentOutOfRangeException(nameof(workflow))
    };
}
[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record LoraReference(string ComfyUrl, string FileName, LoraWorkflow Workflow, string Name)
{
    public LoraReference(string comfyUrl, string fileName, ImageWorkflow workflow, string name)
        : this(comfyUrl, fileName, workflow.LoraWorkflow(), name) { }
}
public sealed record LoraDefinition(LoraReference Reference, float DefaultStrength = 1f, string TriggerText = "")
{
    public IReadOnlyList<string> Tags { get; init; } = [];
}
public sealed record LoraVisibility
{
    public IReadOnlyList<string> OnlyTags { get; init; } = [];
    public IReadOnlyList<string> HiddenTags { get; init; } = [];
}
public sealed record LoraSelection(LoraReference Reference, float Strength = 1f, bool Enabled = true);
public sealed record AppliedLora(LoraReference Reference, float Strength);
public sealed record ComfyLoraCheck(bool Success, string Message, IReadOnlyList<string> Files,
    float MinimumStrength = -100f, float MaximumStrength = 100f);
