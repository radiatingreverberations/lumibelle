using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiVideoJobCapture
{
    public Task<AiJobSubmission> CaptureRegenerationAsync(Guid id, Guid tab, Guid projectId, Guid takeId,
        VideoResolution resolution, long? seed, CancellationToken ct = default)
        => CaptureRegenerationAsync(id, tab, projectId, takeId, resolution, seed, null, ct);

    public Task<AiJobSubmission> CaptureRegenerationAsync(Guid id, Guid tab, Guid projectId, Guid takeId,
        VideoResolution resolution, long? seed, bool? saveLosslessFrames, CancellationToken ct = default)
        => CaptureRegenerationAsync(id, tab, projectId, takeId, resolution, seed, saveLosslessFrames, null, ct);

    // saveLosslessFrames and saveLatents null keep the source take's choice.
    public async Task<AiJobSubmission> CaptureRegenerationAsync(Guid id, Guid tab, Guid projectId, Guid takeId,
        VideoResolution resolution, long? seed, bool? saveLosslessFrames, bool? saveLatents, CancellationToken ct = default)
    {
        var store = jobs ?? throw new WorkspaceStoreException("Saved video requests are unavailable.");
        var document = await shots.LoadAsync(projectId, ct);
        var take = document.Takes.SingleOrDefault(t => t.Id == takeId)
            ?? throw new WorkspaceStoreException("Restore the source take before regenerating it.");
        if (TakeDisplay.RegenerationIssue(take) is { } issue) throw new WorkspaceStoreException(issue);
        var sourceJob = (await store.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == take.AiJobId)
            ?? throw new WorkspaceStoreException("This take's captured request is unavailable.");
        var source = AiVideoJobHandler.Read(sourceJob, await store.ReadSnapshotAsync(sourceJob.Id, ct));
        if (source.Snapshot.ProjectId != projectId || source.BatchId != take.RunId || source.Snapshot.Reel is not null ||
            source.Refinement is not null || sourceJob.Batch!.Candidates.Any(c => c.Id == take.Id && c.Number == take.Candidate && c.Seed == take.Seed) != true ||
            !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(take.Snapshot, AtomicJsonFile.Options),
                JsonSerializer.SerializeToElement(source.Snapshot, AtomicJsonFile.Options)))
            throw new WorkspaceStoreException("The source take does not match its captured request.");
        if (document.Shots.All(s => s.Id != take.ShotId)) throw new WorkspaceStoreException("Restore the destination shot before regenerating.");
        if (source.Snapshot.Production is { } setup && production is not null &&
            (await production.LoadAsync(projectId, ct)).Compositions.All(c => c.Id != setup.CompositionId))
            throw new WorkspaceStoreException("Restore the source setup before regenerating this take.");
        var batch = AiBatchDefinition.Create(id, 1, seed);
        var shot = source.Snapshot.Shot.Copy();
        VideoResolutions.Select(shot, resolution);
        if (saveLosslessFrames is { } keepFrames && keepFrames != (source.Snapshot.OutputPolicy?.SaveLosslessFrames ?? true))
        {
            // Requests captured before output policies always kept frames and cannot record another choice.
            if (source.Snapshot.OutputPolicy is null) throw new WorkspaceStoreException("This take's saved request always keeps lossless frames.");
            shot.SaveLosslessFrames = keepFrames;
        }
        // Regenerating a take with its seed and Save latents on gives a take that can be refined.
        shot.SaveLatents = saveLatents ?? source.Snapshot.CaptureRefinementData;
        var size = VideoResolutions.Size(shot);
        var snapshot = ShotCopy.Of(source.Snapshot) with {
            Shot = shot, Width = size.Width, Height = size.Height, Fingerprint = H3Policy.Fingerprint(shot),
            OutputPolicy = source.Snapshot.OutputPolicy is null ? null : new(shot.SaveLosslessFrames),
            PreviewUpscale = null, CaptureRefinementData = shot.SaveLatents, RegenerationSource = new(take.Id, take.Seed, take.Width, take.Height),
            TargetComfyUrl = null
        };
        // New requests own their immutable inputs, including formerly version-one captures.
        var request = source with { Version = 2, BatchId = id, Snapshot = snapshot, Inputs = ShotCopy.Of(source.Inputs),
            DestinationShotId = take.ShotId != snapshot.Shot.Id ? take.ShotId : null };
        AiVideoJobPolicy.Validate(request);
        var currentSettings = ShotCopy.Of(await settings.LoadAsync(ct));
        var target = AiProviderRegistry.NormalizeComfyUrl(currentSettings.ComfyUrl);
        var check = await generator.CheckAsync(new AiSettings { ComfyUrl = target, H3 = snapshot.Settings }, ct);
        if (!H3ReadyToSubmit(snapshot with { TargetComfyUrl = target }, check, out var currentIssue) &&
            target != snapshot.ComfyUrl)
        {
            var fallback = await generator.CheckAsync(new AiSettings { ComfyUrl = snapshot.ComfyUrl, H3 = snapshot.Settings }, ct);
            if (H3ReadyToSubmit(snapshot, fallback, out _))
            {
                check = fallback;
                target = snapshot.ComfyUrl;
            }
            else if (currentIssue is not null)
            {
                throw new WorkspaceStoreException($"The current ComfyUI connection could not run this take, and the captured server was also unavailable. " +
                    $"Current ({target}): {currentIssue} Captured ({snapshot.ComfyUrl}): {fallback.Message}");
            }
        }
        if (!H3ReadyToSubmit(snapshot with { TargetComfyUrl = target }, check, out var finalIssue)) throw new WorkspaceStoreException(finalIssue ?? check.Message);
        snapshot = snapshot with { TargetComfyUrl = target == snapshot.ComfyUrl ? null : target };
        H3Presets.CheckSubmission(snapshot, check);
        H3Loras.CheckSubmission(snapshot, check.OptionalLoras);
        if (id == source.BatchId) throw new WorkspaceStoreException("Regeneration needs a new request identity.");
        var sourceDirectory = await shots.RunDirectoryAsync(projectId, source.BatchId, ct);
        var directory = await shots.RunDirectoryAsync(projectId, id, ct);
        request = request with { Snapshot = snapshot };
        await AiVideoJobPolicy.CopyPreparedInputsAsync(source, sourceDirectory, request, directory, ct);
        if ((await shots.LoadAsync(projectId, ct)).Shots.All(s => s.Id != take.ShotId)) throw new WorkspaceConflictException();
        var project = await projects.GetAsync(projectId, ct) ?? throw new WorkspaceStoreException("Project unavailable.");
        return AiJobSubmission.Create(id, AiJobKind.Video, AiBackend.ComfyUI, AiVideoJobHandler.Target(request),
            project.Name, take.Snapshot.Shot.Title + " · Regenerate · " + size.Width + " × " + size.Height, tab, request)
            with { Batch = batch };
    }

    private static bool H3ReadyToSubmit(VideoSnapshot snapshot, H3Configuration check, out string? issue)
    {
        try
        {
            H3Presets.CheckSubmission(snapshot, check);
            H3Loras.CheckSubmission(snapshot, check.OptionalLoras);
            issue = null;
            return true;
        }
        catch (WorkspaceStoreException e) { issue = e.Message; return false; }
    }
}
