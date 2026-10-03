using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using lumibelle.Services.Production;

namespace lumibelle.Services.AI;

public sealed partial class AiVideoJobCapture(IShotStore shots, IScriptStore scripts, IAssetStore assets,
    IAiSettingsStore settings, IVideoGenerator generator, IProjectStore projects, IProjectAiPreferencesStore preferences, IProductionStore? production = null, IReferenceVideoStore? referenceVideos = null, IAiJobStore? jobs = null, IProjectDubbingStore? dubbing = null)
{
    public Task<AiJobSubmission> CaptureAsync(Guid id, Guid tab, Guid projectId, Shot shot, long revision,
        int count, long? seed, IReadOnlyList<ShotReferenceGuidance>? reviewedGuidance = null,
        IReadOnlyList<ShotAppearanceContext>? reviewedAppearances = null, CancellationToken ct = default)
        => CaptureCore(id, tab, projectId, shot, revision, count, seed, reviewedGuidance, reviewedAppearances, ct, null);
    public Task<AiJobSubmission> CaptureCompositionAsync(Guid id, Guid tab, Guid projectId, Guid compositionId, long version, int count, long? seed, CancellationToken ct = default, long? generationSetupVersion = null)
        => CaptureCompositionCoreAsync(id, tab, projectId, compositionId, version, count, seed, preset: null, resolution: null, upscalePreview: null, generationSetupVersion, ct);

    public Task<AiJobSubmission> CaptureCompositionWithPresetAsync(Guid id, Guid tab, Guid projectId, Guid compositionId, long version, int count, long? seed, string preset, VideoResolution? resolution = null, bool? upscalePreview = null, CancellationToken ct = default, long? generationSetupVersion = null)
        => CaptureCompositionCoreAsync(id, tab, projectId, compositionId, version, count, seed, preset, resolution, upscalePreview, generationSetupVersion, ct);

    private async Task<AiJobSubmission> CaptureCompositionCoreAsync(Guid id, Guid tab, Guid projectId, Guid compositionId, long version, int count, long? seed, string? preset, VideoResolution? resolution, bool? upscalePreview, long? generationSetupVersion, CancellationToken ct)
    {
        var d = await (production ?? throw new WorkspaceStoreException("Production storage unavailable.")).LoadAsync(projectId, ct);
        var c = d.Compositions.SingleOrDefault(c => c.Id == compositionId) ?? throw new WorkspaceStoreException("Composition unavailable.");

        if (c.Version != version) throw new WorkspaceConflictException();
        if (generationSetupVersion is { } expected && c.GenerationSetupVersion != expected)
            throw new WorkspaceStoreException("This global setup changed elsewhere. Reopen the preset before generating.");
        if (c.GenerationSetupArchived) throw new WorkspaceStoreException("Restore this global setup or choose another before generating.");
        c.SourceFingerprint = ProductionPolicy.SourceFingerprint(c.Shot);
        var project = await projects.GetAsync(projectId, ct) ?? throw new WorkspaceStoreException("Project unavailable.");
        var shot = ShotVideoDefaults.Capture(c.Shot, project);
        if (preset is not null)
        {
            shot.GenerationPreset = preset;
            shot.Turbo = preset is "turbo4" or "turbo8";
            shot.TurboSteps = preset == "turbo8" ? 8 : 4;
        }
        if (upscalePreview == true)
        {
            VideoResolutions.Select(shot, VideoResolution.Preview);
            shot.UpscalePreview = true;
        }
        else if (resolution is { } chosen)
        {
            VideoResolutions.Select(shot, chosen);
        }
        return await CaptureCore(id, tab, projectId, shot, 0, count, seed, null, null, ct, c);
    }
    private async Task<AiJobSubmission> CaptureCore(Guid id, Guid tab, Guid projectId, Shot shot, long revision,
        int count, long? seed, IReadOnlyList<ShotReferenceGuidance>? reviewedGuidance,
        IReadOnlyList<ShotAppearanceContext>? reviewedAppearances, CancellationToken ct, ProductionComposition? composition)
    {
        shot = shot.Copy(); reviewedGuidance = reviewedGuidance is null ? null : ShotCopy.Of(reviewedGuidance);
        reviewedAppearances = reviewedAppearances is null ? null : ShotCopy.Of(reviewedAppearances);
        var batch = AiBatchDefinition.Create(id, count, seed);
        H3Policy.Validate(shot, true);
        var configured = ShotCopy.Of(await settings.LoadAsync(ct));
        var capability = await generator.CheckAsync(configured, ct);
        if (!capability.Ready(shot)) throw new WorkspaceStoreException(capability.Issue(shot));
        var loras = H3Loras.Selections(shot).Any(l => l.Enabled && l.Strength != 0)
            ? H3Loras.Capture(shot, configured, (await preferences.LoadAsync(projectId, ct)).LoraVisibility, capability.OptionalLoras) : null;
        var document = await shots.LoadAsync(projectId, ct);
        var project = await projects.GetAsync(projectId, ct) ?? throw new WorkspaceStoreException("The project is unavailable.");
        if (shot.Aspect != ShotVideoDefaults.Aspect(shot, project))
            throw new WorkspaceStoreException("Project video aspect changed. Refresh the project defaults and review the output dimensions before generating.");
        if (composition is null && (document.Revision != revision || document.Shots.All(s => s.Id != shot.Id || H3Policy.Fingerprint(ShotVideoDefaults.Capture(s, project)) != H3Policy.Fingerprint(shot))))
            throw new WorkspaceConflictException();
        var approved = await scripts.LoadSourceAsync(projectId, shot.ApprovedScriptId ?? Guid.Empty, ct);
        if (approved is null || !ScriptStructure.Sections(approved.Blocks).Any(s => s.Kind == ScriptBlockKind.Scene && s.Id == shot.SceneId))
            throw new WorkspaceStoreException("This shot needs an available script scene.");
        var library = await assets.LoadAsync(projectId, ct); if (composition is null) ShotLooks.Validate(shot, library);
        ProductionVideoContext? composed = null;
        if (composition is not null)
        {
            if (document.Shots.FirstOrDefault(s => s.Id == shot.Id) is not { } savedSource || ProductionPolicy.SourceFingerprint(savedSource) != composition.SourceFingerprint) throw new WorkspaceConflictException();
            if (ProductionPolicy.Issue(composition, library, document, project) is { } issue) throw new WorkspaceStoreException(issue);
            var imageData = await ProductionInputs.CaptureAsync(projectId, shot, assets, ct, referenceVideos, configured.H3);
            var identities = imageData.Select(i => i.Identity).ToArray();
            var capturedPrompt = new CompositionPromptRevision(Guid.NewGuid(), DateTimeOffset.UtcNow, composition.Prompt, composition.ReferenceUsage,
                ProductionPolicy.ContextFingerprint(composition, library, document, project), composition.SourceFingerprint, Images: identities);
            composed = new(composition.Id, composition.Version, composition.Name, capturedPrompt, identities) {
                GenerationSetupId = composition.GenerationSetupId,
                GenerationSetupVersion = composition.GenerationSetupId is null ? null : composition.GenerationSetupVersion
            };
            revision = document.Revision;
        }
        var appearances = ShotLooks.Capture(shot, library); var guidance = ShotReferences.Resolve(shot, library, document);
        if (reviewedGuidance is not null && !ShotReferences.SameEffective(reviewedGuidance, guidance) ||
            reviewedAppearances is not null && !reviewedAppearances.SequenceEqual(appearances))
            throw new WorkspaceStoreException("Reference guidance changed. Refresh Assets and review the prompt before generating.");
        var size = VideoResolutions.Size(shot);
        var snapshot = new VideoSnapshot(projectId, revision, shot, composition?.Prompt ?? H3Policy.Compile(shot, guidance, appearances), H3Policy.Fingerprint(shot),
            AiProviderRegistry.NormalizeComfyUrl(configured.ComfyUrl), configured.H3, size.Width, size.Height, H3Policy.Frames(shot.Duration!.Value), composed is null ? H3Policy.Profile : ProductionPolicy.Profile)
            { Production = composed, AppliedLoras = loras, Preset = H3Presets.Capture(shot, configured.H3), OutputPolicy = new(shot.SaveLosslessFrames), Performance = H3Performance.Capture(H3Presets.NewPerformance(configured.H3)), CaptureRefinementData = !shot.UpscalePreview && capability.PackageCaptureReady,
                PreviewUpscale = shot.UpscalePreview ? H3PreviewUpscaling.Capture(capability.PreviewUpscaling.Implementation!.Value, configured.H3.LatentUpscaler, shot.Aspect) : null,
                ReferenceGuidance = guidance, Appearances = appearances, Sampling = H3Policy.Sampling(shot, configured.H3) };
        var directory = await shots.RunDirectoryAsync(projectId, id, ct);
        using var gate = await ProjectFiles.LockAsync(directory, ct);
        if (Directory.Exists(Path.Combine(directory, "inputs"))) throw new WorkspaceStoreException("This batch already has captured inputs. Retry its saved enqueue request.");
        var run = new VideoRun { Id = id, Snapshot = snapshot };
        // Preparation performs no inference. Files, crops, and audio excerpts are
        // captured before enqueueing, never when a later candidate starts.
        await generator.PrepareAsync(run, directory, ct);
        if (!run.InputsPrepared) throw new WorkspaceStoreException("Video references could not be captured.");
        var inputs = new List<AiVideoInput>();
        foreach (var input in run.Inputs)
        {
            AiVideoJobPolicy.ValidateFileName(input.FileName, input.EffectiveKind);
            await using var file = File.OpenRead(Path.Combine(directory, "inputs", input.FileName));
            inputs.Add(new(input.FileName, input.Audio, file.Length, Convert.ToHexString(await SHA256.HashDataAsync(file, ct))) { Kind = input.Kind, VideoIndex = input.VideoIndex });
        }
        if (composed is not null && !composed.Images.Select(i => i.Sha256).SequenceEqual(inputs.Where(i => i.EffectiveKind == VideoInputKind.Image).Select(i => i.Sha256)))
            throw new WorkspaceStoreException("Reference files changed while preparing the video. Review the composition again.");
        var request = new AiVideoJobRequest(shot.Videos.Count > 0 ? 2 : 1, id, snapshot, inputs); AiVideoJobPolicy.Validate(request);
        return AiJobSubmission.Create(id, AiJobKind.Video, AiBackend.ComfyUI, new(projectId, ShotId: shot.Id, CompositionId: composition?.Id),
            project.Name, shot.Title + " · Video takes", tab, request) with { Batch = batch };
    }
    public async Task<AiJobSubmission> CaptureRefinementAsync(Guid id, Guid tab, Guid projectId, Guid takeId,
        TakeRefinementMode mode, int width, int height, CancellationToken ct = default)
    {
        var document = await shots.LoadAsync(projectId, ct);
        var take = ShotCopy.Of(document.Takes.SingleOrDefault(t => t.Id == takeId) ?? throw new WorkspaceStoreException("The source take is unavailable. Restore it before refining."));
        if (take.RefinementPackage is null) throw new WorkspaceStoreException("Refinement data unavailable. Generate a new take with package capture enabled.");
        var configured = await settings.LoadAsync(ct);
        // Only the explicitly selected upscaler comes from current settings.
        // The source model, prompt, conditioning and sampling origin remain immutable.
        var checkSettings = new AiSettings { ComfyUrl = take.Snapshot.ExecutionComfyUrl, H3 = take.Snapshot.Settings with { LatentUpscaler = configured.H3.LatentUpscaler } };
        var check = await generator.CheckAsync(checkSettings, ct);
        if (check.RefinementIssue is { } issue) throw new WorkspaceStoreException(issue);
        H3Loras.CheckSubmission(take.Snapshot, check.OptionalLoras);
        var refinement = await shots.CaptureRefinementAsync(projectId, takeId, id, mode, width, height, configured.H3.LatentUpscaler, ct);
        var request = new AiVideoJobRequest(1, id, take.Snapshot, []) { Refinement = refinement,
            DestinationShotId = take.ShotId != take.Snapshot.Shot.Id ? take.ShotId : null };
        AiVideoJobPolicy.Validate(request);
        var project = await projects.GetAsync(projectId, ct) ?? throw new WorkspaceStoreException("The project is unavailable.");
        return AiJobSubmission.Create(id, AiJobKind.Video, AiBackend.ComfyUI, AiVideoJobHandler.Target(request),
            project.Name, take.Snapshot.Shot.Title + " · " + mode + " (experimental)", tab, request) with { Batch = AiBatchDefinition.Create(id, 1, null) };
    }
}

