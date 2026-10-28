using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public static class CaptionImagePolicy
{
    public const int MaximumFiboReferences = 4;
    public static bool Supports(ImageWorkflow workflow) => workflow is ImageWorkflow.Fibo2 or ImageWorkflow.ZImage;
    public static CaptionImageSettings Defaults(ImageWorkflow workflow) => workflow switch
    {
        ImageWorkflow.Fibo2 => new() { Model = "fibo2_dev_int8_convrot.safetensors", TextEncoder = "qwen3vl_4b_int8_convrot.safetensors", Vae = "flux2-vae.safetensors" },
        ImageWorkflow.ZImage => new() { Model = "z_image_turbo_bf16.safetensors", TextEncoder = "qwen_3_4b.safetensors", Vae = "ae.safetensors", Variant = CaptionImageVariant.Turbo },
        _ => throw new AiGenerationException("Choose a supported caption image workflow.")
    };
    public static CaptionImageSettings Settings(AiSettings settings, ImageWorkflow workflow) => workflow switch
    {
        ImageWorkflow.Fibo2 => settings.Fibo2 ?? Defaults(workflow), ImageWorkflow.ZImage => settings.ZImage ?? Defaults(workflow),
        _ => throw new AiGenerationException("Choose a supported caption image workflow.")
    };
    public static AiSettings Capture(AiSettings settings, ImageWorkflow workflow) => workflow switch
    {
        ImageWorkflow.Fibo2 => settings with { Fibo2 = Settings(settings, workflow) },
        ImageWorkflow.ZImage => settings with { ZImage = Settings(settings, workflow) }, _ => settings
    };
    public static int Steps(CaptionImageSettings value, ImageWorkflow workflow) => value.Steps ??
        (workflow == ImageWorkflow.Fibo2 ? value.Variant == CaptionImageVariant.Turbo ? 4 : 50 : value.Variant == CaptionImageVariant.Turbo ? 8 : 25);
    public static double Guidance(CaptionImageSettings value, ImageWorkflow workflow) => value.Guidance ??
        (value.Variant == CaptionImageVariant.Turbo ? 1 : workflow == ImageWorkflow.Fibo2 ? 5 : 4);
    public static double Shift(CaptionImageSettings value, ImageWorkflow workflow) => workflow == ImageWorkflow.Fibo2 ? 1.15 : value.Shift ?? 3;
    public static string Sampler(ImageWorkflow workflow) => workflow == ImageWorkflow.Fibo2 ? "euler" : "res_multistep";
    public static CaptionImageSettings Normalize(CaptionImageSettings value) => value with
    { Model = value.Model.Trim(), TextEncoder = value.TextEncoder.Trim(), Vae = value.Vae.Trim(), NegativePrompt = value.NegativePrompt.Trim() };

    public static void ValidateSettings(CaptionImageSettings? value, ImageWorkflow workflow)
    {
        if (value is null) return;
        static bool InvalidFile(string? name) => string.IsNullOrWhiteSpace(name) || name.Length > 2000 || name.IndexOfAny(['\0', '\r', '\n']) >= 0;
        if (!Supports(workflow) || InvalidFile(value.Model) || InvalidFile(value.TextEncoder) || InvalidFile(value.Vae) || !Enum.IsDefined(value.Variant) ||
            Steps(value, workflow) is < 1 or > 150 || !double.IsFinite(Guidance(value, workflow)) || Guidance(value, workflow) is < 1 or > 20 ||
            !double.IsFinite(Shift(value, workflow)) || Shift(value, workflow) is <= 0 or > 20 ||
            value.NegativePrompt is null || value.NegativePrompt.Length > 20000 ||
            value.Variant == CaptionImageVariant.Turbo && (Guidance(value, workflow) != 1 || !string.IsNullOrWhiteSpace(value.NegativePrompt)) ||
            workflow == ImageWorkflow.Fibo2 && value.Shift is not null)
            throw new WorkspaceStoreException("Choose matching model files and a valid variant, steps, guidance and shift. Turbo uses CFG 1 without a negative prompt; Fibo uses its native shift.");
        var file = Path.GetFileName(value.Model.Replace('\\', '/'));
        if ((file.StartsWith("fibo2_", StringComparison.OrdinalIgnoreCase) || file.StartsWith("z_image_", StringComparison.OrdinalIgnoreCase)) &&
            file.Contains("turbo", StringComparison.OrdinalIgnoreCase) != (value.Variant == CaptionImageVariant.Turbo))
            throw new WorkspaceStoreException("Choose the Base/Dev or Turbo variant matching the selected diffusion file.");
    }

    public static void ValidateRequest(AiImageJobRequest request)
    {
        if (!Supports(request.Workflow)) return;
        if (request.Workflow == ImageWorkflow.Fibo2 ? request.Settings.Fibo2 is null : request.Settings.ZImage is null)
            throw new AiGenerationException("Capture the model files and variant before queueing.");
        ValidateSettings(Settings(request.Settings, request.Workflow), request.Workflow);
        if (request.Workflow == ImageWorkflow.ZImage && request.Edit is not null)
            throw new AiGenerationException("Z-Image Base and Turbo support creation here. Choose an editing workflow for references.");
        if (request.Workflow == ImageWorkflow.Fibo2 && Settings(request.Settings, request.Workflow).Variant == CaptionImageVariant.Turbo && request.Edit is null)
            throw new AiGenerationException("Fibo Edit Turbo requires at least one reference. Choose Fibo Dev for creation.");
    }

    internal static AssetGenerationMetadata Metadata(AiImageJobRequest request, Guid jobId, AiBatchCandidate candidate) =>
        Metadata(request.Settings, request.Workflow, request.Prompt, request.AspectRatio, request.Create?.Resolution ?? request.Edit!.Resolution,
            QwenImage21Policy.OutputSize(request), request.Edit, request.Inputs.Select(i => i.Reference).ToArray(), request.AppliedLoras, candidate.Seed)
        with { AiJobId = jobId, BatchId = request.BatchId, CandidateNumber = candidate.Number, Look = request.Look };

    internal static AssetGenerationMetadata Metadata(AiSettings settings, ImageWorkflow workflow, string prompt, string aspect, int resolution,
        (int Width, int Height) size, ReferenceEditRequest? edit, IReadOnlyList<AssetImageReference> references, IReadOnlyList<AppliedLora> loras, long seed)
    {
        var value = Settings(settings, workflow);
        return new()
        {
            Workflow = workflow, Prompt = prompt.Trim(), AspectRatio = aspect, Resolution = resolution, Seed = seed, Steps = Steps(value, workflow),
            DiffusionModel = value.Model, TextEncoder = value.TextEncoder, Vae = value.Vae, Loras = loras,
            CaptionImage = new(value, size.Width, size.Height, Steps(value, workflow), Guidance(value, workflow), Shift(value, workflow), Sampler(workflow)),
            Edit = edit is null ? null : new()
            {
                SourceAssetId = edit.SourceAssetId, SourceImageId = edit.SourceImageId, References = references, ReferenceLooks = edit.ReferenceLooks,
                SourceCrop = edit.SourceCrop, ReferenceCrops = edit.ReferenceCrops, Regions = edit.Regions,
                Lora = "", LoraStrength = 0, ReferenceBoost = 0, GroundingPixels = 0, FitMode = "fibo2-reference-latent"
            }
        };
    }
    public static bool InvalidMetadata(AssetGenerationMetadata? metadata, int width, int height)
    {
        if (metadata is null || !Supports(metadata.Workflow)) return metadata?.CaptionImage is not null;
        if (metadata.CaptionImage is not { Settings: { } value } output) return true;
        try { ValidateSettings(value, metadata.Workflow); } catch (WorkspaceStoreException) { return true; }
        return metadata.DiffusionModel != value.Model || metadata.TextEncoder != value.TextEncoder || metadata.Vae != value.Vae ||
            metadata.Steps != output.Steps || output.Steps != Steps(value, metadata.Workflow) || output.Guidance != Guidance(value, metadata.Workflow) ||
            output.Shift != Shift(value, metadata.Workflow) || output.Sampler != Sampler(metadata.Workflow) ||
            output.Width is < 32 or > 16384 || output.Height is < 32 or > 16384 || output.Width % 32 != 0 || output.Height % 32 != 0 ||
            (long)output.Width * output.Height > QwenImage21Policy.MaximumNativePixels ||
            metadata.Edit?.Regional is null && (output.Width != width || output.Height != height) ||
            metadata.Workflow == ImageWorkflow.ZImage && metadata.Edit is not null ||
            metadata.Workflow == ImageWorkflow.Fibo2 && value.Variant == CaptionImageVariant.Turbo && metadata.Edit is null;
    }
}
