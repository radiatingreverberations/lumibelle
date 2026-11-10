using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public static class MiniMaxH3ImagePolicy
{
    public const int MaximumReferences = 16;
    public const long MaximumPixels = 17_000_000;
    public static (int Width, int Height) Size(string aspect, byte[]? source, int resolution)
    {
        if (resolution is not (2048 or 4096)) throw new AiGenerationException("Choose 4 MP or native 16 MP for MiniMax H3 Image.");
        if (aspect == ImageAspectPolicy.FromImage1) return ImageAspectPolicy.Size(aspect, source, resolution);
        var native = aspect switch
        {
            "1:1" => (4096, 4096), "4:3" => (4736, 3552), "3:4" => (3552, 4736),
            "3:2" => (5024, 3360), "2:3" => (3360, 5024), "16:9" => (5472, 3072),
            "9:16" => (3072, 5472), "21:9" => (6272, 2688),
            _ => throw new AiGenerationException("Choose a supported image aspect ratio.")
        };
        if (resolution == 4096) return native;
        return (native.Item1 / 64 * 32, native.Item2 / 64 * 32);
    }

    public static MiniMaxH3ImageSettings Settings(AiSettings settings) => settings.MiniMaxH3Image ?? new();
    public static MiniMaxH3ImageSettings Normalize(MiniMaxH3ImageSettings value) => value with
    { Model = value.Model.Trim(), TextEncoder = value.TextEncoder.Trim(), Vae = value.Vae.Trim() };

    public static void ValidateSettings(MiniMaxH3ImageSettings? value)
    {
        if (value is null) return;
        if (new[] { value.Model, value.TextEncoder, value.Vae }.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 2000 || name.IndexOfAny(['\0', '\r', '\n']) >= 0))
            throw new WorkspaceStoreException("Choose the MiniMax H3 Image diffusion model, H3 text encoder and image VAE.");
    }

    internal static AssetGenerationMetadata Metadata(AiImageJobRequest request, Guid jobId, AiBatchCandidate candidate) =>
        Metadata(request.Settings, request.Prompt, request.AspectRatio, request.Create?.Resolution ?? request.Edit!.Resolution,
            QwenImage21Policy.OutputSize(request), request.Edit, request.Inputs.Select(i => i.Reference).ToArray(), request.AppliedLoras, candidate.Seed)
        with { AiJobId = jobId, BatchId = request.BatchId, CandidateNumber = candidate.Number, Look = request.Look };

    internal static AssetGenerationMetadata Metadata(AiSettings settings, string prompt, string aspect, int resolution,
        (int Width, int Height) size, ReferenceEditRequest? edit, IReadOnlyList<AssetImageReference> references, IReadOnlyList<AppliedLora> loras, long seed)
    {
        var files = Settings(settings);
        return new()
        {
            Workflow = ImageWorkflow.MiniMaxH3Image, Prompt = prompt.Trim(), Seed = seed, AspectRatio = aspect, Resolution = resolution,
            DiffusionModel = files.Model, TextEncoder = files.TextEncoder, Vae = files.Vae, Steps = 6, Loras = loras,
            MiniMaxH3Image = new(files, size.Width, size.Height),
            Edit = edit is null ? null : new()
            {
                SourceAssetId = edit.SourceAssetId, SourceImageId = edit.SourceImageId, References = references, ReferenceLooks = edit.ReferenceLooks,
                SourceCrop = edit.SourceCrop, ReferenceCrops = edit.ReferenceCrops, Regions = edit.Regions,
                Lora = "", LoraStrength = 0, ReferenceBoost = 0, GroundingPixels = 0, FitMode = "minimax-h3-image-reference-latent"
            }
        };
    }

    public static bool InvalidMetadata(AssetGenerationMetadata? metadata, int width, int height)
    {
        if (metadata?.Workflow != ImageWorkflow.MiniMaxH3Image) return metadata?.MiniMaxH3Image is not null;
        if (metadata.MiniMaxH3Image is not { Settings: { } settings } output) return true;
        try { ValidateSettings(settings); } catch (WorkspaceStoreException) { return true; }
        return metadata.DiffusionModel != settings.Model || metadata.TextEncoder != settings.TextEncoder || metadata.Vae != settings.Vae ||
            metadata.Steps != 6 || output.Width is < 32 or > 16384 || output.Height is < 32 or > 16384 ||
            output.Width % 32 != 0 || output.Height % 32 != 0 || (long)output.Width * output.Height > MaximumPixels ||
            metadata.Edit?.Regional is null && (output.Width != width || output.Height != height);
    }
}
