using lumibelle.Models;

namespace lumibelle.Services.Shots;

public enum H3RequirementKind { File, NodePack }
public enum H3RequirementState { NotChecked, Installed, OtherFile, Missing }
// A short fact about a download, explained when hovered or focused.
public sealed record H3Badge(string Label, string Hint);
// Note is reserved for instructions people have to follow; descriptive facts go in Size, Format and Tags.
public sealed record H3Download(string Name, string Url, string? Note = null)
{
    // The account the link points at, so the badge always matches the link.
    public string Publisher => new Uri(Url).Segments.ElementAtOrDefault(1)?.TrimEnd('/') ?? new Uri(Url).Host;
    public string Site => new Uri(Url).Host.Contains("github") ? "GitHub" : "Hugging Face";
    public string? Size { get; init; }
    public string? Format { get; init; }
    public IReadOnlyList<H3Badge> Tags { get; init; } = [];
}
// One supported file or node pack. The first download is the suggested one.
public sealed record H3Requirement(string Id, H3RequirementKind Kind, string Label, string Folder, IReadOnlyList<H3Download> Downloads)
{
    // A node class the pack registers, so discovery can tell whether it is installed.
    public string? Node { get; init; }
}
public sealed record H3RequirementStatus(H3Requirement Requirement, H3RequirementState State, string? Selected);

