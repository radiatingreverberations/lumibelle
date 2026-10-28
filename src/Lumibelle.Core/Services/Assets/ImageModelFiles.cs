using lumibelle.Models;

namespace lumibelle.Services.Assets;

public enum ImageFileKind { Model, TextEncoder, Vae, EditLora }
public sealed record ImageModelFile(ImageFileKind Kind, string Name, string Url, string Format, double Gigabytes,
    string? Variant = null, string? Note = null)
{
    public string Publisher => new Uri(Url).Segments[1].TrimEnd('/');
    public string Size => Gigabytes.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " GB";
}
public sealed record ImageModelPackage(string ModelCard, string Guide, IReadOnlyList<ImageModelFile> Files);

// Curated native-ComfyUI files. Links and download sizes checked against publisher metadata, 2026-10-25.
// These are setup references, independent of installed-file discovery and saved selections.
public static class ImageModelFiles
{
    public static string Folder(ImageFileKind kind) => kind switch {
        ImageFileKind.Model => "models/diffusion_models", ImageFileKind.TextEncoder => "models/text_encoders",
        ImageFileKind.Vae => "models/vae", _ => "models/loras"
    };
    public static string Label(ImageFileKind kind) => kind switch {
        ImageFileKind.Model => "Model", ImageFileKind.TextEncoder => "Text encoder", ImageFileKind.Vae => "VAE", _ => "Edit LoRA"
    };
    private static ImageModelFile F(ImageFileKind kind, string repo, string path, string format, double size, string? variant = null, string? note = null) =>
        new(kind, path.Split('/')[^1], $"https://huggingface.co/{repo}/blob/main/{path}", format, size, variant, note);
    private const ImageFileKind Model = ImageFileKind.Model, Encoder = ImageFileKind.TextEncoder, Vae = ImageFileKind.Vae, Lora = ImageFileKind.EditLora;
    private const string Krea = "Comfy-Org/Krea-2", Moss = "Comfy-Org/MOSS-Image", Fibo = "Comfy-Org/Fibo-2.0",
        Qwen = "Comfy-Org/Qwen-Image-2.1", Flux3 = "black-forest-labs/flux-3-image-klein-comfy", Klein = "Comfy-Org/flux2-klein-9B";
    private const string Nvidia4Bit = "NVFP4 requires a supported NVIDIA Blackwell GPU and a ComfyUI build with NVFP4 support.";

