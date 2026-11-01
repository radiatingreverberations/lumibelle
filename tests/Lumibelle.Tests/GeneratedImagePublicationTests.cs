using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    private static GeneratedImageInput Candidate(Guid assetId, Guid? lookId = null, AssetLookContext? look = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), assetId, new("candidate.png", ["portrait"], AssetImageOrigin.Generated,
            new() { BatchId = Guid.NewGuid(), CandidateNumber = 1, Look = look }, lookId));
    private static GeneratedImageInput WithJob(GeneratedImageInput input) => input with { Image = input.Image with { Generation = input.Image.Generation! with { AiJobId = input.JobId } } };

    [Fact]
    public async Task BackgroundPublicationPreservesConcurrentMetadataAndItsReceiptSurvivesDiscardAndPurge()
    {
        var ct = TestContext.Current.CancellationToken; var (project, store) = CreateStore(); var asset = Asset("Mira");
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [asset] }, 0, ct);
        var request = WithJob(Candidate(asset.Id));
        library = await store.SaveAsync(library with { Assets = [asset with { Name = "Mira renamed", Description = "new draft" }] }, library.Revision, ct);
        using var content = new MemoryStream(Png(5, 4));
        var published = await store.PublishGeneratedImageAsync(project.Id, request, content, ct);
        Assert.Equal("Mira renamed", published.Library.Assets[0].Name); Assert.Equal("new draft", published.Library.Assets[0].Description);
        Assert.Equal(request.ImageId, published.Library.Assets[0].Images.Single().Id); Assert.True(published.Available);
        var receipt = Assert.Single(published.Library.ImagePublications);
        Assert.False(published.Library.Assets[0].Images[0].IsReference); Assert.False(published.Library.Assets[0].Images[0].IsCover);
        // Ordinary metadata saves cannot remove the store's publication receipt.
        library = await store.SaveAsync(published.Library with { ImagePublications = [] }, published.Library.Revision, ct);
        Assert.Equal(receipt, Assert.Single(library.ImagePublications));
        var trash = await store.DeleteImageAsync(project.Id, asset.Id, request.ImageId, library.Revision, ct);
        content.Position = 0; var afterDiscard = await store.PublishGeneratedImageAsync(project.Id, request, content, ct);
        Assert.False(afterDiscard.Available); Assert.Equal(trash.Library.Revision, afterDiscard.Library.Revision); Assert.Single(afterDiscard.Library.Trash);
        await store.PurgeImagesAsync(project.Id, trash.TrashIds, trash.Library.Revision, ct);
        content.Position = 0; var afterPurge = await store.PublishGeneratedImageAsync(project.Id, request, content, ct);
        Assert.False(afterPurge.Available); Assert.Empty(afterPurge.Library.Trash); Assert.Empty(afterPurge.Library.Assets[0].Images);
        content.Position = 0;
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.PublishGeneratedImageAsync(project.Id,
            request with { Image = request.Image with { Tags = ["different"] } }, content, ct));
    }
    [Fact]
    public async Task CandidateMetadataIsCapturedBeforeWaitingAndLookChangesDoNotRewriteIt()
    {
        var ct = TestContext.Current.CancellationToken; var (project, store) = CreateStore();
        var look = new CharacterLook { Name = "Everyday", Description = "a blue hoodie" }; var asset = Asset("Mira") with { Looks = [look] };
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [asset] }, 0, ct);
        var context = new AssetLookContext(asset.Id, "Mira", "identity", "same face", look.Id, look.Name, look.Description, "hoodie");
        var tags = new List<string> { "portrait" };
        var request = WithJob(Candidate(asset.Id, look.Id, context)); request = request with { Image = request.Image with { Tags = tags } };
        // A finished candidate can be published after an author archives/edits its look.
        library = await store.SaveAsync(library with { Assets = [asset with { Looks = [look with { Archived = true, Description = "changed" }] }] }, library.Revision, ct);
        using var bytes = new MemoryStream(Png(5, 4)); Task<SavedAssetImage> save;
        using (await ProjectFiles.LockAsync(Path.Combine(_directory, "projects", project.Id.ToString("D")), ct))
        { save = store.PublishGeneratedImageAsync(project.Id, request, bytes, ct); tags.Clear(); }
        var saved = await save; var image = saved.Library.Assets[0].Images.Single();
        Assert.Equal(new[] { "portrait" }, image.Tags); Assert.Equal(context, image.Generation!.Look);
        Assert.True(saved.Library.Assets[0].Looks[0].Archived); Assert.Equal("changed", saved.Library.Assets[0].Looks[0].Description);
    }
    [Fact]
    public async Task FailedManifestPublicationKeepsCandidateRetryableWithoutAnOrphanImage()
    {
        var ct = TestContext.Current.CancellationToken; var (project, store) = CreateStore(); var asset = Asset("Mira");
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [asset] }, 0, ct);
        var request = WithJob(Candidate(asset.Id)); var directory = Path.Combine(_directory, "projects", project.Id.ToString("D"));
        using var content = new MemoryStream(Png(4, 4));
        using (var locked = TestProjectDatabase.HoldReadOnly(Path.Combine(directory)))
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.PublishGeneratedImageAsync(project.Id, request, content, ct));
        Assert.Equal(library.Revision, (await store.LoadAsync(project.Id, ct)).Revision);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, "assets", asset.Id.ToString("D"), "images")));
        content.Position = 0; var saved = await store.PublishGeneratedImageAsync(project.Id, request, content, ct);
        content.Position = 0; var retry = await store.PublishGeneratedImageAsync(project.Id, request, content, ct);
        Assert.Equal(saved.Library.Revision, retry.Library.Revision); Assert.Single(retry.Library.Assets[0].Images);
    }
    [Fact]
    public async Task MissingTargetAndIncompleteProvenanceCannotPublish()
    {
        var ct = TestContext.Current.CancellationToken; var (project, store) = CreateStore(); var asset = Asset("Mira");
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [asset] }, 0, ct);
        var request = WithJob(Candidate(asset.Id)); using var content = new MemoryStream(Png(4, 4));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.PublishGeneratedImageAsync(project.Id, Candidate(asset.Id), content, ct));
        library = await store.DeleteAssetAsync(project.Id, asset.Id, library.Revision, ct); content.Position = 0;
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.PublishGeneratedImageAsync(project.Id, request, content, ct));
        Assert.Empty((await store.LoadAsync(project.Id, ct)).ImagePublications);
    }
}
