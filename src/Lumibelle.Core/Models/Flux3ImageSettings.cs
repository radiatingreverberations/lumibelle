namespace lumibelle.Models;

public enum Flux3ImageVariant { Distilled, Base }

// Explicit variant selection also supports renamed checkpoints and local fine-tunes.
public sealed record Flux3ImageSettings
{
    public string Model { get; init; } = "flux3-image-klein-int8-convrot.safetensors";
    public string TextEncoder { get; init; } = "qwen3vl_4b_int8_convrot.safetensors";
    public string Vae { get; init; } = "flux2-vae.safetensors";
    public Flux3ImageVariant Variant { get; init; } = Flux3ImageVariant.Distilled;
    public int? Steps { get; init; }
    public double? Guidance { get; init; }
    public double ReferenceMegapixels { get; init; } = 1;
}

public sealed record Flux3ImageOutput(Flux3ImageSettings Settings, int Width, int Height);
