using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed partial class AssetBatchWorkflow
{
    public async Task<AssetBatches> LoadAsync(Guid project, CancellationToken ct = default)
    {
        var doc = await store.LoadAsync(project, ct);
        if (!doc.Runs.Any(r => r.Kind == AssetBatchKind.Images)) return doc;
        var library = await assets.LoadAsync(project, ct);
        var candidates = doc.Runs.Where(r => r.Kind == AssetBatchKind.Images).SelectMany(r => r.Items.Where(i => i.State != AssetBatchItemState.Skipped))
            .SelectMany(i => CandidateReferences(library, i)).Distinct().ToArray();
        var unknown = candidates.Where(r => library.Assets.Any(a => a.Id == r.AssetId && a.Images.Any(i => i.Id == r.ImageId && i.PendingReview is null && !i.IsReference)) ||
            library.Trash.Any(t => t.Asset.Id == r.AssetId && t.Image.Id == r.ImageId && t.Image.PendingReview is null && !t.Image.IsReference)).ToArray();
        if (unknown.Length > 0) await assets.MarkImagesPendingReviewAsync(project, unknown, library.Revision, ct);
        return doc;
    }
    public static IEnumerable<AssetImageReference> CandidateReferences(AssetLibrary library, AssetBatchItem item) => item.Candidates
        .Concat(library.ImagePublications.Where(p => item.GenerationJobs.Contains(p.JobId) && p.AssetId == item.Context.AssetId).Select(p => p.ImageId))
        .Distinct().Select(id => new AssetImageReference(item.Context.AssetId, id));
    private static AssetBatchItem CandidateItem(AssetBatch run, AssetLibrary library, AssetImageReference reference)
    {
        if (run.Kind != AssetBatchKind.Images) throw new WorkspaceStoreException("Choose an image batch.");
        var item = run.Items.SingleOrDefault(i => CandidateReferences(library, i).Contains(reference)) ?? throw new WorkspaceStoreException("Choose a candidate from this batch.");
        return item;
    }
    public async Task<AssetLibrary> KeepAsync(Guid project, Guid batch, Guid itemId, Guid imageId, CancellationToken ct = default)
    {
        var run = Find(await store.LoadAsync(project, ct), batch); var library = await assets.LoadAsync(project, ct);
        var item = CandidateItem(run, library, new(run.Items.Single(i => i.Id == itemId).Context.AssetId, imageId));
        var asset = library.Assets.Single(a => a.Id == item.Context.AssetId);
        var image = asset.Images.SingleOrDefault(i => i.Id == imageId && i.LookId == item.Context.LookId) ?? throw new WorkspaceStoreException("Restore this candidate before keeping it.");
        if (image.PendingReview == false) return library;
        return await assets.SaveAsync(library with { Assets = library.Assets.Select(a => a.Id == asset.Id ? a with {
            Images = a.Images.Select(i => i.Id == imageId ? i with { PendingReview = false } : i).ToList() } : a).ToList() }, library.Revision, ct);
    }
    public async Task<ImageTrashResult> DiscardAsync(Guid project, Guid batch, Guid operationId, IReadOnlyList<AssetImageReference> captured, CancellationToken ct = default)
    {
        var run = Find(await store.LoadAsync(project, ct), batch); var library = await assets.LoadAsync(project, ct);
        foreach (var reference in captured) {
            var item = CandidateItem(run, library, reference);
            if (run.PendingItemId == item.Id) throw new WorkspaceStoreException("Wait for this item's generation and evaluation to finish before discarding candidates.");
        }
        return await assets.DiscardPendingImagesAsync(project, operationId, captured, library.Revision, ct);
    }
    public async Task<AssetLibrary> UndoDiscardAsync(Guid project, Guid batch, Guid operationId, CancellationToken ct = default)
    {
        if (operationId == Guid.Empty) throw new WorkspaceStoreException("Choose a saved discard operation.");
        var run = Find(await store.LoadAsync(project, ct), batch); var library = await assets.LoadAsync(project, ct);
        var entries = library.Trash.Where(t => t.DiscardOperationId == operationId).ToArray();
        if (entries.Length == 0) return library;
        foreach (var entry in entries) CandidateItem(run, library, new(entry.Asset.Id, entry.Image.Id));
        return await (assets as IImageTrashStore ?? throw new WorkspaceStoreException("Image restoration is unavailable."))
            .RestoreImagesAsync(project, entries.Select(t => t.Id).ToArray(), library.Revision, ct);
    }
}
