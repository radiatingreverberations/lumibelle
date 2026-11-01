using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    private async Task<(Guid Project, Guid Asset, FileAssetStore Store, AssetLibrary Library)> TrashFixture(int count = 2)
    {
        var (project, store) = CreateStore(); var asset = Asset("Mira", "Original description");
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [asset] }, 0, TestContext.Current.CancellationToken);
        for (int i = 0; i < count; i++)
        {
            await using var bytes = new MemoryStream(Png(8 + i, 12));
            library = await store.AddImageAsync(project.Id, asset.Id, bytes,
                new("take.png", ["costume", "blue"], AssetImageOrigin.Generated, new() { Prompt = "Character portrait", Seed = 42 + i }),
                library.Revision, TestContext.Current.CancellationToken);
        }
        return (project.Id, asset.Id, store, library);
    }
    private string TrashPath(Guid project, Guid asset, AssetImage image) => Path.Combine(_directory, "projects", project.ToString("D"), "assets", asset.ToString("D"), "images", image.FileName);
    private string ProjectPath(Guid project) => Path.Combine(_directory, "projects", project.ToString("D"));

    [Fact]
    public async Task TrashPersistsExactMetadataAndOnlyDedicatedMediaCanReadIt()
    {
        var (project, asset, store, library) = await TrashFixture();
        var image = library.Assets[0].Images[0];
        var result = await store.DeleteImageAsync(project, asset, image.Id, library.Revision, TestContext.Current.CancellationToken);
        var entry = Assert.Single(result.Library.Trash);
        Assert.Equal(entry.Id, Assert.Single(result.TrashIds));
        Assert.Equal(image.Id, entry.Image.Id); Assert.Equal(image.Generation, entry.Image.Generation);
        Assert.Equal(image.Tags, entry.Image.Tags); Assert.Empty(entry.Asset.Images);
        Assert.Equal("Original description", entry.Asset.Description);
        Assert.Equal(_clock.Now.AddDays(30), entry.ExpiresUtc);
        Assert.Null(await store.OpenImageAsync(project, asset, image.Id, TestContext.Current.CancellationToken));
        await using var media = await store.OpenTrashImageAsync(project, entry.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(media);
        var reopened = await CreateFreshStore().ListTrashAsync(TestContext.Current.CancellationToken);
        Assert.Equal(entry.Id, Assert.Single(reopened.Images).Entry.Id);
        Assert.Equal(project, reopened.Images[0].ProjectId);
    }

    [Fact]
    public async Task MissingPersistedTrashIdentityIsRejectedInsteadOfRegeneratedOnEveryRead()
    {
        var (project, asset, store, library) = await TrashFixture(1);
        await store.DeleteImageAsync(project, asset, library.Assets[0].Images[0].Id, library.Revision, TestContext.Current.CancellationToken);
        var json = await TestProjectDatabase.JsonAsync(FileAssetStore.Documents, ProjectPath(project), project, TestContext.Current.CancellationToken);
        json["trash"]![0]!.AsObject().Remove("id");
        await TestProjectDatabase.SaveJsonAsync(FileAssetStore.Documents, ProjectPath(project), project, json, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.LoadAsync(project, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(TrashPath(project, asset, library.Assets[0].Images[0])));
    }

    [Fact]
    public async Task MetadataSavesCannotLoseTrashOrBypassImageLifecycle()
    {
        var (project, asset, store, library) = await TrashFixture();
        var image = library.Assets[0].Images[0];
        var result = await store.DeleteImageAsync(project, asset, image.Id, library.Revision, TestContext.Current.CancellationToken);
        library = await store.SaveAsync(result.Library with { Trash = [], Assets = [result.Library.Assets[0] with { Description = "New notes" }] }, result.Library.Revision, TestContext.Current.CancellationToken);
        Assert.Single(library.Trash);
        foreach (var invalid in new[]
        {
            library with { Assets = [] },
            library with { Assets = [library.Assets[0] with { Images = [] }] },
            library with { Assets = [library.Assets[0] with { Images = [.. library.Assets[0].Images, image] }] }
        })
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.SaveAsync(invalid, library.Revision, TestContext.Current.CancellationToken));
        Assert.Equal(library.Revision, (await store.LoadAsync(project, TestContext.Current.CancellationToken)).Revision);
    }

    [Fact]
    public async Task RestoreKeepsExistingCoverAndRediscardHasFreshIdentityAndExpiry()
    {
        var (project, asset, store, library) = await TrashFixture();
        library = await store.SaveAsync(library with { Assets = [library.Assets[0] with { Images = library.Assets[0].Images.Select((i, n) => i with { IsCover = n == 0 }).ToList() }] }, library.Revision, TestContext.Current.CancellationToken);
        var image = library.Assets[0].Images[0];
        var deleted = await store.DeleteImageAsync(project, asset, image.Id, library.Revision, TestContext.Current.CancellationToken);
        library = await store.SaveAsync(deleted.Library with { Assets = [deleted.Library.Assets[0] with { Images = [deleted.Library.Assets[0].Images[0] with { IsCover = true }] }] }, deleted.Library.Revision, TestContext.Current.CancellationToken);
        library = await store.RestoreImagesAsync(project, deleted.TrashIds, library.Revision, TestContext.Current.CancellationToken);
        var restored = library.Assets[0].Images.Single(i => i.Id == image.Id);
        Assert.True(restored.IsReference); Assert.False(restored.IsCover); Assert.Equal(image.FileName, restored.FileName);
        Assert.Single(library.Assets[0].Images, i => i.IsCover); Assert.Empty(library.Trash);
        _clock.Now = _clock.Now.AddDays(2);
        var again = await store.DeleteImageAsync(project, asset, image.Id, library.Revision, TestContext.Current.CancellationToken);
        Assert.NotEqual(deleted.TrashIds[0], again.TrashIds[0]); Assert.Equal(_clock.Now.AddDays(30), again.Library.Trash[0].ExpiresUtc);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.PurgeImagesAsync(project, deleted.TrashIds, again.Library.Revision, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(TrashPath(project, asset, image)));
    }

    [Fact]
    public async Task DeletingAssetPreservesPriorTrashAndRestoresOnlyChosenImagesIntoOriginalAsset()
    {
        var (project, asset, store, library) = await TrashFixture(3);
        var first = await store.DeleteImageAsync(project, asset, library.Assets[0].Images[0].Id, library.Revision, TestContext.Current.CancellationToken);
        _clock.Now = _clock.Now.AddDays(1);
        library = await store.DeleteAssetAsync(project, asset, first.Library.Revision, TestContext.Current.CancellationToken);
        Assert.Empty(library.Assets); Assert.Equal(3, library.Trash.Count);
        Assert.Equal(first.Library.Trash[0].ExpiresUtc, library.Trash[0].ExpiresUtc);
        foreach (var item in library.Trash) Assert.True(File.Exists(TrashPath(project, asset, item.Image)));
        library = await store.RestoreImagesAsync(project, first.TrashIds, library.Revision, TestContext.Current.CancellationToken);
        var recreated = Assert.Single(library.Assets);
        Assert.Equal(asset, recreated.Id); Assert.Equal("Mira", recreated.Name); Assert.Equal("Original description", recreated.Description);
        Assert.Single(recreated.Images); Assert.Equal(2, library.Trash.Count);
    }

    [Fact]
    public async Task CleanupHonorsExactExpiryAndOnlyExpiresDueImages()
    {
        var (project, asset, store, library) = await TrashFixture();
        var first = await store.DeleteImageAsync(project, asset, library.Assets[0].Images[0].Id, library.Revision, TestContext.Current.CancellationToken);
        var expiredImage = first.Library.Trash[0].Image;
        _clock.Now = _clock.Now.AddDays(1);
        var second = await store.DeleteImageAsync(project, asset, first.Library.Assets[0].Images[0].Id, first.Library.Revision, TestContext.Current.CancellationToken);
        _clock.Now = first.Library.Trash[0].ExpiresUtc.AddTicks(-1);
        Assert.Empty(await store.CleanupExpiredAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, (await store.LoadAsync(project, TestContext.Current.CancellationToken)).Trash.Count);
        _clock.Now = _clock.Now.AddTicks(1);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.RestoreImagesAsync(project, first.TrashIds, second.Library.Revision, TestContext.Current.CancellationToken));
        await CreateFreshStore().CleanupExpiredAsync(TestContext.Current.CancellationToken);
        library = await store.LoadAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal(second.TrashIds[0], Assert.Single(library.Trash).Id);
        Assert.False(File.Exists(TrashPath(project, asset, expiredImage)));
        var revision = library.Revision;
        await store.CleanupExpiredAsync(TestContext.Current.CancellationToken);
        Assert.Equal(revision, (await store.LoadAsync(project, TestContext.Current.CancellationToken)).Revision);
    }

    [Fact]
    public async Task RestoreValidatesEntireSelectionAndMissingFilesStayInTrash()
    {
        var (project, asset, store, library) = await TrashFixture();
        var deleted = await store.DeleteImagesAsync(project, asset, library.Assets[0].Images.Select(i => i.Id).ToArray(), library.Revision, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.RestoreImagesAsync(project, deleted.TrashIds, library.Revision, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.RestoreImagesAsync(project, [deleted.TrashIds[0], Guid.NewGuid()], deleted.Library.Revision, TestContext.Current.CancellationToken));
        File.Delete(TrashPath(project, asset, deleted.Library.Trash[0].Image));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.RestoreImagesAsync(project, deleted.TrashIds, deleted.Library.Revision, TestContext.Current.CancellationToken));
        library = await store.LoadAsync(project, TestContext.Current.CancellationToken);
        Assert.Empty(library.Assets[0].Images); Assert.Equal(2, library.Trash.Count);
    }

    [Fact]
    public async Task FailedRestoreOrPurgePublicationPreservesRecoverableFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (project, asset, store, library) = await TrashFixture(1);
        var deleted = await store.DeleteImageAsync(project, asset, library.Assets[0].Images[0].Id, library.Revision, TestContext.Current.CancellationToken);
        await using (var locked = TestProjectDatabase.HoldReadOnly(ProjectPath(project)))
        {
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.RestoreImagesAsync(project, deleted.TrashIds, deleted.Library.Revision, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.PurgeImagesAsync(project, deleted.TrashIds, deleted.Library.Revision, TestContext.Current.CancellationToken));
        }
        library = await store.LoadAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal(ImageTrashState.Recoverable, library.Trash[0].State);
        Assert.True(File.Exists(TrashPath(project, asset, library.Trash[0].Image)));
    }

    [Fact]
    public async Task LockedFileRemainsPendingWhileOtherPurgesCompleteAndRetryFinishes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (project, asset, store, library) = await TrashFixture();
        var deleted = await store.DeleteImagesAsync(project, asset, library.Assets[0].Images.Select(i => i.Id).ToArray(), library.Revision, TestContext.Current.CancellationToken);
        var lockedEntry = deleted.Library.Trash[0];
        await using (var locked = new FileStream(TrashPath(project, asset, lockedEntry.Image), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await store.PurgeImagesAsync(project, deleted.TrashIds, deleted.Library.Revision, TestContext.Current.CancellationToken);
            Assert.Single(result.RemovedIds); Assert.Single(result.Errors);
            var pending = Assert.Single(result.Library.Trash);
            Assert.Equal(lockedEntry.Id, pending.Id); Assert.Equal(ImageTrashState.Purging, pending.State); Assert.NotNull(pending.CleanupError);
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.RestoreImagesAsync(project, [pending.Id], result.Library.Revision, TestContext.Current.CancellationToken));
        }
        await CreateFreshStore().CleanupExpiredAsync(TestContext.Current.CancellationToken);
        Assert.Empty((await store.LoadAsync(project, TestContext.Current.CancellationToken)).Trash);
        Assert.False(File.Exists(TrashPath(project, asset, lockedEntry.Image)));
    }

    [Fact]
    public async Task RestartFinishesPurgeAfterFileRemovalBeforeFinalPublication()
    {
        var (project, asset, store, library) = await TrashFixture(1);
        var deleted = await store.DeleteImageAsync(project, asset, library.Assets[0].Images[0].Id, library.Revision, TestContext.Current.CancellationToken);
        var item = deleted.Library.Trash[0];
        await TestProjectDatabase.SaveAssetsAsync(ProjectPath(project), deleted.Library with { Trash = [item with { State = ImageTrashState.Purging }] }, TestContext.Current.CancellationToken);
        File.Delete(TrashPath(project, asset, item.Image));
        await CreateFreshStore().CleanupExpiredAsync(TestContext.Current.CancellationToken);
        Assert.Empty((await store.LoadAsync(project, TestContext.Current.CancellationToken)).Trash);
    }

    [Fact]
    public async Task RestoreAndPurgeRacePublishesOnlyOneWinner()
    {
        var (project, asset, store, library) = await TrashFixture(1);
        var deleted = await store.DeleteImageAsync(project, asset, library.Assets[0].Images[0].Id, library.Revision, TestContext.Current.CancellationToken);
        var restore = store.RestoreImagesAsync(project, deleted.TrashIds, deleted.Library.Revision, TestContext.Current.CancellationToken);
        var purge = CreateFreshStore().PurgeImagesAsync(project, deleted.TrashIds, deleted.Library.Revision, TestContext.Current.CancellationToken);
        var errors = await Task.WhenAll(Record.ExceptionAsync(async () => await restore).AsTask(), Record.ExceptionAsync(async () => await purge).AsTask());
        Assert.Single(errors, e => e is WorkspaceConflictException); Assert.Single(errors, e => e is null);
        library = await store.LoadAsync(project, TestContext.Current.CancellationToken);
        Assert.Empty(library.Trash);
        Assert.Equal(library.Assets[0].Images.Count == 1, File.Exists(TrashPath(project, asset, deleted.Library.Trash[0].Image)));
    }

    [Fact]
    public async Task GlobalTrashKeepsOtherProjectsVisibleWhenOneManifestIsUnreadable()
    {
        var (first, asset1, store, library1) = await TrashFixture(1);
        var (second, asset2, _, library2) = await TrashFixture(1);
        await store.DeleteImageAsync(first, asset1, library1.Assets[0].Images[0].Id, library1.Revision, TestContext.Current.CancellationToken);
        await store.DeleteImageAsync(second, asset2, library2.Assets[0].Images[0].Id, library2.Revision, TestContext.Current.CancellationToken);
        Assert.Equal(2, (await store.ListTrashAsync(TestContext.Current.CancellationToken)).Images.Count);
        TestProjectDatabase.Corrupt(ProjectPath(first), "assets");
        var list = await store.ListTrashAsync(TestContext.Current.CancellationToken);
        Assert.Equal(second, Assert.Single(list.Images).ProjectId); Assert.Single(list.Issues);
        _clock.Now = _clock.Now.AddDays(31);
        Assert.Single(await store.CleanupExpiredAsync(TestContext.Current.CancellationToken));
        Assert.True(File.Exists(TrashPath(first, asset1, library1.Assets[0].Images[0])));
    }
}
