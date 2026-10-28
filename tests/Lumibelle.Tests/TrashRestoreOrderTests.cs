using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    private async Task<(Guid Project, FileAssetStore Store, AssetLibrary Library)> OrderedTrashAssets(int count)
    {
        var (project, store) = CreateStore(); var ct = TestContext.Current.CancellationToken;
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = Enumerable.Range(0, count).Select(i => Asset($"Asset {i}", "Original notes")).ToList() }, 0, ct);
        foreach (var owner in library.Assets.ToArray()) {
            for (var i = 0; i < 2; i++) {
                using var image = new MemoryStream(Png(8 + i, 8));
                library = await store.AddImageAsync(project.Id, owner.Id, image, new("image.png", [], AssetImageOrigin.Imported), library.Revision, ct);
            }
        }
        return (project.Id, store, library);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RestoringAllAssetsUsesRecordedOrderInsteadOfNewestFirstTrashDisplay(bool legacy)
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, store, original) = await OrderedTrashAssets(3);
        var deleted = await store.DeleteAssetsAsync(project, original.Assets.Select(a => a.Id).Reverse().ToArray(), original.Revision, ct);
        // Deliberately make newest-first sorting the exact opposite of library order.
        deleted = deleted with { Trash = deleted.Trash.Select((t, n) => t with {
            DeletedUtc = _clock.Now.AddTicks(n), ExpiresUtc = _clock.Now.AddTicks(n).AddDays(30),
            AssetOrder = legacy ? null : t.AssetOrder, ImageOrder = legacy ? null : t.ImageOrder
        }).ToList() };
        await AtomicJsonFile.WriteAsync(ManifestPath(project), deleted, ct);
        var restored = await store.RestoreImagesAsync(project, deleted.Trash.Select(t => t.Id).Reverse().ToArray(), deleted.Revision, ct);
        Assert.Equal(original.Assets.Select(a => a.Id), restored.Assets.Select(a => a.Id));
        foreach (var owner in original.Assets) Assert.Equal(owner.Images.Select(i => i.Id), restored.Assets.Single(a => a.Id == owner.Id).Images.Select(i => i.Id));
        Assert.Equal(restored.Assets.Select(a => a.Id), (await CreateFreshStore().LoadAsync(project, ct)).Assets.Select(a => a.Id));
    }

    [Fact]
    public async Task RestoringAssetsInSeparateReverseSelectionsRebuildsTheirOriginalPositions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, store, original) = await OrderedTrashAssets(4);
        var deleted = await store.DeleteAssetsAsync(project, original.Assets.Select(a => a.Id).ToArray(), original.Revision, ct);
        var restored = deleted;
        foreach (var owner in original.Assets.AsEnumerable().Reverse()) {
            restored = await store.RestoreImagesAsync(project, deleted.Trash.Where(t => t.Asset.Id == owner.Id).Select(t => t.Id).ToArray(), restored.Revision, ct);
            Assert.Equal(original.Assets.Where(a => restored.Assets.Any(r => r.Id == a.Id)).Select(a => a.Id), restored.Assets.Select(a => a.Id));
        }
        Assert.Equal(original.Assets.Select(a => a.Id), restored.Assets.Select(a => a.Id));
    }

    [Fact]
    public async Task RestoringMissingAssetsPreservesLaterMovesOfSurvivingAssets()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, store, original) = await OrderedTrashAssets(5);
        var missing = new[] { original.Assets[1].Id, original.Assets[3].Id };
        var deleted = await store.DeleteAssetsAsync(project, missing, original.Revision, ct);
        var survivors = deleted.Assets.Select(a => a.Id).Reverse().ToArray();
        var moved = await store.ReorderAssetsAsync(project, survivors, deleted.Revision, ct);
        var restored = await store.RestoreImagesAsync(project, deleted.Trash.Select(t => t.Id).Reverse().ToArray(), moved.Revision, ct);
        Assert.Equal(survivors, restored.Assets.Where(a => !missing.Contains(a.Id)).Select(a => a.Id));
        Assert.Equal(5, restored.Assets.Count); Assert.Empty(restored.Trash);
    }

    [Fact]
    public async Task RestoringImagesPreservesManualImageOrderAndTheExistingCover()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, owner, store, library) = await TrashFixture(4);
        var order = library.Assets[0].Images.AsEnumerable().Reverse().Select(i => i.Id).ToArray();
        library = await store.SaveAsync(library with { Assets = [library.Assets[0] with {
            Images = library.Assets[0].Images.AsEnumerable().Reverse().Select((i, n) => i with { IsReference = n == 0, IsCover = n == 0 }).ToList()
        }] }, library.Revision, ct);
        var deleted = await store.DeleteImagesAsync(project, owner, [order[1], order[3]], library.Revision, ct);
        var restored = await store.RestoreImagesAsync(project, deleted.TrashIds.Reverse().ToArray(), deleted.Library.Revision, ct);
        Assert.Equal(order, restored.Assets[0].Images.Select(i => i.Id));
        Assert.Equal(order[0], Assert.Single(restored.Assets[0].Images, i => i.IsCover).Id);
    }
}
