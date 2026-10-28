namespace lumibelle.Models;

public enum CaptionImageVariant { Base, Turbo }

// Shared settings for the Fibo 2.0 and Z-Image native ComfyUI workflows.
public sealed record CaptionImageSettings
{
    public string Model { get; init; } = "";
    public string TextEncoder { get; init; } = "";
    public string Vae { get; init; } = "";
    public CaptionImageVariant Variant { get; init; }
    public int? Steps { get; init; }
    public double? Guidance { get; init; }
    public double? Shift { get; init; }
    public string NegativePrompt { get; init; } = "";
}

public sealed record CaptionImageOutput(CaptionImageSettings Settings, int Width, int Height,
    int Steps, double Guidance, double Shift, string Sampler);
