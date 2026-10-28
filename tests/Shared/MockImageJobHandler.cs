using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Testing;

// Test-only bridge: exercise the durable queue and real publication with the
// deterministic image providers already used by component/browser fixtures.
public sealed class MockImageJobHandler(IAssetStore assets, IReferenceImageGenerator generator, IReferenceImageEditor editor) : IAiBatchJobHandler
{
    public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.ImageCreate, AiJobKind.ImageEdit];
    private static AiImageJobRequest Read(JsonElement snapshot) => snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
    public async Task ValidateExtensionAsync(AiJobHeader root, JsonElement snapshot, CancellationToken ct)
    {
        var request = Read(snapshot); var library = await assets.LoadAsync(request.ProjectId, ct);
        LookPolicy.ValidateTarget(library, request.Look!, requireCurrent: false);
        if (request.Edit is { } edit) LookPolicy.ValidateReferences(library, edit.ReferenceLooks);
        foreach (var input in request.Inputs)
            if (library.Assets.FirstOrDefault(a => a.Id == input.Reference.AssetId)?.Images.Any(i => i.Id == input.Reference.ImageId) != true)
                throw new AiGenerationException("A source or reference is missing or in Trash. Restore it before adding another take.");
    }
    public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => Task.FromResult(true);
    public async Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct)
    {
        var request = Read(snapshot); var library = await assets.LoadAsync(request.ProjectId, ct);
        return AiJobOutcome.BatchCheckpoint(context.Job.Batch!.Candidates.Count(c => library.ImagePublications.Any(p => p.ImageId == c.Id && p.JobId == context.Job.Id)));
    }
    public async Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct)
    {
        var request = Read(snapshot); var results = new List<AiImageCandidateResult>();
        while (true)
        {
            var job = await context.CurrentAsync(ct); var library = await assets.LoadAsync(request.ProjectId, ct);
            var pending = job.Batch!.Candidates.Where(c => !library.ImagePublications.Any(p => p.ImageId == c.Id && p.JobId == job.Id) && !results.Any(r => r.CandidateId == c.Id)).ToArray();
            if (pending.Length == 0) return AiJobOutcome.BatchCheckpoint(job.Batch.Candidates.Count);
            await ValidateExtensionAsync(job, snapshot, ct);
            // Create fixtures yield one result. Edit fixtures retain the initial
            // segment so partial-failure/cancellation controls remain meaningful.
            var initialCount = request.Create?.Count ?? request.Edit!.Count;
            var segment = request.Edit is null || pending[0].Number > initialCount ? pending.Take(1).ToArray() : pending;
            var streams = request.Inputs.Select(i => new MemoryStream(i.Png)).ToArray();
            try
            {
                var updates = request.Create is { } create
                    ? generator.GenerateAsync(create with { Count = segment.Length, Seed = segment[0].Seed }, ct)
                    : editor.EditAsync(request.Edit! with { Count = segment.Length, Seed = segment[0].Seed, SettingsSnapshot = request.Settings },
                        request.Inputs.Select((i, n) => new ReferenceImageSource(i.Reference.AssetId, i.Reference.ImageId, streams[n])).ToArray(), ct);
                var completed = 0;
                await foreach (var update in updates.WithCancellation(ct))
                {
                    var candidate = segment[update.Candidate - 1];
                    await context.ReportAsync(new(update.Progress ?? new(GenerationPhase.Generating, update.Status), candidate.Number, job.Batch.Candidates[^1].Number));
                    if (update.Image is null || update.Metadata is null) continue;
                    var metadata = update.Metadata with { AiJobId = job.Id, BatchId = request.BatchId, CandidateNumber = candidate.Number, Seed = candidate.Seed,
                        Look = request.Look, Loras = request.AppliedLoras.ToArray(), VisualStyle = request.Create?.VisualStyle ?? request.Edit?.VisualStyle, AuthorPrompt = request.OriginalPrompt,
                        Edit = update.Metadata.Edit is { } edit ? edit with { ReferenceLooks = request.Edit!.ReferenceLooks.ToArray() } : null };
                    await using var content = new MemoryStream(update.Image);
                    if (request.Regional is not null)
                        await context.SaveOperationAsync("candidate/" + candidate.Id.ToString("D"), AiOperationArtifact.Image, new AiImageStaging(candidate.Id, "regional.png", update.Image, metadata), ct);
                    else await assets.PublishGeneratedImageAsync(request.ProjectId, new(job.Id, candidate.Id, request.AssetId,
                        new(update.FileName ?? "candidate.png", request.Tags, request.Edit is null ? AssetImageOrigin.Generated : AssetImageOrigin.Edited, metadata, request.Look?.LookId,
                            (request.Create?.PendingReview ?? request.Edit?.PendingReview) == true ? true : null)), content, ct);
                    results.Add(new(candidate.Id, candidate.Number, candidate.Id, metadata));
                    await context.SaveResultAsync(new AiImageJobResult(results.ToArray()));
                    await context.MarkReviewableAsync(ct); completed++;
                }
                if (completed != segment.Length) return AiJobOutcome.Attention("The provider ended before returning all requested takes.", AiJobRecovery.GenerateAgain, completed > 0);
            }
            finally { foreach (var stream in streams) stream.Dispose(); }
        }
    }
}