// The video files and node packs Lumibelle supports. The settings page and the manual both list these,
// so this is the one place that says what to download and where it goes.
public static class H3Requirements
{
    private const string ComfyOrg = "https://huggingface.co/Comfy-Org/MiniMax-H3/blob/main/";
    private const string Drbaph = "https://huggingface.co/drbaph/MiniMax-H3-Turbo-Lora-ComfyUI/blob/main/";
    private static readonly H3Badge Default = new("Default", "Lumibelle's default choice.");
    // Common GPU memory sizes, and the working room sampling needs beyond the model weights at preview size.
    private static readonly int[] CardSizes = [8, 12, 16, 20, 24, 32, 48, 80];
    private const double SamplingRoomGb = 3;
    // An estimate from the file size of the smallest common card that holds a model fully. Smaller cards
    // still work: ComfyUI streams the rest from system memory, more slowly. Only the model needs this;
    // the encoder and VAEs load at other times.
    public static int? GpuFit(H3Requirement r, H3Download d)
    {
        if (r.Id != Model.Id || d.Size is null || !double.TryParse(d.Size.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture, out var gb)) return null;
        var card = CardSizes.FirstOrDefault(c => c >= gb + SamplingRoomGb);
        return card == 0 ? null : card;
    }
    // The size badge's explanation; for models it also estimates which card holds them fully.
    public static string SizeHint(H3Requirement r, H3Download d) => GpuFit(r, d) is { } card
        ? $"Download size. Estimate: holds fully on a {card} GB card, counting about {SamplingRoomGb:0} GB for sampling at preview size. " +
          "Cards with less memory still work, more slowly, because ComfyUI streams the rest from system memory."
        : "Download size.";
    private static readonly H3Badge Template = new("ComfyUI template", "ComfyUI's own MiniMax H3 Ref2VA template uses this file.");
    private static H3Badge Tested(string commit) => new($"Tested {commit}", $"Lumibelle was tested with commit {commit}. Newer versions may work too.");
    public static string FormatHint(string format) => format switch
    {
        "int8" => "8-bit weights.", "W6A8" => "6-bit weights with 8-bit activations: between int8 and W4A8 in size.", "W4A8" => "4-bit weights with 8-bit activations: smaller than int8, at slightly lower precision.",
        "NVFP4" => "NVIDIA's 4-bit format, meant for RTX 50 series cards.", "fp16" or "bf16" => "16-bit weights.", "fp32" => "32-bit weights.", _ => format
    };
    public static readonly H3Requirement Model = new("model", H3RequirementKind.File, "Model", "models/diffusion_models", [
        new("minimax_h3_ref2va_pruned_int8_convrot.safetensors", ComfyOrg + "diffusion_models/minimax_h3_ref2va_pruned_int8_convrot.safetensors")
            { Size = "21 GB", Format = "int8", Tags = [Default, Template, new("Official", "MiniMax's own Ref2VA weights. The Turbo, PDD and HyperFlow speed-ups were trained on these.")] },
        new("minimax_h3_ref2va_pruned_w6a8.safetensors", ComfyOrg + "diffusion_models/minimax_h3_ref2va_pruned_w6a8.safetensors")
            { Size = "16 GB", Format = "W6A8", Tags = [new("Official", "MiniMax's own Ref2VA weights in a smaller format.")] },
        new("minimax_h3_ref2va_pruned_w4a8_mixed.safetensors", "https://huggingface.co/Kijai/MiniMax-H3-experimental/blob/main/minimax_h3_ref2va_pruned_w4a8_mixed.safetensors")
            { Size = "12 GB", Format = "W4A8", Tags = [new("Official", "MiniMax's own Ref2VA weights in a smaller format."),
                new("Experimental", "From Kijai's repository of experiments, so it may change or disappear.")] },
        new("Minimax-h3_Singularity_ref2va_Pruned_v1.3_int8.safetensors", "https://huggingface.co/WarmBloodAban/Minimax-h3_Singularity/blob/main/Minimax-h3_Singularity_ref2va_Pruned_v1.3_int8.safetensors")
            { Size = "21 GB", Format = "int8", Tags = [new("Finetune", "Singularity v1.3: a community finetune of an FL2VA/Ref2VA merge. Its author tuned it for clarity, faces, action scenes and camera control.")] },
        new("Minimax-h3_Singularity_ref2va_v1.3_Pruned_w4a8.safetensors", "https://huggingface.co/WarmBloodAban/Minimax-h3_Singularity/blob/main/Minimax-h3_Singularity_ref2va_v1.3_Pruned_w4a8.safetensors")
            { Size = "12 GB", Format = "W4A8", Tags = [new("Finetune", "Singularity v1.3 at about half the size.")] },
        new("minimax_h3_hybrid_fl2va_ref2va_b25-49-int8.safetensors", "https://huggingface.co/smhfacct/Minimax-H3-fl2va-ref2va-hybrid-models/blob/main/minimax_h3_hybrid_fl2va_ref2va_b25-49-int8.safetensors")
            { Size = "21 GB", Format = "int8", Tags = [new("Merge", "A plain FL2VA/Ref2VA merge of the official weights, without further training.")] },
        new("minimax_h3_hybrid_fl2va_ref2va_b25-49_w6a8.safetensors", "https://huggingface.co/binglingzhimeng/minimax_h3_hybrid_fl2va_ref2va_b25-49_w6a8/blob/main/minimax_h3_hybrid_fl2va_ref2va_b25-49_w6a8.safetensors",
            "Requires ComfyUI with W6A8 support. Native acceleration needs an NVIDIA Ampere or newer GPU.")
            { Size = "16 GB", Format = "W6A8", Tags = [new("Merge", "The b25-49 FL2VA/Ref2VA merge in a smaller format, without further training.")] }]);
    public static readonly H3Requirement Encoder = new("encoder", H3RequirementKind.File, "Encoder", "models/text_encoders", [
        new("qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors", ComfyOrg + "text_encoders/qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors") { Size = "16 GB", Format = "NVFP4", Tags = [Default, Template] },
        new("qwen3vl_32b_minimax_h3_int8_convrot.safetensors", ComfyOrg + "text_encoders/qwen3vl_32b_minimax_h3_int8_convrot.safetensors") { Size = "27 GB", Format = "int8" },
        new("qwen3vl_32b_minimax_h3-w4a8_convrot.safetensors", "https://huggingface.co/koongrizzly/MiniMax_H3_int4_W4A8_ConvRot_Pruned/blob/main/text_encoders/qwen3vl_32b_minimax_h3-w4a8_convrot.safetensors")
            { Size = "16 GB", Format = "W4A8" }]);
    public static readonly H3Requirement VideoVae = new("video-vae", H3RequirementKind.File, "Video VAE", "models/vae", [
        new("minimax_h3_video_vae_int8_convrot.safetensors", ComfyOrg + "vae/minimax_h3_video_vae_int8_convrot.safetensors") { Size = "3 GB", Format = "int8", Tags = [Default, Template] },
        new("minimax_h3_video_vae_fp16.safetensors", ComfyOrg + "vae/minimax_h3_video_vae_fp16.safetensors") { Size = "5 GB", Format = "fp16" }]);
    public static readonly H3Requirement AudioVae = new("audio-vae", H3RequirementKind.File, "Audio VAE", "models/vae", [
        new("minimax_h3_audio_vae_fp32.safetensors", ComfyOrg + "vae/minimax_h3_audio_vae_fp32.safetensors") { Size = "0.6 GB", Format = "fp32" }]);
    public static IReadOnlyList<H3Requirement> Required { get; } = [Model, Encoder, VideoVae, AudioVae];

