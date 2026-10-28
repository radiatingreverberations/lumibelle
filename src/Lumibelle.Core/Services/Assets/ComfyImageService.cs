using System.Runtime.CompilerServices;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Assets;

// Capture settings once for the entire run, including all candidates.
public sealed class ComfyImageService(IAiSettingsStore settingsStore,
    ComfyReferenceImageGenerator kreaGenerator, ComfyReferenceImageEditor kreaEditor,
    ComfyFluxKleinImages klein, IProjectAiPreferencesStore preferencesStore, IAssetStore assets, ICodexClient? codex = null, ComfyQwenImage21? qwen = null, ComfyMossImages? moss = null, ComfyFlux3Images? flux3 = null, ComfyCaptionImages? caption = null) : IReferenceImageGenerator, IReferenceImageEditor
{
    public async Task<ComfyImageConfiguration> CheckAsync(AiSettings? settings = null, CancellationToken cancellationToken = default)
    {
        settings ??= await settingsStore.LoadAsync(cancellationToken);
        FileAiSettingsStore.Validate(settings);
        if (CaptionImagePolicy.Supports(settings.DefaultImageWorkflow)) return await Caption.CheckAsync(settings, settings.DefaultImageWorkflow, false, cancellationToken);
        if (settings.DefaultImageWorkflow == ImageWorkflow.CodexImages) return await CheckCodexAsync(settings, cancellationToken);
        if (settings.DefaultImageWorkflow == ImageWorkflow.MossImage) return await Moss.CheckAsync(settings, false, cancellationToken);
        if (settings.DefaultImageWorkflow == ImageWorkflow.Flux3ImageKlein) return await Flux3.CheckAsync(settings, false, cancellationToken);
        return settings.DefaultImageWorkflow == ImageWorkflow.QwenImage21
            ? await Qwen.CheckAsync(settings, false, cancellationToken)
            : settings.DefaultImageWorkflow == ImageWorkflow.Flux2Klein9bKv
            ? await klein.CheckAsync(settings, false, cancellationToken)
            : await kreaGenerator.CheckAsync(settings, cancellationToken);
    }

    async Task<ComfyImageEditConfiguration> IReferenceImageEditor.CheckAsync(AiSettings? settings, CancellationToken cancellationToken)
    {
        settings ??= await settingsStore.LoadAsync(cancellationToken);
        FileAiSettingsStore.Validate(settings);
        if (settings.DefaultImageWorkflow == ImageWorkflow.CodexImages) { var c = await CheckCodexAsync(settings, cancellationToken, true); return new(c.Success, c.Message, [], 8); }
        if (CaptionImagePolicy.Supports(settings.DefaultImageWorkflow))
        {
            var captionCheck = await Caption.CheckAsync(settings, settings.DefaultImageWorkflow, true, cancellationToken);
            return new(captionCheck.Success, captionCheck.Message, [], ImageWorkflowLimits.MaximumReferences(settings.DefaultImageWorkflow));
        }
        if (settings.DefaultImageWorkflow == ImageWorkflow.MossImage) return await Moss.CheckEditAsync(settings, cancellationToken);
        if (settings.DefaultImageWorkflow == ImageWorkflow.Flux3ImageKlein)
        {
            var fluxCheck = await Flux3.CheckAsync(settings, true, cancellationToken);
            return new(fluxCheck.Success, fluxCheck.Message, [], Flux3ImagePolicy.MaximumReferences);
        }
        if (settings.DefaultImageWorkflow == ImageWorkflow.QwenImage21)
        {
            var qwenCheck = await Qwen.CheckAsync(settings, true, cancellationToken);
            return new(qwenCheck.Success, qwenCheck.Message, [], QwenImage21Policy.MaximumReferences);
        }
        if (settings.DefaultImageWorkflow == ImageWorkflow.Krea2) return await kreaEditor.CheckAsync(settings, cancellationToken);
        var check = await klein.CheckAsync(settings, true, cancellationToken);
        return new(check.Success, check.Message, [], ComfyFluxKleinImages.MaximumReferences);
    }

    public async IAsyncEnumerable<ReferenceGenerationUpdate> GenerateAsync(ReferenceGenerationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        request = request with { Loras = LoraPolicy.Capture(request.Loras), Tags = request.Tags.ToArray() };
        if (request.Look is not null) LookPolicy.ValidateTarget(await assets.LoadAsync(request.ProjectId ?? throw new AiGenerationException("Choose a project."), cancellationToken), request.Look);
        var settings = await SnapshotAsync(request.ProjectId, request.Workflow, cancellationToken);
        if (!ImageWorkflowLimits.SupportsResolution(settings.DefaultImageWorkflow, request.Resolution)) throw new AiGenerationException("This workflow does not support the selected image resolution.");
        if (settings.DefaultImageWorkflow == ImageWorkflow.CodexImages) throw new AiGenerationException("Submit Codex images through the background AI queue.");
        await ValidateProjectLorasAsync(request.ProjectId, request.Loras, settings, cancellationToken);
        var updates = CaptionImagePolicy.Supports(settings.DefaultImageWorkflow)
            ? Caption.GenerateAsync(request, settings, cancellationToken)
            : settings.DefaultImageWorkflow == ImageWorkflow.Flux3ImageKlein
            ? Flux3.GenerateAsync(request, settings, cancellationToken)
            : settings.DefaultImageWorkflow == ImageWorkflow.MossImage
            ? Moss.GenerateAsync(request, settings, cancellationToken)
            : settings.DefaultImageWorkflow == ImageWorkflow.QwenImage21
            ? Qwen.GenerateAsync(request, settings, cancellationToken)
            : settings.DefaultImageWorkflow == ImageWorkflow.Flux2Klein9bKv
            ? klein.GenerateAsync(request, settings, cancellationToken)
            : kreaGenerator.GenerateAsync(request, settings, cancellationToken);
        await foreach (var update in updates.WithCancellation(cancellationToken)) yield return update with { Metadata = update.Metadata is { } metadata ? metadata with { Look = request.Look } : null };
    }

    public IAsyncEnumerable<ReferenceGenerationUpdate> EditAsync(ReferenceEditRequest request, Stream source,
        CancellationToken cancellationToken = default) => EditAsync(request,
            [new(request.SourceAssetId, request.SourceImageId, source)], cancellationToken);

    public async IAsyncEnumerable<ReferenceGenerationUpdate> EditAsync(ReferenceEditRequest request, IReadOnlyList<ReferenceImageSource> sources,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var captured = sources.ToArray();
        request = ReferenceEditInputs.Capture(request, captured, ImageWorkflowLimits.MaximumAnyReferences);
        if (request.Look is not null) LookPolicy.ValidateTarget(await assets.LoadAsync(request.ProjectId ?? throw new AiGenerationException("Choose a project."), cancellationToken), request.Look);
        if (request.ReferenceLooks.Count > 0) LookPolicy.ValidateReferences(await assets.LoadAsync(request.ProjectId ?? throw new AiGenerationException("Choose a project."), cancellationToken), request.ReferenceLooks);
        var settings = await SnapshotAsync(request.ProjectId, request.Workflow, cancellationToken, request.SettingsSnapshot);
        if (!ImageWorkflowLimits.SupportsResolution(settings.DefaultImageWorkflow, request.Resolution)) throw new AiGenerationException("This workflow does not support the selected image resolution.");
        request = ReferenceEditInputs.Capture(request, captured, ImageWorkflowLimits.MaximumReferences(settings.DefaultImageWorkflow));
        if (settings.DefaultImageWorkflow == ImageWorkflow.CodexImages) throw new AiGenerationException("Submit Codex images through the background AI queue.");
        await ValidateProjectLorasAsync(request.ProjectId, request.Loras, settings, cancellationToken);
        var updates = CaptionImagePolicy.Supports(settings.DefaultImageWorkflow)
            ? Caption.EditAsync(request, captured, settings, cancellationToken)
            : settings.DefaultImageWorkflow == ImageWorkflow.Flux3ImageKlein
            ? Flux3.EditAsync(request, captured, settings, cancellationToken)
            : settings.DefaultImageWorkflow == ImageWorkflow.MossImage
            ? Moss.EditAsync(request, captured, settings, cancellationToken)
            : settings.DefaultImageWorkflow == ImageWorkflow.QwenImage21
            ? Qwen.EditAsync(request, captured, settings, cancellationToken)
            : settings.DefaultImageWorkflow == ImageWorkflow.Flux2Klein9bKv
            ? klein.EditAsync(request, captured, settings, cancellationToken)
            : kreaEditor.EditAsync(request, captured, settings, cancellationToken);
        await foreach (var update in updates.WithCancellation(cancellationToken)) yield return update with { Metadata = update.Metadata is { } metadata ? metadata with { Look = request.Look, Edit = metadata.Edit is { } edit ? edit with { ReferenceLooks = request.ReferenceLooks.ToArray() } : null } : null };
    }

    private ComfyQwenImage21 Qwen => qwen ?? throw new AiGenerationException("Qwen Image 2.1 service is unavailable.");
    private ComfyMossImages Moss => moss ?? throw new AiGenerationException("MOSS-Image service is unavailable.");
    private ComfyFlux3Images Flux3 => flux3 ?? throw new AiGenerationException("FLUX 3 service is unavailable.");
    private ComfyCaptionImages Caption => caption ?? throw new AiGenerationException("Caption image service is unavailable.");

    private async Task<ComfyImageConfiguration> CheckCodexAsync(AiSettings settings, CancellationToken ct, bool edit = false)
    {
        if (codex is null) return new(false, "Codex is unavailable.", [], [], []);
        var check = await codex.CheckAsync(settings.Codex, ct);
        try { CodexClient.Capture(check, settings.Codex.ImageModel, settings.Codex.ImageEffort, true); if (edit && check.Models.First(m => m.Id == settings.Codex.ImageModel).SupportsImages != true) throw new AiGenerationException("This Codex model does not accept reference images. Choose an image-capable model in Image models."); return new(true, "Codex Images ready · up to eight ordered references.", check.Models, [], []); }
        catch (AiGenerationException e) { return new(false, e.Message, check.Models, [], []); }
    }
    private async Task<AiSettings> SnapshotAsync(Guid? projectId, ImageWorkflow? workflow, CancellationToken ct, AiSettings? captured = null)
    {
        var settings = captured ?? await settingsStore.LoadAsync(ct);
        settings = settings with { DefaultImageWorkflow = await ProjectImageDefaults.ResolveAsync(projectId,
            workflow ?? (captured is not null ? settings.DefaultImageWorkflow : null), settings, preferencesStore, ct) };
        FileAiSettingsStore.Validate(settings);
        return settings with { LoraLibrary = LoraPolicy.CopyLibrary(settings.LoraLibrary) };
    }

    private async Task ValidateProjectLorasAsync(Guid? projectId, IReadOnlyList<LoraSelection> selections, AiSettings settings, CancellationToken ct)
    {
        if (projectId is not { } id || !selections.Any(s => s.Enabled && s.Strength != 0)) return;
        var preferences = await preferencesStore.LoadAsync(id, ct);
        LoraPolicy.ValidateVisibility(preferences.LoraVisibility);
        foreach (var selection in selections.Where(s => s.Enabled && s.Strength != 0))
            if (LoraPolicy.VisibilityIssue(selection, settings, preferences.LoraVisibility) is { } issue)
                throw new AiGenerationException($"{selection.Reference.Name}: {issue}");
    }
}