    public static ImageModelPackage? For(ImageWorkflow workflow, string? variant = null) => workflow switch {
        ImageWorkflow.Krea2 => new($"https://huggingface.co/{Krea}", "https://github.com/Comfy-Org/workflow_templates/blob/main/templates/image_krea2_turbo_t2i.json", [
            F(Model, Krea, "diffusion_models/krea2_turbo_int8_convrot.safetensors", "INT8", 13.49),
            F(Model, Krea, "diffusion_models/krea2_turbo_fp8_scaled.safetensors", "FP8", 13.14),
            F(Model, Krea, "diffusion_models/krea2_turbo_bf16.safetensors", "BF16", 26.28),
            F(Encoder, Krea, "text_encoders/qwen3vl_4b_fp8_scaled.safetensors", "FP8", 5.24),
            F(Encoder, Krea, "text_encoders/qwen3vl_4b_int8_convrot.safetensors", "INT8", 4.86),
            F(Encoder, Krea, "text_encoders/qwen3vl_4b_bf16.safetensors", "BF16", 8.88),
            F(Vae, Krea, "vae/qwen_image_vae.safetensors", "VAE", .25),
            F(Lora, "conradlocke/krea2-identity-edit", "krea2_identity_edit_v1_2.safetensors", "LoRA", 1.83)
        ]),
        ImageWorkflow.Flux2Klein9bKv => new("https://huggingface.co/black-forest-labs/FLUX.2-klein-9b-kv-fp8", "https://github.com/Comfy-Org/workflow_templates/blob/main/templates/image_flux2_klein_9b_kv_image_edit.json", [
            F(Model, "black-forest-labs/FLUX.2-klein-9b-kv-fp8", "flux-2-klein-9b-kv-fp8.safetensors", "FP8", 9.82),
            F(Model, "black-forest-labs/FLUX.2-klein-9b-kv", "flux-2-klein-9b-kv.safetensors", "BF16", 18.16),
            F(Encoder, Klein, "split_files/text_encoders/qwen_3_8b_fp8mixed.safetensors", "FP8", 8.66),
            F(Encoder, Klein, "split_files/text_encoders/qwen_3_8b.safetensors", "BF16", 16.38),
            F(Vae, Klein, "split_files/vae/flux2-vae.safetensors", "VAE", .34)
        ]),
        ImageWorkflow.MossImage => new($"https://huggingface.co/{Moss}", "https://github.com/OpenMOSS/MOSS-Image/blob/main/comfyui/README.md", [
            F(Model, Moss, "diffusion_models/moss_image_base_int8_convrot.safetensors", "INT8", 5.41, note: "Base checkpoint."),
            F(Model, Moss, "diffusion_models/moss_image_base_bf16.safetensors", "BF16", 10.16, note: "Base checkpoint."),
            F(Model, Moss, "diffusion_models/moss_image_sft_int8_convrot.safetensors", "INT8", 5.41, note: "SFT checkpoint."),
            F(Model, Moss, "diffusion_models/moss_image_sft_bf16.safetensors", "BF16", 10.16, note: "SFT checkpoint."),
            F(Encoder, Moss, "text_encoders/qwen3.5_9b_w4a8_gptq.safetensors", "W4A8", 7.34),
            F(Encoder, Moss, "text_encoders/qwen3.5_9b_int8_convrot.safetensors", "INT8", 9.75),
            F(Encoder, Moss, "text_encoders/qwen3.5_9b_bf16.safetensors", "BF16", 19.31),
            F(Vae, Moss, "vae/flux2-vae.safetensors", "VAE", .34),
            F(Lora, "chinoll/Moss-Image-Edit", "adapter_model.safetensors", "LoRA", .30, note: "Use the Moss-Image-Edit format. Give the adapter a distinct filename in models/loras, then select that exact name.")
        ]),
        ImageWorkflow.QwenImage21 => new($"https://huggingface.co/{Qwen}", "https://docs.comfy.org/tutorials/image/qwen/qwen-image-2-1", [
            F(Model, Qwen, "diffusion_models/qwen_image_2.1_int8_convrot.safetensors", "INT8", 7.26, "Base"),
            F(Model, Qwen, "diffusion_models/qwen_image_2.1_bf16.safetensors", "BF16", 14.23, "Base"),
            F(Model, Qwen, "diffusion_models/qwen_image_2.1_turbo_int8_convrot.safetensors", "INT8", 7.26, "Turbo"),
            F(Model, Qwen, "diffusion_models/qwen_image_2.1_turbo_bf16.safetensors", "BF16", 14.23, "Turbo"),
            F(Encoder, Qwen, "text_encoders/qwen3vl_8b_int8_convrot.safetensors", "INT8", 9.35),
            F(Encoder, Qwen, "text_encoders/qwen3vl_8b_w4a8.safetensors", "W4A8", 6.31),
            F(Encoder, Qwen, "text_encoders/qwen3vl_8b_bf16.safetensors", "BF16", 17.53),
            F(Vae, Qwen, "vae/qwen_image_2.1_vae_bf16.safetensors", "BF16", .68)
        ]),
        ImageWorkflow.Flux3ImageKlein => new($"https://huggingface.co/{Flux3}", $"https://huggingface.co/{Flux3}#using-with-comfyui", [
            F(Model, Flux3, "diffusion_models/flux3-image-klein-int8-convrot.safetensors", "INT8", 5.83, "Distilled"),
            F(Model, Flux3, "diffusion_models/flux3-image-klein-bf16.safetensors", "BF16", 11.10, "Distilled"),
            F(Model, Flux3, "diffusion_models/flux3-image-klein-nvfp4.safetensors", "NVFP4", 5.28, "Distilled", Nvidia4Bit),
            F(Model, Flux3, "diffusion_models/flux3-image-klein-base-int8-convrot.safetensors", "INT8", 5.81, "Base"),
            F(Model, Flux3, "diffusion_models/flux3-image-klein-base-bf16.safetensors", "BF16", 11.08, "Base"),
            F(Model, Flux3, "diffusion_models/flux3-image-klein-base-nvfp4.safetensors", "NVFP4", 5.26, "Base", Nvidia4Bit),
            F(Encoder, Flux3, "text_encoders/qwen3vl_4b_int8_convrot.safetensors", "INT8", 4.86),
            F(Encoder, Flux3, "text_encoders/qwen3vl_4b_bf16.safetensors", "BF16", 8.88),
            F(Vae, Flux3, "vae/flux2-vae.safetensors", "VAE", .34)
        ]),
        ImageWorkflow.Fibo2 => new($"https://huggingface.co/{Fibo}", $"https://huggingface.co/{Fibo}", [
            F(Model, Fibo, "diffusion_models/fibo2_dev_int8_convrot.safetensors", "INT8", 8.41, "Base"),
            F(Model, Fibo, "diffusion_models/fibo2_dev_bf16.safetensors", "BF16", 16.45, "Base"),
            F(Model, Fibo, "diffusion_models/fibo2_edit_turbo_int8_convrot.safetensors", "INT8", 8.41, "Turbo"),
            F(Model, Fibo, "diffusion_models/fibo2_edit_turbo_bf16.safetensors", "BF16", 16.45, "Turbo"),
            F(Encoder, Fibo, "text_encoders/qwen3vl_4b_int8_convrot.safetensors", "INT8", 4.86),
            F(Encoder, Fibo, "text_encoders/qwen3vl_4b_bf16.safetensors", "BF16", 8.88),
            F(Vae, Fibo, "vae/flux2-vae.safetensors", "VAE", .34)
        ]),
        ImageWorkflow.ZImage => new(variant == "Turbo" ? "https://huggingface.co/Comfy-Org/z_image_turbo" : "https://huggingface.co/Comfy-Org/z_image",
            variant == "Turbo" ? "https://docs.comfy.org/tutorials/image/z-image/z-image-turbo" : "https://docs.comfy.org/tutorials/image/z-image/z-image", [
            F(Model, "Comfy-Org/z_image", "split_files/diffusion_models/z_image_bf16.safetensors", "BF16", 12.31, "Base"),
            F(Model, "Comfy-Org/z_image", "split_files/diffusion_models/z_image_int8_convrot.safetensors", "INT8", 6.20, "Base"),
            F(Model, "Comfy-Org/z_image_turbo", "split_files/diffusion_models/z_image_turbo_bf16.safetensors", "BF16", 12.31, "Turbo"),
            F(Model, "Comfy-Org/z_image_turbo", "split_files/diffusion_models/z_image_turbo_int8_convrot.safetensors", "INT8", 6.20, "Turbo"),
            F(Encoder, "Comfy-Org/z_image", "split_files/text_encoders/qwen_3_4b.safetensors", "BF16", 8.04),
            F(Encoder, "Comfy-Org/z_image", "split_files/text_encoders/qwen_3_4b_fp8_mixed.safetensors", "FP8", 5.63),
            F(Vae, "Comfy-Org/z_image", "split_files/vae/ae.safetensors", "VAE", .34)
        ]),
        _ => null
    };
}
