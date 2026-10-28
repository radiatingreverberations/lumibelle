using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiImageJobHandler(IAssetStore assets, IComfyImageJobAdapter adapter, IHttpClientFactory clients,
    ComfyJobExecution comfy, TimeProvider clock, ICodexClient? codex = null) : IAiBatchJobHandler
{
    public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.ImageCreate, AiJobKind.ImageEdit];
    public Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => RunAsync(context, Read(context.Job, snapshot), ct);
    public Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => RunAsync(context, Read(context.Job, snapshot), ct);
    public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => context.Job.Backend == AiBackend.Codex ? Task.FromResult(true) : comfy.CancelAsync(context, Client, ct);
    public async Task ValidateExtensionAsync(AiJobHeader root, JsonElement snapshot, CancellationToken ct)
    {
        var request = Read(root, snapshot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        await ValidateInputsAsync(request, linked.Token);
        if (request.Codex is not null) await CheckCodexAsync(request, linked.Token); else await adapter.ValidateAsync(request, linked.Token);
    }
    public static AiImageJobRequest Read(AiJobHeader job, JsonElement snapshot)
    {
        var request = snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options) ?? throw new AiGenerationException("The saved image request is missing.");
        AiImageJobPolicy.Validate(request);
        if (job.Batch is null || job.Batch.RootId != request.BatchId || job.Backend != (request.Codex is null ? AiBackend.ComfyUI : AiBackend.Codex) ||
            job.Target != new AiJobTarget(request.ProjectId, request.AssetId) || job.Kind != (request.Edit is null ? AiJobKind.ImageCreate : AiJobKind.ImageEdit))
            throw new AiGenerationException("The saved image request belongs to another target or batch.");
        return request;
    }
    private async Task ValidateInputsAsync(AiImageJobRequest request, CancellationToken ct)
    {
        var library = await assets.LoadAsync(request.ProjectId, ct);
        if (!library.Assets.Any(a => a.Id == request.AssetId)) throw new AiGenerationException("The destination asset was removed. Restore it before generating.");
        LookPolicy.ValidateTarget(library, request.Look);
        LookPolicy.ValidateReferences(library, request.Inputs.Select(i => new AssetReferenceLook(i.Reference, i.Context)).ToArray());
        foreach (var reference in request.Inputs)
        {
            await using var media = await assets.OpenImageAsync(request.ProjectId, reference.Reference.AssetId, reference.Reference.ImageId, ct);
            if (media is null) throw new AiGenerationException("A captured reference is missing or in Trash. Restore it before continuing this batch.");
        }
    }
    private async Task<AiJobOutcome> RunAsync(AiJobContext context, AiImageJobRequest request, CancellationToken ct)
    {
        if (request.Codex is not null) return await RunCodexAsync(context, request, ct);
        var completed = new List<AiImageCandidateResult>();
        AiJobRecoveryException? batchFailure = null;
        var failedCandidates = 0;
        while (true)
        {
            var current = await context.CurrentAsync(ct);
            var candidate = current.Batch!.Candidates.Skip(completed.Count + failedCandidates).FirstOrDefault();
            if (candidate is null || batchFailure is not null && candidate.AppendCommandId is not null)
            {
                if (batchFailure is not null) throw batchFailure;
                return AiJobOutcome.BatchCheckpoint(completed.Count);
            }
            var total = current.Batch.Candidates[^1].Number;
            var metadata = AiImageJobPolicy.Metadata(request, context.Job.Id, candidate);
            var operation = "candidate/" + candidate.Id.ToString("D");
            // Publication receipts survive discarding, purging, and owner removal.
            // Never infer completion from the current visible image list.
            var library = await assets.LoadAsync(request.ProjectId, ct);
            if (library.ImagePublications.SingleOrDefault(r => r.ImageId == candidate.Id) is { } receipt)
            {
                if (receipt.JobId != context.Job.Id || receipt.AssetId != request.AssetId) throw new AiGenerationException("This candidate's saved publication belongs to another request.");
                completed.Add(new(candidate.Id, candidate.Number, candidate.Id, metadata));
                await SaveResultAsync(context, completed, ct); await context.MarkReviewableAsync(ct); continue;
            }
            var staged = await context.ReadOperationAsync<AiImageStaging>(operation, AiOperationArtifact.Image, ct);
            var journal = await context.ExecutionAsync(ct);
            var group = ComfyMultiTakeWorkflow.Group(current.Batch, candidate, journal, staged is not null, context.Recovering);
            var remoteOperation = group is null ? operation : ComfyMultiTakeWorkflow.Operation;
            var submitted = journal.Submissions.Any(r => r.Operation == remoteOperation);
            // Reconciliation may retrieve existing output, but cannot issue inference.
            // The coordinator supplies a new execution context for untouched candidates.
            if (context.Recovering && !submitted && staged is null) return AiJobOutcome.BatchCheckpoint(completed.Count);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds((double)request.Settings.ImageTimeoutSeconds * (group?.Count ?? 1)), clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                if (staged is null)
                {
                    using var http = Client(request.Settings.ComfyUrl);
                    if (!submitted)
                    {
                        await context.ReportAsync(new(new(GenerationPhase.Preparing, "Checking the captured models and image references…"), candidate.Number, total), true);
                        await ValidateInputsAsync(request, linked.Token);
                        await adapter.ValidateAsync(request, linked.Token);
                    }
                    JsonElement? output = null;
                    var updates = submitted ? comfy.ObserveAsync(context, remoteOperation, http, ct: linked.Token)
                        : comfy.ExecuteAsync(context, remoteOperation, http, clientId => group is null
                            ? adapter.Build(request, candidate, clientId)
                            : ComfyMultiTakeWorkflow.Build(group, c => adapter.Build(request, c, c.Id.ToString("D")), clientId),
                            group is null ? adapter.Options(request) : ComfyMultiTakeWorkflow.Options(adapter.Options(request), group), linked.Token);
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
                    if (output is null) throw new AiJobRecoveryException("The image output has not been retrieved. Check its saved remote job.", AiJobRecovery.CheckStatus);
                    if (group is not null)
                    {
                        if (batchFailure is not null && !ComfyMultiTakeWorkflow.HasOutputs(output.Value, candidate, [adapter.OutputNode(request)]))
                        { failedCandidates++; continue; }
                        output = ComfyMultiTakeWorkflow.Output(output.Value, candidate);
                    }
                    try
                    {
                        var image = ComfyReferenceImageGenerator.ReadOutput(output.Value, adapter.OutputNode(request));
                        await context.ReportAsync(new(new(GenerationPhase.Downloading, "Downloading the completed candidate…"), candidate.Number, total), true);
                        var bytes = await ComfyReferenceImageGenerator.DownloadAsync(http, image, linked.Token);
                        var imageInfo = ImageInspector.Inspect(bytes); var expected = QwenImage21Policy.OutputSize(request);
                        if ((request.Regional is null || request.Workflow == ImageWorkflow.QwenImage21) && (imageInfo.Width, imageInfo.Height) != expected) throw new AiGenerationException("The output dimensions do not match the captured canvas.");
                        staged = new(candidate.Id, image.FileName, bytes, metadata);
                        await context.SaveOperationAsync(operation, AiOperationArtifact.Image, staged, linked.Token);
                    }
                    catch (Exception e) when (group is not null && e is IOException or HttpRequestException or WorkspaceStoreException or AiGenerationException or JsonException)
                    {
                        // A shared workflow already produced every candidate. Do not let
                        // one damaged candidate strand the remaining finished outputs.
                        failedCandidates++;
                        batchFailure ??= new AiJobRecoveryException("A completed candidate could not be downloaded or staged. Other completed candidates remain available; retry output without generating again. " + e.Message, AiJobRecovery.RetryOutput, e);
                        continue;
                    }
                    catch (Exception e) when (e is IOException or HttpRequestException or WorkspaceStoreException or AiGenerationException or JsonException)
                    { throw new AiJobRecoveryException("The completed candidate could not be downloaded or staged. Retry output without generating again. " + e.Message, AiJobRecovery.RetryOutput, e); }
                }
                if (staged.CandidateId != candidate.Id || staged.Bytes is null || staged.Bytes.Length == 0 || staged.Bytes.LongLength > FileAssetStore.MaximumImageBytes ||
                    !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(staged.Metadata, AtomicJsonFile.Options), JsonSerializer.SerializeToElement(metadata, AtomicJsonFile.Options)))
                    throw new AiJobRecoveryException("The staged candidate does not match its captured inputs. Inspect its saved output before retrying.", AiJobRecovery.RetryOutput);
                if (request.Regional is not null)
                {
                    completed.Add(new(candidate.Id, candidate.Number, candidate.Id, staged.Metadata));
                    await SaveResultAsync(context, completed, linked.Token);
                    await context.MarkReviewableAsync(linked.Token);
                    continue;
                }
                try
                {
                    await context.ReportAsync(new(new(GenerationPhase.Saving, "Saving candidate to Assets…"), candidate.Number, total), true);
                    using var content = new MemoryStream(staged.Bytes, writable: false);
                    await assets.PublishGeneratedImageAsync(request.ProjectId, new(context.Job.Id, candidate.Id, request.AssetId,
                        new(staged.FileName, request.Tags, request.Edit is null ? AssetImageOrigin.Generated : AssetImageOrigin.Edited, metadata, request.Look?.LookId,
                            (request.Create?.PendingReview ?? request.Edit?.PendingReview) == true ? true : null)), content, linked.Token);
                    completed.Add(new(candidate.Id, candidate.Number, candidate.Id, metadata));
                    await SaveResultAsync(context, completed, linked.Token);
                    await context.MarkReviewableAsync(linked.Token);
                    await context.ReportAsync(new(new(GenerationPhase.Saving, $"Take {candidate.Number} saved · ready to review"), candidate.Number, total), true);
                }
                catch (Exception e) when (e is WorkspaceStoreException or IOException)
                { throw new AiJobRecoveryException("The generated candidate is staged safely. Retry saving without generating again. " + e.Message, AiJobRecovery.RetryOutput, e); }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, cancelTimeout.Token);
                try { await comfy.CancelAsync(context, Client, cancel.Token); }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or AiGenerationException or WorkspaceStoreException) { }
                var execution = await context.ExecutionAsync(ct);
                var recovery = execution.MayBeRunning ? AiJobRecovery.CheckStatus : execution.Submissions.Any(s => s.Operation == remoteOperation && s.State == AiRemoteState.Finished)
                    ? AiJobRecovery.RetryOutput : AiJobRecovery.GenerateAgain;
                throw new AiJobRecoveryException("Image generation or transfer timed out. Completed candidates remain available.", recovery);
            }
        }
    }
    private static async Task SaveResultAsync(AiJobContext context, List<AiImageCandidateResult> completed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { await context.SaveResultAsync(new AiImageJobResult(completed.ToArray())); }
        catch (Exception e) when (e is WorkspaceStoreException or IOException)
        { throw new AiJobRecoveryException("The images are saved, but batch review metadata could not be published. Retry output without generating again.", AiJobRecovery.RetryOutput, e); }
    }
    private HttpClient Client(string server)
    {
        var http = clients.CreateClient("ComfyUI"); http.BaseAddress = new(AiProviderRegistry.NormalizeComfyUrl(server) + "/");
        http.Timeout = Timeout.InfiniteTimeSpan; return http;
    }
}
