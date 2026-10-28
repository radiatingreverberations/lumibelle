using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed class AiImageJobCapture(IAiSettingsStore settings, IProjectStore projects, IAssetStore assets, ICodexClient? codex = null,
    IProjectAiPreferencesStore? preferences = null)
{
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
    public Task<AiJobSubmission> CreateAsync(Guid id, Guid tab, Guid assetId, ReferenceGenerationRequest request, CancellationToken ct = default, AiSettings? capturedSettings = null) =>
        CaptureAsync(id, tab, assetId, Copy(request), null, [], capturedSettings is null ? null : Copy(capturedSettings), ct);
    public Task<AiJobSubmission> EditAsync(Guid id, Guid tab, Guid assetId, ReferenceEditRequest request,
        IReadOnlyList<AssetImageReference> references, CancellationToken ct = default, AiSettings? capturedSettings = null) =>
        CaptureAsync(id, tab, assetId, null, Copy(request), CaptureReferences(references), capturedSettings is null ? null : Copy(capturedSettings), ct);

    private static IReadOnlyList<AssetImageReference> CaptureReferences(IReadOnlyList<AssetImageReference> references) =>
        references is null || references.Count is < 1 or > ImageWorkflowLimits.MaximumAnyReferences ||
        references.Any(r => r is null || r.AssetId == Guid.Empty || r.ImageId == Guid.Empty) || references.Distinct().Count() != references.Count
            ? throw new AiGenerationException("Choose distinct, exact image references, with the base first.") : references.ToArray();

    private async Task<AiJobSubmission> CaptureAsync(Guid id, Guid tab, Guid assetId, ReferenceGenerationRequest? create,
        ReferenceEditRequest? edit, IReadOnlyList<AssetImageReference> references, AiSettings? capturedSettings, CancellationToken ct)
    {
        var projectId = create?.ProjectId ?? edit?.ProjectId ?? throw new AiGenerationException("Choose an exact project.");
        var configured = capturedSettings ?? Copy(await settings.LoadAsync(ct));
        configured = configured with { DefaultImageWorkflow = await ProjectImageDefaults.ResolveAsync(projectId,
            create?.Workflow ?? edit?.Workflow ?? (capturedSettings is not null ? configured.DefaultImageWorkflow : null), configured, preferences, ct) };
        var library = await assets.LoadAsync(projectId, ct);
        var asset = library.Assets.SingleOrDefault(a => a.Id == assetId) ?? throw new AiGenerationException("The destination asset is unavailable.");
        var style = await VisualStyles.CaptureAsync(projectId, asset, create?.VisualStyle ?? edit?.VisualStyle, preferences, ct);
        var originalPrompt = create?.Prompt ?? edit!.Prompt;
        if (create is not null) create = create with { VisualStyle = style, Prompt = VisualStyles.ApplyImage(create.Prompt, style, configured.DefaultImageWorkflow) };
        if (edit is not null) edit = edit with { VisualStyle = style, Prompt = VisualStyles.ApplyImage(edit.Prompt, style, configured.DefaultImageWorkflow) };
        var look = create?.Look ?? edit?.Look ?? LookPolicy.Capture(asset, null);
        if (look.AssetId != asset.Id) throw new AiGenerationException("The target look belongs to a different asset.");
        LookPolicy.ValidateTarget(library, look, requireCurrent: true);
        var sourceContexts = references.Select(reference =>
        {
            var owner = library.Assets.SingleOrDefault(a => a.Id == reference.AssetId);
            var image = owner?.Images.SingleOrDefault(i => i.Id == reference.ImageId);
            if (owner is null || image is null) throw new AiGenerationException("A reference is missing or in Trash. Restore or replace it before queueing.");
            return new AssetReferenceLook(reference, LookPolicy.Capture(owner, image.LookId));
        }).ToArray();
        if (create is not null) create = create with { ProjectId = projectId, Workflow = configured.DefaultImageWorkflow, Look = look };
        if (edit is not null)
        {
            if (edit.ReferenceLooks.Count > 0 && !edit.ReferenceLooks.SequenceEqual(sourceContexts))
                throw new AiGenerationException("Reference context changed. Refresh and review the selected images before queueing.");
            edit = edit with { ProjectId = projectId, Workflow = configured.DefaultImageWorkflow, Look = look, ReferenceLooks = sourceContexts };
            edit = ReferenceEditInputs.Capture(edit, references.Select(r => new ReferenceImageSource(r.AssetId, r.ImageId, Stream.Null)).ToArray(),
                ImageWorkflowLimits.MaximumReferences(configured.DefaultImageWorkflow));
            LookPolicy.ValidateReferences(library, sourceContexts);
        }
        CodexCapture? codexCapture = null;
        if (configured.DefaultImageWorkflow == ImageWorkflow.CodexImages)
        {
            if (codex is null) throw new AiGenerationException("Codex is unavailable.");
            var check = await codex.CheckAsync(configured.Codex, ct);
            codexCapture = CodexClient.Capture(check, configured.Codex.ImageModel, configured.Codex.ImageEffort, true);
            if (edit is not null && check.Models.First(m => m.Id == codexCapture.Model).SupportsImages != true)
                throw new AiGenerationException("This Codex model does not accept reference images. Choose an image-capable model.");
            if ((create?.Loras ?? edit!.Loras).Count != 0 || create?.Seed is not null || edit?.Seed is not null)
                throw new AiGenerationException("Codex Images does not support LoRAs or seed settings.");
        }
        var selected = create?.Loras ?? edit!.Loras; LoraPolicy.Capture(selected);
        var applied = new List<AppliedLora>();
        foreach (var selection in selected.Where(s => s.Enabled && s.Strength != 0))
        {
            var definition = configured.LoraLibrary.SingleOrDefault(d => LoraPolicy.Same(d.Reference, selection.Reference));
            if (definition is null || definition.Reference.Workflow != configured.DefaultImageWorkflow.LoraWorkflow() ||
                AiProviderRegistry.NormalizeComfyUrl(definition.Reference.ComfyUrl) != AiProviderRegistry.NormalizeComfyUrl(configured.ComfyUrl))
                throw new AiGenerationException("A selected LoRA is missing or belongs to another workflow or server.");
            applied.Add(new(definition.Reference, selection.Strength));
        }
        if (configured.DefaultImageWorkflow == ImageWorkflow.QwenImage21)
        {
            configured = configured with { QwenImage21 = QwenImage21Policy.Settings(configured) };
            if (create is not null) create = create with { QwenImage21 = QwenImage21Policy.CaptureOptions(configured, create.QwenImage21) };
            if (edit is not null) edit = edit with { QwenImage21 = QwenImage21Policy.CaptureOptions(configured, edit.QwenImage21) };
        }
        var inputs = new List<AiImageInput>();
        configured = CaptionImagePolicy.Capture(configured, configured.DefaultImageWorkflow);
        if (configured.DefaultImageWorkflow == ImageWorkflow.MossImage)
            configured = configured with { MossImage = MossImagePolicy.Settings(configured) };
        if (configured.DefaultImageWorkflow == ImageWorkflow.Flux3ImageKlein)
            configured = configured with { Flux3Image = Flux3ImagePolicy.Settings(configured) };
        RegionalImageCapture? regional = null;
        foreach (var reference in sourceContexts)
        {
            var crop = reference.Reference == references[0] ? edit!.SourceCrop : edit!.ReferenceCrops.SingleOrDefault(c => c.Reference == reference.Reference)?.Crop;
            await using var media = await assets.OpenImageAsync(projectId, reference.Reference.AssetId, reference.Reference.ImageId, ct);
            if (media is null) throw new AiGenerationException("A reference is unavailable. Restore or replace it before queueing.");
            var region = edit?.Regions?.SingleOrDefault(r => r.Source == reference.Reference);
            byte[] png;
            if (region is not null || inputs.Count == 0 && edit?.Regions?.Count > 0)
            {
                var prepared = inputs.Count == 0
                    ? await RegionalImageEdits.PrepareBaseAsync(media.Content, reference.Reference, region, crop, edit!.AspectRatio, ct, edit.Resolution, edit.QwenImage21)
                    : await RegionalImageEdits.PrepareAsync(media.Content, region!, edit!.AspectRatio, ct, edit.Resolution, edit.QwenImage21);
                png = prepared.Png;
                if (inputs.Count == 0)
                {
                    if (!RegionalImageEdits.Masks(prepared.Capture.Selection).Edit.Any(x => x)) throw new AiGenerationException("Select at least one editable pixel.");
                    regional = prepared.Capture;
                    if (region is null) edit = edit! with { Regions = new[] { regional.Selection }.Concat(edit!.Regions!).ToArray(), SourceCrop = regional.Selection.Context };
                    crop = regional.Selection.Context;
                }
            }
            else png = configured.DefaultImageWorkflow == ImageWorkflow.QwenImage21
                ? await QwenImage21Inputs.PrepareAsync(media.Content, crop, ct)
                : configured.DefaultImageWorkflow == ImageWorkflow.Flux3ImageKlein || CaptionImagePolicy.Supports(configured.DefaultImageWorkflow)
                ? await NativeImageInputs.PrepareAsync(media.Content, crop, ct, configured.DefaultImageWorkflow.Label())
                : await ComfyReferenceImageEditor.PrepareSourcePngAsync(media.Content, crop, ct);
            inputs.Add(new(reference.Reference, crop, reference.Context, png));
        }
        var kind = edit is null ? AiJobKind.ImageCreate : AiJobKind.ImageEdit;
        var request = new AiImageJobRequest(1, id, projectId, assetId, library.Revision, configured,
            AiImageJobPolicy.Profile(configured.DefaultImageWorkflow, edit is not null), create, edit, inputs, applied) { Codex = codexCapture, Regional = regional, OriginalPrompt = VisualStyles.Brief(style).Length > 0 ? originalPrompt : null };
        AiImageJobPolicy.Validate(request);
        var project = await projects.GetAsync(projectId, ct) ?? throw new AiGenerationException("The project is unavailable.");
        return AiJobSubmission.Create(id, kind, codexCapture is null ? AiBackend.ComfyUI : AiBackend.Codex, new(projectId, assetId), project.Name,
            asset.Name + (edit is null ? " · Create images" : " · Edit images"), tab, request) with
        { Batch = AiBatchDefinition.Create(id, create?.Count ?? edit!.Count, create is not null ? create.Seed : edit!.Seed) };
    }
}
