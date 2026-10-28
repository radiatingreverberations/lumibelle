using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiReelCapture
{
    public async Task<AiJobSubmission> CaptureRefinementAsync(Guid id, Guid tab, Guid project, Guid reelId,
        TakeRefinementMode mode, int width, int height, CancellationToken ct = default)
    {
        var library = await assets.LoadAsync(project, ct);
        var reel = library.Reels.SingleOrDefault(r => r.Id == reelId)
            ?? throw new WorkspaceStoreException("Restore the source reel before refining it.");
        if (reel.RefinementPackage is null) throw new WorkspaceStoreException(ReferenceReels.NoRefinementLatents);
        var generation = reel.Generation ?? throw new WorkspaceStoreException("This reel has no saved generation request.");
        var store = jobs ?? throw new WorkspaceStoreException("Saved reel requests are unavailable.");
        var sourceJob = (await store.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == generation.JobId)
            ?? throw new WorkspaceStoreException("The source reel's captured request is unavailable.");
        var source = AiVideoJobHandler.Read(sourceJob, await store.ReadSnapshotAsync(generation.JobId, ct));
        if (source.Snapshot.ProjectId != project || source.BatchId != generation.BatchId || source.Snapshot.Reel is null ||
            sourceJob.Batch?.Candidates.Any(c => c.Id == reel.Id && c.Number == generation.Candidate && c.Seed == generation.Seed) != true ||
            source.Refinement != reel.Refinement || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(source.Snapshot, AtomicJsonFile.Options),
                JsonSerializer.SerializeToElement(generation.Snapshot, AtomicJsonFile.Options)))
            throw new WorkspaceStoreException("The source reel does not match its captured request.");
        if (id == source.BatchId) throw new WorkspaceStoreException("Refinement needs a new request identity.");
        var configured = await settings.LoadAsync(ct);
        var snapshot = ShotCopy.Of(source.Snapshot);
        var check = await generator.CheckAsync(new() { ComfyUrl = snapshot.ExecutionComfyUrl,
            H3 = snapshot.Settings with { LatentUpscaler = configured.H3.LatentUpscaler } }, ct);
        if (check.RefinementIssue is { } issue) throw new WorkspaceStoreException(issue);
        var implementation = check.PreviewUpscaling.Implementation
            ?? throw new WorkspaceStoreException(check.PreviewUpscaling.Issue ?? "Install the learned 3D latent upscaler to refine reels.");
        H3Loras.CheckSubmission(snapshot, check.OptionalLoras);
        var refinement = await reels.CaptureRefinementAsync(project, reel.Id, id, mode, width, height, configured.H3.LatentUpscaler, implementation, ct);
        var request = new AiVideoJobRequest(4, id, snapshot, ShotCopy.Of(source.Inputs)) { Refinement = refinement };
        AiVideoJobPolicy.Validate(request);
        await AiVideoJobPolicy.CopyPreparedInputsAsync(source, await reels.RunDirectoryAsync(project, source.BatchId, ct), request,
            await reels.RunDirectoryAsync(project, id, ct), ct);
        var name = (await projects.GetAsync(project, ct))?.Name ?? throw new WorkspaceStoreException("Project unavailable.");
        return AiJobSubmission.Create(id, AiJobKind.ReelVideo, AiBackend.ComfyUI, AiVideoJobHandler.Target(request),
            name, reel.Name + " · " + mode + " · " + width + " × " + height, tab, request)
            with { Batch = AiBatchDefinition.Create(id, 1, null) };
    }
}
