namespace lumibelle.Models;

public enum QwenImage21Variant { Base, Turbo }

// Model/server choices are captured by AiSettings in every queued request.
public sealed record QwenImage21Settings
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public QwenImage21Variant Variant { get; init; }
    public string Model { get; init; } = "qwen_image_2.1_int8_convrot.safetensors";
    public string TextEncoder { get; init; } = "qwen3vl_8b_int8_convrot.safetensors";
    public string Vae { get; init; } = "qwen_image_2.1_vae_bf16.safetensors";
    public string CacheDevice { get; init; } = "auto";
    public string CacheDtype { get; init; } = "default";
}

// Resolution is the square root of the pixel budget, NOT an edge length or MP count.
// Zero is edit-only: keep each cropped reference's size, rounded to multiples of 32.
public sealed record QwenImage21Options(int Resolution = 1024, int Steps = 25);

// Capture the actual output geometry alongside the requested aspect ratio.
public sealed record QwenImage21Output(QwenImage21Options Options, int Width, int Height,
    string CacheDevice, string CacheDtype)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public QwenImage21Variant Variant { get; init; }
}
