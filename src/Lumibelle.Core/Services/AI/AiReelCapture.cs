using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiReelCapture(IAssetStore assets, IAssetReelStore reels, IAiSettingsStore settings,
    IVideoGenerator generator, IProjectStore projects, IProjectAiPreferencesStore preferences, IAiJobStore? jobs = null, IReferenceVideoStore? referenceVideos = null,
    IGenerationSetupStore? generationSetups = null)
{
    public async Task<AiJobSubmission> CaptureAsync(Guid id, Guid tab, Guid project, Guid draftId, long revision, int count, CancellationToken ct = default)
    {
        var library = await assets.LoadAsync(project, ct);
        var draft = library.ReelDrafts.SingleOrDefault(d => d.Id == draftId)?.Copy() ?? throw new WorkspaceStoreException("Save the reel recipe first.");
        if (draft.Revision != revision) throw new WorkspaceConflictException();
        await ValidateGenerationSetup(draft, ct);
        ReferenceReels.Validate(draft, true);
        var owner = library.Assets.SingleOrDefault(a => a.Id == draft.AssetId) ?? throw new WorkspaceStoreException("Choose an available character, environment or prop.");
        ReferenceReels.ValidateOwner(draft, owner);
        var character = LookPolicy.Capture(owner, draft.LookId); LookPolicy.ValidateTarget(library, character);
        var configured = ShotCopy.Of(await settings.LoadAsync(ct)); var inputs = ReferenceReels.Inputs(draft);
        var check = await generator.CheckAsync(configured, ct);
        if (!check.Ready(inputs)) throw new WorkspaceStoreException(check.Issue(inputs));
        var loras = H3Loras.Selections(inputs).Any(l => l.Enabled && l.Strength != 0)
            ? H3Loras.Capture(inputs, configured, (await preferences.LoadAsync(project, ct)).LoraVisibility, check.OptionalLoras) : null;
        var size = VideoResolutions.Size(draft);
        var snapshot = new VideoSnapshot(project, revision, inputs, draft.Prompt, VideoResolutions.Fingerprint(draft),
            AiProviderRegistry.NormalizeComfyUrl(configured.ComfyUrl), configured.H3, size.Width, size.Height, H3Policy.Frames(draft.Duration), draft.PresetVersion)
        { Reel = new(draft, character) { Owner = owner with { Images = [] } }, AppliedLoras = loras, OutputPolicy = new(inputs.SaveLosslessFrames), Preset = H3Presets.Capture(inputs, configured.H3),
            Performance = H3Performance.Capture(H3Presets.NewPerformance(configured.H3)), Sampling = H3Policy.Sampling(inputs, configured.H3),
            PreviewUpscale = inputs.UpscalePreview ? H3PreviewUpscaling.Capture(check.PreviewUpscaling.Implementation!.Value, configured.H3.LatentUpscaler, draft.Aspect) : null };
        var expectedImages = await ProductionInputs.CaptureAsync(project, inputs, assets, ct, referenceVideos, configured.H3);
        var directory = await reels.RunDirectoryAsync(project, id, ct); using var gate = await ProjectFiles.LockAsync(directory, ct);
        if (Directory.Exists(Path.Combine(directory, "inputs"))) throw new WorkspaceStoreException("This reel already has captured inputs. Retry its saved enqueue request.");
        var run = new VideoRun { Id = id, Snapshot = snapshot };
        await generator.PrepareAsync(run, directory, ct);
        if (!run.InputsPrepared) throw new WorkspaceStoreException("Reel inputs could not be prepared.");
        List<AiVideoInput> captured = [];
        foreach (var input in run.Inputs)
        {
            AiVideoJobPolicy.ValidateFileName(input.FileName, input.EffectiveKind);
            await using var file = File.OpenRead(Path.Combine(directory, "inputs", input.FileName));
            captured.Add(new(input.FileName, input.Audio, file.Length, Convert.ToHexString(await SHA256.HashDataAsync(file, ct))) { Kind = input.Kind, VideoIndex = input.VideoIndex });
        }
        if (!expectedImages.Select(i => i.Identity.Sha256).SequenceEqual(captured.Where(i => i.EffectiveKind == VideoInputKind.Image).Select(i => i.Sha256)))
            throw new WorkspaceStoreException("Pictures changed during capture. Review the reel recipe again.");
        var current = await assets.LoadAsync(project, ct);
        if (current.ReelDrafts.SingleOrDefault(d => d.Id == draftId)?.Revision != revision) throw new WorkspaceConflictException();
        await ValidateGenerationSetup(draft, ct);
        LookPolicy.ValidateTarget(current, character, requireCurrent: true);
        ReferenceReels.ValidateOwner(draft, current.Assets.Single(a => a.Id == draft.AssetId));
        var request = new AiVideoJobRequest(3, id, snapshot, captured); AiVideoJobPolicy.Validate(request);
        CapturedInputStore.Share(directory, captured);
        var name = (await projects.GetAsync(project, ct))?.Name ?? throw new WorkspaceStoreException("Project unavailable.");
        return AiJobSubmission.Create(id, AiJobKind.ReelVideo, AiBackend.ComfyUI, new(project, draft.AssetId, ReelId: draft.Id),
            name, draft.Name + " · Reference reel", tab, request) with { Batch = AiBatchDefinition.Create(id, count, draft.GenerationSetup?.Settings.Seed) };
    }

    private async Task ValidateGenerationSetup(ReferenceReelDraft draft, CancellationToken ct)
    {
        if (draft.GenerationSetup is not { } captured) return;
        var library = await (generationSetups ?? throw new WorkspaceStoreException("Generation presets are unavailable.")).LoadAsync(ct);
        var current = library.Setups.SingleOrDefault(s => s.Id == captured.Id);
        if (current is null || current.Archived)
            throw new WorkspaceStoreException("Choose an available generation preset before generating.");
        if (current.Version != captured.Version)
            throw new WorkspaceStoreException("This preset changed elsewhere. Reopen the preset before generating.");
    }
}
