using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public static class MossImagePolicy
{
    public const int MaximumReferences = 4;
    public static readonly IReadOnlyList<string> EditFormats = Array.AsReadOnly(new[] { "Moss-Image-Edit", "ai-toolkit", "ai-toolkit (kv_cache off)" });
    public static MossImageSettings Settings(AiSettings settings) => settings.MossImage ?? new();
    public static MossImageSettings Normalize(MossImageSettings value) => value with
    { Model = value.Model.Trim(), TextEncoder = value.TextEncoder.Trim(), Vae = value.Vae.Trim(), EditLora = value.EditLora.Trim() };

    public static void ValidateSettings(MossImageSettings? value)
    {
        if (value is null) return;
        static bool InvalidFile(string? name) => string.IsNullOrWhiteSpace(name) || name.Length > 2000 || name.IndexOfAny(['\0', '\r', '\n']) >= 0;
        if (InvalidFile(value.Model) || InvalidFile(value.TextEncoder) || InvalidFile(value.Vae) ||
            value.EditLora is null || value.NegativePrompt is null || !EditFormats.Contains(value.EditFormat) ||
            value.EditLora.Length > 0 && InvalidFile(value.EditLora) ||
            value.Steps is < 1 or > 150 || value.EditSteps is < 1 or > 150 ||
            !double.IsFinite(value.Guidance) || value.Guidance is < 1 or > 20 ||
            !double.IsFinite(value.EditGuidance) || value.EditGuidance is < 1 or > 20 ||
            !double.IsFinite(value.Shift) || value.Shift is <= 0 or > 100)
            throw new WorkspaceStoreException("Check the MOSS-Image files, edit format, steps (1–150), guidance (1–20), and shift (above 0, up to 100).");
    }

    internal static AssetGenerationMetadata Metadata(AiImageJobRequest request, Guid jobId, AiBatchCandidate candidate)
    {
        var settings = Settings(request.Settings);
        var size = QwenImage21Policy.OutputSize(request);
        return new()
        {
            AiJobId = jobId, BatchId = request.BatchId, CandidateNumber = candidate.Number, Look = request.Look,
            Workflow = ImageWorkflow.MossImage, Prompt = request.Prompt.Trim(), Seed = candidate.Seed,
            AspectRatio = request.AspectRatio, Resolution = request.Create?.Resolution ?? request.Edit!.Resolution,
            DiffusionModel = settings.Model, TextEncoder = settings.TextEncoder, Vae = settings.Vae,
            Steps = request.Edit is null ? settings.Steps : settings.EditSteps, Loras = request.AppliedLoras,
            MossImage = new(settings, size.Width, size.Height),
            Edit = request.Edit is { } edit ? new()
            {
                SourceAssetId = edit.SourceAssetId, SourceImageId = edit.SourceImageId,
                References = request.Inputs.Select(i => i.Reference).ToArray(), ReferenceLooks = edit.ReferenceLooks,
                SourceCrop = edit.SourceCrop, ReferenceCrops = edit.ReferenceCrops, Regions = edit.Regions,
                Lora = settings.EditLora, LoraStrength = 1, ReferenceBoost = 0, GroundingPixels = 0, FitMode = "moss-reference-latent"
            } : null
        };
    }

    public static bool InvalidMetadata(AssetGenerationMetadata? metadata, int width, int height)
    {
        if (metadata?.Workflow != ImageWorkflow.MossImage) return metadata?.MossImage is not null;
        if (metadata.MossImage is not { Settings: { } settings } output) return true;
        try { ValidateSettings(settings); } catch (WorkspaceStoreException) { return true; }
        return metadata.DiffusionModel != settings.Model || metadata.TextEncoder != settings.TextEncoder || metadata.Vae != settings.Vae ||
            metadata.Steps != (metadata.Edit is null ? settings.Steps : settings.EditSteps) ||
            output.Width < 16 || output.Height < 16 || output.Width % 16 != 0 || output.Height % 16 != 0 ||
            (long)output.Width * output.Height > 4_500_000 ||
            metadata.Edit?.Regional is null && (output.Width != width || output.Height != height) ||
            metadata.Edit is { } edit && (string.IsNullOrWhiteSpace(settings.EditLora) || edit.Lora != settings.EditLora || edit.LoraStrength != 1);
    }
}
