using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using lumibelle.Services.Production;

namespace lumibelle.Services.AI;

public sealed partial class AiVideoJobCapture
{
    public async Task<AiJobSubmission> CaptureExtensionVersionAsync(Guid id, Guid tab, Guid projectId, Guid takeId, CancellationToken ct = default)
    {
        var request = await shots.CaptureExtensionVersionAsync(projectId, takeId, id, ct);
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, await shots.RunDirectoryAsync(projectId, id, ct), ct);
        var project = await projects.GetAsync(projectId, ct) ?? throw new WorkspaceStoreException("The project is unavailable.");
        return AiJobSubmission.Create(id, AiJobKind.Video, AiBackend.ComfyUI, AiVideoJobHandler.Target(request), project.Name, request.Snapshot.Shot.Title + " · Another extension version", tab, request)
            with { Batch = AiBatchDefinition.Create(id, 1, null) };
    }
    public static Shot ExtensionShot(ShotTake take, TakeExtensionOptions options, int generationFrames)
    {
        var shot = ShotCopy.Of(take.Snapshot.Shot);
        shot.Characters = ShotCopy.Of(ShotReferences.Characters(take.Snapshot.Shot).ToList());
        shot.Description = options.Action; shot.Dialogue = ShotCopy.Of(options.Dialogue.ToList());
        shot.Duration = Math.Min(15, generationFrames / 24d); shot.SelectedTakeId = null; shot.StartFrame = null;
        shot.GenerationPreset = "standard"; shot.Turbo = false; shot.UpscalePreview = false; shot.SaveLatents = true;
        shot.SaveLosslessFrames = options.SaveLosslessFrames;
        return shot;
    }
    public async Task<AiJobSubmission> CaptureExtensionAsync(Guid id, Guid tab, Guid projectId, TakeExtensionOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.Action)) throw new WorkspaceStoreException("Describe the next action before queueing an extension.");
        var captured = false;
        try {
        var (source, motion) = await shots.CaptureExtensionAsync(projectId, options.SourceTakeId, id, options.EndFrameExclusive, options.AddedSeconds, options.Combine, ct);
        captured = true;
        var take = source.Source;
        var contextTake = H3Motion.Tail(take, options.EndFrameExclusive).Segment;
        var shot = ExtensionShot(contextTake.Source, options, motion.GenerationFrames);
        shot.Id = take.ShotId;
        var document = await shots.LoadAsync(projectId, ct);
        var addShot = false;
        if (options.Combine && options.DestinationShotId is not null) throw new WorkspaceStoreException("A combined extension belongs to the source shot.");
        if (options.DestinationShotId is { } destination) shot.Id = document.Shots.SingleOrDefault(s => s.Id == destination)?.Id ?? throw new WorkspaceStoreException("The continuation shot is unavailable.");
        else if (!options.Combine) {
            var original = document.Shots.SingleOrDefault(s => s.Id == take.ShotId) ?? throw new WorkspaceStoreException("Restore the source shot before adding its continuation.");
            var continuation = ProductionPolicy.Continuation(original, take, options.EndFrameExclusive - 1);
            continuation.StartFrame = null; continuation.Description = options.Action; continuation.Dialogue = ShotCopy.Of(options.Dialogue.ToList());
            continuation.Duration = (motion.GenerationFrames - motion.Frames) / 24d;
            document.Shots.Insert(document.Shots.IndexOf(original) + 1, continuation);
            addShot = true;
            shot.Id = continuation.Id; shot.Title = continuation.Title;
        }
        var origin = contextTake.Source.Snapshot;
        var prompt = string.IsNullOrWhiteSpace(options.Prompt) ? H3Policy.Compile(shot, origin.ReferenceGuidance!, origin.Appearances!, motionContext: true) : options.Prompt.Trim();
        ProductionPolicy.ValidateGenerationPrompt(prompt);
        var snapshot = origin with { Shot = shot, Production = null, Profile = H3Motion.Profile, Prompt = prompt, Fingerprint = H3Policy.Fingerprint(shot),
            SourceRevision = document.Revision, Width = take.Width, Height = take.Height, FrameCount = motion.GenerationFrames, Motion = motion,
            Sampling = H3Policy.Sampling(shot, origin.Settings), Preset = H3Presets.Capture(shot, origin.Settings), OutputPolicy = new(options.SaveLosslessFrames),
            CaptureRefinementData = true, PreviewUpscale = null, RegenerationSource = null, Dub = null };
        var directory = await shots.RunDirectoryAsync(projectId, id, ct);
        var inputs = contextTake.Source.RetainedSource!.Inputs.Where(i => i.EffectiveKind != VideoInputKind.StartFrame).ToArray();
        var inputFolder = Path.Combine(directory, "inputs");
        var contextFolder = take.Composition is null ? Path.Combine(directory, H3Motion.SourceFolder) : Path.Combine(directory, H3Motion.SourceFolder, "segments", contextTake.Key.ToString("D"));
        foreach (var input in inputs) await TakeTrimming.CopyVerifiedAsync(Path.Combine(contextFolder, TakeTrimming.InputsFolder, input.FileName), Path.Combine(inputFolder, input.FileName), input.Bytes, input.Sha256, ct);
        var request = new AiVideoJobRequest(2, id, snapshot, inputs) { Extension = source };
        AiVideoJobPolicy.Validate(request); await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, directory, ct);
        var check = await generator.CheckAsync(new() { ComfyUrl = snapshot.ExecutionComfyUrl, H3 = snapshot.Settings }, ct);
        if (!check.PackageCaptureReady) throw new WorkspaceStoreException(H3Presets.LatentsIssue);
        if (generator is ComfyH3Video comfy) await comfy.CheckMotionAsync(snapshot.ExecutionComfyUrl, motion.Route, ct);
        H3Loras.CheckSubmission(snapshot, check.OptionalLoras); H3Presets.CheckSubmission(snapshot, check);
        var project = await projects.GetAsync(projectId, ct) ?? throw new WorkspaceStoreException("The project is unavailable.");
        if (addShot) {
            document = await shots.SaveAsync(projectId, document.Shots, document.Revision, "Add continuation shot", ct);
            request = request with { Snapshot = request.Snapshot with { SourceRevision = document.Revision } };
        }
        return AiJobSubmission.Create(id, AiJobKind.Video, AiBackend.ComfyUI, AiVideoJobHandler.Target(request), project.Name,
            shot.Title + " · Extend", tab, request) with { Batch = AiBatchDefinition.Create(id, 1, null) };
        } catch {
            if (captured) {
                var run = await shots.RunDirectoryAsync(projectId, id, CancellationToken.None);
                if (Directory.Exists(run)) Directory.Delete(run, true);
            }
            throw;
        }
    }
}
