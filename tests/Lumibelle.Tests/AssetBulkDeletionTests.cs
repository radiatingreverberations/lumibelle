using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    [Fact]
    public async Task DeleteAssetsPublishesOnceAndKeepsExistingTrashAndOtherAssets()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, assetId, store, library) = await TrashFixture();
        var original = library.Assets[0];
        var empty = Asset("Empty asset"); var keep = Asset("Keep me");
        library = await store.SaveAsync(library with { Assets = [.. library.Assets, empty, keep] }, library.Revision, ct);
        library = (await store.DeleteImageAsync(project, assetId, original.Images[0].Id, library.Revision, ct)).Library;
        var previousTrash = library.Trash[0].Id;
        var deleted = await store.DeleteAssetsAsync(project, [assetId, empty.Id], library.Revision, ct);
        Assert.Equal(library.Revision + 1, deleted.Revision);
        Assert.Equal(keep.Id, Assert.Single(deleted.Assets).Id);
        Assert.Equal(2, deleted.Trash.Count);
        Assert.Contains(deleted.Trash, t => t.Id == previousTrash);
        foreach (var entry in deleted.Trash)
        {
            Assert.Equal(original.Description, entry.Asset.Description);
            Assert.Empty(entry.Asset.Images);
            Assert.True(File.Exists(TrashPath(project, assetId, entry.Image)));
            Assert.Null(await store.OpenImageAsync(project, assetId, entry.Image.Id, ct));
        }
        var restore = deleted.Trash.Single(t => t.Id != previousTrash);
        var restored = await store.RestoreImagesAsync(project, [restore.Id], deleted.Revision, ct);
        var recreated = restored.Assets.Single(a => a.Id == assetId);
        Assert.Equal(original.Description, recreated.Description);
        Assert.Equal(restore.Image.Id, Assert.Single(recreated.Images).Id);
        Assert.DoesNotContain(restored.Assets, a => a.Id == empty.Id);
    }

    [Fact]
    public async Task DeleteAssetsRejectsInvalidSelectionsWithoutDeletingAnyAssets()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, assetId, store, library) = await TrashFixture();
        foreach (var selection in new Guid[][] { [], [assetId, assetId], [assetId, Guid.Empty], [assetId, Guid.NewGuid()] })
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.DeleteAssetsAsync(project, selection, library.Revision, ct));
        var reloaded = await store.LoadAsync(project, ct);
        Assert.Equal(library.Revision, reloaded.Revision);
        Assert.Equal(2, Assert.Single(reloaded.Assets).Images.Count);
        Assert.Empty(reloaded.Trash);
    }

    [Fact]
    public async Task DeleteAssetsRejectsChangesAfterConfirmationWithoutDeletingNewAssets()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, assetId, store, library) = await TrashFixture();
        var newer = await store.SaveAsync(library with { Assets = [.. library.Assets, Asset("Added in another tab")] }, library.Revision, ct);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.DeleteAssetsAsync(project, [assetId], library.Revision, ct));
        var reloaded = await store.LoadAsync(project, ct);
        Assert.Equal(newer.Revision, reloaded.Revision);
        Assert.Equal(2, reloaded.Assets.Count);
        Assert.Empty(reloaded.Trash);
    }

    [Fact]
    public async Task DeleteAssetsFailedPublicationPreservesEverythingForRetry()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ct = TestContext.Current.CancellationToken;
        var (project, assetId, store, library) = await TrashFixture();
        library = await store.SaveAsync(library with { Assets = [.. library.Assets, Asset("Another asset")] }, library.Revision, ct);
        var ids = library.Assets.Select(a => a.Id).ToArray();
        await using (var locked = TestProjectDatabase.HoldReadOnly(ProjectPath(project)))
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.DeleteAssetsAsync(project, ids, library.Revision, ct));
        var reloaded = await store.LoadAsync(project, ct);
        Assert.Equal(library.Revision, reloaded.Revision); Assert.Equal(2, reloaded.Assets.Count); Assert.Empty(reloaded.Trash);
        foreach (var image in library.Assets[0].Images)
            Assert.True(File.Exists(TrashPath(project, assetId, image)));
        var retry = await store.DeleteAssetsAsync(project, ids, library.Revision, ct);
        Assert.Empty(retry.Assets); Assert.Equal(2, retry.Trash.Count);
    }
}