    public static readonly H3Requirement TurboLora = new("turbo4-lora", H3RequirementKind.File, "Turbo 4-step LoRA", "models/loras", [
        new("minimax_h3_ref2v_turbo_4step_v0.1_comfyui_bf16.safetensors", ComfyOrg + "loras/minimax_h3_ref2v_turbo_4step_v0.1_comfyui_bf16.safetensors",
            "Use the ref2v file; the fl2v LoRAs beside it belong to a different workflow.") { Size = "2 GB", Format = "bf16", Tags = [new("Recommended", "The Singularity author recommends this LoRA.")] }]);
    public static readonly H3Requirement Turbo8Lora = new("turbo8-lora", H3RequirementKind.File, "Turbo 8-step LoRA", "models/loras", [
        new("minimax_h3_ref2v_turbo_8step_v1.0_768p_comfyui_bf16.safetensors", "https://huggingface.co/lightx2v/Minimax-h3-Turbo/blob/main/minimax_h3_ref2v_turbo_8step_v1.0_768p_comfyui_bf16.safetensors") { Size = "2 GB", Format = "bf16" }]);
    public static readonly H3Requirement LarryNodes = new("larry-nodes", H3RequirementKind.NodePack, "Larry Turbo nodes", "custom_nodes", [
        new("ComfyUI-MiniMax-H3-Turbo", "https://github.com/Larryvrh/ComfyUI-MiniMax-H3-Turbo/tree/4274783a23afcfdbea3b4876cb79effd6c510785",
            "Install it with its bundled support files.") { Tags = [Tested("4274783")] }]) { Node = "MiniMaxH3TurboLoRA" };
    public static readonly H3Requirement LarryLora = new("larry-lora", H3RequirementKind.File, "Larry LoRA", "models/loras", [
        new(H3Presets.LarryCheckpoint, "https://huggingface.co/larryvrh/MiniMax-H3-Turbo-Lora/blob/43a74557ac3f6539db8e0f2a959d03feb7a81480/minimax_h3_turbo_v4_step600_ema.safetensors")
            { Size = "0.8 GB", Tags = [new("v4 step600 EMA", "The version its author calls the current best.")] }]);
    public static readonly H3Requirement PddNodes = new("pdd-nodes", H3RequirementKind.NodePack, "PDD Acc nodes", "custom_nodes", [
        new("ComfyUI-MiniMax-H3-PDD-Acc", "https://github.com/Jalen-Brunson/ComfyUI-MiniMax-H3-PDD-Acc/tree/311a65dd53832d8a5f8177a9d5fb923c09e35a90") { Tags = [Tested("311a65d")] }]) { Node = "MiniMaxH3PDDAccApply" };
    public static readonly H3Requirement PddFile = new("pdd-file", H3RequirementKind.File, "PDD Ref2VA weights", "models/pdd_acc", [
        new(H3Presets.PddCheckpoint, "https://huggingface.co/alibaba-pai/MiniMax-H3-Acc-LoRAs/blob/335001fb9e5455d68a0caa18ec2e319072150328/MiniMax-H3-Ref2VA-Acc-8Step.safetensors",
            "Needs the PDD loader. Don't add it as a character or style LoRA.") { Size = "1.4 GB", Format = "bf16" }]);
    private static H3Badge HyperFlowKind(string name) => name.Contains("_pruned_") ? new("Pruned", "For pruned models such as Singularity.") : new("Full", "For full, unpruned models.");
    private static H3Badge? HyperFlowRank(string name) => name.Contains("rank_20") ? new("Rank 20", "Resized to an average rank of 20: much smaller, possibly less faithful.") : null;
    public static readonly H3Requirement HyperFlowLora = new("hyperflow-lora", H3RequirementKind.File, "HyperFlow ComfyUI conversion", "models/loras",
        H3HyperFlow.Checkpoints.Select((name, i) => new H3Download(name, Drbaph + name, i == 0 ? "Match the file to your model: pruned with pruned, full with full. Keep the published name." : null)
            { Size = name.Contains("rank_20") ? "0.3 GB" : "3.9 GB", Format = "bf16", Tags = new[] { i == 0 ? Default : null, HyperFlowKind(name), HyperFlowRank(name) }.OfType<H3Badge>().ToArray() }).ToArray());
    public static readonly H3Requirement SpectrumNodes = new("spectrum-nodes", H3RequirementKind.NodePack, "Spectrum nodes", "custom_nodes", [
        new("ComfyUI-Spectrum-MiniMax-H3", "https://github.com/xmarre/ComfyUI-Spectrum-MiniMax-H3/tree/455bd357cb45637c8e852f7f448dc57b52de94f8") { Tags = [Tested("455bd35")] }]) { Node = "SpectrumApplyMiniMaxH3" };

