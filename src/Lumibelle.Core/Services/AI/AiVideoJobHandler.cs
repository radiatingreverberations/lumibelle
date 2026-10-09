using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed class AiVideoJobHandler(IShotStore shots, IVideoGenerator generator, IComfyVideoJobAdapter adapter,
    IHttpClientFactory clients, ComfyJobExecution comfy, TimeProvider clock, ReelVideoPublication? reels = null) : IAiBatchJobHandler, IAiCancelledOutputHandler
{
    public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.Video, AiJobKind.ReelVideo];
    public Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => RunAsync(context, Read(context.Job, snapshot), ct);
    public Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => RunAsync(context, Read(context.Job, snapshot), ct);
    public Task<AiJobOutcome> RecoverCancelledOutputsAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) =>
        context.RecoveringCancelledOutputs ? RunAsync(context, Read(context.Job, snapshot), ct)
            : throw new AiGenerationException("Cancelled-output recovery requires its own retrieval context.");
    public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => comfy.CancelAsync(context, Client, ct);
    public async Task ValidateExtensionAsync(AiJobHeader root, JsonElement snapshot, CancellationToken ct)
    {
        var request = Read(root, snapshot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        await ValidateInputsAsync(request, linked.Token);
        await adapter.ValidateAsync(request, linked.Token);
    }
    public static AiVideoJobRequest Read(AiJobHeader job, JsonElement snapshot)
    {
        var request = snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options) ?? throw new WorkspaceStoreException("The captured video request is missing.");
        AiVideoJobPolicy.Validate(request);
        var expected = Target(request);
        if (job.Kind != (request.Snapshot.Reel is null ? AiJobKind.Video : AiJobKind.ReelVideo) || job.Backend != AiBackend.ComfyUI || job.Batch?.RootId != request.BatchId ||
            job.Target != expected)
            throw new WorkspaceStoreException("The captured video request belongs to a different shot or batch.");
        return request;
    }
    private async Task ValidateInputsAsync(AiVideoJobRequest request, CancellationToken ct)
    {
        var s = request.Snapshot;
        if (s.Reel is not null) await ReelStore.ValidateAsync(s, ct);
        else if ((await shots.LoadAsync(s.ProjectId, ct)).Shots.All(shot => shot.Id != request.OutputShotId))
            throw new WorkspaceStoreException("The destination shot was removed. Restore it before generating more takes.");
        // Historical requests retain their library-membership checks. Version 2 owns
        // all prepared media, so retries no longer depend on mutable attachments.
        if (request.Version == 1 && request.Refinement is null) await generator.ValidateInputsAsync(s, ct);
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, await RunDirectory(s, request.BatchId, ct), ct);
    }
    private async Task<AiJobOutcome> RunAsync(AiJobContext context, AiVideoJobRequest request, CancellationToken ct)
    {
        var completed = new List<AiVideoCandidateResult>(); var s = request.Snapshot;
        AiJobRecoveryException? batchFailure = null;
        var failedCandidates = 0;
        var directory = await RunDirectory(s, request.BatchId, ct);
        while (true)
        {
            var current = await context.CurrentAsync(ct);
            var candidate = current.Batch!.Candidates.Skip(completed.Count + failedCandidates).FirstOrDefault();
            if (candidate is null || !context.RecoveringCancelledOutputs && batchFailure is not null && candidate.AppendCommandId is not null)
            {
                if (batchFailure is not null) throw batchFailure;
                return context.RecoveringCancelledOutputs ? AiJobOutcome.CancelledCheckpoint(completed.Count) : AiJobOutcome.BatchCheckpoint(completed.Count);
            }
            var operation = "candidate/" + candidate.Id.ToString("D");
            var total = current.Batch.Candidates[^1].Number;
            if (s.Reel is not null && await ReelStore.PublishedAsync(s, candidate.Id, context.Job.Id, request.BatchId, candidate.Number, ct))
            {
                completed.Add(new(candidate.Id, candidate.Number, context.Job.Id));
                await SaveResultAsync(context, completed); await context.MarkReviewableAsync(ct); continue;
            }
            var document = s.Reel is null ? await shots.LoadAsync(s.ProjectId, ct) : new ShotDocument();
            if (document.TakePublications.SingleOrDefault(r => r.TakeId == candidate.Id) is { } receipt)
            {
                if (receipt.JobId != context.Job.Id || receipt.RunId != request.BatchId || receipt.ShotId != request.OutputShotId || receipt.Candidate != candidate.Number)
                    throw new WorkspaceStoreException("This saved video candidate belongs to another request.");
                var reviewId = await ApplyOutputTrim(request, candidate.Id, ct);
                completed.Add(new(reviewId, candidate.Number, context.Job.Id));
                await SaveResultAsync(context, completed); await context.MarkReviewableAsync(ct); continue;
            }
            var staged = await context.ReadOperationAsync<ShotTake>(operation, AiOperationArtifact.Video, ct);
            var journal = await context.ExecutionAsync(ct);
            var group = ComfyMultiTakeWorkflow.Group(current.Batch, candidate, journal, staged is not null, context.Recovering);
            var remoteOperation = group is null ? operation : ComfyMultiTakeWorkflow.Operation;
            var submitted = journal.Submissions.Any(r => r.Operation == remoteOperation);
            var stagingDirectory = Path.Combine(directory, "candidate-" + candidate.Id.ToString("D"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds((double)s.Settings.TimeoutSeconds * (group?.Count ?? 1)), clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                if (context.Recovering && staged is null && !submitted)
                {
                    if (context.RecoveringCancelledOutputs) { failedCandidates++; continue; }
                    await adapter.RecoverPreparationAsync(context, request, linked.Token);
                    // The coordinator may continue never-submitted candidates in a
                    // fresh execution context after reconciliation. Recovery itself
                    // never prepares a new cache or repeats an accepted video.
                    return AiJobOutcome.BatchCheckpoint(completed.Count);
                }
                if (staged is null)
                {
                    using var http = Client(s.ExecutionComfyUrl);
                    JsonElement? output = null;
                    if (context.RecoveringCancelledOutputs)
                        output = await comfy.ReadStoppedOutputAsync(context, remoteOperation, http, linked.Token);
                    else
                    {
                        Func<string, object>? workflow = null;
                        if (!submitted)
                        {
                            await context.ReportAsync(new(new(GenerationPhase.Preparing, request.Refinement is null ? "Checking captured H3 models and references…" : "Checking saved refinement package and source models…"), candidate.Number, total), true);
                            await ValidateInputsAsync(request, linked.Token); await adapter.ValidateAsync(request, linked.Token);
                            await context.ReportAsync(new(new(GenerationPhase.Preparing, request.Refinement is null ? "Uploading the captured images and voice excerpts…" : "Uploading retained video/audio latents and conditioning…"), candidate.Number, total), true);
                            workflow = await adapter.PrepareQueuedWorkflowAsync(context, request,
                                group ?? new[] { candidate }, directory, linked.Token);
                        }
                        var updates = submitted ? comfy.ObserveAsync(context, remoteOperation, http, ct: linked.Token)
                            : comfy.ExecuteAsync(context, remoteOperation, http, workflow!,
                                group is null ? adapter.Options : ComfyMultiTakeWorkflow.Options(adapter.Options, group), linked.Token);
                        try
                        {
                            await foreach (var update in updates.WithCancellation(linked.Token))
                            {
                                total = (await context.CurrentAsync(linked.Token)).Batch!.Candidates[^1].Number;
                                await context.ReportAsync(new(update.Progress, group is null ? candidate.Number : ComfyMultiTakeWorkflow.ProgressCandidate(update.Progress, group), total));
                                if (update.Complete && update.Job is { } job) output = job;
                            }
                        }
                        catch (AiJobRecoveryException e) when (group is not null && e.Recovery == AiJobRecovery.GenerateAgain)
                        {
                            output = await ComfyMultiTakeWorkflow.FailedOutputAsync(context, remoteOperation, linked.Token);
                            if (output is null) throw;
                            batchFailure = e;
                        }
                    }
                    if (context.RecoveringCancelledOutputs && (output is null ||
                        (group is not null ? !ComfyMultiTakeWorkflow.HasOutputs(output.Value, candidate, RequiredOutputs(request))
                            : !output.Value.TryGetProperty("outputs", out var outputs) || RequiredOutputs(request).Any(n => !outputs.TryGetProperty(n, out _)))))
                    { failedCandidates++; continue; }
                    if (output is null) throw new AiJobRecoveryException("The video output has not been retrieved. Check its saved remote job.", AiJobRecovery.CheckStatus);
                    if (group is not null)
                    {
                        if (batchFailure is not null && !ComfyMultiTakeWorkflow.HasOutputs(output.Value, candidate, RequiredOutputs(request)))
                        { failedCandidates++; continue; }
                        output = ComfyMultiTakeWorkflow.Output(output.Value, candidate);
                    }
                    try
                    {
                        var transferStarted = clock.GetTimestamp();
                        var remote = (await context.ExecutionAsync(linked.Token)).Submissions.Single(r => r.Operation == remoteOperation);
                        var take = new VideoCandidate { TakeId = candidate.Id, Number = candidate.Number, Seed = candidate.Seed,
                            PromptId = remote.PromptId, ClientId = group is null ? remote.ClientId : candidate.Id.ToString("D"), Output = output };
                        staged = await adapter.DownloadAsync(request, take, stagingDirectory, async message =>
                            await context.ReportAsync(new(new(GenerationPhase.Downloading, message), candidate.Number,
                                (await context.CurrentAsync(linked.Token)).Batch!.Candidates[^1].Number)), linked.Token);
                        staged.AiJobId = context.Job.Id;
                        if (staged.ShotId != request.Snapshot.Shot.Id)
                            throw new WorkspaceStoreException("The downloaded take does not match its captured shot.");
                        staged.ShotId = request.OutputShotId;
                        if (request.Refinement is null)
                        {
                            var observed = await context.ReadOperationAsync<ComfyObservedTimings>(remoteOperation, AiOperationArtifact.Timings, linked.Token);
                            double? Seconds(string stage) => group is not null
                                ? ComfyMultiTakeWorkflow.Seconds(observed, candidate, stage, candidate.Id == group[0].Id)
                                : observed?.Seconds.TryGetValue(stage, out var value) == true ? value : null;
                            staged.Timings = new() { PreparationSeconds = Seconds("Preparation"), SamplingSeconds = Seconds("Sampling"),
                                UpscalingSeconds = Seconds("Upscaling"),
                                DecodingSeconds = Seconds("Decoding"), ArchiveSeconds = Seconds("Archive"),
                                TransferSaveSeconds = clock.GetElapsedTime(transferStarted).TotalSeconds, PartialObservation = observed?.Partial ?? true };
                        }
                        ValidateTake(staged, context, request, candidate);
                        await context.SaveOperationAsync(operation, AiOperationArtifact.Video, staged, linked.Token);
                    }
                    catch (Exception e) when ((group is not null || context.RecoveringCancelledOutputs) && e is IOException or HttpRequestException or WorkspaceStoreException or AiGenerationException or JsonException)
                    {
                        // A shared workflow already produced every candidate. Do not let
                        // one damaged candidate strand the remaining finished outputs.
                        failedCandidates++;
                        batchFailure ??= new AiJobRecoveryException("A completed take could not be transferred or archived. Other completed takes remain available; retry download/save without generating again. " + e.Message, AiJobRecovery.RetryOutput, e);
                        continue;
                    }
                    catch (Exception e) when (e is IOException or HttpRequestException or WorkspaceStoreException or AiGenerationException or JsonException)
                    { throw new AiJobRecoveryException("The completed video could not be transferred or archived. Retry download/save without generating again. " + e.Message, AiJobRecovery.RetryOutput, e); }
                }
                ValidateTake(staged, context, request, candidate);
                try
                {
                    await context.ReportAsync(new(new(GenerationPhase.Saving, "Saving the video and lossless frame archive…"), candidate.Number, total), true);
                    if (s.Reel is not null) await ReelStore.PublishAsync(staged, stagingDirectory, linked.Token);
                    else await shots.PublishTakeAsync(s.ProjectId, staged, stagingDirectory, linked.Token);
                    var reviewId = await ApplyOutputTrim(request, candidate.Id, linked.Token);
                    completed.Add(new(reviewId, candidate.Number, context.Job.Id));
                    await SaveResultAsync(context, completed); await context.MarkReviewableAsync(linked.Token);
                    await context.ReportAsync(new(new(GenerationPhase.Saving, $"Take {candidate.Number} saved · ready to review"), candidate.Number, total), true);
                }
                catch (Exception e) when (e is WorkspaceStoreException or IOException)
                { throw new AiJobRecoveryException("The video is staged safely. Retry saving without generating again. " + e.Message, AiJobRecovery.RetryOutput, e); }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
                try { await comfy.CancelAsync(context, Client, cancellation.Token); }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or AiGenerationException or WorkspaceStoreException) { }
                var execution = await context.ExecutionAsync(ct);
                var recovery = execution.MayBeRunning ? AiJobRecovery.CheckStatus : execution.Submissions.Any(r => r.Operation == remoteOperation && r.State == AiRemoteState.Finished)
                    ? AiJobRecovery.RetryOutput : AiJobRecovery.GenerateAgain;
                throw new AiJobRecoveryException("Video generation or transfer timed out. Completed takes remain available.", recovery);
            }
        }
    }
    private async Task<Guid> ApplyOutputTrim(AiVideoJobRequest request, Guid takeId, CancellationToken ct)
    {
        if (request.OutputTrim is not { } trim) return takeId;
        var id = TakeTrimming.OutputId(takeId);
        try {
            await shots.TrimTakeAsync(request.Snapshot.ProjectId, new(takeId, id, trim.StartFrame, trim.EndFrameExclusive), ct: ct);
            return id;
        }
        catch (Exception e) when (e is WorkspaceStoreException or IOException) {
            throw new AiJobRecoveryException("The full refinement is saved. Retry output to reapply the trim without generating again. " + e.Message, AiJobRecovery.RetryOutput, e);
        }
    }
    private static IEnumerable<string> RequiredOutputs(AiVideoJobRequest request)
    {
        yield return "14";
        if (request.Refinement is not null || request.Snapshot.OutputPolicy?.SaveLosslessFrames != false) yield return "15";
        if (request.Refinement is not null || request.Snapshot.CaptureRefinementData) { yield return "21"; yield return "22"; }
    }
    private static void ValidateTake(ShotTake take, AiJobContext context, AiVideoJobRequest request, AiBatchCandidate candidate)
    {
        var s = request.Snapshot;
        var (width, height) = H3PreviewUpscaling.OutputSize(s, request.Refinement);
        if (take.Id != candidate.Id || take.AiJobId != context.Job.Id || take.RunId != request.BatchId || take.ShotId != request.OutputShotId ||
            take.Candidate != candidate.Number || take.Seed != candidate.Seed || (take.Width, take.Height) != (width, height) || take.Refinement != request.Refinement ||
            !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(take.Snapshot, AtomicJsonFile.Options), JsonSerializer.SerializeToElement(s, AtomicJsonFile.Options)))
            throw new WorkspaceStoreException("The archived take does not match the captured candidate.");
        if (s.Reel is null) FileShotStore.Validate(new() { ProjectId = s.ProjectId, Takes = [take] }, s.ProjectId);
    }
    private static async Task SaveResultAsync(AiJobContext context, List<AiVideoCandidateResult> completed)
    {
        try { await context.SaveResultAsync(new AiVideoJobResult(completed.ToArray())); }
        catch (Exception e) when (e is WorkspaceStoreException or IOException)
        { throw new AiJobRecoveryException("The takes are saved but their review metadata could not be published. Retry output without generating again.", AiJobRecovery.RetryOutput, e); }
    }
    private ReelVideoPublication ReelStore => reels ?? throw new WorkspaceStoreException("Reel publication is unavailable.");
    private Task<string> RunDirectory(VideoSnapshot s, Guid batch, CancellationToken ct) => s.Reel is not null
        ? ReelStore.DirectoryAsync(s, batch, ct) : shots.RunDirectoryAsync(s.ProjectId, batch, ct);
    public static AiJobTarget Target(VideoSnapshot s, TakeRefinement? refinement = null) => s.Reel is { } reel
        ? new(s.ProjectId, reel.Recipe.AssetId, ReelId: reel.Recipe.Id)
        : new(s.ProjectId, ShotId: s.Shot.Id, TakeId: refinement?.ParentTakeId, CompositionId: s.Production?.CompositionId);
    public static AiJobTarget Target(AiVideoJobRequest request) => request.DestinationShotId is { } destination
        ? Target(request.Snapshot, request.Refinement) with { ShotId = destination, CompositionId = null }
        : Target(request.Snapshot, request.Refinement);
    private HttpClient Client(string server)
    {
        var http = clients.CreateClient("ComfyUI"); http.BaseAddress = new(AiProviderRegistry.NormalizeComfyUrl(server) + "/");
        http.Timeout = Timeout.InfiniteTimeSpan; return http;
    }
}
