using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public static class Flux3ImagePolicy
{
    public const int MaximumReferences = 10;
    public const long MaximumPixels = 4096L * 4096;
    public static Flux3ImageSettings Settings(AiSettings settings) => settings.Flux3Image ?? new();
    public static int Steps(Flux3ImageSettings settings) => settings.Steps ?? (settings.Variant == Flux3ImageVariant.Base ? 50 : 16);
    public static double Guidance(Flux3ImageSettings settings) => settings.Guidance ?? (settings.Variant == Flux3ImageVariant.Base ? 3.5 : 1);
    public static Flux3ImageSettings Normalize(Flux3ImageSettings value) => value with
    { Model = value.Model.Trim(), TextEncoder = value.TextEncoder.Trim(), Vae = value.Vae.Trim() };

    public static void ValidateSettings(Flux3ImageSettings? value)
    {
        if (value is null) return;
        static bool InvalidFile(string? name) => string.IsNullOrWhiteSpace(name) || name.Length > 2000 || name.IndexOfAny(['\0', '\r', '\n']) >= 0;
        if (InvalidFile(value.Model) || InvalidFile(value.TextEncoder) || InvalidFile(value.Vae) || !Enum.IsDefined(value.Variant) ||
            Steps(value) is < 1 or > 150 || !double.IsFinite(Guidance(value)) || Guidance(value) is < 1 or > 20 ||
            value.Variant == Flux3ImageVariant.Distilled && Guidance(value) != 1 ||
            !double.IsFinite(value.ReferenceMegapixels) || value.ReferenceMegapixels is < .01 or > 4)
            throw new WorkspaceStoreException("Choose the FLUX 3 files, variant, steps (1–150), reference size (0.01–4 MP) and guidance. Distilled uses CFG 1; Base accepts CFG 1–20.");
        var file = Path.GetFileName(value.Model.Replace('\\', '/'));
        if (file.StartsWith("flux3-image-klein-", StringComparison.OrdinalIgnoreCase) &&
            file.Contains("-base-", StringComparison.OrdinalIgnoreCase) != (value.Variant == Flux3ImageVariant.Base))
            throw new WorkspaceStoreException("Choose the FLUX 3 checkpoint variant matching the selected Base or Distilled diffusion file.");
    }

    internal static AssetGenerationMetadata Metadata(AiImageJobRequest request, Guid jobId, AiBatchCandidate candidate) =>
        Metadata(request.Settings, request.Prompt, request.AspectRatio, request.Create?.Resolution ?? request.Edit!.Resolution,
            QwenImage21Policy.OutputSize(request), request.Edit, request.Inputs.Select(i => i.Reference).ToArray(), request.AppliedLoras, candidate.Seed)
        with { AiJobId = jobId, BatchId = request.BatchId, CandidateNumber = candidate.Number, Look = request.Look };

    internal static AssetGenerationMetadata Metadata(AiSettings settings, string prompt, string aspect, int resolution,
        (int Width, int Height) size, ReferenceEditRequest? edit, IReadOnlyList<AssetImageReference> references, IReadOnlyList<AppliedLora> loras, long seed)
    {
        var flux = Settings(settings);
        return new()
        {
            Workflow = ImageWorkflow.Flux3ImageKlein, Prompt = prompt.Trim(), Seed = seed, AspectRatio = aspect, Resolution = resolution,
            DiffusionModel = flux.Model, TextEncoder = flux.TextEncoder, Vae = flux.Vae, Steps = Steps(flux), Loras = loras,
            Flux3Image = new(flux, size.Width, size.Height),
            Edit = edit is null ? null : new()
            {
                SourceAssetId = edit.SourceAssetId, SourceImageId = edit.SourceImageId, References = references, ReferenceLooks = edit.ReferenceLooks,
                SourceCrop = edit.SourceCrop, ReferenceCrops = edit.ReferenceCrops, Regions = edit.Regions,
                Lora = "", LoraStrength = 0, ReferenceBoost = 0, GroundingPixels = 0, FitMode = "flux3-reference-latent"
            }
        };
    }

    public static bool InvalidMetadata(AssetGenerationMetadata? metadata, int width, int height)
    {
        if (metadata?.Workflow != ImageWorkflow.Flux3ImageKlein) return metadata?.Flux3Image is not null;
        if (metadata.Flux3Image is not { Settings: { } settings } output) return true;
        try { ValidateSettings(settings); } catch (WorkspaceStoreException) { return true; }
        return metadata.DiffusionModel != settings.Model || metadata.TextEncoder != settings.TextEncoder || metadata.Vae != settings.Vae ||
            metadata.Steps != Steps(settings) || output.Width is < 32 or > 16384 || output.Height is < 32 or > 16384 ||
            output.Width % 32 != 0 || output.Height % 32 != 0 || (long)output.Width * output.Height > MaximumPixels ||
            metadata.Edit?.Regional is null && (output.Width != width || output.Height != height);
    }
}
