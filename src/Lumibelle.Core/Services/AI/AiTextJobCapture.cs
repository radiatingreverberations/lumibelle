using System.Text.Json;
using lumibelle.Models;
using System.Linq;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.AI;

public sealed partial class AiTextJobCapture(IAiSettingsStore settings, IProjectStore projects, IAssetStore assets,
    IPromptEnhancer enhancer, IGuidanceAssistant guidance, ICodexClient? codex = null,
    IProductionStore? production = null, IShotStore? shots = null, IScriptStore? scripts = null, IReferenceVideoStore? referenceVideos = null, ReelRefModStore? refmods = null, IProjectDubbingStore? dubbing = null,
    VisualBriefCache? briefs = null)
{
    public static AiJobSubmission Reissue(AiJobHeader job, AiTextJobRequest captured, Guid tab)
    {
        if (job.Kind is not (AiJobKind.PromptEnhancement or AiJobKind.Guidance) || captured.Kind != job.Kind)
            throw new WorkspaceStoreException("This request cannot be repeated through text assistance.");
        return AiJobSubmission.Create(Guid.NewGuid(), job.Kind, job.Backend, job.Target, job.ProjectName,
            job.TargetName, tab, captured);
    }
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
    public async Task<AiJobSubmission> ComposeAsync(Guid id, Guid tab, Guid projectId, Guid compositionId, long version,
        TextModelReference model, bool followsDefault, CancellationToken ct = default, bool inspectReferenceImages = true, bool reducedScriptContext = false)
    {
        var document = await (production ?? throw new WorkspaceStoreException("Production storage is unavailable.")).LoadAsync(projectId, ct);
        var c = document.Compositions.SingleOrDefault(c => c.Id == compositionId && !c.Archived) ?? throw new WorkspaceStoreException("Choose an active composition.");
        if (c.Version != version) throw new WorkspaceConflictException();
        var source = await shots!.LoadAsync(projectId, ct); var shot = source.Shots.SingleOrDefault(s => s.Id == c.ShotId) ?? throw new WorkspaceStoreException("The source shot was removed.");

        var project = await projects.GetAsync(projectId, ct) ?? throw new WorkspaceStoreException("Project unavailable.");
        var library = await assets.LoadAsync(projectId, ct); var effective = ShotVideoDefaults.Capture(c.Shot, project);
        H3Policy.Validate(effective, true);
        if (effective.Videos.Count > 0) await (referenceVideos ?? throw new WorkspaceStoreException("Reference video storage is unavailable.")).ValidateAsync(projectId, effective.Videos, ct);
        foreach (var binding in effective.Images) if (lumibelle.Services.Production.ProductionPolicy.MediaIssue(binding, library) is { } issue) throw new WorkspaceStoreException(issue);
        var descriptions = CompositionDescriptions.Capture(effective, library);
        if (!inspectReferenceImages && CompositionDescriptions.MissingIssue(effective, library) is { } descriptionIssue)
            throw new AiGenerationException(descriptionIssue);
        if (inspectReferenceImages && (ResolvedReferences.For(effective).Pictures.Count > 0 || ReelRefMods.Uses(effective)) && !TextVisionPolicy.SupportsBackend(model.Backend)) throw new AiGenerationException(TextVisionPolicy.SetupHint);
        // Retain media identities for validation and generation; only the LLM attachments are optional.
        var images = await ProductionInputs.CaptureAsync(projectId, effective, assets, ct, referenceVideos, (await settings.LoadAsync(ct)).H3);
        var modFrames = inspectReferenceImages && ReelRefMods.Uses(effective)
            ? await (refmods ?? throw new WorkspaceStoreException("RefMod preview storage is unavailable.")).InspectionAsync(projectId, effective, ct)
            : Array.Empty<RefModInspectionFrame>();
        var approved = await scripts!.LoadSourceAsync(projectId, shot.ApprovedScriptId ?? Guid.Empty, ct) ?? throw new WorkspaceStoreException("The captured source is unavailable.");
        var scene = ScriptStructure.Sections(approved.Blocks).FirstOrDefault(s => s.Id == shot.SceneId) ?? throw new WorkspaceStoreException("The script scene is unavailable.");
        var index = source.Shots.FindIndex(s => s.Id == shot.Id);

        string NearbyShotSummary(Shot neighbor, string relation)
        {
            var summary = relation + " shot — " + neighbor.Title + ": " + neighbor.Description;
            if (!string.Equals(relation, "Previous", StringComparison.Ordinal)) return summary;
            var priorPrompt = document.Compositions
                .Where(x => x.ShotId == neighbor.Id && !x.Archived && !string.IsNullOrWhiteSpace(x.Prompt))
                .OrderByDescending(x => x.Version)
                .Select(x => x.Prompt!.Trim())
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(priorPrompt)
                ? summary
                : summary + "\nAccepted/composed prompt: " + priorPrompt;
        }

        var nearbyShots = new List<string>();
        var previousShot = source.Shots.Take(Math.Max(0, index)).Where(s => s.SceneId == shot.SceneId && s.Id != shot.Id).LastOrDefault();
        var nextShot = source.Shots.Skip(index + 1).FirstOrDefault(s => s.SceneId == shot.SceneId && s.Id != shot.Id);
        // Reduced context keeps the shot itself but leaves out the scene text and neighbouring shots to shorten the prompt.
        if (previousShot is not null && !reducedScriptContext) nearbyShots.Add(NearbyShotSummary(previousShot, "Previous"));
        if (nextShot is not null && !reducedScriptContext) nearbyShots.Add(NearbyShotSummary(nextShot, "Next"));
        var request = new PromptCompositionRequest(projectId, c.Id, c.Version, ProductionPolicy.ContextFingerprint(c, library, source, project), c.SourceFingerprint,
            effective, reducedScriptContext ? "" : ScriptStructure.Markdown(approved.Blocks.Skip(scene.Start).Take(scene.Count)),
            nearbyShots.ToArray(),
            ShotReferences.Resolve(effective, library, source), [], images.Select(i => i.Identity).ToArray(),
            c.DirectingNotes, c.Prompt, c.RevisionNotes, model, followsDefault)
        {
            InspectReferenceImages = inspectReferenceImages ? null : false,
            ReducedScriptContext = reducedScriptContext,
            VisualDescriptions = descriptions.Any(d => !string.IsNullOrWhiteSpace(d.Text)) || !inspectReferenceImages ? descriptions : null
        };
        var configured = Copy(await settings.LoadAsync(ct));
        var attached = inspectReferenceImages ? images.Select(i => i.Bytes).ToArray() : Array.Empty<byte[]>();
        var target = new AiJobTarget(projectId, ShotId: shot.Id, CompositionId: c.Id);
        var label = shot.Title + " · " + c.Name + " · Compose prompt";
        if (model.Backend != AiBackend.ComfyUI || attached.Length == 0 && modFrames.Count == 0)
            return await BuildAsync(id, tab, AiJobKind.PromptComposition, target, label, request, model, followsDefault, configured, ProductionPolicy.Profile,
                PromptComposer.BuildMessages(request, attached, modFrames), 0.7f, null, ct);
        // ComfyUI composes in two steps, so the images and the long composition guide never share one prompt:
        // a visual brief from the images, then a text-only composition from that brief. A cached brief skips step one.
        var briefMessages = PromptComposer.BuildBriefMessages(request, attached, modFrames).Select(AiTextMessage.Capture).ToArray();
        var key = VisualBriefCache.Key(model, ComfyTextSettings.BatchImageSide(model, configured), briefMessages);
        var cached = briefs is null ? null : await briefs.ReadAsync(key, ct);
        return await BuildAsync(id, tab, AiJobKind.PromptComposition, target, label, request, model, followsDefault, configured, ProductionPolicy.Profile,
            PromptComposer.BuildMessages(request, [], [], visualBrief: true), 0.7f, null, ct, (cached is null ? briefMessages : null, cached, key));
    }
    public async Task<AiJobSubmission> ScriptAsync(Guid id, Guid tab, ScriptAssistantRequest request, bool followsDefault, CancellationToken ct = default)
    {
        request = Copy(request);
        if (request.Run.Id != id || request.Run.Revision != 0 || request.Run.JobId is { } linked && linked != id)
            throw new WorkspaceStoreException("A new script request needs its own matching job identity.");
        request = request with { Run = request.Run with { JobId = id } };
        var configured = Copy(await settings.LoadAsync(ct));
        var model = request.Selection ?? new(request.Run.Backend, request.Run.Model, request.Run.Model, request.Run.Backend == AiBackend.ComfyUI ? configured.ComfyUrl : null);
        if (request.Run.Backend != model.Backend || request.Run.Model != model.Model) throw new AiGenerationException("The script request and model selection do not match.");
        ScriptStructure.ValidateBlocks(request.Script.Blocks);
        return await BuildAsync(id, tab, AiJobKind.ScriptAssistant, new(request.Script.ProjectId), $"{request.Run.Operation} · {request.Run.Target.Name}",
            request, model, followsDefault, configured, request.Run.EditFormat == 1 ? ScriptEdits.Profile : "script-writing-v2",
            ScriptAssistant.BuildMessages(request), configured.Temperature, null, ct);
    }
    public async Task<AiJobSubmission> ExtractAsync(Guid id, Guid tab, AssetExtractionRequest request, bool followsDefault, CancellationToken ct = default)
    {
        request = Copy(request); var configured = Copy(await settings.LoadAsync(ct));
        if (request.Library.ProjectId != request.Script.ProjectId) throw new AiGenerationException("The asset library and saved script belong to different projects.");
        if (scripts is not null) await scripts.CaptureSourceAsync(request.Script.ProjectId, request.Script.SourceRevision, ct);
        ValidateScenes(request.Script, request.SceneIds);
        var model = request.Selection ?? new(request.Backend, request.Model, request.Model, request.Backend == AiBackend.ComfyUI ? configured.ComfyUrl : null);
        if (request.Backend != model.Backend || request.Model != model.Model) throw new AiGenerationException("The extraction request and model selection do not match.");
        return await BuildAsync(id, tab, AiJobKind.AssetExtraction, new(request.Script.ProjectId), "Extract assets", request, model,
            followsDefault, configured, "asset-extraction-v1", AssetExtractor.BuildMessages(request), 0.2f, null, ct);
    }
    public async Task<AiJobSubmission> PlanShotsAsync(Guid id, Guid tab, ShotPlanningRequest request, bool followsDefault, CancellationToken ct = default)
    {
        request = Copy(request); var configured = Copy(await settings.LoadAsync(ct));
        if (request.Assets.ProjectId != request.Script.ProjectId) throw new AiGenerationException("The asset library and saved script belong to different projects.");
        if (scripts is not null) await scripts.CaptureSourceAsync(request.Script.ProjectId, request.Script.SourceRevision, ct);
        ValidateScenes(request.Script, request.SceneIds); H3Policy.Frames(request.MaximumSeconds);
        return await BuildAsync(id, tab, AiJobKind.ShotPlanning, new(request.Script.ProjectId), "Draft shots", request, request.Selection,
            followsDefault, configured, request.CoverageOnly ? "shot-coverage-v3" : "h3-shot-planner-v2", ShotPlanner.BuildMessages(request), configured.Temperature, null, ct);
    }
    public async Task<AiJobSubmission> EnhanceAsync(Guid id, Guid tab, PromptEnhancementRequest request, CancellationToken ct = default)
    {
        request = Copy(request); var configured = Copy(await settings.LoadAsync(ct));
        if (string.IsNullOrWhiteSpace(request.Context.Prompt)) throw new AiGenerationException("Write a prompt before enhancing it.");
        await enhancer.ValidateInputsAsync(request.Context, ct);
        var images = new List<byte[]>();
        if (request.InspectImages)
        {
            if (!request.Context.IsEdit || !TextVisionPolicy.SupportsBackend(request.Model.Backend)) throw new AiGenerationException("Image inspection requires edit references. " + TextVisionPolicy.SetupHint);
            foreach (var reference in request.Context.References)
                {
                await using var media = await assets.OpenImageAsync(request.Context.ProjectId, reference.Image.AssetId, reference.Image.ImageId, ct);
                if (media is null) throw new AiGenerationException("An image is unavailable.");
                images.Add(await RegionalImageEdits.InputAsync(media.Content, reference.Region, reference.Crop, request.Context.AspectRatio, ct,
                    images.Count == 0 && request.Context.References.Any(r => r.Region is not null) ? reference.Image : null,
                    request.Context.Resolution, request.Context.Workflow == ImageWorkflow.QwenImage21 ? request.Context.QwenImage21 ?? new() : null));
                }
        }
        var profile = PromptProfiles.Id(request.Context.Workflow, request.Context.IsEdit);
        return await BuildAsync(id, tab, AiJobKind.PromptEnhancement, new(request.Context.ProjectId, request.Context.AssetId), request.Context.AssetName + " · Enhance prompt",
            request, request.Model, request.FollowsDefault, configured, profile,
            PromptEnhancer.BuildMessages(request, PromptProfiles.Read(request.Context.Workflow, request.Context.IsEdit), images), configured.Temperature, null, ct);
    }
    public async Task<AiJobSubmission> GuidanceAsync(Guid id, Guid tab, GuidanceRequest request, CancellationToken ct = default)
    {
        request = Copy(request); var configured = Copy(await settings.LoadAsync(ct)); var target = request.Context.Target;
        VisualDescriptionAssistance.ValidateTarget(request);
        var baseline = (await guidance.ReadTargetAsync(target, ct)).Fingerprint();
        byte[]? image = null;
        if (request.InspectionImage is { } reference)
        {
            if (!TextVisionPolicy.SupportsBackend(request.Model.Backend)) throw new AiGenerationException(TextVisionPolicy.SetupHint);
            image = await PrepareImageAsync(target.ProjectId, reference, null, ct);
        }
        return await BuildAsync(id, tab, AiJobKind.Guidance, new(target.ProjectId, target.AssetId, GuidanceScope: target.Scope, LookId: target.LookId, ImageId: target.ImageId),
            request.Context.AssetName + " · " + request.Context.Label, request, request.Model, request.FollowsDefault, configured,
            target.Scope == GuidanceScope.ImageDescription ? VisualDescriptionAssistance.Profile : GuidanceAssistant.Profile,
            GuidanceAssistant.BuildMessages(request, image), configured.Temperature, baseline, ct);
    }
    private async Task<byte[]> PrepareImageAsync(Guid project, AssetImageReference reference, ImageCropRegion? crop, CancellationToken ct)
    {
        await using var media = await assets.OpenImageAsync(project, reference.AssetId, reference.ImageId, ct);
        if (media is null) throw new AiGenerationException("A reference is missing or in Trash. Restore or replace it before queueing.");
        return await ComfyReferenceImageEditor.PrepareSourcePngAsync(media.Content, crop, ct);
    }
    private async Task<AiJobSubmission> BuildAsync<T>(Guid id, Guid tab, AiJobKind kind, AiJobTarget target, string label, T payload,
        TextModelReference model, bool followsDefault, AiSettings configured, string profile, List<ChatMessage> messages, float temperature, string? baseline, CancellationToken ct,
        (IReadOnlyList<AiTextMessage>? Messages, string? Brief, string Key)? brief = null)
    {
        model = TextModelPolicy.WithDefaultEffort(model, configured);
        TextModelPolicy.Validate(model); TextModelPolicy.CheckRequestServer(model, configured); FileAiSettingsStore.Validate(configured);
        configured = ComfyTextSettings.Capture(model, configured);
        if (kind != AiJobKind.AssetExtraction) temperature = configured.Temperature;
        temperature = model.Temperature ?? temperature;
        var project = await projects.GetAsync(target.ProjectId!.Value, ct) ?? throw new ProjectStoreException("This project no longer exists.");
        var version = TextModelProfiles.RequiresSnapshotVersion3(model) ? 3 : 2;
        var snapshot = new AiTextJobRequest(version, kind, model, followsDefault, configured, profile, temperature, Random.Shared.NextInt64(1, long.MaxValue),
            JsonSerializer.SerializeToElement(payload, AtomicJsonFile.Options), messages.Select(AiTextMessage.Capture).ToArray(), baseline)
        { BriefMessages = brief?.Messages, VisualBrief = brief?.Brief, BriefKey = brief?.Key };
        ComfyTextVision.ValidateSnapshot(snapshot);
        if (model.Backend == AiBackend.Codex)
            snapshot = snapshot with { Codex = CodexClient.Capture(await (codex ?? throw new AiGenerationException("Codex is not configured.")).CheckAsync(configured.Codex, ct), model.Model, model.ReasoningEffort) };
        return AiJobSubmission.Create(id, kind, model.Backend, target, project.Name, label, tab, snapshot);
    }
    internal static void ValidateScenes(ScriptSourceSnapshot script, IReadOnlyList<Guid> sceneIds)
    {
        var scenes = ScriptStructure.Sections(script.Blocks).Where(s => s.Kind == ScriptBlockKind.Scene).Select(s => s.Id).ToHashSet();
        if (sceneIds.Count == 0 || sceneIds.Distinct().Count() != sceneIds.Count || sceneIds.Any(id => !scenes.Contains(id)))
            throw new AiGenerationException("Choose distinct scenes from the captured saved script.");
    }
}
