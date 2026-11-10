namespace lumibelle.Models;

public sealed record MiniMaxH3ImageSettings
{
    public string Model { get; init; } = "minimax_h3_image_pruned_int8_convrot.safetensors";
    public string TextEncoder { get; init; } = "qwen3vl_32b_minimax_h3_int8_convrot.safetensors";
    public string Vae { get; init; } = "minimax_h3_image_vae_fp16.safetensors";
}

public sealed record MiniMaxH3ImageOutput(MiniMaxH3ImageSettings Settings, int Width, int Height);
