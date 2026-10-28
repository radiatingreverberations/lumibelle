using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    [Fact]
    public async Task CandidateDiscardAcrossAssetsIsAtomicAndCannotIncludeApprovedImages()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, first, store, library) = await TrashFixture(2);
        var second = Asset("Lamp");
        library = await store.SaveAsync(library with { Assets = [.. library.Assets, second] }, library.Revision, ct);
        using var png = new MemoryStream(Png(8, 8));
        library = await store.AddImageAsync(project, second.Id, png, new("candidate.png", [], AssetImageOrigin.Generated, new(), PendingReview: true), library.Revision, ct);
        library = await store.SaveAsync(library with { Assets = library.Assets.Select(a => a.Id == first ? a with { Images = a.Images.Select((i, n) => i with { PendingReview = n > 0, IsReference = n == 0 }).ToList() } : a).ToList() }, library.Revision, ct);
        var firstCandidate = new AssetImageReference(first, library.Assets[0].Images[1].Id);
        var secondCandidate = new AssetImageReference(second.Id, library.Assets[1].Images[0].Id);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.DiscardPendingImagesAsync(project, Guid.NewGuid(), [new(first, library.Assets[0].Images[0].Id), secondCandidate], library.Revision, ct));
        Assert.Empty((await store.LoadAsync(project, ct)).Trash);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.DiscardPendingImagesAsync(project, Guid.NewGuid(), [firstCandidate, secondCandidate], library.Revision - 1, ct));
        var discarded = await store.DiscardPendingImagesAsync(project, Guid.NewGuid(), [firstCandidate, secondCandidate], library.Revision, ct);
        Assert.Equal(2, discarded.Library.Assets.Count); Assert.Single(discarded.Library.Assets[0].Images); Assert.Empty(discarded.Library.Assets[1].Images);
        Assert.Equal(2, discarded.Library.Trash.Select(t => t.Asset.Id).Distinct().Count());
        var restored = await store.RestoreImagesAsync(project, discarded.TrashIds, discarded.Library.Revision, ct);
        Assert.Equal(2, restored.Assets[0].Images.Count); Assert.Single(restored.Assets[1].Images);
    }
    private static async Task<AssetBatch> FinishReviewBatch(ImageJobFixture f, AssetBatchWorkflow workflow, int count)
    {
        var ct = TestContext.Current.CancellationToken;
        var item = AssetBatchPlan.Build(await f.Assets.LoadAsync(f.Project.Id, ct), ImageWorkflow.Krea2)[0].Item;
        var doc = await workflow.StartAsync(f.Project.Id, Guid.NewGuid(), ImageWorkflow.Krea2, [item], count, "1:1", 1024, null, ct);
        var pending = doc.Runs[0].Pending!; var context = await f.Claim(pending);
        await f.Worker.ExecuteAsync(context, pending.Snapshot, ct);
        await f.Jobs.UpdateAsync(pending.Id, j => j with { State = AiJobState.Completed, LeaseId = null, FinishedUtc = DateTimeOffset.UtcNow }, ct);
        await workflow.AdvanceAsync(f.Project.Id, ct); doc = await workflow.AdvanceAsync(f.Project.Id, ct);
        return doc.Runs[0];
    }
    [Fact]
    public async Task CandidateDecisionsPersistAndBulkDiscardProtectsKeptImagesAndUndoesExactlyItsOwnCandidates()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var workflow = BulkWorkflow(f, out var queue); using var owned = queue;
        var batch = await FinishReviewBatch(f, workflow, 4); var item = batch.Items[0];
        var original = await f.Assets.LoadAsync(f.Project.Id, ct);
        Assert.All(original.Assets[0].Images.Where(i => item.Candidates.Contains(i.Id)), i => Assert.True(i.PendingReview));
        await workflow.KeepAsync(f.Project.Id, batch.Id, item.Id, item.Candidates[0], ct);
        await workflow.ApproveAsync(f.Project.Id, batch.Id, item.Id, item.Candidates[1], ct);
        var references = item.Candidates.Select(id => new AssetImageReference(item.Context.AssetId, id)).ToArray();
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => workflow.DiscardAsync(f.Project.Id, batch.Id, Guid.NewGuid(), references, ct));
        Assert.Empty((await f.Assets.LoadAsync(f.Project.Id, ct)).Trash);
        var operation = Guid.NewGuid();
        var discarded = await workflow.DiscardAsync(f.Project.Id, batch.Id, operation, references[2..], ct);
        Assert.Equal(2, discarded.TrashIds.Count); Assert.Equal(original.Assets[0].Images.Count - 2, discarded.Library.Assets[0].Images.Count);
        Assert.All(discarded.Library.Trash, t => Assert.Equal(operation, t.DiscardOperationId));
        var kept = discarded.Library.Assets[0].Images.Single(i => i.Id == item.Candidates[0]);
        var approved = discarded.Library.Assets[0].Images.Single(i => i.Id == item.Candidates[1]);
        Assert.False(kept.IsReference); Assert.False(kept.PendingReview);
        Assert.True(approved.IsReference); Assert.False(approved.PendingReview);
        var repeated = await workflow.DiscardAsync(f.Project.Id, batch.Id, operation, references[2..], ct);
        Assert.Equal(discarded.Library.Revision, repeated.Library.Revision);
        var restored = await workflow.UndoDiscardAsync(f.Project.Id, batch.Id, operation, ct);
        Assert.Empty(restored.Trash); Assert.Equal(original.Assets[0].Images.Count, restored.Assets[0].Images.Count);
        Assert.Equal(2, restored.Assets[0].Images.Count(i => i.PendingReview == true));
        Assert.Equal(original.Assets[0].Description, restored.Assets[0].Description);
        foreach (var image in original.Assets[0].Images) Assert.Equal(JsonSerializer.Serialize(image.Generation), JsonSerializer.Serialize(restored.Assets[0].Images.Single(i => i.Id == image.Id).Generation));
        var reopened = await workflow.LoadAsync(f.Project.Id, ct);
        Assert.Equal(item.Candidates[1], reopened.Runs[0].Items[0].ApprovedImageId);
        Assert.Equal(restored.Revision, (await f.Assets.LoadAsync(f.Project.Id, ct)).Revision);
    }

    [Fact]
    public async Task OlderBatchCandidatesBecomePendingWithoutChangingExplicitKeepDecisionsOrOriginalReceipts()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var workflow = BulkWorkflow(f, out var queue); using var owned = queue;
        var batch = await FinishReviewBatch(f, workflow, 3); var item = batch.Items[0];
        await workflow.KeepAsync(f.Project.Id, batch.Id, item.Id, item.Candidates[0], ct);
        var library = await f.Assets.LoadAsync(f.Project.Id, ct);
        library = await f.Assets.SaveAsync(library with { Assets = library.Assets.Select(a => a with { Images = a.Images.Select(i => i.Id == item.Candidates[0] ? i : i with { PendingReview = null }).ToList() }).ToList() }, library.Revision, ct);
        await workflow.LoadAsync(f.Project.Id, ct);
        var migrated = await f.Assets.LoadAsync(f.Project.Id, ct);
        Assert.False(migrated.Assets[0].Images.Single(i => i.Id == item.Candidates[0]).PendingReview); Assert.Equal(2, migrated.Assets[0].Images.Count(i => i.PendingReview == true));
        Assert.Equal(library.ImagePublications, migrated.ImagePublications);
        await workflow.LoadAsync(f.Project.Id, ct);
        Assert.Equal(migrated.Revision, (await f.Assets.LoadAsync(f.Project.Id, ct)).Revision);
        var legacyInput = new AssetImageInput("candidate.png", [], AssetImageOrigin.Generated);
        Assert.DoesNotContain("pendingReview", JsonSerializer.Serialize(legacyInput, AtomicJsonFile.Options));
    }
}