    public static readonly H3Requirement UpscalerNodes = new("upscaler-nodes", H3RequirementKind.NodePack, "Latent upscaler nodes", "custom_nodes", [
        new("Comfyui_Minimax_h3_latent_Upscaler-Plus", "https://github.com/xmarre/Comfyui_Minimax_h3_latent_Upscaler-Plus/tree/db76324d6bbf231bebcb9d794e133ef4d4d9ee87",
            "Install only one of these two packs: they register the same node.") { Tags = [Tested("db76324"), new("Plus", "Upscaler-Plus processes the whole clip at once.")] },
        new("Comfyui_Minimax_h3_latent_Upscaler", "https://github.com/LBH-123-AI/Comfyui_Minimax_h3_latent_Upscaler/tree/d7c01b9011f2e8439493f6c02c29995a27df276f")
            { Tags = [Tested("d7c01b9"), new("Original", "The original pack. It processes the clip in chunks.")] }]) { Node = H3PreviewUpscaling.Node };
    public static readonly H3Requirement UpscalerFile = new("upscaler-file", H3RequirementKind.File, "Latent upscaler checkpoint", "models/latent_upscale_models", [
        new("minimax_h3_latent_upscaler_3d_conv_v1_fp16.safetensors", "https://huggingface.co/LBH-123-AI/Minimax_h3_latent_Upscaler/blob/main/minimax_h3_latent_upscaler_3d_conv_v1/minimax_h3_latent_upscaler_3d_conv_v1_fp16.safetensors")
            { Size = "0.7 GB", Format = "fp16" }]);
    public static readonly H3Requirement SageNodes = new("sage-nodes", H3RequirementKind.NodePack, "KJNodes", "custom_nodes", [
        new("ComfyUI-KJNodes", "https://github.com/kijai/ComfyUI-KJNodes", "Also needs the sageattention package in ComfyUI's Python environment.")]) { Node = H3Performance.SageNode };

    // What each preset needs beyond the required files.
    public static IReadOnlyList<H3Requirement> ForPreset(string key) => key switch
    {
        "turbo4" or H3Presets.TurboLight => [TurboLora], "turbo8" => [Turbo8Lora], "larry" => [LarryNodes, LarryLora],
        "pdd" => [PddNodes, PddFile], H3HyperFlow.Key => [HyperFlowLora], "spectrum" => [SpectrumNodes], _ => []
    };
    public static IReadOnlyList<H3Requirement> UpscaledPreview { get; } = [UpscalerNodes, UpscalerFile];
    public static IReadOnlyList<H3Requirement> All { get; } =
        [.. Required, TurboLora, LarryNodes, LarryLora, PddNodes, PddFile, HyperFlowLora, Turbo8Lora, SpectrumNodes, UpscalerNodes, UpscalerFile, SageNodes];

