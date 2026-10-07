using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public interface IComfyVideoJobAdapter
{
    Task ValidateAsync(AiVideoJobRequest request, CancellationToken ct);
    Task<Func<string, object>> PrepareWorkflowAsync(AiVideoJobRequest request, AiBatchCandidate candidate, string directory, CancellationToken ct);
    async Task<Func<string, object>> PrepareBatchWorkflowAsync(AiVideoJobRequest request, IReadOnlyList<AiBatchCandidate> candidates, string directory, CancellationToken ct)
    {
        var workflows = new Dictionary<Guid, Func<string, object>>();
        foreach (var candidate in candidates) workflows.Add(candidate.Id, await PrepareWorkflowAsync(request, candidate, directory, ct));
        return clientId => ComfyMultiTakeWorkflow.Build(candidates, c => workflows[c.Id](c.Id.ToString("D")), clientId);
    }
    Task<Func<string, object>> PrepareQueuedWorkflowAsync(AiJobContext context, AiVideoJobRequest request,
        IReadOnlyList<AiBatchCandidate> candidates, string directory, CancellationToken ct) => candidates.Count == 1
        ? PrepareWorkflowAsync(request, candidates[0], directory, ct)
        : PrepareBatchWorkflowAsync(request, candidates, directory, ct);
    Task<bool> RecoverPreparationAsync(AiJobContext context, AiVideoJobRequest request, CancellationToken ct) => Task.FromResult(false);
    ComfyExecutionOptions Options { get; }
    Task<ShotTake> DownloadAsync(AiVideoJobRequest request, VideoCandidate candidate, string directory, Func<string, Task> progress, CancellationToken ct);
}

public sealed class ComfyVideoJobAdapter(ComfyH3Video video, ComfyRefModCache? refModCache = null) : IComfyVideoJobAdapter
{
    public ComfyExecutionOptions Options => ComfyH3Video.MonitorOptions;
    public async Task<Func<string, object>> PrepareQueuedWorkflowAsync(AiJobContext context, AiVideoJobRequest request,
        IReadOnlyList<AiBatchCandidate> candidates, string directory, CancellationToken ct)
    {
        if (!ReelRefMods.Uses(request.Snapshot.Shot))
            return candidates.Count == 1 ? await PrepareWorkflowAsync(request, candidates[0], directory, ct)
                : await PrepareBatchWorkflowAsync(request, candidates, directory, ct);
        if (candidates.Count is < 1 or > 4) throw new WorkspaceStoreException("Choose one to four captured candidates.");
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, directory, ct);
        var cache = refModCache ?? throw new WorkspaceStoreException("Reference-cache preparation is unavailable. Restart Lumibelle with the current services.");
        var scope = candidates.Count == 1 ? ComfyMultiTakeWorkflow.CandidateOperation(candidates[0]) : ComfyMultiTakeWorkflow.Operation;
        var prepared = await cache.EnsureAsync(context, request.Snapshot, scope, ct);
        var refine = await RefineSourceAsync(request, directory, ct);
        var uploaded = await video.UploadAsync(AiVideoJobPolicy.Run(request), directory, ct, prepared);
        if (candidates.Count == 1)
            return clientId => ComfyH3Video.BuildWorkflow(request.Snapshot, candidates[0].Seed, clientId, uploaded, prepared, refine);
        return clientId => ComfyMultiTakeWorkflow.Build(candidates,
            c => ComfyH3Video.BuildWorkflow(request.Snapshot, c.Seed, c.Id.ToString("D"), uploaded, prepared, refine), clientId);
    }
    // Uploads a refinement's saved latents; null for normal generation.
    private async Task<ComfyH3Video.RefineSource?> RefineSourceAsync(AiVideoJobRequest request, string directory, CancellationToken ct)
    {
        if (request.Refinement is not { } refinement) return null;
        var (videoLatent, audioLatent) = await video.UploadRefinementAsync(request.Snapshot.ExecutionComfyUrl,
            Path.Combine(directory, "inputs", H3RefinementPackage.FileName), Path.Combine(directory, "refinement-upload"), ct);
        return new(refinement, videoLatent, audioLatent);
    }
    public Task<bool> RecoverPreparationAsync(AiJobContext context, AiVideoJobRequest request, CancellationToken ct) =>
        !ReelRefMods.Uses(request.Snapshot.Shot) ? Task.FromResult(false)
            : (refModCache ?? throw new WorkspaceStoreException("Reference-cache recovery is unavailable."))
                .RecoverAsync(context, request.Snapshot, ct);
    public async Task ValidateAsync(AiVideoJobRequest request, CancellationToken ct)
    {
        var s = request.Snapshot;
        var configuration = await video.CheckAsync(new() { ComfyUrl = s.ExecutionComfyUrl, H3 = s.Settings with {
            Performance = H3Performance.Preferences(s.Performance), LatentUpscaler = request.Refinement?.Upscaler ?? s.Settings.LatentUpscaler } }, ct);
        H3Loras.CheckSubmission(s, configuration.OptionalLoras);
        if (ReelRefMods.Uses(s.Shot) && configuration.RefModIssue is { } refmodIssue) throw new WorkspaceStoreException(refmodIssue);
        if (s.Shot.Videos.Any(v => v.EffectiveVisuals == ReelVisuals.FullReel) && configuration.VideoReferenceIssue is { } videoIssue) throw new WorkspaceStoreException(videoIssue);
        if (request.Refinement is { } refinement)
        {
            if (configuration.RefinementIssue is { } issue) throw new WorkspaceStoreException(issue);
            if (configuration.PreviewUpscaling.Implementation != refinement.Implementation)
                throw new WorkspaceStoreException("The installed latent upscaler changed after this refinement was queued. Restore it or refine the take again.");
        }
        else
        {
            if (s.Shot.StartFrame is not null && configuration.StartFrameIssue is { } startIssue) throw new WorkspaceStoreException(startIssue);
            H3Presets.CheckSubmission(s, configuration);
            H3PreviewUpscaling.CheckSubmission(s, configuration);
            if (s.CaptureRefinementData && !configuration.PackageCaptureReady) throw new WorkspaceStoreException("Update ComfyUI to keep refinement data for new takes.");
        }
    }
    public async Task<Func<string, object>> PrepareWorkflowAsync(AiVideoJobRequest request, AiBatchCandidate candidate, string directory, CancellationToken ct)
    {
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, directory, ct);
        var refine = await RefineSourceAsync(request, directory, ct);
        var uploaded = await video.UploadAsync(AiVideoJobPolicy.Run(request), directory, ct);
        return clientId => ComfyH3Video.BuildWorkflow(request.Snapshot, candidate.Seed, clientId, uploaded, refine: refine);
    }
    public async Task<Func<string, object>> PrepareBatchWorkflowAsync(AiVideoJobRequest request, IReadOnlyList<AiBatchCandidate> candidates, string directory, CancellationToken ct)
    {
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, directory, ct);
        var refine = await RefineSourceAsync(request, directory, ct);
        var uploaded = await video.UploadAsync(AiVideoJobPolicy.Run(request), directory, ct);
        return clientId => ComfyMultiTakeWorkflow.Build(candidates,
            c => ComfyH3Video.BuildWorkflow(request.Snapshot, c.Seed, c.Id.ToString("D"), uploaded, refine: refine), clientId);
    }
    public Task<ShotTake> DownloadAsync(AiVideoJobRequest request, VideoCandidate candidate, string directory, Func<string, Task> progress, CancellationToken ct) =>
        video.DownloadAsync(AiVideoJobPolicy.Run(request), candidate, directory, progress, ct);
}
