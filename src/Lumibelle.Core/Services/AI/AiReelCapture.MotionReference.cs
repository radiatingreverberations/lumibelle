using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiReelCapture
{
    public async Task<MotionReferenceDraft> PrepareMotionReferenceAsync(Guid projectId, Guid reelId, CancellationToken ct = default, IProgress<VideoPreparationProgress>? progress = null)
    {
        progress?.Report(new(VideoPreparationStage.Source, "Opening the source reel"));
        var library = await assets.LoadAsync(projectId, ct);
        var reel = library.Reels.SingleOrDefault(r => r.Id == reelId) ?? throw new WorkspaceStoreException("Restore the source reel before using it as a reference.");
        if (MotionReferencePolicy.Issue(reel) is { } issue) throw new WorkspaceStoreException(issue);
        var context = reel.Generation!.Snapshot.Reel ?? throw new WorkspaceStoreException("This reel has no saved generation recipe.");
        if (reel.Generation.Snapshot.ProjectId != projectId) throw new WorkspaceStoreException("The source reel belongs to another project.");
        var owner = library.Assets.SingleOrDefault(a => a.Id == reel.AssetId) ?? throw new WorkspaceStoreException("Restore the reel's asset first.");
        var recipe = context.Recipe.Copy() with {
            Id = Guid.NewGuid(), Revision = 0, AssetId = reel.AssetId, LookId = reel.LookId, Name = reel.Name, UseGuidance = reel.UseGuidance,
            GenerationSetup = null, OutputOverrides = null, GenerationPreset = "standard", Turbo = false, TurboSteps = 4,
            PendingJobId = null, ResolvedJobs = [], CheckedInputs = null, SaveLatents = false, SaveLosslessFrames = true
        };
        ReferenceReels.ValidateOwner(recipe, owner);
        var character = LookPolicy.Capture(owner, recipe.LookId); LookPolicy.ValidateTarget(library, character);
        var configured = await settings.LoadAsync(ct);
        var storage = referenceVideos ?? throw new WorkspaceStoreException("Video reference storage is unavailable.");
        await storage.ValidateAsync(projectId, [new() { Media = reel.Media, Name = reel.Name }], ct);
        progress?.Report(new(VideoPreparationStage.Source, "Copying the source clip", Detail: "Saving an independent video reference"));
        await using var original = await storage.OpenAsync(projectId, reel.Media.Id, ct) ?? throw new WorkspaceStoreException("The source reel's video is unavailable.");
        var media = await storage.ImportAsync(projectId, original.Content, "motion-reference.mp4", configured.H3, ct);
        if (media.Frames != reel.Media.Frames || media.Fps != reel.Media.Fps) throw new WorkspaceStoreException("The copied MP4 does not match the source reel.");
        recipe.KeyframeReels ??= [];
        var index = reel.Generation.Snapshot.MotionReference is { } prior ? recipe.KeyframeReels.FindIndex(v => v.Id == prior.BindingId) : -1;
        if (index >= 0) recipe.KeyframeReels.RemoveAt(index);
        var binding = new ShotVideoBinding { Media = media, Name = reel.Name + " · Motion reference", Description = MotionReferencePolicy.DefaultInstructions,
            UseSoundtrack = false, Visuals = ReelVisuals.FullReel };
        recipe.KeyframeReels.Insert(index >= 0 ? index : recipe.KeyframeReels.Count, binding);
        if (recipe.Duration is < 2 or > 15 || H3Policy.Frames(recipe.Duration) != media.Frames) recipe.Duration = Math.Min(15, media.Frames / media.Fps);
        VideoResolutions.Select(recipe, VideoResolution.Native);
        var shot = ReferenceReels.Inputs(recipe);
        var prompt = reel.Generation.Snapshot.MotionReference?.BasePrompt ?? context.Recipe.Prompt;
        return new(shot, new(Guid.Empty, binding.Id, media, media.Frames, media.Fps, prompt, MotionReferencePolicy.DefaultInstructions) {
                ReelId = reel.Id, SourceKeyframes = reel.Keyframes is { Frames.Count: > 0 } picked ? ShotCopy.Of(picked.Frames) : null },
            ShotReferences.Resolve(shot, library, new()), ShotLooks.Capture(shot, library)) {
            Reel = new(recipe, character) { Owner = owner with { Images = [] } }
        };
    }

    // Only for a batch item that was never enqueued: an interrupted preparation left partial inputs.
    public async Task DiscardUnqueuedInputsAsync(Guid projectId, Guid id, CancellationToken ct = default)
    {
        if (jobs is not null && (await jobs.ReadAsync(ct)).Jobs.Any(j => j.Id == id)) throw new WorkspaceStoreException("This request is already queued.");
        var directory = await reels.RunDirectoryAsync(projectId, id, ct);
        using var gate = await ProjectFiles.LockAsync(directory, ct);
        if (Directory.Exists(Path.Combine(directory, "inputs"))) Directory.Delete(Path.Combine(directory, "inputs"), true);
    }

    // frozen and check behave as in AiVideoJobCapture.CaptureMotionReferenceAsync.
    public async Task<AiJobSubmission> CaptureMotionReferenceAsync(Guid id, Guid tab, Guid projectId, MotionReferenceOptions options, CancellationToken ct = default,
        IProgress<VideoPreparationProgress>? progress = null, H3Configuration? check = null, bool frozen = false)
    {
        progress?.Report(new(VideoPreparationStage.Setup, "Checking the reviewed settings"));
        var draft = ShotCopy.Of(options.Draft);
        if (draft.Reel is null || draft.Source.ReelId is null) throw new WorkspaceStoreException("Choose a captured reel motion reference.");
        var guide = draft.Shot.Videos.SingleOrDefault(v => v.Id == draft.Source.BindingId) ?? throw new WorkspaceStoreException("Keep the motion-reference clip selected.");
        guide.UseSoundtrack = false; guide.Speaker = null; guide.Visuals = ReelVisuals.FullReel; guide.Description = options.Instructions;
        var recipe = MotionReferencePolicy.ReelRecipe(options with { Draft = draft }, id);
        var shot = ReferenceReels.Inputs(recipe);
        var library = await assets.LoadAsync(projectId, ct);
        var owner = library.Assets.SingleOrDefault(a => a.Id == recipe.AssetId) ?? throw new WorkspaceStoreException("Restore the destination asset first.");
        ReferenceReels.ValidateOwner(recipe, owner); LookPolicy.ValidateTarget(library, draft.Reel.Character, requireCurrent: !frozen);
        IReadOnlyList<ShotReferenceGuidance> guidance = draft.Guidance; IReadOnlyList<ShotAppearanceContext> appearances = draft.Appearances;
        if (!frozen) {
            guidance = ShotReferences.Resolve(shot, library, new()); appearances = ShotLooks.Capture(shot, library);
            if (!ShotReferences.SameEffective(draft.Guidance, guidance) || !draft.Appearances.SequenceEqual(appearances))
                throw new WorkspaceStoreException("Reference guidance changed. Reopen references and review the prompt.");
        }
        var configured = ShotCopy.Of(await settings.LoadAsync(ct));
        check ??= await generator.CheckAsync(configured, ct);
        if (!check.Ready(shot)) throw new WorkspaceStoreException(check.Issue(shot));
        var loras = H3Loras.Selections(shot).Any(l => l.Enabled && l.Strength != 0)
            ? H3Loras.Capture(shot, configured, (await preferences.LoadAsync(projectId, ct)).LoraVisibility, check.OptionalLoras) : null;
        var size = VideoResolutions.Size(recipe);
        var snapshot = new VideoSnapshot(projectId, 0, shot, VisualStyles.Apply(recipe.Prompt, recipe.CapturedVisualStyle), VideoResolutions.Fingerprint(recipe),
            AiProviderRegistry.NormalizeComfyUrl(configured.ComfyUrl), configured.H3, size.Width, size.Height, H3Policy.Frames(recipe.Duration), MotionReferencePolicy.Profile) {
            Reel = draft.Reel with { Recipe = recipe },
            MotionReference = draft.Source with { BasePrompt = options.BasePrompt, Instructions = options.Instructions,
                SourceKeyframes = options.KeepKeyframes && draft.Source.SourceKeyframes is { Count: > 0 } keyframes ? keyframes : null },
            ReferenceGuidance = guidance, Appearances = appearances, AppliedLoras = loras,
            Preset = H3Presets.Capture(shot, configured.H3), Sampling = H3Policy.Sampling(shot, configured.H3),
            Performance = H3Performance.Capture(H3Presets.NewPerformance(configured.H3)), CaptureRefinementData = shot.SaveLatents,
            // Without the full archive, the picked keyframes are kept losslessly on the final's timeline. All or none:
            // past the limit they are carried from the MP4 instead.
            OutputPolicy = new(shot.SaveLosslessFrames) { KeptFrames = options.KeepKeyframes && !shot.SaveLosslessFrames && check.KeptFramesReady &&
                draft.Source.SourceKeyframes is { Count: > 0 } picked && picked.Select(k => MotionReferencePolicy.MapFrame(k.Frame.Index, draft.Source.VisibleFrames,
                    H3Policy.Frames(recipe.Duration))).Distinct().Order().ToArray() is { Length: <= H3OutputPolicy.MaximumKeptFrames } mapped ? mapped : null }
        };
        MotionReferencePolicy.Validate(snapshot);
        var expectedImages = await ProductionInputs.CaptureAsync(projectId, shot, assets, ct, referenceVideos, configured.H3);
        var directory = await reels.RunDirectoryAsync(projectId, id, ct);
        using var gate = await ProjectFiles.LockAsync(directory, ct);
        if (Directory.Exists(Path.Combine(directory, "inputs"))) throw new WorkspaceStoreException("This request is already captured. Retry its saved submission.");
        var run = new VideoRun { Id = id, Snapshot = snapshot };
        progress?.Report(new(VideoPreparationStage.References, "Preparing reference inputs"));
        await generator.PrepareAsync(run, directory, ct, progress);
        if (!run.InputsPrepared) throw new WorkspaceStoreException("Motion-reference inputs could not be prepared.");
        List<AiVideoInput> inputs = [];
        foreach (var input in run.Inputs) {
            progress?.Report(new(VideoPreparationStage.Capture, "Saving reference inputs", inputs.Count, run.Inputs.Count, input.FileName));
            AiVideoJobPolicy.ValidateFileName(input.FileName, input.EffectiveKind);
            await using var file = File.OpenRead(Path.Combine(directory, "inputs", input.FileName));
            inputs.Add(new(input.FileName, input.Audio, file.Length, Convert.ToHexString(await SHA256.HashDataAsync(file, ct))) { Kind = input.Kind, VideoIndex = input.VideoIndex });
            progress?.Report(new(VideoPreparationStage.Capture, "Reference input saved", inputs.Count, run.Inputs.Count, input.FileName));
        }
        if (!expectedImages.Select(i => i.Identity.Sha256).SequenceEqual(inputs.Where(i => i.EffectiveKind == VideoInputKind.Image).Select(i => i.Sha256)))
            throw new WorkspaceStoreException("Pictures changed during capture. Review the reel references again.");
        progress?.Report(new(VideoPreparationStage.Request, "Saving the reviewed request"));
        var request = await H3GenerationRecipes.StampAsync(new AiVideoJobRequest(3, id, snapshot, inputs), generator, ct);
        AiVideoJobPolicy.Validate(request); CapturedInputStore.Share(directory, inputs);
        var name = (await projects.GetAsync(projectId, ct))?.Name ?? throw new WorkspaceStoreException("The project is unavailable.");
        return AiJobSubmission.Create(id, AiJobKind.ReelVideo, AiBackend.ComfyUI, AiVideoJobHandler.Target(request), name,
            recipe.Name + " · Motion reference", tab, request) with { Batch = AiBatchDefinition.Create(id, 1, options.Seed) };
    }
}
