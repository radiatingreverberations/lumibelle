using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed class FileAssetBatchStore(ProjectFiles files)
{
    public event Action<Guid>? Changed;
    // Keep the original filename so saved image batches remain resumable after adding reels.
    private async Task<string> PathAsync(Guid project, CancellationToken ct) => Path.Combine(await files.DirectoryAsync(project, ct), "asset-image-batches.json");
    public async Task<AssetBatches> LoadAsync(Guid project, CancellationToken ct = default)
    {
        var path = await PathAsync(project, ct); using var gate = await ProjectFiles.LockAsync(path, ct);
        return await ReadAsync(path, project, ct);
    }
    public async Task<AssetBatches> UpdateAsync(Guid project, Func<AssetBatches, Task<AssetBatches>> update, CancellationToken ct = default)
    {
        var path = await PathAsync(project, ct); AssetBatches next;
        using (await ProjectFiles.LockAsync(path, ct))
        {
            var current = await ReadAsync(path, project, ct); next = await update(current);
            if (next == current) return current;
            next = next with { Revision = current.Revision + 1 }; Validate(next, project);
            await AtomicJsonFile.WriteAsync(path, next, ct);
        }
        Changed?.Invoke(project); return next;
    }
    public static AssetBatches Replace(AssetBatches doc, AssetBatch run) =>
        doc.Runs.Any(r => r.Id == run.Id && r == run) ? doc : doc with { Runs = doc.Runs.Select(r => r.Id == run.Id ? run : r).ToArray() };
    public static AssetBatch Replace(AssetBatch run, AssetBatchItem item) =>
        run with { Items = run.Items.Select(i => i.Id == item.Id ? item : i).ToArray() };
    private static async Task<AssetBatches> ReadAsync(string path, Guid project, CancellationToken ct)
    {
        var doc = await AtomicJsonFile.ReadAsync<AssetBatches>(path, ct) ?? new() { ProjectId = project };
        Validate(doc, project); return doc;
    }
    internal static void Validate(AssetBatches doc, Guid project)
    {
        if (doc.SchemaVersion != 1 || doc.ProjectId != project || project == Guid.Empty || doc.Revision < 0 || doc.Runs is null ||
            doc.Runs.Any(r => r is null) || doc.Runs.Select(r => r.Id).Distinct().Count() != doc.Runs.Count || doc.Runs.Count(r => r.Active) > 1)
            throw new WorkspaceStoreException("The saved asset batches are invalid.");
        foreach (var run in doc.Runs)
        {
            if (run.Id == Guid.Empty || run.TabId == Guid.Empty || run.Settings is null || !Enum.IsDefined(run.Kind) || !Enum.IsDefined(run.Settings.DefaultImageWorkflow) || !Enum.IsDefined(run.State) ||
                run.CandidateCount is < 1 or > 4 || !ImageAspectPolicy.IsSupported(run.AspectRatio, false) || !ImageAspectPolicy.IsResolutionSupported(run.Resolution) ||
                run.Items is not { Count: > 0 and <= 200 } || run.Items.Any(i => i?.Context is null) || run.Items.DistinctBy(i => i.Id).Count() != run.Items.Count ||
                run.Items.DistinctBy(i => (i.Context.AssetId, i.Context.LookId)).Count() != run.Items.Count ||
                (run.Pending is null) != (run.PendingItemId is null) || run.PendingItemId is { } itemId && !run.Items.Any(i => i.Id == itemId) ||
                run.Kind != AssetBatchKind.Images && run.PromptModel is not null ||
                run.State == AssetBatchState.Completed && run.Items.Any(i => i.State is AssetBatchItemState.Pending or AssetBatchItemState.Generating or AssetBatchItemState.Evaluating or AssetBatchItemState.Enhancing))
                throw new WorkspaceStoreException("The saved asset batch is invalid.");
            foreach (var item in run.Items)
            {
                if (item.PromptAttempts is < 0 or > 2 || item.PromptJobs is null || item.PromptJobs.Count != item.PromptAttempts ||
                    item.PromptJobs.Contains(Guid.Empty) || item.PromptJobs.Distinct().Count() != item.PromptJobs.Count ||
                    item.EnhancedPrompt is not null && (string.IsNullOrWhiteSpace(item.EnhancedPrompt) || item.EnhancedPrompt.Length > 30000 || item.PromptJobs.Count == 0) ||
                    run.PromptModel is null && (item.PromptJobs.Count > 0 || item.EnhancedPrompt is not null || item.State == AssetBatchItemState.Enhancing))
                    throw new WorkspaceStoreException("The saved prompt enhancement checkpoint is invalid.");
                VisualStyles.Validate(item.VisualStyle);
                if (item.AdditionalInstructions is null || item.AdditionalInstructions.Length > AssetBatchPlan.MaximumAdditionalInstructions ||
                    run.Kind == AssetBatchKind.Images && AssetBatchPlan.ImagePrompt(item).Length > 30000)
                    throw new WorkspaceStoreException("The saved asset framing or additional instructions are invalid.");
                if (item.Id == Guid.Empty || item.Context is null || item.Context.AssetId == Guid.Empty || item.Context.LookId == Guid.Empty ||
                    !Enum.IsDefined(item.Category) || !Enum.IsDefined(item.State) || string.IsNullOrWhiteSpace(item.Prompt) || item.Prompt.Length > 30000 ||
                    item.Attempts is < 0 or > 2 || item.ReviewAttempts is < 0 or > 2 || item.GenerationJobs is null || item.Candidates is null || item.References is null || item.Loras is null ||
                    item.GenerationJobs.Contains(Guid.Empty) || item.GenerationJobs.Distinct().Count() != item.GenerationJobs.Count || item.ReviewJobs is null || item.ReviewJobId == Guid.Empty ||
                    item.ReviewJobs.Contains(Guid.Empty) || item.ReviewJobs.Distinct().Count() != item.ReviewJobs.Count ||
                    item.Candidates.Contains(Guid.Empty) || item.Candidates.Distinct().Count() != item.Candidates.Count ||
                    item.ReelCandidates is null || item.ReelReviews is null || item.ReviewedReels is null ||
                    item.ReelCandidates.Contains(Guid.Empty) || item.ReelCandidates.Distinct().Count() != item.ReelCandidates.Count ||
                    item.ReviewedReels.Distinct().Count() != item.ReviewedReels.Count || item.ReviewedReels.Any(id => !item.ReelCandidates.Contains(id)) ||
                    item.ApprovedImageId is { } approved && !item.Candidates.Contains(approved))
                    throw new WorkspaceStoreException("A saved asset batch item is invalid.");
                if (run.Kind == AssetBatchKind.Images && (item.Recipe is not null || item.ExistingReelId is not null || item.ReelCandidates.Count != 0 || item.ReelReviews.Count != 0 || item.ReviewedReels.Count != 0))
                    throw new WorkspaceStoreException("An image batch cannot contain reel recipes or reviews.");
                if (run.Kind != AssetBatchKind.Images)
                {
                    if (run.CandidateCount != 1 || item.Recipe is null || item.Recipe.AssetId != item.Context.AssetId || item.Recipe.LookId != item.Context.LookId ||
                        item.Candidates.Count != 0 || item.Category != (run.Kind == AssetBatchKind.CharacterReels ? AssetCategory.Character : AssetCategory.Environment) ||
                        ReferenceReels.OwnerCategory(item.Recipe) != item.Category || item.Recipe.VoiceMode != ReelVoiceMode.Silent)
                        throw new WorkspaceStoreException("The saved reel recipe has the wrong asset, look or output type.");
                    foreach (var review in item.ReelReviews) {
                        if (review is null || review.Result is null || review.Request?.Views is null || !item.ReelCandidates.Contains(review.Request.Views.ReelId) || review.Request.BatchId != run.Id || review.Request.ItemId != item.Id)
                            throw new WorkspaceStoreException("The saved reel review belongs to another batch.");
                        AssetReelEvaluator.Validate(review.Request);
                    }
                }
            }
            if (run.Pending is { } pending && (pending.Target is null || pending.Target.ProjectId != project || pending.Target.AssetId != run.Items.Single(i => i.Id == run.PendingItemId).Context.AssetId ||
                (run.Kind == AssetBatchKind.Images ? pending.Kind is not (AiJobKind.ImageCreate or AiJobKind.ImageEdit or AiJobKind.AssetImageEvaluation or AiJobKind.AssetPromptEnhancement) : pending.Kind is not (AiJobKind.ReelVideo or AiJobKind.AssetReelEvaluation))))
                throw new WorkspaceStoreException("The pending asset request belongs to another batch item.");
            if (run.Pending is { } captured)
            {
                var item = run.Items.Single(i => i.Id == run.PendingItemId);
                if (captured.Id == Guid.Empty || captured.OriginTabId != run.TabId || captured.Snapshot.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    (captured.Kind == AiJobKind.AssetPromptEnhancement ? item.State != AssetBatchItemState.Enhancing || item.PromptJobs.LastOrDefault() != captured.Id :
                    captured.Kind is AiJobKind.AssetImageEvaluation or AiJobKind.AssetReelEvaluation ? item.ReviewJobId != captured.Id : !item.GenerationJobs.Contains(captured.Id)))
                    throw new WorkspaceStoreException("The saved asset request does not match its checkpoint.");
                captured.Target.Validate(captured.Kind);
            }
        }
    }
}
