using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed partial class FileAssetStore
{
    // Older batches have no explicit decision flag. Migrate only unknown decisions;
    // false is an author's saved Keep choice and must never become pending again.
    public async Task<AssetLibrary> MarkImagesPendingReviewAsync(Guid projectId, IReadOnlyCollection<AssetImageReference> images,
        long expectedRevision, CancellationToken ct = default)
    {
        var targets = images.ToHashSet();
        var directory = await files.DirectoryAsync(projectId, ct);
        using var gate = await ProjectFiles.LockAsync(directory, ct);
        var current = await ReadAsync(directory, projectId, ct); EnsureRevision(current, expectedRevision);
        bool Unknown(Guid asset, AssetImage image) => targets.Contains(new(asset, image.Id)) && image.PendingReview is null && !image.IsReference && !image.IsCover && image.Origin != AssetImageOrigin.Imported;
        if (!current.Assets.Any(a => a.Images.Any(i => Unknown(a.Id, i))) && !current.Trash.Any(t => Unknown(t.Asset.Id, t.Image))) return current;
        return await PublishAsync(directory, current with {
            Assets = current.Assets.Select(a => a with { Images = a.Images.Select(i => Unknown(a.Id, i) ? i with { PendingReview = true } : i).ToList() }).ToList(),
            Trash = current.Trash.Select(t => Unknown(t.Asset.Id, t.Image) ? t with { Image = t.Image with { PendingReview = true } } : t).ToList()
        }, current.Revision, ct);
    }

    public async Task<ImageTrashResult> DiscardPendingImagesAsync(Guid projectId, Guid operationId, IReadOnlyCollection<AssetImageReference> images,
        long expectedRevision, CancellationToken ct = default)
    {
        var targets = images.ToHashSet();
        if (operationId == Guid.Empty || targets.Count == 0 || targets.Count != images.Count || targets.Any(r => r.AssetId == Guid.Empty || r.ImageId == Guid.Empty))
            throw new WorkspaceStoreException("Choose distinct pending candidates to discard.");
        var directory = await files.DirectoryAsync(projectId, ct);
        using var gate = await ProjectFiles.LockAsync(directory, ct);
        var current = await ReadAsync(directory, projectId, ct); EnsureRevision(current, expectedRevision);
        var previous = current.Trash.Where(t => t.DiscardOperationId == operationId).ToArray();
        if (previous.Length > 0) {
            if (!targets.SetEquals(previous.Select(t => new AssetImageReference(t.Asset.Id, t.Image.Id)))) throw new WorkspaceConflictException();
            return new(current, previous.Select(t => t.Id).ToArray());
        }
        var selected = current.Assets.SelectMany(a => a.Images.Where(i => targets.Contains(new(a.Id, i.Id))).Select(i => (Asset: a, Image: i))).ToArray();
        if (selected.Length != targets.Count || selected.Any(x => x.Image.PendingReview != true || x.Image.IsReference || x.Image.IsCover || x.Image.Origin == AssetImageOrigin.Imported))
            throw new WorkspaceStoreException("A candidate was already kept, approved, moved or discarded. Refresh the review before trying again.");
        var trash = selected.Select(x => TrashEntry(current, x.Asset, x.Image) with { DiscardOperationId = operationId }).ToArray();
        var next = current with {
            Assets = current.Assets.Select(a => a with { Images = a.Images.Where(i => !targets.Contains(new(a.Id, i.Id))).ToList(),
                UpdatedUtc = selected.Any(x => x.Asset.Id == a.Id) ? clock.GetUtcNow() : a.UpdatedUtc }).ToList(),
            Trash = [.. current.Trash, .. trash]
        };
        return new(await PublishAsync(directory, next, current.Revision, ct), trash.Select(t => t.Id).ToArray());
    }
}
