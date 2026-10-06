using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class FileAiJobStore
{
    // Older planners discarded usable proposals for editorial dialogue or camera wording.
    // Reopen their captured response for manual review, never making another provider request.
    public async Task<AiJobHeader> ReviewShotPlanningAsync(Guid projectId, Guid jobId, CancellationToken ct = default)
    {
        using var gate = await ProjectFiles.LockAsync(Index, ct);
        var document = await ReadIndexAsync(ct);
        var job = document.Jobs.SingleOrDefault(j => j.Id == jobId && j.Kind == AiJobKind.ShotPlanning && j.Target.ProjectId == projectId)
            ?? throw new WorkspaceStoreException("Shot planning request not found in this project.");
        if (job.State != AiJobState.NeedsAttention || job.CancelRequested || job.RemoteUnconfirmed ||
            !(job.Error?.StartsWith("The proposal changed or invented dialogue.", StringComparison.Ordinal) == true ||
              job.Error?.StartsWith("The model returned an invalid or incomplete shot list.", StringComparison.Ordinal) == true)) return job;
        var saved = await ReadArtifactAsync<AiTextJobResult>(jobId, AiJobArtifact.Result, ct);
        if (saved is not { Complete: true }) return job;
        // The artifact is published first. Reopening after an interrupted index save reuses
        // its shot identities instead of parsing again and invalidating a review draft.
        var parsed = saved.Error is null && saved.Read<ShotPlanningResult>() is { Error: null, Shots.Count: > 0 } ? saved
            : AiTextResults.Parse(AiTextJobHandler.Read(job, await ReadSnapshotAsync(job, ct)), saved.Raw, saved.FinishReason)
                with { OpenRouterUsage = saved.OpenRouterUsage };
        // Malformed or incomplete responses still need attention. Repeated review
        // must not rewrite their artifacts or churn the queue revision.
        if (parsed.Error is not null) return job;
        await WriteArtifactFileAsync(ArtifactPath(jobId, AiJobArtifact.Result), parsed, ct);
        var next = job with { State = AiJobState.Completed,
            Error = null, Recovery = AiJobRecovery.None,
            Version = job.Version + 1 };
        await PublishAsync(document with { Jobs = document.Jobs.Select(j => j.Id == jobId ? next : j).ToArray() }, ct);
        return next;
    }
}
