using lumibelle.Models;

namespace lumibelle.Services.Shots;

// Historical metadata for archived drbaph conversions. This is not an executable preset.
// Keep the descriptor frozen so saved takes retain their exact sampler-domain grid.
public static class H3HyperFlow
{
    public const string Key = "hyperflow";
    public const string SamplingProfile = "h3-hyperflow-comfy-v1";
    public const string DefaultCheckpoint = "minimax_h3_hyperflow_8step_v1.0_comfyui_pruned_bf16.safetensors";
    public const int Steps = 8;
    public const double VideoShift = 12.0;
    public const double AudioShift = 3.0;
    public const string RawSigmas = "1.0, 0.931506, 0.839236, 0.703462, 0.5, 0.296538, 0.160764, 0.068494, 0.0";
    // ManualSigmas bypasses BasicScheduler. These are 12*s/(1+11*s), NOT raw s.
    // MiniMaxH3SigmaShift makes the DiT invert this video grid and derive audio at shift 3.
    // Decimal notation is intentional: the native ManualSigmas parser is not an exponent parser.
    public const string VideoSigmas = "1.0, 0.9939097854, 0.9842874953, 0.9660637197, 0.9230769231, 0.8349423898, 0.6968520491, 0.4687533149, 0.0";
    public static IReadOnlyList<string> Checkpoints { get; } = Array.AsReadOnly(new[]
    {
        DefaultCheckpoint,
        "minimax_h3_hyperflow_8step_v1.0_comfyui_pruned_bf16_resized_avg_rank_20_bf16.safetensors",
        "minimax_h3_hyperflow_8step_v1.0_comfyui_bf16.safetensors",
        "minimax_h3_hyperflow_8step_v1.0_comfyui_bf16_resized_avg_rank_20_bf16.safetensors"
    });
    public static string Checkpoint(H3Settings settings) => settings.HyperFlowLora ?? DefaultCheckpoint;
    public static string? FileIssue(string? file)
    {
        if (string.IsNullOrWhiteSpace(file) || file.Length > 2048 || file.Any(char.IsControl))
            return "Select an installed ComfyUI-converted HyperFlow LoRA in Video models.";
        var parts = file.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.Contains(':')))
            return "Choose HyperFlow by its relative path in the ComfyUI LoRA catalog.";
        if (!Checkpoints.Contains(parts[^1], StringComparer.OrdinalIgnoreCase))
            return "HyperFlow requires one of the four v1.0 ComfyUI BF16 conversions (full/pruned, original/resized). " +
                "The raw upstream file needs its own two-time loader and is not supported here. Keep the published basename; subfolders are supported.";
        return null;
    }
    public static H3Sampling Sampling(H3Settings settings) =>
        new(SamplingProfile, Steps, "euler", "manual", VideoShift, AudioShift, Checkpoint(settings), 1.0);
    public static Dictionary<string, object> Inputs(H3Settings settings) => new()
    {
        ["profile"] = SamplingProfile, ["lora_name"] = Checkpoint(settings), ["strength_model"] = 1.0,
        ["steps"] = Steps, ["sampler"] = "euler", ["scheduler"] = "manual", ["guider"] = "BasicGuider", ["denoise"] = 1.0,
        ["shift_video"] = VideoShift, ["shift_audio"] = AudioShift,
        ["sigma_domain"] = "video-shifted", ["raw_sigmas"] = RawSigmas, ["sigmas"] = VideoSigmas
    };
}
