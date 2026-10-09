using lumibelle.Models;

namespace lumibelle.Services.AI;

public static class AiVideoBatchReview
{
    // Old run files have no shared submission journal. Keep their saved media and
    // receipts inspectable, without presenting an abandoned page worker as active.
    public static VideoRun Legacy(VideoRun original)
    {
        var run = original.Copy(); run.Paused = true;
        var unfinished = run.Candidates.Any(c => c.State is not (VideoCandidateState.Complete or VideoCandidateState.Cancelled));
        run.Status = unfinished
            ? "Earlier batch · saved takes are retained. Unfinished candidates cannot resume without the shared submission journal. Check the recorded ComfyUI jobs before generating a new batch."
            : "Earlier batch · saved takes are retained. Use the shot controls to generate a new batch.";
        foreach (var candidate in run.Candidates.Where(c => c.State is not (VideoCandidateState.Complete or VideoCandidateState.Cancelled)))
        {
            candidate.State = candidate.PromptId is null && candidate.State is VideoCandidateState.Waiting or VideoCandidateState.Preparing
                ? VideoCandidateState.Cancelled : VideoCandidateState.Uncertain;
            candidate.Error = candidate.PromptId is { } prompt ? "Recorded ComfyUI job: " + prompt : "No accepted ComfyUI job was recorded.";
        }
        return run;
    }

    // A project copied or imported without its batch records still has its takes. Show
    // them as a closed batch, so they can be played and chosen like any other take.
    public static VideoRun Archived(IReadOnlyList<ShotTake> takes)
    {
        var first = takes.OrderBy(t => t.Candidate).First();
        var run = new VideoRun { Id = first.RunId, Snapshot = first.Snapshot, CreatedUtc = takes.Min(t => t.CreatedUtc), Paused = true,
            Status = "Earlier batch · its batch record is not in this project, so only its saved takes are shown. Use the shot controls to generate a new batch." };
        run.Candidates.AddRange(takes.Where(t => t.Trim is null || !takes.Any(p => p.Id == t.Trim.ParentTakeId)).OrderBy(t => t.Candidate).Select(t => new VideoCandidate { TakeId = t.Id, Number = t.Candidate, Seed = t.Seed, State = VideoCandidateState.Complete }));
        return run;
    }

    public static VideoRun Project(AiVideoJobRequest request, IReadOnlyList<AiJobHeader> jobs, ShotDocument document,
        Func<Guid, AiJobProgress?> progress)
    {
        var related = jobs.Where(j => j.Batch?.RootId == request.BatchId).OrderBy(j => j.Batch!.Candidates[0].Number).ToArray();
        var root = related.Single(j => j.Id == request.BatchId);
        var current = related.FirstOrDefault(j => j.LocksTarget) ?? related[^1];
        var run = AiVideoJobPolicy.Run(request); run.CreatedUtc = root.CreatedUtc;
        run.CancelRequested = current.CancelRequested;
        run.Paused = current.State is AiJobState.NeedsAttention or AiJobState.Cancelled;
        run.Status = current.CancelRequested ? current.RemoteUnconfirmed
            ? "Cancelled locally. ComfyUI cancellation could not be confirmed; completed takes are retained."
            : current.Recovery == AiJobRecovery.RetryOutput && current.Error is { } recoveryError ? recoveryError : "Cancelled. Completed takes are retained."
            : current.Error ?? progress(current.Id)?.Progress.Label ?? current.State switch
        {
            AiJobState.Waiting => "Waiting in the ComfyUI queue", AiJobState.Running => "Generating takes…",
            AiJobState.Completed => "All takes saved", AiJobState.Cancelled => "Cancelled. Completed takes are retained.",
            _ => "This video request needs attention."
        };
        foreach (var job in related)
        {
            var reported = progress(job.Id);
            foreach (var candidate in job.Batch!.Candidates)
            {
                var complete = document.TakePublications.Any(r => r.TakeId == candidate.Id && r.JobId == job.Id);
                var executing = reported?.Candidate ?? job.Batch.Candidates.FirstOrDefault(c => !document.TakePublications.Any(r => r.TakeId == c.Id && r.JobId == job.Id))?.Number;
                var state = complete ? VideoCandidateState.Complete : job.CancelRequested || job.State == AiJobState.Cancelled ? VideoCandidateState.Cancelled
                    : job.State is AiJobState.NeedsAttention ? job.RemoteUnconfirmed ? VideoCandidateState.Uncertain : VideoCandidateState.Failed
                    : job.State != AiJobState.Running || executing != candidate.Number ? VideoCandidateState.Waiting
                    : reported?.Progress.Phase switch
                    {
                        GenerationPhase.Downloading or GenerationPhase.Saving => VideoCandidateState.Downloading,
                        GenerationPhase.Preparing => VideoCandidateState.Preparing,
                        _ => VideoCandidateState.Running
                    };
                run.Candidates.Add(new() { TakeId = candidate.Id, Number = candidate.Number, Seed = candidate.Seed, State = state, Error = complete ? null : job.Error });
            }
        }
        return run;
    }
}
