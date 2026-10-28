using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed partial class AssetBatchWorkflow
{
    private static AssetBatch Find(AssetBatches doc, Guid id) => doc.Runs.SingleOrDefault(r => r.Id == id)
        ?? throw new WorkspaceStoreException("This asset batch is unavailable.");
    public Task<AssetBatches> PauseAsync(Guid project, Guid batch, CancellationToken ct = default) => store.UpdateAsync(project, doc =>
    {
        var run = Find(doc, batch);
        return Task.FromResult(run.State == AssetBatchState.Running ? FileAssetBatchStore.Replace(doc, run with { State = AssetBatchState.Paused }) : doc);
    }, ct);
    public Task<AssetBatches> ResumeAsync(Guid project, Guid batch, CancellationToken ct = default) => store.UpdateAsync(project, doc =>
    {
        var run = Find(doc, batch);
        return Task.FromResult(run.State == AssetBatchState.Paused ? FileAssetBatchStore.Replace(doc, run with { State = AssetBatchState.Running, Error = null }) : doc);
    }, ct);
    public async Task<AssetBatches> StopAsync(Guid project, Guid batch, CancellationToken ct = default)
    {
        Guid? pendingId = null;
        var result = await store.UpdateAsync(project, async doc =>
        {
            var run = Find(doc, batch); pendingId = run.Pending?.Id;
            if (run.Pending is { } pending && !(await jobs.ReadAsync(ct)).Jobs.Any(j => j.Id == pending.Id))
            {
                var item = run.Items.Single(i => i.Id == run.PendingItemId);
                run = FileAssetBatchStore.Replace(run, item with { State = item.OutputCount > 0 ? AssetBatchItemState.Ready : AssetBatchItemState.Skipped })
                    with { Pending = null, PendingItemId = null };
            }
            run = run with { Items = run.Items.Select(i => i.State == AssetBatchItemState.Pending ? i with { State = i.OutputCount > 0 ? AssetBatchItemState.Ready : AssetBatchItemState.Skipped } : i).ToArray() };
            return FileAssetBatchStore.Replace(doc, run with { State = AssetBatchState.Stopped });
        }, ct);
        if (pendingId is { } id && (await jobs.ReadAsync(ct)).Jobs.Any(j => j.Id == id && j.State is AiJobState.Waiting or AiJobState.Running)) await queue.CancelAsync(id, ct);
        return result;
    }
    public Task<AssetBatches> SkipAsync(Guid project, Guid batch, Guid itemId, CancellationToken ct = default) => store.UpdateAsync(project, doc =>
    {
        var run = Find(doc, batch);
        if (run.PendingItemId == itemId) throw new WorkspaceStoreException("Wait for this request to finish, or stop the batch.");
        var item = run.Items.Single(i => i.Id == itemId);
        return Task.FromResult(FileAssetBatchStore.Replace(doc, FileAssetBatchStore.Replace(run, item with { State = AssetBatchItemState.Skipped })));
    }, ct);
    public Task<AssetBatches> RetryAsync(Guid project, Guid batch, Guid itemId, CancellationToken ct = default) => store.UpdateAsync(project, async doc =>
    {
        var run = Find(doc, batch);
        if (run.Pending is not null || doc.Runs.Any(r => r.Id != batch && (r.Active || r.Pending is not null)))
            throw new WorkspaceStoreException("Wait for the current request or batch to finish before retrying.");
        var item = run.Items.Single(i => i.Id == itemId);
        if (item.State != AssetBatchItemState.NeedsAttention) throw new WorkspaceStoreException("This item no longer needs a retry.");
        var last = (await jobs.ReadAsync(ct)).Jobs.LastOrDefault(j => item.GenerationJobs.Contains(j.Id));
        if (last is not null && (last.Recovery is AiJobRecovery.RetryOutput or AiJobRecovery.CheckStatus || last.State is AiJobState.Waiting or AiJobState.Running))
        {
            if (last.State is not (AiJobState.Waiting or AiJobState.Running)) await queue.ResumeAsync(last.Id, ct);
            var snapshot = await jobs.ReadSnapshotAsync(last.Id, ct);
            var pending = new AiJobSubmission(last.Id, last.Kind, last.Backend, last.Target, last.ProjectName, last.TargetName, run.TabId, snapshot, last.Batch);
            run = FileAssetBatchStore.Replace(run, item with { State = AssetBatchItemState.Generating, Error = null })
                with { Pending = pending, PendingItemId = item.Id, State = AssetBatchState.Running, Error = null };
        }
        else
        {
            if (run.PromptModel is not null && item.EnhancedPrompt is null ? item.PromptAttempts >= 2 : item.OutputCount == 0 ? item.Attempts >= 2 : item.ReviewAttempts >= 2)
                throw new WorkspaceStoreException("This item reached its retry limit. Inspect the saved responses and images.");
            item = item with { State = AssetBatchItemState.Pending, Error = null };
            run = FileAssetBatchStore.Replace(run, item) with { State = AssetBatchState.Running, Error = null };
        }
        return FileAssetBatchStore.Replace(doc, run);
    }, ct);

    public Task<AssetBatches> GenerateAgainAsync(Guid project, Guid batch, Guid itemId, CancellationToken ct = default) => store.UpdateAsync(project, doc =>
    {
        var run = Find(doc, batch); var item = run.Items.Single(i => i.Id == itemId);
        if (run.Pending is not null || doc.Runs.Any(r => r.Id != batch && (r.Active || r.Pending is not null)) || item.State != AssetBatchItemState.Ready || item.Attempts >= 2)
            throw new WorkspaceStoreException("Wait for the batch to finish. Each item allows one additional generation attempt.");
        item = item with { State = AssetBatchItemState.Pending, GenerateAgain = true, Evaluation = null, Error = null };
        return Task.FromResult(FileAssetBatchStore.Replace(doc, FileAssetBatchStore.Replace(run, item) with { State = AssetBatchState.Running, Error = null }));
    }, ct);

    public Task<AssetBatches> ApproveAsync(Guid project, Guid batch, Guid itemId, Guid imageId, CancellationToken ct = default) => store.UpdateAsync(project, async doc =>
    {
        var run = Find(doc, batch); var item = run.Items.Single(i => i.Id == itemId);
        var library = await assets.LoadAsync(project, ct); LookPolicy.ValidateTarget(library, item.Context, requireCurrent: true);
        if (!item.Candidates.Contains(imageId))
        {
            if (!library.ImagePublications.Any(p => item.GenerationJobs.Contains(p.JobId) && p.AssetId == item.Context.AssetId && p.ImageId == imageId))
                throw new WorkspaceStoreException("Choose a saved candidate from this item.");
            item = item with { Candidates = [.. item.Candidates, imageId] };
        }
        var asset = library.Assets.Single(a => a.Id == item.Context.AssetId);
        var image = asset.Images.SingleOrDefault(i => i.Id == imageId && i.LookId == item.Context.LookId)
            ?? throw new WorkspaceStoreException("The candidate was moved or discarded. Restore it before approval.");
        if (!image.IsReference || image.PendingReview == true)
        {
            var next = library with { Assets = library.Assets.Select(a => a.Id != asset.Id ? a : a with
                { Images = a.Images.Select(i => i.Id == imageId ? i with { IsReference = true, PendingReview = false } : i).ToList() }).ToList() };
            await assets.SaveAsync(next, library.Revision, ct);
        }
        return FileAssetBatchStore.Replace(doc, FileAssetBatchStore.Replace(run, item with { ApprovedImageId = imageId }));
    }, ct);
}

public sealed class AssetBatchWorker(IProjectStore projects, AssetBatchWorkflow workflow, ILogger<AssetBatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (var project in (await projects.ListAsync(stoppingToken)).Projects)
                {
                    try { await workflow.AdvanceAsync(project.Id, stoppingToken); }
                    catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or IOException)
                    { logger.LogWarning(e, "Asset batch checkpoint unavailable for {Project}", project.Id); }
                }
            }
            catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or IOException)
            { logger.LogWarning(e, "Asset batch discovery unavailable"); }
        }
    }
}