public static class AiVideoJobPolicy
{
    public static void ValidateFileName(string file, bool audio)
        => ValidateFileName(file, audio ? VideoInputKind.Audio : VideoInputKind.Image);
    public static void ValidateFileName(string file, VideoInputKind kind)
    {
        if (string.IsNullOrWhiteSpace(file) || file != Path.GetFileName(file) || file.Contains('/') || file.Contains('\\') ||
            !Enum.IsDefined(kind) || Path.GetExtension(file) != Extension(kind)) throw new WorkspaceStoreException("Invalid captured video input file.");
    }
    public static string Extension(VideoInputKind kind) => kind == VideoInputKind.Video ? ".mp4" : kind is VideoInputKind.Image or VideoInputKind.StartFrame ? ".png" : ".wav";
    public static void Validate(AiVideoJobRequest r)
    {
        if (r.Version is not (1 or 2 or 3) || r.BatchId == Guid.Empty || r.Snapshot is null || r.Inputs is null || r.Inputs.Any(i => i is null) ||
            r.Snapshot.ProjectId == Guid.Empty || r.Snapshot.Shot is null || r.Snapshot.Settings is null)
            throw new WorkspaceStoreException("Invalid queued video request.");
        var s = r.Snapshot;
        if (r.DestinationShotId is { } destination && (destination == Guid.Empty || s.Reel is not null ||
            r.Refinement is null && s.RegenerationSource is null && s.Dub is null))
            throw new WorkspaceStoreException("Invalid destination for the captured take request.");
        ShotDubbing.ValidateSnapshot(s);
        if (s.RegenerationSource is { } origin && (origin.TakeId == Guid.Empty || origin.Seed < 0 || origin.Width < 1 || origin.Height < 1 || s.Reel is not null || r.Refinement is not null))
            throw new WorkspaceStoreException("Invalid source take for regeneration.");
        if ((s.Reel is not null) != (r.Version == 3) || s.Reel is not null && r.Refinement is not null) throw new WorkspaceStoreException("Invalid reel request version.");
        H3Policy.Validate(s.Shot, true, requireScene: s.Reel is null); H3Policy.ValidateSettings(s.Settings);
        H3Performance.Validate(s.Performance);
        H3Presets.Validate(s);
        H3Loras.ValidateSnapshot(s);
        H3PreviewUpscaling.Validate(s);
        if (r.Refinement is { } refinement)
        {
            RefinementPolicy.Validate(refinement, s);
            if (r.Inputs.Count != 0 || string.IsNullOrWhiteSpace(s.Prompt) || !RefinementPolicy.Hash(s.Fingerprint))
                throw new WorkspaceStoreException("Invalid captured refinement context.");
            AiProviderRegistry.NormalizeComfyUrl(s.ExecutionComfyUrl);
            return; // Saved packages already contain conditioning; never recompile/re-encode current references.
        }
        var size = s.Reel is { } reel ? VideoResolutions.Size(reel.Recipe) : VideoResolutions.Size(s.Shot);
        var fingerprint = s.Reel is { } reelContext ? VideoResolutions.Fingerprint(reelContext.Recipe) : H3Policy.Fingerprint(s.Shot);
        if (s.ReferenceGuidance is null || s.Appearances is null || !ValidPrompt(s) || s.Fingerprint != fingerprint ||
            s.FrameCount != H3Policy.Frames(s.Shot.Duration!.Value) ||
            (s.Width, s.Height) != (size.Width, size.Height) || s.Sampling != H3Policy.Sampling(s.Shot, s.Settings) ||
            r.Version == 1 && s.Shot.Videos.Count != 0 ||
            !r.Inputs.Select(i => (i.EffectiveKind, i.VideoIndex)).SequenceEqual(ReferenceVideos.InputOrder(s.Shot)) ||
            r.Inputs.Any(i => i.Audio != (i.EffectiveKind is VideoInputKind.Audio or VideoInputKind.VideoSoundtrack)) ||
            r.Inputs.Select(i => i.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != r.Inputs.Count)
            throw new WorkspaceStoreException("The captured video prompt, dimensions, or ordered inputs do not match the shot.");
        if (s.Production is { } production && !production.Images.Select(i => i.Sha256).SequenceEqual(r.Inputs.Where(i => i.EffectiveKind == VideoInputKind.Image).Select(i => i.Sha256)))
            throw new WorkspaceStoreException("The video inputs do not match the composition's captured images.");
        AiProviderRegistry.NormalizeComfyUrl(s.ExecutionComfyUrl);
        foreach (var input in r.Inputs)
        {
            ValidateFileName(input.FileName, input.EffectiveKind);
            if (input.Bytes <= 0 || input.Bytes > (input.EffectiveKind == VideoInputKind.Video ? ReferenceVideos.MaximumBytes : 50 * 1024 * 1024) || input.Sha256?.Length != 64 || input.Sha256.Any(c => !Uri.IsHexDigit(c)))
                throw new WorkspaceStoreException("A captured video reference is empty, too large, or has no content identity.");
        }
    }
    private static bool ValidPrompt(VideoSnapshot s)
    {
        if (s.Reel is { } reel)
        {
            ReferenceReels.Validate(reel.Recipe, true);
            if (reel.Owner is { } owner) ReferenceReels.ValidateOwner(reel.Recipe, owner);
            return s.Production is null && s.Profile == reel.Recipe.PresetVersion && s.Prompt == reel.Recipe.Prompt &&
                H3Policy.Fingerprint(s.Shot) == H3Policy.Fingerprint(ReferenceReels.Inputs(reel.Recipe)) && reel.Character.AssetId == reel.Recipe.AssetId;
        }
        if (s.Production is null) return s.Profile == H3Policy.Profile && s.Prompt == H3Policy.Compile(s.Shot, s.ReferenceGuidance, s.Appearances);
        var p = s.Production;
        if (s.Profile != ProductionPolicy.Profile || p.CompositionId == Guid.Empty || p.Version < 1 || p.Revision is null || p.Revision.Id == Guid.Empty || p.Revision.Prompt != s.Prompt || p.Images is null ||
            !p.Images.Select(i => i.BindingId).SequenceEqual(ResolvedReferences.For(s.Shot).Pictures.Select(i => i.BindingId))) return false;
        ProductionPolicy.ValidateGenerationPrompt(s.Prompt);
        return true;
    }
    public static async Task ValidatePreparedFilesAsync(AiVideoJobRequest request, string directory, CancellationToken ct)
    {
        Validate(request);
        if (request.Refinement is { } refinement)
        {
            await RefinementPackages.VerifyFileAsync(Path.Combine(directory, "inputs", H3RefinementPackage.FileName), refinement.SourcePackage.Bytes, refinement.SourcePackage.Sha256, ct);
            await RefinementPackages.VerifyFileAsync(Path.Combine(directory, "inputs", "source.mp4"), refinement.SourceVideoBytes, refinement.SourceVideoSha256, ct);
            return;
        }
        foreach (var input in request.Inputs)
        {
            await using var file = File.OpenRead(Path.Combine(directory, "inputs", input.FileName));
            if (file.Length != input.Bytes || Convert.ToHexString(await SHA256.HashDataAsync(file, ct)) != input.Sha256)
                throw new WorkspaceStoreException("A captured video reference file changed. This batch cannot silently use different inputs.");
        }
    }
    public static async Task CopyPreparedInputsAsync(AiVideoJobRequest source, string sourceDirectory, AiVideoJobRequest request, string directory, CancellationToken ct)
    {
        if (source.BatchId == request.BatchId || string.Equals(sourceDirectory, directory, StringComparison.OrdinalIgnoreCase))
            throw new WorkspaceStoreException("Regeneration needs a new request identity.");
        using var gate = await ProjectFiles.LockAsync(directory, ct);
        using var sourceGate = await ProjectFiles.LockAsync(sourceDirectory, ct);
        var inputs = Path.Combine(directory, "inputs");
        if (Directory.Exists(inputs)) throw new WorkspaceStoreException("This request already has captured inputs. Retry its saved submission.");
        await ValidatePreparedFilesAsync(source, sourceDirectory, ct);
        Directory.CreateDirectory(inputs);
        foreach (var input in request.Inputs)
        {
            var target = Path.Combine(inputs, input.FileName);
            await using (var from = File.OpenRead(Path.Combine(sourceDirectory, "inputs", input.FileName)))
            await using (var to = new FileStream(target + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
                await from.CopyToAsync(to, ct);
            DurableFile.Flush(target + ".tmp");
            File.Move(target + ".tmp", target, overwrite: true);
        }
        await ValidatePreparedFilesAsync(request, directory, ct);
    }
    public static VideoRun Run(AiVideoJobRequest request) => new() { Id = request.BatchId, Snapshot = ShotCopy.Of(request.Snapshot), Refinement = ShotCopy.Of(request.Refinement),
        Inputs = request.Inputs.Select(i => new PreparedVideoInput(i.FileName, i.Audio) { Kind = i.Kind, VideoIndex = i.VideoIndex }).ToList(), InputsPrepared = true };
}
