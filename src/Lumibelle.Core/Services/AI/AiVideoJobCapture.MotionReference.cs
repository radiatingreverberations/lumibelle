using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiVideoJobCapture
{
    public async Task<MotionReferenceDraft> PrepareMotionReferenceAsync(Guid projectId, Guid takeId, CancellationToken ct = default, IProgress<VideoPreparationProgress>? progress = null)
    {
        progress?.Report(new(VideoPreparationStage.Source, "Opening the source take"));
        var document = await shots.LoadAsync(projectId, ct);
        var take = document.Takes.SingleOrDefault(t => t.Id == takeId) ?? throw new WorkspaceStoreException("Restore the source take before using it as a reference.");
        if (MotionReferencePolicy.Issue(take) is { } issue) throw new WorkspaceStoreException(issue);
        var configured = await settings.LoadAsync(ct);
        progress?.Report(new(VideoPreparationStage.Source, "Copying the source clip", Detail: "Saving an independent video reference"));
        var media = await (referenceVideos ?? throw new WorkspaceStoreException("Video reference storage is unavailable."))
            .CopyTakeAsync(projectId, takeId, configured.H3, ct);
        if (media.Frames != take.FrameCount || media.Fps != take.Fps) throw new WorkspaceStoreException("The copied MP4 does not match this take's visible frame count.");
        var shot = take.Snapshot.Shot.Copy(); shot.Id = take.ShotId; shot.SelectedTakeId = null;
        // Replace a previously managed guide; preserve all unrelated references and audio mappings.
        var index = take.Snapshot.MotionReference is { } prior ? shot.Videos.FindIndex(v => v.Id == prior.BindingId) : -1;
        if (index >= 0) shot.Videos.RemoveAt(index);
        var binding = new ShotVideoBinding { Media = media, Name = take.Snapshot.Shot.Title + " · Motion reference",
            Description = MotionReferencePolicy.DefaultInstructions, UseSoundtrack = false, Visuals = ReelVisuals.FullReel };
        shot.Videos.Insert(index >= 0 ? index : shot.Videos.Count, binding);
        // Keep the authored duration when it already maps to the visible frame count.
        // A trimmed/composed duration instead follows the visible clip.
        if (shot.Duration is not { } duration || duration is < 1 or > 15 || H3Policy.Frames(duration) != take.FrameCount)
            shot.Duration = Math.Min(15, take.FrameCount / take.Fps);
        shot.GenerationPreset = "standard"; shot.Turbo = false; shot.TurboSteps = 4; shot.UpscalePreview = false;
        shot.SaveLatents = false; shot.SaveLosslessFrames = false;
        VideoResolutions.Select(shot, VideoResolution.Native);
        var prompt = take.Snapshot.MotionReference?.BasePrompt ?? take.Snapshot.Prompt;
        return new(shot, new(take.Id, binding.Id, media, take.FrameCount, take.Fps, prompt, MotionReferencePolicy.DefaultInstructions),
            ShotCopy.Of(take.Snapshot.ReferenceGuidance ?? []), ShotCopy.Of(take.Snapshot.Appearances ?? [])) { SceneFingerprint = take.Snapshot.SceneFingerprint };
    }

    // Only for a batch item that was never enqueued: an interrupted preparation left partial inputs.
    public async Task DiscardUnqueuedInputsAsync(Guid projectId, Guid id, CancellationToken ct = default)
    {
        if (jobs is not null && (await jobs.ReadAsync(ct)).Jobs.Any(j => j.Id == id)) throw new WorkspaceStoreException("This request is already queued.");
        var directory = await shots.RunDirectoryAsync(projectId, id, ct);
        using var gate = await ProjectFiles.LockAsync(directory, ct);
        if (Directory.Exists(Path.Combine(directory, "inputs"))) Directory.Delete(Path.Combine(directory, "inputs"), true);
    }

    // Frames a later clip will use are kept losslessly when the full archive is off and ComfyUI has the stock nodes.
    private static IReadOnlyList<int>? KeptFrames(IReadOnlyList<int>? requested, Shot shot, H3Configuration check)
    {
        if (requested is not { Count: > 0 } || shot.SaveLosslessFrames || !check.KeptFramesReady) return null;
        var count = H3Policy.Frames(shot.Duration!.Value);
        var frames = requested.Where(f => f >= 0 && f < count).Distinct().Order().Take(H3OutputPolicy.MaximumKeptFrames).ToArray();
        return frames.Length == 0 ? null : frames;
    }

    // A queued batch passes frozen = true: the guidance and looks reviewed when it was queued stay in force,
    // so later library edits cannot change the inputs. check reuses one model discovery per server.
    public async Task<AiJobSubmission> CaptureMotionReferenceAsync(Guid id, Guid tab, Guid projectId, MotionReferenceOptions options, CancellationToken ct = default,
        IProgress<VideoPreparationProgress>? progress = null, H3Configuration? check = null, bool frozen = false, IReadOnlyList<int>? keepFrames = null)
    {
        progress?.Report(new(VideoPreparationStage.Setup, "Checking the reviewed settings"));
        var draft = ShotCopy.Of(options.Draft); var shot = draft.Shot.Copy();
        var document = await shots.LoadAsync(projectId, ct);
        if (document.Shots.All(s => s.Id != shot.Id)) throw new WorkspaceStoreException("Restore the destination shot before generating.");
        shot.GenerationPreset = "standard"; shot.Turbo = false; shot.TurboSteps = 4; shot.UpscalePreview = false;
        shot.SelectedTakeId = null; shot.SaveLatents = options.SaveLatents; shot.SaveLosslessFrames = options.SaveLosslessFrames;
        VideoResolutions.Select(shot, options.Resolution);
        var binding = shot.Videos.SingleOrDefault(v => v.Id == draft.Source.BindingId) ?? throw new WorkspaceStoreException("Keep the motion-reference clip selected.");
        binding.UseSoundtrack = false; binding.Speaker = null; binding.Visuals = ReelVisuals.FullReel;
        binding.Description = options.Instructions;
        H3Policy.Validate(shot, true, requireScene: false); ReferenceVideos.Validate(shot);
        var number = ResolvedReferences.For(shot).Videos.Single(v => v.Reel.Id == binding.Id).Number;
        var source = draft.Source with { BasePrompt = options.BasePrompt, Instructions = options.Instructions };
        var prompt = MotionReferencePolicy.Render(options.BasePrompt, options.Instructions, number);
        var configured = ShotCopy.Of(await settings.LoadAsync(ct));
        check ??= await generator.CheckAsync(configured, ct);
        if (!check.Ready(shot)) throw new WorkspaceStoreException(check.Issue(shot));
        var library = await assets.LoadAsync(projectId, ct);
        IReadOnlyList<ShotReferenceGuidance> guidance = draft.Guidance; IReadOnlyList<ShotAppearanceContext> appearances = draft.Appearances;
        if (!frozen) {
            guidance = ShotReferences.Resolve(shot, library, document); appearances = ShotLooks.Capture(shot, library);
            if (!ShotReferences.SameEffective(draft.Guidance, guidance) || !draft.Appearances.SequenceEqual(appearances))
                throw new WorkspaceStoreException("Reference guidance changed. Reopen references and review the prompt.");
        }
        var loras = H3Loras.Selections(shot).Any(l => l.Enabled && l.Strength != 0)
            ? H3Loras.Capture(shot, configured, (await preferences.LoadAsync(projectId, ct)).LoraVisibility, check.OptionalLoras) : null;
        var size = VideoResolutions.Size(shot);
        var snapshot = new VideoSnapshot(projectId, document.Revision, shot, prompt, H3Policy.Fingerprint(shot),
            AiProviderRegistry.NormalizeComfyUrl(configured.ComfyUrl), configured.H3, size.Width, size.Height,
            H3Policy.Frames(shot.Duration!.Value), MotionReferencePolicy.Profile) {
            MotionReference = source, ReferenceGuidance = guidance, Appearances = appearances, AppliedLoras = loras,
            SceneFingerprint = options.Draft.SceneFingerprint,
            Preset = H3Presets.Capture(shot, configured.H3), Sampling = H3Policy.Sampling(shot, configured.H3),
            Performance = H3Performance.Capture(H3Presets.NewPerformance(configured.H3)),
            OutputPolicy = new(shot.SaveLosslessFrames) { KeptFrames = KeptFrames(keepFrames, shot, check) },
            CaptureRefinementData = shot.SaveLatents
        };
        MotionReferencePolicy.Validate(snapshot);
        var directory = await shots.RunDirectoryAsync(projectId, id, ct);
        using var gate = await ProjectFiles.LockAsync(directory, ct);
        if (Directory.Exists(Path.Combine(directory, "inputs"))) throw new WorkspaceStoreException("This request is already captured. Retry its saved submission.");
        var run = new VideoRun { Id = id, Snapshot = snapshot };
        progress?.Report(new(VideoPreparationStage.References, "Preparing reference inputs"));
        await generator.PrepareAsync(run, directory, ct, progress);
        if (!run.InputsPrepared) throw new WorkspaceStoreException("Motion-reference inputs could not be prepared.");
        List<AiVideoInput> inputs = [];
        foreach (var input in run.Inputs) {
            progress?.Report(new(VideoPreparationStage.Capture, "Saving reference inputs", inputs.Count, run.Inputs.Count, input.FileName));
            await using var file = File.OpenRead(Path.Combine(directory, "inputs", input.FileName));
            inputs.Add(new(input.FileName, input.Audio, file.Length, Convert.ToHexString(await SHA256.HashDataAsync(file, ct))) { Kind = input.Kind, VideoIndex = input.VideoIndex });
            progress?.Report(new(VideoPreparationStage.Capture, "Reference input saved", inputs.Count, run.Inputs.Count, input.FileName));
        }
        var request = new AiVideoJobRequest(2, id, snapshot, inputs);
        progress?.Report(new(VideoPreparationStage.Request, "Saving the reviewed request"));
        request = await H3GenerationRecipes.StampAsync(request, generator, ct);
        AiVideoJobPolicy.Validate(request); CapturedInputStore.Share(directory, inputs);
        var project = await projects.GetAsync(projectId, ct) ?? throw new WorkspaceStoreException("The project is unavailable.");
        return AiJobSubmission.Create(id, AiJobKind.Video, AiBackend.ComfyUI, AiVideoJobHandler.Target(request), project.Name,
            shot.Title + " · Motion reference", tab, request) with { Batch = AiBatchDefinition.Create(id, 1, options.Seed) };
    }
}
