using lumibelle.Models;
using lumibelle.Services.Assets;

namespace lumibelle.Components.Assets;

public partial class AssetBatchPanel
{
    private bool _showDecided;
    private AssetImage? CandidateImage(AssetBatchItem item, Guid id) => _library?.Assets.SingleOrDefault(a => a.Id == item.Context.AssetId)?.Images.SingleOrDefault(i => i.Id == id)
        ?? CandidateTrash(item, id)?.Image;
    private TrashedImage? CandidateTrash(AssetBatchItem item, Guid id) => _library?.Trash.FirstOrDefault(t => t.Asset.Id == item.Context.AssetId && t.Image.Id == id);
    private List<Guid> PendingCandidates(AssetBatchItem item) => VisibleCandidates(item).Where(id => CandidateImage(item, id) is { PendingReview: true, IsReference: false }).ToList();
    private int CandidateNumber(AssetBatchItem item, Guid id) => _library is null ? 0 : AssetBatchWorkflow.CandidateReferences(_library, item).Select(r => r.ImageId).ToList().IndexOf(id) + 1;
    private List<Guid> ReviewCandidates(AssetBatchItem item) => _showDecided && _library is not null
        ? AssetBatchWorkflow.CandidateReferences(_library, item).Select(r => r.ImageId).Where(id => CandidateImage(item, id) is not null).ToList() : PendingCandidates(item);
    private AssetImageReference[] RemainingCandidates => _run?.Items.SelectMany(i => PendingCandidates(i).Select(id => new AssetImageReference(i.Context.AssetId, id))).ToArray() ?? [];
    private IGrouping<Guid?, TrashedImage>? LastDiscard => _run is null || _library is null ? null : _library.Trash
        .Where(t => t.DiscardOperationId is not null && t.CanRestore(DateTimeOffset.UtcNow) && _run.Items.Any(i => AssetBatchWorkflow.CandidateReferences(_library, i).Contains(new(t.Asset.Id, t.Image.Id))))
        .GroupBy(t => t.DiscardOperationId).OrderByDescending(g => g.Max(t => t.DeletedUtc)).FirstOrDefault();
    private bool DecisionBusy(AssetBatchItem item) => _busy || _run?.PendingItemId == item.Id;
    private Task Keep(Guid item, Guid image) => Act(async () => { await Workflow.KeepAsync(ProjectId, _run!.Id, item, image, _lifetime.Token); });
    private Task Discard(IReadOnlyList<AssetImageReference> candidates) => Act(async () => {
        if (candidates.Count > 0) await Workflow.DiscardAsync(ProjectId, _run!.Id, Guid.NewGuid(), candidates, _lifetime.Token);
    });
    private Task UndoDiscard(Guid operation) => Act(async () => { await Workflow.UndoDiscardAsync(ProjectId, _run!.Id, operation, _lifetime.Token); });
}
