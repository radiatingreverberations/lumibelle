using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    private async Task<(ProjectInfo Project, FileAssetStore Store, AssetLibrary Library)> MoveFixture()
    {
        var (project, store) = CreateStore();
        var source = Asset("Frames") with { Looks = [new() { Id = Guid.NewGuid(), Name = "Everyday" }] };
        var target = Asset("Juniper") with { Looks = [new() { Id = Guid.NewGuid(), Name = "Gala" }, new() { Id = Guid.NewGuid(), Name = "Archived", Archived = true }] };
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [source, target] }, 0, cancellationToken: TestContext.Current.CancellationToken);
        for (var n = 0; n < 3; n++)
        {
            using var png = new MemoryStream(Png(n + 4, 3));
            library = await store.AddImageAsync(project.Id, source.Id, png, new("frame.png", ["face", "state"], AssetImageOrigin.Generated,
                new() { Prompt = "Immutable prompt", Look = LookPolicy.Capture(source, source.Looks[0].Id) }, source.Looks[0].Id), library.Revision, cancellationToken: TestContext.Current.CancellationToken);
        }
        var first = library.Assets[0].Images[0];
        library.Assets[0] = library.Assets[0] with
        {
            Images = library.Assets[0].Images.Select((i, n) => i with { Name = $"Frame {n}", PreservationGuidance = "Keep the face", IsCover = n == 0, IsReference = true }).ToList(),
            PreferredIdentityReferences = [new(first.Id, first.LookId, "Face")]
        };
        library = await store.SaveAsync(library, library.Revision, cancellationToken: TestContext.Current.CancellationToken);
        return (project, store, library);
    }

    [Fact]
    public async Task MovePublishesMembershipTogetherWithoutChangingPixelsIdsOrProvenance()
    {
        var (project, store, before) = await MoveFixture(); var source = before.Assets[0]; var target = before.Assets[1];
        var ids = source.Images.Take(2).Select(i => i.Id).ToArray();
        var saved = await store.MoveImagesAsync(project.Id, source.Id, ids, new(target.Id, target.Looks[0].Id), before.Revision, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(before.Revision + 1, saved.Revision);
        Assert.Single(saved.Assets[0].Images); Assert.Empty(saved.Assets[0].PreferredIdentityReferences);
        Assert.Equal(ids, saved.Assets[1].Images.Select(i => i.Id));
        foreach (var moved in saved.Assets[1].Images)
        {
            var original = source.Images.Single(i => i.Id == moved.Id);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(original with { LookId = target.Looks[0].Id, StorageAssetId = source.Id, PreviousAssetIds = moved.PreviousAssetIds }), System.Text.Json.JsonSerializer.Serialize(moved));
            await using var media = await store.OpenImageAsync(project.Id, target.Id, moved.Id, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(Png(original.Width, original.Height), await Bytes(media!.Content));
            Assert.Null(await store.OpenImageAsync(project.Id, source.Id, moved.Id, cancellationToken: TestContext.Current.CancellationToken));
        }
        var reopened = await CreateFreshStore().LoadAsync(project.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(saved.Assets[1].Images[0].Id, reopened.Assets[1].Images[0].Id);
        var copy = reopened.Copy(); ((Guid[])copy.Assets[1].Images[0].PreviousAssetIds)[0] = Guid.NewGuid();
        Assert.Equal(source.Id, reopened.Assets[1].Images[0].PreviousAssetIds[0]);
        var changed = reopened with { Assets = reopened.Assets.Select(a => a with { Description = "New notes" }).ToList() };
        await store.SaveAsync(changed, changed.Revision, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MoveValidatesEntireSelectionDestinationAndRevisionBeforePublication()
    {
        var (project, store, before) = await MoveFixture(); var source = before.Assets[0]; var target = before.Assets[1]; var id = source.Images[0].Id;
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.MoveImagesAsync(project.Id, source.Id, [id], new(target.Id), before.Revision - 1, cancellationToken: TestContext.Current.CancellationToken));
        foreach (var ids in new Guid[][] { [], [id, id], [id, Guid.NewGuid()] })
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.MoveImagesAsync(project.Id, source.Id, ids, new(target.Id), before.Revision, cancellationToken: TestContext.Current.CancellationToken));
        foreach (var destination in new ImageDestination[] { new(source.Id), new(Guid.NewGuid()), new(target.Id, source.Looks[0].Id), new(target.Id, target.Looks[1].Id), new(Guid.NewGuid(), NewAssetName: " ") })
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.MoveImagesAsync(project.Id, source.Id, [id], destination, before.Revision, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(before.Revision, (await store.LoadAsync(project.Id, cancellationToken: TestContext.Current.CancellationToken)).Revision);
        var file = Path.Combine(_directory, "projects", project.Id.ToString(), "assets", source.Id.ToString(), "images", source.Images[1].FileName);
        File.Delete(file);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.MoveImagesAsync(project.Id, source.Id, source.Images.Take(2).Select(i => i.Id).ToArray(), new(target.Id), before.Revision, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(before.Revision, (await store.LoadAsync(project.Id, cancellationToken: TestContext.Current.CancellationToken)).Revision);
    }

    [Fact]
    public async Task FailedMovePublicationKeepsFilesAndMembershipIntact()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (project, store, before) = await MoveFixture(); var source = before.Assets[0];
        var path = Path.Combine(_directory, "projects", project.Id.ToString(), "assets.json");
        using (var locked = TestProjectDatabase.HoldReadOnly(Path.GetDirectoryName(path)!))
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.MoveImagesAsync(project.Id, source.Id, source.Images.Select(i => i.Id).ToArray(), new(before.Assets[1].Id), before.Revision, cancellationToken: TestContext.Current.CancellationToken));
        var saved = await store.LoadAsync(project.Id, cancellationToken: TestContext.Current.CancellationToken); Assert.Equal(before.Revision, saved.Revision); Assert.Empty(saved.Assets[1].Images);
        foreach (var image in source.Images) { await using var media = await store.OpenImageAsync(project.Id, source.Id, image.Id, cancellationToken: TestContext.Current.CancellationToken); Assert.NotNull(media); }
    }

    [Fact]
    public async Task MovesSurviveSourceAssetDeletionAndDestinationTrashRestoreAndPurge()
    {
        var (project, store, before) = await MoveFixture(); var source = before.Assets[0]; var id = source.Images[0].Id;
        var destination = new ImageDestination(Guid.NewGuid(), NewAssetName: "Continuity", NewAssetCategory: AssetCategory.Reference);
        var saved = await store.MoveImagesAsync(project.Id, source.Id, [id], destination, before.Revision, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(saved.Assets[^1].Images[0].LookId); Assert.True(saved.Assets[^1].Images[0].IsCover);
        saved = await store.DeleteAssetAsync(project.Id, source.Id, saved.Revision, cancellationToken: TestContext.Current.CancellationToken);
        var purged = await store.PurgeImagesAsync(project.Id, saved.Trash.Select(t => t.Id).ToArray(), saved.Revision, cancellationToken: TestContext.Current.CancellationToken);
        saved = await store.DeleteAssetAsync(project.Id, destination.AssetId, purged.Library.Revision, cancellationToken: TestContext.Current.CancellationToken);
        var trash = Assert.Single(saved.Trash); Assert.Equal(source.Id, trash.Image.StorageAssetId);
        await using (var media = await store.OpenTrashImageAsync(project.Id, trash.Id, cancellationToken: TestContext.Current.CancellationToken)) Assert.NotNull(media);
        saved = await store.RestoreImagesAsync(project.Id, [trash.Id], saved.Revision, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(id, saved.Assets.Single(a => a.Id == destination.AssetId).Images.Single().Id);
        await using (var media = await store.OpenImageAsync(project.Id, destination.AssetId, id, cancellationToken: TestContext.Current.CancellationToken)) Assert.NotNull(media);
        saved = (await store.DeleteImageAsync(project.Id, destination.AssetId, id, saved.Revision, cancellationToken: TestContext.Current.CancellationToken)).Library;
        await store.PurgeImagesAsync(project.Id, [saved.Trash.Single().Id], saved.Revision, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(_directory, "projects", project.Id.ToString(), "assets", source.Id.ToString(), "images", source.Images[0].FileName)));
    }

    [Fact]
    public async Task MovingAgainKeepsRecordedInputsViewableButRequiresExplicitShotRepair()
    {
        var (project, store, before) = await MoveFixture(); var source = before.Assets[0]; var target = before.Assets[1]; var id = source.Images[0].Id;
        var saved = await store.MoveImagesAsync(project.Id, source.Id, [id], new(target.Id), before.Revision, cancellationToken: TestContext.Current.CancellationToken);
        var final = Guid.NewGuid(); saved = await store.MoveImagesAsync(project.Id, target.Id, [id], new(final, NewAssetName: "Final"), saved.Revision, cancellationToken: TestContext.Current.CancellationToken);
        foreach (var owner in new[] { source.Id, target.Id })
        {
            var resolved = ReviewImageResolution.Resolve(saved, new(new(owner, id), "Source", SourceCrop: new() { Width = .5 }));
            Assert.Equal(ReviewImageState.Active, resolved.State); Assert.Contains(final.ToString(), resolved.MediaUrl);
            Assert.Equal(ReviewImageState.Unavailable, ReviewImageResolution.Resolve(saved, new(new(owner, id), "Take", true)).State);
        }
        Assert.Equal(ReviewImageState.Unavailable, ReviewImageResolution.Resolve(saved, new(new(Guid.NewGuid(), id), "Unknown")).State);
        var shot = new Shot { Images = [new() { AssetId = source.Id, MediaId = id, Name = "Face" }] };
        Assert.Contains("moved to Final", ShotLooks.Issue(shot, saved));
        saved = (await store.DeleteImageAsync(project.Id, final, id, saved.Revision, cancellationToken: TestContext.Current.CancellationToken)).Library;
        Assert.Equal(ReviewImageState.Trashed, ReviewImageResolution.Resolve(saved, new(new(source.Id, id), "Source")).State);
        saved = await store.MoveImagesAsync(project.Id, source.Id, [source.Images[1].Id], new(target.Id), saved.Revision, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(saved.Assets.First(a => a.Id == target.Id).Images.Single().IsCover);
    }

    [Fact]
    public async Task DestinationCoverTakesPrecedenceWhenMovingAnotherCover()
    {
        var (project, store, before) = await MoveFixture(); var source = before.Assets[0]; var target = before.Assets[1];
        var saved = await store.MoveImagesAsync(project.Id, source.Id, [source.Images[0].Id], new(target.Id), before.Revision, cancellationToken: TestContext.Current.CancellationToken);
        saved.Assets[0].Images[0] = saved.Assets[0].Images[0] with { IsCover = true };
        saved = await store.SaveAsync(saved, saved.Revision, cancellationToken: TestContext.Current.CancellationToken);
        saved = await store.MoveImagesAsync(project.Id, source.Id, [source.Images[1].Id], new(target.Id), saved.Revision, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(source.Images[0].Id, Assert.Single(saved.Assets[1].Images, i => i.IsCover).Id);
        Assert.All(saved.Assets[1].Images, i => Assert.True(i.IsReference));
    }

    [Fact]
    public async Task OrdinarySavesCannotForgeStorageOrMoveHistoryOrResurrectOldMembership()
    {
        var (project, store, before) = await MoveFixture(); var source = before.Assets[0]; var target = before.Assets[1]; var id = source.Images[0].Id;
        var saved = await store.MoveImagesAsync(project.Id, source.Id, [id], new(target.Id), before.Revision, cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.SaveAsync(before, before.Revision, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.SaveAsync(before with { Revision = saved.Revision }, saved.Revision, cancellationToken: TestContext.Current.CancellationToken));
        foreach (var forged in new[] { saved.Assets[1].Images[0] with { StorageAssetId = Guid.NewGuid() }, saved.Assets[1].Images[0] with { PreviousAssetIds = [Guid.NewGuid()] } })
        {
            var draft = saved.Copy(); draft.Assets[1].Images[0] = forged;
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.SaveAsync(draft, saved.Revision, cancellationToken: TestContext.Current.CancellationToken));
        }
        Assert.Equal(saved.Revision, (await store.LoadAsync(project.Id, cancellationToken: TestContext.Current.CancellationToken)).Revision);
    }

    [Fact]
    public async Task PublicationAndCopyRetriesReturnMovedImageWithoutRecreatingOriginal()
    {
        var (project, store, before) = await MoveFixture(); var source = before.Assets[0]; var target = before.Assets[1];
        var crop = new DerivedImageRequest(Guid.NewGuid(), new(source.Id), "Crop", "", Parent: new(source.Id, source.Images[0].Id), Crop: new());
        var copy = await store.SaveDerivedImageAsync(project.Id, crop, before.Revision, cancellationToken: TestContext.Current.CancellationToken);
        var job = Guid.NewGuid(); var input = new GeneratedImageInput(job, Guid.NewGuid(), source.Id,
            new("take.png", [], AssetImageOrigin.Generated, new() { AiJobId = job, BatchId = job, CandidateNumber = 1 }));
        using var first = new MemoryStream(Png(4, 4)); var published = await store.PublishGeneratedImageAsync(project.Id, input, first, cancellationToken: TestContext.Current.CancellationToken);
        var saved = await store.MoveImagesAsync(project.Id, source.Id, [copy.ImageId, input.ImageId], new(target.Id), published.Library.Revision, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(published.Library.ImageCopyReceipts, saved.ImageCopyReceipts);
        Assert.Equal(published.Library.ImagePublications, saved.ImagePublications);
        var retriedCopy = await store.SaveDerivedImageAsync(project.Id, crop, before.Revision, cancellationToken: TestContext.Current.CancellationToken);
        using var again = new MemoryStream(Png(4, 4)); var retriedImage = await store.PublishGeneratedImageAsync(project.Id, input, again, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(retriedCopy.Available); Assert.True(retriedImage.Available);
        Assert.Equal(target.Id, retriedCopy.AssetId); Assert.Equal(target.Id, retriedImage.AssetId);
        Assert.Equal(saved.Revision, retriedImage.Library.Revision);
    }
}
