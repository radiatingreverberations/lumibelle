using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;

namespace lumibelle.Services.Assets;

public static class H3ImagePolicy
{
    public const int MaximumReferences = 9;
    public const long MaximumPixels = 5_000_000;
    public const string FitMode = "h3-reference-conditioning";
    public const string EditOnlyMessage = "MiniMax H3 edits an existing image here. Select an image to edit, or choose a workflow that creates images.";
    public const string StandardPreset = "standard";
    // The encoder node's shortest length (its 17k+5 grid), cut to one latent frame before sampling.
    // Changing any of these means a new profile version, because the handler compares staged metadata with a fresh computation.
    public const int Length = 5;
    public const int LatentFrames = 1;
    public const string RefImageSize = "match";

    public static string Preset(ReferenceEditRequest? edit) => edit?.H3Preset ?? StandardPreset;
    public static H3Sampling Sampling(H3Settings settings, string preset) => H3Policy.Sampling(new Shot { GenerationPreset = preset }, settings);
    public static string? PresetIssue(string preset) => H3Presets.Offered.Contains(preset) ? null
        : H3Presets.Retired(preset) ? H3Presets.RemovedIssue(preset) : "Choose a supported MiniMax H3 generation preset.";

    internal static AssetGenerationMetadata Metadata(AiImageJobRequest request, Guid jobId, AiBatchCandidate candidate)
    {
        var edit = request.Edit ?? throw new AiGenerationException(EditOnlyMessage);
        var h3 = request.Settings.H3; var preset = Preset(edit); var sampling = Sampling(h3, preset);
        var size = QwenImage21Policy.OutputSize(request);
        return new()
        {
            AiJobId = jobId, BatchId = request.BatchId, CandidateNumber = candidate.Number, Look = request.Look,
            Workflow = ImageWorkflow.MiniMaxH3, Prompt = request.Prompt.Trim(), Seed = candidate.Seed,
            AspectRatio = request.AspectRatio, Resolution = edit.Resolution,
            DiffusionModel = H3Presets.Model(preset, h3), TextEncoder = h3.Encoder, Vae = h3.VideoVae, Steps = sampling.Steps, Loras = request.AppliedLoras,
            H3Image = new(h3.AudioVae, preset, sampling.Profile, sampling.Sampler, sampling.Scheduler, H3Presets.Checkpoint(preset, h3), Length, LatentFrames, RefImageSize, size.Width, size.Height),
            Edit = new()
            {
                SourceAssetId = edit.SourceAssetId, SourceImageId = edit.SourceImageId,
                References = request.Inputs.Select(i => i.Reference).ToArray(), ReferenceLooks = edit.ReferenceLooks,
                SourceCrop = edit.SourceCrop, ReferenceCrops = edit.ReferenceCrops, Regions = edit.Regions,
                Lora = "", LoraStrength = 0, ReferenceBoost = 0, GroundingPixels = 0, FitMode = FitMode
            }
        };
    }

    // Checks shape rather than the current constants, so images made under an earlier recipe still load.
    public static bool InvalidMetadata(AssetGenerationMetadata? metadata, int width, int height)
    {
        if (metadata?.Workflow != ImageWorkflow.MiniMaxH3) return metadata?.H3Image is not null;
        if (metadata.H3Image is not { } output || metadata.Edit is null) return true;
        return new[] { metadata.DiffusionModel, metadata.TextEncoder, metadata.Vae, output.AudioVae, output.Preset, output.SamplingProfile, output.Sampler, output.Scheduler, output.RefImageSize }.Any(string.IsNullOrWhiteSpace) ||
            !H3Presets.Keys.Contains(output.Preset) || metadata.Steps < 1 || output.Length < 5 || (output.Length - 5) % 17 != 0 || output.LatentFrames < 1 ||
            output.Width < 32 || output.Height < 32 || output.Width % 32 != 0 || output.Height % 32 != 0 || (long)output.Width * output.Height > MaximumPixels ||
            metadata.Edit.Regional is null && (output.Width != width || output.Height != height);
    }
}
