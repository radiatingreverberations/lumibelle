namespace lumibelle.Models;

// Captured with the request, so changing defaults cannot alter a queued run.
public sealed record MossImageSettings
{
    public string Model { get; init; } = "moss_image_base_int8_convrot.safetensors";
    public string TextEncoder { get; init; } = "qwen3.5_9b_w4a8_gptq.safetensors";
    public string Vae { get; init; } = "flux2-vae.safetensors";
    public string EditLora { get; init; } = "";
    public string EditFormat { get; init; } = "Moss-Image-Edit";
    public int Steps { get; init; } = 50;
    public double Guidance { get; init; } = 3;
    public int EditSteps { get; init; } = 30;
    public double EditGuidance { get; init; } = 4;
    public double Shift { get; init; } = 5;
    public bool SkipLayerGuidance { get; init; } = true;
    public string NegativePrompt { get; init; } = "";
}

public sealed record MossImageOutput(MossImageSettings Settings, int Width, int Height);
