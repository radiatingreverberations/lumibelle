using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    [Fact]
    public async Task StoredImagesFollowMovesTrashAndRestoreWithoutServingStaleAssetUrls()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, store) = CreateStore();
        var first = Asset("First"); var second = Asset("Second");
        var saved = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [first, second] }, 0, ct);
        await using var input = new MemoryStream(Png(3, 5));
        saved = await store.AddImageAsync(project.Id, first.Id, input, new("image.png", [], AssetImageOrigin.Imported), saved.Revision, ct);
        var image = saved.Assets[0].Images[0];
        await using (var initial = await store.OpenImageAsync(project.Id, first.Id, image.Id, ct))
            Assert.NotNull(initial);
        saved = await store.MoveImagesAsync(project.Id, first.Id, [image.Id], new(second.Id), saved.Revision, ct);
        Assert.Null(await store.OpenImageAsync(project.Id, first.Id, image.Id, ct));
        await using (var moved = await store.OpenImageAsync(project.Id, second.Id, image.Id, ct))
            Assert.Equal(input.Length, moved?.Content.Length);
        saved = (await store.DeleteImageAsync(project.Id, second.Id, image.Id, saved.Revision, ct)).Library;
        Assert.Null(await store.OpenImageAsync(project.Id, second.Id, image.Id, ct));
        saved = await store.RestoreImagesAsync(project.Id, [saved.Trash[0].Id], saved.Revision, ct);
        await using var restored = await CreateFreshStore().OpenImageAsync(project.Id, second.Id, image.Id, ct);
        Assert.Equal(input.Length, restored?.Content.Length);
    }

    [Fact]
    public async Task StoredImageRequiresValidRecordAndExistingMediaFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, store) = CreateStore(); var asset = Asset("Mira");
        var saved = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [asset] }, 0, ct);
        await using var input = new MemoryStream(Png(3, 5));
        saved = await store.AddImageAsync(project.Id, asset.Id, input, new("image.png", [], AssetImageOrigin.Imported), saved.Revision, ct);
        var image = saved.Assets[0].Images[0];
        await using (var initial = await store.OpenImageAsync(project.Id, asset.Id, image.Id, ct)) Assert.NotNull(initial);
        var projectDirectory = Path.Combine(_directory, "projects", project.Id.ToString("D"));
        File.Delete(Path.Combine(projectDirectory, "assets", asset.Id.ToString("D"), "images", image.FileName));
        Assert.Null(await store.OpenImageAsync(project.Id, asset.Id, image.Id, ct));
        TestProjectDatabase.Execute(projectDirectory, "UPDATE parts SET payload = 'broken' WHERE kind = 'assets' AND list = 'assets'; UPDATE documents SET stamp = stamp + 1 WHERE kind = 'assets'");
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.OpenImageAsync(project.Id, asset.Id, image.Id, ct));
    }
}
