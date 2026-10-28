using lumibelle.Models;
using lumibelle.Services.Assets;

namespace lumibelle.Services.AI;

public interface IComfyImageJobAdapter
{
    Task ValidateAsync(AiImageJobRequest request, CancellationToken ct);
    object Build(AiImageJobRequest request, AiBatchCandidate candidate, string clientId);
    ComfyExecutionOptions Options(AiImageJobRequest request);
    string OutputNode(AiImageJobRequest request);
}

public sealed class ComfyImageJobAdapter(ComfyReferenceImageGenerator krea, ComfyReferenceImageEditor edit, ComfyFluxKleinImages klein,
    IHttpClientFactory clients, IAiSettingsStore settings, IProjectAiPreferencesStore preferences, ComfyQwenImage21? qwen = null, ComfyMossImages? moss = null, ComfyFlux3Images? flux3 = null, ComfyCaptionImages? caption = null) : IComfyImageJobAdapter
{
    public async Task ValidateAsync(AiImageJobRequest request, CancellationToken ct)
    {
        if (CaptionImagePolicy.Supports(request.Workflow))
        {
            var check = await (caption ?? throw new AiGenerationException("Caption image service is unavailable.")).CheckAsync(request.Settings, request.Workflow, request.Edit is not null, ct);
            if (!check.Success) throw new AiGenerationException(check.Message);
        }
        else if (request.Workflow == ImageWorkflow.Flux3ImageKlein)
        {
            var check = await (flux3 ?? throw new AiGenerationException("FLUX 3 service is unavailable.")).CheckAsync(request.Settings, request.Edit is not null, ct);
            if (!check.Success) throw new AiGenerationException(check.Message);
        }
        else if (request.Workflow == ImageWorkflow.MossImage)
        {
            var check = await (moss ?? throw new AiGenerationException("MOSS-Image service is unavailable.")).CheckAsync(request.Settings, request.Edit is not null, ct);
            if (!check.Success) throw new AiGenerationException(check.Message);
        }
        else if (request.Workflow == ImageWorkflow.QwenImage21)
        {
            QwenImage21Policy.ValidateRequest(request);
            var check = await (qwen ?? throw new AiGenerationException("Qwen Image 2.1 service is unavailable.")).CheckAsync(request.Settings, request.Edit is not null, ct);
            if (!check.Success) throw new AiGenerationException(check.Message);
        }
        else if (request.Workflow == ImageWorkflow.Flux2Klein9bKv)
        {
            var check = await klein.CheckAsync(request.Settings, request.Edit is not null, ct);
            if (!check.Success) throw new AiGenerationException(check.Message);
        }
        else if (request.Edit is not null)
        {
            var check = await edit.CheckAsync(request.Settings, ct);
            if (!check.Success) throw new AiGenerationException(check.Message);
            if (request.Inputs.Count > check.MaximumReferences) throw new AiGenerationException(check.ReferenceCapabilityMessage ?? "This installation cannot accept every captured reference.");
        }
        else
        {
            var check = await krea.CheckAsync(request.Settings, ct);
            if (!check.Success) throw new AiGenerationException(check.Message);
        }
        if (request.AppliedLoras.Count == 0) return;
        // Registration and visibility are current policy. Sampling configuration and
        // the submitted model/server remain those captured by the original request.
        var current = await settings.LoadAsync(ct);
        var validation = request.Settings with { LoraLibrary = current.LoraLibrary };
        var visibility = (await preferences.LoadAsync(request.ProjectId, ct)).LoraVisibility;
        LoraPolicy.ValidateVisibility(visibility);
        foreach (var selection in request.Loras.Where(s => s.Enabled && s.Strength != 0))
            if (LoraPolicy.VisibilityIssue(selection, validation, visibility) is { } issue) throw new AiGenerationException(issue);
        await LoraPolicy.PrepareAsync(request.Loras, validation, request.Workflow, clients, ct);
    }
    public object Build(AiImageJobRequest request, AiBatchCandidate candidate, string clientId)
    {
        if (request.Edit is { Regions.Count: > 0 } regionalEdit)
            request = request with { Edit = regionalEdit with { Prompt = regionalEdit.Prompt + "\n\n" + RegionalImageEdits.PlacementInstruction } };
        var size = QwenImage21Policy.OutputSize(request);
        var images = request.Inputs.Select(i => Convert.ToBase64String(i.Png)).ToArray();
        if (CaptionImagePolicy.Supports(request.Workflow))
            return ComfyCaptionImages.BuildWorkflow(request.Settings, request.Workflow, request.Prompt.Trim(), candidate.Seed, size.Width, size.Height, images, clientId, request.AppliedLoras);
        if (request.Workflow == ImageWorkflow.Flux3ImageKlein)
            return ComfyFlux3Images.BuildWorkflow(request.Settings, request.Prompt.Trim(), candidate.Seed, size.Width, size.Height, images, clientId, request.AppliedLoras);
        if (request.Workflow == ImageWorkflow.MossImage)
            return ComfyMossImages.BuildWorkflow(request.Settings, request.Prompt.Trim(), candidate.Seed, size.Width, size.Height, images, clientId, request.AppliedLoras,
                legacyReferenceSizing: request.Profile == "moss-image-edit-v1");
        if (request.Workflow == ImageWorkflow.QwenImage21)
            return ComfyQwenImage21.BuildWorkflow(request.Settings, request.Prompt.Trim(), candidate.Seed, size.Width, size.Height, images, QwenImage21Policy.Options(request), clientId, request.AppliedLoras);
        return request.Workflow == ImageWorkflow.Flux2Klein9bKv
            ? ComfyFluxKleinImages.BuildWorkflow(request.Settings, request.Prompt.Trim(), candidate.Seed, size.Width, size.Height, images, clientId, request.AppliedLoras)
            : request.Edit is { } edit
                ? ComfyReferenceImageEditor.BuildWorkflow(request.Settings, edit, images, candidate.Seed, size.Width, size.Height, clientId)
                : ComfyReferenceImageGenerator.BuildWorkflow(request.Settings, request.Prompt.Trim(), candidate.Seed, size.Width, size.Height, clientId, request.AppliedLoras);
    }
    public ComfyExecutionOptions Options(AiImageJobRequest request) => CaptionImagePolicy.Supports(request.Workflow) ? ComfyCaptionImages.ExecutionOptions
        : request.Workflow == ImageWorkflow.Flux3ImageKlein ? ComfyFlux3Images.ExecutionOptions
        : request.Workflow == ImageWorkflow.MossImage ? ComfyMossImages.ExecutionOptions
        : request.Workflow == ImageWorkflow.QwenImage21 ? ComfyQwenImage21.ExecutionOptions
        : request.Workflow == ImageWorkflow.Flux2Klein9bKv ? ComfyFluxKleinImages.ExecutionOptions
        : request.Edit is null ? ComfyReferenceImageGenerator.ExecutionOptions : ComfyReferenceImageEditor.ExecutionOptions;
    public string OutputNode(AiImageJobRequest request) => CaptionImagePolicy.Supports(request.Workflow) ? ComfyCaptionImages.OutputNode
        : request.Workflow == ImageWorkflow.Flux3ImageKlein ? ComfyFlux3Images.OutputNode
        : request.Workflow == ImageWorkflow.MossImage ? ComfyMossImages.OutputNode
        : request.Workflow == ImageWorkflow.QwenImage21 ? ComfyQwenImage21.OutputNode
        : request.Workflow == ImageWorkflow.Flux2Klein9bKv ? ComfyFluxKleinImages.OutputNode : request.Edit is null ? "9" : "13";
}
