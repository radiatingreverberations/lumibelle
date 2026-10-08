using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Testing;

// Real queue and publication, deterministic in-process video generation. Remote
// receipts, uncertainty and transfer recovery use the production handler tests.
public sealed class MockVideoJobHandler(IShotStore shots, IVideoGenerator generator, ReelVideoPublication? reels = null) : IAiBatchJobHandler
{
    public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.Video, AiJobKind.ReelVideo];
    private Task<string> DirectoryAsync(AiVideoJobRequest r, CancellationToken ct) => r.Snapshot.Reel is null
        ? shots.RunDirectoryAsync(r.Snapshot.ProjectId, r.BatchId, ct) : reels!.DirectoryAsync(r.Snapshot, r.BatchId, ct);
    private async Task<bool> PublishedAsync(AiVideoJobRequest r, AiJobHeader job, AiBatchCandidate c, CancellationToken ct) => r.Snapshot.Reel is not null
        ? await reels!.PublishedAsync(r.Snapshot, c.Id, job.Id, r.BatchId, c.Number, ct)
        : (await shots.LoadAsync(r.Snapshot.ProjectId, ct)).TakePublications.Any(p => p.TakeId == c.Id && p.JobId == job.Id);
    private static AiVideoJobRequest Read(JsonElement snapshot) => snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
    public async Task ValidateExtensionAsync(AiJobHeader root, JsonElement snapshot, CancellationToken ct)
    {
        var request=Read(snapshot);
        if(request.Version == 1 && request.Refinement is null) await generator.ValidateInputsAsync(request.Snapshot,ct);
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(request,await DirectoryAsync(request,ct),ct);
    }
    public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => Task.FromResult(true);
    public async Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct)
    {
        var request = Read(snapshot); var count = 0;
        foreach (var candidate in context.Job.Batch!.Candidates) if (await PublishedAsync(request, context.Job, candidate, ct)) count++;
        return AiJobOutcome.BatchCheckpoint(count);
    }
    public async Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct)
    {
        var request = Read(snapshot); var run = AiVideoJobPolicy.Run(request);
        var directory = await DirectoryAsync(request, ct);
        var results = new List<AiVideoCandidateResult>();
        while (true)
        {
            var current = await context.CurrentAsync(ct);
            AiBatchCandidate? candidate = null;
            foreach (var item in current.Batch!.Candidates) if (!await PublishedAsync(request, current, item, ct)) { candidate = item; break; }
            if (candidate is null) return AiJobOutcome.BatchCheckpoint(current.Batch.Candidates.Count);
            if(request.Version == 1 && run.Refinement is null) await generator.ValidateInputsAsync(run.Snapshot, ct);
            await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, directory, ct);
            var take = new VideoCandidate { Number = candidate.Number, TakeId = candidate.Id, Seed = candidate.Seed };
            await context.ReportAsync(new(new(GenerationPhase.Preparing, "Preparing captured references…"), take.Number, current.Batch.Candidates[^1].Number), true);
            take.PromptId = await generator.SubmitAsync(run, take, ct);
            await foreach (var update in generator.ObserveAsync(run, take, ct).WithCancellation(ct))
            {
                await context.ReportAsync(new(update.Progress, take.Number, (await context.CurrentAsync(ct)).Batch!.Candidates[^1].Number));
                if (update.Complete) take.Output = update.Job;
            }
            var stage = Path.Combine(directory, "candidate-" + take.TakeId);
            var saved = await generator.DownloadAsync(run, take, stage, async message => await context.ReportAsync(new(new(GenerationPhase.Downloading, message), take.Number,
                (await context.CurrentAsync(ct)).Batch!.Candidates[^1].Number)), ct);
            saved.AiJobId = context.Job.Id;
            saved.ShotId = request.OutputShotId;
            if (run.Snapshot.Reel is not null) await reels!.PublishAsync(saved, stage, ct);
            else await shots.PublishTakeAsync(run.Snapshot.ProjectId, saved, stage, ct);
            var reviewId = saved.Id;
            if (request.OutputTrim is { } trim) {
                reviewId = TakeTrimming.OutputId(saved.Id);
                await shots.TrimTakeAsync(run.Snapshot.ProjectId, new(saved.Id, reviewId, trim.StartFrame, trim.EndFrameExclusive), ct: ct);
            }
            results.Add(new(reviewId, saved.Candidate, context.Job.Id)); await context.SaveResultAsync(new AiVideoJobResult(results.ToArray()));
            await context.MarkReviewableAsync(ct); await context.ReportAsync(new(new(GenerationPhase.Saving, $"Take {take.Number} saved"), take.Number,
                (await context.CurrentAsync(ct)).Batch!.Candidates[^1].Number), true);
        }
    }
}
