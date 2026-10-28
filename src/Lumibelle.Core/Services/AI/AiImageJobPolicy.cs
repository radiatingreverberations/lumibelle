using lumibelle.Models;
using lumibelle.Services.Assets;

namespace lumibelle.Services.AI;

internal static class AiImageJobPolicy
{
    public static string Profile(ImageWorkflow workflow, bool edit) => (workflow, edit) switch
    {
        (ImageWorkflow.Krea2, false) => "krea-create-v1", (ImageWorkflow.Krea2, true) => "krea-edit-v1",
        (ImageWorkflow.QwenImage21, false) => "qwen-image21-create-v1", (ImageWorkflow.QwenImage21, true) => "qwen-image21-edit-v1",
        (ImageWorkflow.MossImage, false) => "moss-image-create-v1", (ImageWorkflow.MossImage, true) => "moss-image-edit-v2",
        (ImageWorkflow.Flux3ImageKlein, false) => "flux3-klein-create-v1", (ImageWorkflow.Flux3ImageKlein, true) => "flux3-klein-edit-v1",
        (ImageWorkflow.Fibo2, false) => "fibo2-create-v1", (ImageWorkflow.Fibo2, true) => "fibo2-edit-v1",
        (ImageWorkflow.ZImage, false) => "z-image-create-v1",
        (ImageWorkflow.Flux2Klein9bKv, false) => "klein-create-v1", (ImageWorkflow.Flux2Klein9bKv, true) => "klein-edit-v1",
        (ImageWorkflow.CodexImages, false) => "codex-create-v1", (ImageWorkflow.CodexImages, true) => "codex-edit-v1",
        _ => throw new AiGenerationException("This image workflow is unavailable.")
    };
    public static void Validate(AiImageJobRequest request)
    {
        if (request is null || request.Version != 1 || request.BatchId == Guid.Empty || request.ProjectId == Guid.Empty || request.AssetId == Guid.Empty ||
            request.SourceRevision < 0 || request.Settings is null || (request.Create is null) == (request.Edit is null) || request.Inputs is null ||
            request.Inputs.Any(i => i is null || i.Reference is null || i.Context is null || i.Png is null || i.Png.Length == 0 || i.Png.Length > ImageWorkflowLimits.MaximumPreparedImageBytes(request.Workflow)))
            throw new AiGenerationException("The captured image request is incomplete.");
        VisualStyles.Validate(request.Create?.VisualStyle ?? request.Edit?.VisualStyle);
        if (request.Regional is { } regional)
        {
            RegionalImageEdits.Validate(regional.Selection);
            if (request.Edit is null || regional.Selection.Source != new AssetImageReference(request.Edit.SourceAssetId, request.Edit.SourceImageId) ||
                RegionalImageEdits.Hash(regional.OriginalPng) != regional.Selection.SourceHash ||
                !System.Text.Json.JsonElement.DeepEquals(System.Text.Json.JsonSerializer.SerializeToElement(regional.Selection), System.Text.Json.JsonSerializer.SerializeToElement(request.Edit.Regions?.SingleOrDefault(r => r.Source == regional.Selection.Source))))
                throw new AiGenerationException("The restoration original does not match the captured source selection.");
        }
        else if (request.Edit?.Regions?.Any(r => r.Source == new AssetImageReference(request.Edit.SourceAssetId, request.Edit.SourceImageId)) == true)
            throw new AiGenerationException("The restoration original is missing.");
        FileAiSettingsStore.Validate(request.Settings);
        QwenImage21Policy.ValidateRequest(request);
        CaptionImagePolicy.ValidateRequest(request);
        if (request.Workflow == ImageWorkflow.MossImage && (request.Settings.MossImage is null ||
            request.Edit is not null && string.IsNullOrWhiteSpace(request.Settings.MossImage.EditLora)))
            throw new AiGenerationException("Capture the MOSS-Image settings and choose an edit LoRA before editing.");
        var resolution = request.Create?.Resolution ?? request.Edit!.Resolution;
        if (request.Workflow == ImageWorkflow.Flux3ImageKlein && request.Settings.Flux3Image is null)
            throw new AiGenerationException("Capture the FLUX 3 model files and variant before queueing.");
        if (!ImageWorkflowLimits.SupportsResolution(request.Workflow, resolution))
            throw new AiGenerationException("This workflow does not support the selected image resolution.");
        if (request.Workflow == ImageWorkflow.CodexImages && (request.Codex is not { AccountId.Length: > 0, Version.Length: > 0, Model.Length: > 0, Effort.Length: > 0 } || request.Loras.Count != 0 || request.AppliedLoras.Count != 0 || request.Create?.Seed is not null || request.Edit?.Seed is not null))
            throw new AiGenerationException("The Codex image capture is incomplete or contains unsupported settings.");
        var legacyMossEdit = request.Workflow == ImageWorkflow.MossImage && request.Edit is not null && request.Profile == "moss-image-edit-v1";
        if (request.Profile != Profile(request.Workflow, request.Edit is not null) && !legacyMossEdit || string.IsNullOrWhiteSpace(request.Prompt) ||
            !ImageAspectPolicy.IsSupported(request.AspectRatio, request.Edit is not null) || request.Tags is null || request.Tags.Any(t => t is null) ||
            (request.Create?.ProjectId ?? request.Edit?.ProjectId) != request.ProjectId ||
            (request.Create?.Workflow ?? request.Edit?.Workflow) != request.Workflow ||
            request.Look is null || request.Look.AssetId != request.AssetId || LoraPolicy.InvalidSelections(request.Loras) ||
            LoraPolicy.InvalidApplied(request.AppliedLoras, request.Workflow))
            throw new AiGenerationException("The captured prompt, workflow or target does not match its image request.");
        var selected = request.Loras.Where(s => s.Enabled && s.Strength != 0).ToArray();
        if (selected.Length != request.AppliedLoras.Count || selected.Where((s, i) => !LoraPolicy.Same(s.Reference, request.AppliedLoras[i].Reference) ||
                s.Reference.Workflow != request.Workflow.LoraWorkflow() || s.Strength != request.AppliedLoras[i].Strength).Any() ||
            request.AppliedLoras.Any(l => AiProviderRegistry.NormalizeComfyUrl(l.Reference.ComfyUrl) != AiProviderRegistry.NormalizeComfyUrl(request.Settings.ComfyUrl)))
            throw new AiGenerationException("Applied LoRAs must match the exact enabled selections and server.");
        var count = request.Create?.Count ?? request.Edit!.Count; var seed = request.Create?.Seed ?? request.Edit?.Seed;
        if (count is < 1 or > 4 || seed is < 0 || seed > long.MaxValue - count + 1)
            throw new AiGenerationException("Choose one to four initial candidates and a valid seed.");
        if (request.Edit is { } edit)
        {
            ReferenceEditInputs.Capture(edit, request.Inputs.Select(i => new ReferenceImageSource(i.Reference.AssetId, i.Reference.ImageId, Stream.Null)).ToArray(),
                ImageWorkflowLimits.MaximumReferences(request.Workflow));
            if (!edit.ReferenceLooks.SequenceEqual(request.Inputs.Select(i => new AssetReferenceLook(i.Reference, i.Context))))
                throw new AiGenerationException("Reference look context must match the submitted images in order.");
            foreach (var input in request.Inputs)
            {
                var crop = input.Reference == request.Inputs[0].Reference ? edit.SourceCrop : edit.ReferenceCrops.SingleOrDefault(c => c.Reference == input.Reference)?.Crop;
                if (input.Crop != crop) throw new AiGenerationException("The prepared reference crop differs from the captured edit.");
            }
            if (request.Workflow == ImageWorkflow.Krea2 && (!float.IsFinite(edit.ReferenceBoost) || edit.ReferenceBoost is < 0 or > 10 ||
                !float.IsFinite(edit.BaseReferenceBoost) || edit.BaseReferenceBoost is < 0 or > 10 || edit.GroundingPixels is < 384 or > 1024 || edit.GroundingPixels % 64 != 0))
                throw new AiGenerationException("Check the Krea reference fidelity and grounding resolution.");
        }
        else if (request.Inputs.Count != 0) throw new AiGenerationException("Image creation cannot silently consume edit references.");
        if (request.AspectRatio == ImageAspectPolicy.FromImage1 && request.Workflow != ImageWorkflow.CodexImages)
            QwenImage21Policy.OutputSize(request);
    }
    public static AssetGenerationMetadata Metadata(AiImageJobRequest request, Guid jobId, AiBatchCandidate candidate)
        => MetadataCore(request, jobId, candidate) with { VisualStyle = request.Create?.VisualStyle ?? request.Edit?.VisualStyle, AuthorPrompt = request.OriginalPrompt };
    private static AssetGenerationMetadata MetadataCore(AiImageJobRequest request, Guid jobId, AiBatchCandidate candidate)
    {
        if (request.Workflow == ImageWorkflow.QwenImage21) return QwenImage21Policy.Metadata(request, jobId, candidate);
        if (request.Workflow == ImageWorkflow.MossImage) return MossImagePolicy.Metadata(request, jobId, candidate);
        if (request.Workflow == ImageWorkflow.Flux3ImageKlein) return Flux3ImagePolicy.Metadata(request, jobId, candidate);
        if (CaptionImagePolicy.Supports(request.Workflow)) return CaptionImagePolicy.Metadata(request, jobId, candidate);
        var klein = request.Workflow == ImageWorkflow.Flux2Klein9bKv; var settings = request.Settings;
        return new()
        {
            AiJobId = jobId, BatchId = request.BatchId, CandidateNumber = candidate.Number, Look = request.Look,
            Workflow = request.Workflow, Prompt = request.Prompt.Trim(), Seed = candidate.Seed, AspectRatio = request.AspectRatio, Resolution = request.Create?.Resolution ?? request.Edit!.Resolution,
            DiffusionModel = klein ? settings.FluxKleinModel : settings.ComfyImageModel,
            TextEncoder = klein ? settings.FluxKleinTextEncoder : settings.ComfyImageTextEncoder,
            Vae = klein ? settings.FluxKleinVae : settings.ComfyImageVae, Steps = klein ? 4 : request.Edit is null ? 8 : 10,
            Loras = request.AppliedLoras,
            Edit = request.Edit is { } edit ? new()
            {
                Regions = edit.Regions,
                SourceAssetId = edit.SourceAssetId, SourceImageId = edit.SourceImageId,
                References = request.Inputs.Select(i => i.Reference).ToArray(), ReferenceLooks = edit.ReferenceLooks,
                SourceCrop = edit.SourceCrop, ReferenceCrops = edit.ReferenceCrops,
                Lora = klein ? "" : settings.ComfyImageEditLora, LoraStrength = klein ? 0 : 1,
                ReferenceBoost = klein ? 0 : edit.ReferenceBoost, GroundingPixels = klein ? 0 : edit.GroundingPixels,
                BaseReferenceBoost = !klein && request.Inputs.Count == 2 ? edit.BaseReferenceBoost : null, FitMode = klein ? "reference-latent" : "fit"
            } : null
        };
    }
}