    public static string? Selected(H3Requirement r, H3Settings s) => r.Id switch
    {
        "model" => s.Model, "encoder" => s.Encoder, "video-vae" => s.VideoVae, "audio-vae" => s.AudioVae,
        "turbo4-lora" => s.TurboLora, "turbo8-lora" => s.Turbo8StepLora, "larry-lora" => H3Presets.Checkpoint("larry", s),
        "pdd-file" => H3Presets.Checkpoint("pdd", s), "hyperflow-lora" => H3HyperFlow.Checkpoint(s), "upscaler-file" => s.LatentUpscaler, _ => null
    };
    public static void Select(H3Requirement r, H3Settings s, string value)
    {
        switch (r.Id)
        {
            case "model": s.Model = value; break; case "encoder": s.Encoder = value; break;
            case "video-vae": s.VideoVae = value; break; case "audio-vae": s.AudioVae = value; break;
            case "turbo4-lora": s.TurboLora = value; break; case "turbo8-lora": s.Turbo8StepLora = value; break;
            case "larry-lora": s.LarryLora = value; break; case "pdd-file": s.PddCheckpoint = value; break;
            case "hyperflow-lora": s.HyperFlowLora = value; break; case "upscaler-file": s.LatentUpscaler = value; break;
        }
    }
    // Files installed in the requirement's folder, as the ComfyUI loader lists them.
    public static IReadOnlyList<string> Installed(H3Requirement r, H3Configuration c) => r.Folder switch
    {
        "models/diffusion_models" => c.InstalledModels, "models/text_encoders" => c.InstalledEncoders, "models/vae" => c.InstalledVaes,
        "models/loras" => c.InstalledLoras, "models/latent_upscale_models" => c.LatentUpscalers,
        "models/pdd_acc" => c.Presets.FirstOrDefault(p => p.Key == "pdd")?.Files ?? [], _ => []
    };
    // Installed files whose names discovery recognizes as H3 files for this role, such as renamed Singularity builds.
    public static IReadOnlyList<string> Recognized(H3Requirement r, H3Configuration c) => r.Id switch
    {
        "model" => c.Models, "encoder" => c.Encoders,
        "video-vae" => c.Vaes.Where(v => v.Contains("video_vae", StringComparison.OrdinalIgnoreCase)).ToArray(),
        "audio-vae" => c.Vaes.Where(v => v.Contains("audio_vae", StringComparison.OrdinalIgnoreCase)).ToArray(),
        "turbo4-lora" or "turbo8-lora" => c.Loras, "hyperflow-lora" => c.Presets.FirstOrDefault(p => p.Key == H3HyperFlow.Key)?.Files ?? [], _ => []
    };
    public static bool Suggested(H3Requirement r, string file) =>
        r.Downloads.Any(d => string.Equals(d.Name, Path.GetFileName(file.Replace('\\', '/')), StringComparison.OrdinalIgnoreCase));
    public static H3RequirementStatus Status(H3Requirement r, H3Settings s, H3Configuration? c)
    {
        var selected = Selected(r, s);
        if (c is not { DiscoverySucceeded: true }) return new(r, H3RequirementState.NotChecked, selected);
        if (r.Kind == H3RequirementKind.NodePack)
            return new(r, r.Node is { } node && c.Nodes.Contains(node) ? H3RequirementState.Installed : H3RequirementState.Missing, null);
        if (string.IsNullOrEmpty(selected) || !Installed(r, c).Contains(selected)) return new(r, H3RequirementState.Missing, selected);
        return new(r, Suggested(r, selected) ? H3RequirementState.Installed : H3RequirementState.OtherFile, selected);
    }
}
