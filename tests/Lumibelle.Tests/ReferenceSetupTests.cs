using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private static (Shot Shot, AssetLibrary Library, ReferenceAsset Character) ReferenceFixture()
    {
        var asset = LookFixtures.Character();
        var face = LookFixtures.Image() with { Name = "Identity portrait" };
        var first = LookFixtures.Image(asset.Looks[0].Id) with { Name = "Everyday image" };
        var end = LookFixtures.Image(asset.Looks[1].Id) with { Name = "Armor image" };
        PreferredImageReference Ref(AssetImage i) => new(i.Id, i.LookId, i.Name!);
        asset = asset with { Images = [face, first, end], PreferredIdentityReferences = [Ref(face)], Looks = [
            asset.Looks[0] with { PreferredAppearanceReferences = [Ref(first)] }, asset.Looks[1] with { PreferredAppearanceReferences = [Ref(end)] }] };
        var shot = Ready(); shot.Characters = [new(Guid.NewGuid(), "JUNIPER") { Appearance = new(asset.Id, asset.Looks[0].Id, asset.Looks[1].Id) }];
        return (shot, new() { ProjectId = Guid.NewGuid(), Assets = [asset] }, asset);
    }
    [Fact]
    public async Task SceneSetupRecoveryIsAtomicAndOrdinarySavesPreserveSetups()
    {
        var (project, files, store, assets) = Fixture(); var shot = Ready();
        var scripts = new FileScriptStore(files, _clock);
        var script = await scripts.SaveAsync(new ScriptDocument { ProjectId = project.Id, Blocks = [ScriptBlock.Create(ScriptBlockKind.Scene, "Bedroom") with { Id = shot.SceneId!.Value }, ScriptBlock.Create(ScriptBlockKind.Action, "A quiet room.")] }, 0, cancellationToken: _ct);
        await scripts.ApproveAsync(project.Id, script.Revision, _ct);
        var doc = await store.SaveAsync(project.Id, [shot], 0, ct: _ct);
        var setup = new SceneReferenceSetup { SceneId = shot.SceneId!.Value, SceneTitle = "Bedroom" };
        doc = await store.SaveWorkspaceAsync(project.Id, doc.Shots, [setup], doc.Revision, ct: _ct);
        Assert.Single(doc.SceneSetups); var recover = doc.Recovery[0].Id;
        setup.SceneTitle = "Caller mutation";
        doc = await store.SaveAsync(project.Id, doc.Shots, doc.Revision, ct: _ct); Assert.Equal("Bedroom", doc.SceneSetups[0].SceneTitle);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.SaveWorkspaceAsync(project.Id, doc.Shots, [], doc.Revision - 1, ct: _ct));
        var copied = doc.Copy(); copied.SceneSetups.Clear(); Assert.Single(doc.SceneSetups);
        doc = await store.RecoverAsync(project.Id, recover, doc.Revision, _ct); Assert.Empty(doc.SceneSetups);
        var bad = new SceneReferenceSetup { SceneId = shot.SceneId.Value, Images = [new() { Image = Picture("Missing") }] };
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.SaveWorkspaceAsync(project.Id, doc.Shots, [bad], doc.Revision, ct: _ct));
        Assert.Equal(doc.Revision, (await store.LoadAsync(project.Id, _ct)).Revision);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.SaveWorkspaceAsync(project.Id, doc.Shots, [setup with { SceneId = Guid.NewGuid() }], doc.Revision, ct: _ct));
    }
    [Fact]
    public async Task LegacyAspectsBecomeFollowingOrExplicitAndCapturedDimensionsStayFixed()
    {
        var (project, files, store, _) = Fixture(); var following = Ready(); var portrait = Ready(); portrait.Aspect = "9:16";
        var dir = await files.DirectoryAsync(project.Id, _ct);
        await AtomicJsonFile.WriteAsync(Path.Combine(dir, "shots.json"), new ShotDocument { ProjectId = project.Id, SchemaVersion = 1, Shots = [following, portrait] }, _ct);
        var doc = await store.LoadAsync(project.Id, _ct);
        Assert.Null(doc.Shots[0].AspectOverride); Assert.Equal("9:16", doc.Shots[1].AspectOverride);
        var captured = ShotVideoDefaults.Capture(doc.Shots[0], project); var fingerprint = H3Policy.Fingerprint(captured);
        project = project with { VideoAspect = "1:1" };
        Assert.Equal("1:1", ShotVideoDefaults.Aspect(doc.Shots[0], project)); Assert.Equal("9:16", ShotVideoDefaults.Aspect(doc.Shots[1], project));
        Assert.Equal("16:9", captured.Aspect); Assert.Equal(fingerprint, H3Policy.Fingerprint(captured));
        doc.Shots[1].AspectOverride = null; Assert.Equal("1:1", ShotVideoDefaults.Aspect(doc.Shots[1], project));
    }

    [Fact]
    public async Task ReferenceReviewRejectsChangedAssetsAndPublicationFailurePreservesPreviousDocument()
    {
        var (project, files, store, assets) = Fixture(); var shot = Ready();
        var library = await assets.LoadAsync(project.Id, _ct);
        var doc = await store.SaveAsync(project.Id, [shot], 0, ct: _ct);
        var changed = await assets.SaveAsync(library with { Assets = [LookFixtures.Character()] }, library.Revision, _ct);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.SaveImageReferencesAsync(project.Id, doc.Shots, doc.Revision, library.Revision, _ct));
        Assert.Equal(doc.Revision, (await store.LoadAsync(project.Id, _ct)).Revision);
        var dir = await files.DirectoryAsync(project.Id, _ct);
        var before = TestProjectDatabase.Stamp(dir, "shots");
        var draft = doc.Copy(); draft.Shots[0].Title = "Reviewed title";
        // Deny publication while permitting readers; the prepared save must never
        // replace the previous manifest after an explicit publication failure.
        using (var locked = TestProjectDatabase.HoldReadOnly(dir))
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.SaveImageReferencesAsync(project.Id, draft.Shots, doc.Revision, changed.Revision, _ct));
        Assert.Equal(before, TestProjectDatabase.Stamp(dir, "shots"));
        var saved = await store.SaveImageReferencesAsync(project.Id, draft.Shots, doc.Revision, changed.Revision, _ct);
        Assert.Equal("Reviewed title", saved.Shots[0].Title); Assert.Equal("The arrival", saved.Recovery[0].Shots[0].Title);
    }
}

