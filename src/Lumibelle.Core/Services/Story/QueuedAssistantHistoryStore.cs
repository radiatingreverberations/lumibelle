using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Story;

// Job results own generated content. Project history owns the author's review
// decisions. Reading both avoids a second background writer of project history
// and allows a response to survive navigation, restart, or history-save failure.
public sealed class QueuedAssistantHistoryStore(FileAssistantHistoryStore history, IAiJobStore jobs) : IAssistantHistoryStore
{
    public async Task<AssistantHistory> LoadAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var saved = await history.LoadAsync(projectId, cancellationToken);
        var runs = saved.Runs.ToDictionary(r => r.Id);
        var queue = await jobs.ReadAsync(cancellationToken);
        foreach (var job in queue.Jobs.Where(j => j.Kind == AiJobKind.ScriptAssistant && j.Target.ProjectId == projectId))
        {
            var run = await ReadJobAsync(job, cancellationToken);
            if (runs.TryGetValue(run.Id, out var reviewed))
            {
                if (reviewed.JobId != job.Id) throw new WorkspaceStoreException("A script request conflicts with another history entry.");
                run = run with { Revision = reviewed.Revision, Applied = reviewed.Applied, Rejected = reviewed.Rejected, AppliedTarget = reviewed.AppliedTarget };
            }
            runs[run.Id] = run;
        }
        return saved with { Runs = runs.Values.OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id).ToList() };
    }

    public async Task<AssistantRun> SaveRunAsync(Guid projectId, AssistantRun run, CancellationToken cancellationToken = default)
    {
        run = JsonSerializer.Deserialize<AssistantRun>(JsonSerializer.SerializeToUtf8Bytes(run, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
        var queue = await jobs.ReadAsync(cancellationToken);
        if (run.JobId is not { } id)
        {
            if (queue.Jobs.Any(j => j.Id == run.Id)) throw new WorkspaceStoreException("A queued script response must retain its job identity.");
            return await history.SaveRunAsync(projectId, run, cancellationToken);
        }
        var job = queue.Jobs.SingleOrDefault(j => j.Id == id && j.Kind == AiJobKind.ScriptAssistant && j.Target.ProjectId == projectId)
            ?? throw new WorkspaceStoreException("The saved script job does not belong to this project.");
        var captured = await ReadJobAsync(job, cancellationToken);
        var decisions = (await history.LoadAsync(projectId, cancellationToken)).Runs.SingleOrDefault(r => r.Id == captured.Id);
        if ((decisions?.Revision ?? 0) != run.Revision) throw new WorkspaceConflictException();
        // Only explicit review decisions can be written back. A stale or altered
        // caller must not replace the captured target, response, model or proposal.
        var expected = captured with { Revision = run.Revision, Applied = run.Applied, Rejected = run.Rejected, AppliedTarget = run.AppliedTarget };
        if (JsonSerializer.Serialize(expected, AtomicJsonFile.Options) != JsonSerializer.Serialize(run, AtomicJsonFile.Options))
            throw new WorkspaceStoreException("The script response changed. Reopen the saved response before applying it.");
        if (decisions?.Applied == true && !run.Applied || decisions?.Rejected == true && !run.Rejected || run.Applied && run.Rejected)
            throw new WorkspaceStoreException("This script response has already been reviewed.");
        if (run.Applied && (captured.Status != AssistantRunStatus.Completed || captured.Error is not null || captured.Proposal is null))
            throw new WorkspaceStoreException("Only a complete, valid script proposal can be applied.");
        return await history.SaveRunAsync(projectId, expected, cancellationToken);
    }

    private async Task<AssistantRun> ReadJobAsync(AiJobHeader job, CancellationToken ct)
    {
        var request = AiTextJobHandler.Read(job, await jobs.ReadSnapshotAsync(job.Id, ct));
        var run = request.Payload<ScriptAssistantRequest>().Run;
        if (run.Id != job.Id || run.JobId is { } linked && linked != job.Id)
            throw new WorkspaceStoreException("The script job has a mismatched request identity.");
        var result = await jobs.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result, ct);
        var active = job.State is AiJobState.Waiting or AiJobState.Running;
        var update = result is { Complete: true } ? result.Read<AssistantUpdate>() : null;
        var status = job.CancelRequested || job.State == AiJobState.Cancelled ? AssistantRunStatus.Cancelled
            : active ? AssistantRunStatus.Running : result is { Complete: true } ? AssistantRunStatus.Completed : AssistantRunStatus.Failed;
        var error = job.CancelRequested ? "Request cancelled. Partial output is kept for reference."
            : result?.Error ?? update?.ValidationError ?? job.Error;
        // A terminal job without validated output is never an applicable proposal,
        // even if its raw response happens to contain well-formed-looking JSON.
        if (!active && status == AssistantRunStatus.Completed && update is null)
            error ??= "No validated screenplay was saved. Inspect the response and retry explicitly.";
        return run with { JobId = job.Id, Revision = 0, Status = status, Output = result?.Raw ?? "", Proposal = update?.Blocks, Edits = update?.Edits,
            Error = error, NormalizationNotes = update?.NormalizationNotes, Applied = false, Rejected = false, AppliedTarget = null };
    }
}