public sealed partial class AssetStoreTests
{
    [Fact]
    public async Task PreferredReferencesPersistDeepCopyAndSurviveDiscardAndAssetRecreation()
    {
        var (project, store) = CreateStore(); var ct = TestContext.Current.CancellationToken; var asset = LookFixtures.Character();
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [asset] }, 0, ct);
        using var content = new MemoryStream(Png(32, 32));
        library = await store.AddImageAsync(project.Id, asset.Id, content, new("face.png", [], AssetImageOrigin.Imported, LookId: asset.Looks[0].Id), library.Revision, ct);
        var image = library.Assets[0].Images[0];
        asset = library.Assets[0] with { PreferredIdentityReferences = [new(image.Id, image.LookId, "Identity")],
            Looks = [library.Assets[0].Looks[0] with { PreferredAppearanceReferences = [new(image.Id, image.LookId, "Look")] }, library.Assets[0].Looks[1]] };
        library = await store.SaveAsync(library with { Assets = [asset] }, library.Revision, ct);
        var copy = library.Copy(); Assert.NotSame(asset.PreferredIdentityReferences, copy.Assets[0].PreferredIdentityReferences);
        Assert.NotSame(asset.Looks[0].PreferredAppearanceReferences, copy.Assets[0].Looks[0].PreferredAppearanceReferences);
        var deleted = await store.DeleteImageAsync(project.Id, asset.Id, image.Id, library.Revision, ct);
        library = await store.SaveAsync(deleted.Library, deleted.Library.Revision, ct); Assert.Single(library.Assets[0].PreferredIdentityReferences);
        library = await store.DeleteAssetAsync(project.Id, asset.Id, library.Revision, ct);
        library = await store.RestoreImagesAsync(project.Id, [library.Trash.Single().Id], library.Revision, ct);
        Assert.Equal(image.Id, library.Assets[0].PreferredIdentityReferences.Single().ImageId);
        Assert.Equal(image.Id, library.Assets[0].Looks[0].PreferredAppearanceReferences.Single().ImageId);
        Assert.Equal(image.Id, library.Assets[0].Images.Single().Id);
    }
    [Fact]
    public async Task PreferredReferencesRejectForeignMissingAndDuplicateSelections()
    {
        var (project, store) = CreateStore(); var ct = TestContext.Current.CancellationToken; var asset = LookFixtures.Character();
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [asset] }, 0, ct);
        var missing = new PreferredImageReference(Guid.NewGuid(), null, "Missing");
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.SaveAsync(library with { Assets = [asset with { PreferredIdentityReferences = [missing] }] }, library.Revision, ct));
        Assert.True(LookPolicy.Invalid(asset with { PreferredIdentityReferences = [missing, missing] }));
        Assert.Empty((await store.LoadAsync(project.Id, ct)).Assets[0].PreferredIdentityReferences);
    }
}
