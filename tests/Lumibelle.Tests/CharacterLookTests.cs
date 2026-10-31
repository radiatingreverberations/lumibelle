using System.Text.Json;
using Bunit;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;

namespace Lumibelle.Tests;

public static class LookFixtures
{
    public static ReferenceAsset Character() => new() { Id = Guid.NewGuid(), Name = "Juniper", Description = "Dark eyes, a round face.", PreservationGuidance = "Keep facial identity.", Looks = [new() { Name = "Everyday", Description = "Gray hoodie.", PreservationGuidance = "Keep the gray fabric." }, new() { Name = "Gala costume", Description = "White and gold armor.", PreservationGuidance = "Keep the gold trim." }] };
    public static AssetImage Image(Guid? look = null) => new() { Id = Guid.NewGuid(), LookId = look, FileName = "look.png", ContentType = "image/png", Width = 32, Height = 32, PreservationGuidance = "Keep the hood up." };
}

public sealed partial class AssetStoreTests
{
    [Fact]
    public async Task LooksAndAssignmentsPersistWithoutRewritingGenerationProvenance()
    {
        var (project, store) = CreateStore(); var asset = LookFixtures.Character(); var ct = Xunit.TestContext.Current.CancellationToken;
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [asset] }, 0, ct);
        var captured = LookPolicy.Capture(asset, asset.Looks[0].Id);
        await using var bytes = new MemoryStream(Png(32, 32));
        library = await store.AddImageAsync(project.Id, asset.Id, bytes, new("take.png", ["face"], AssetImageOrigin.Generated, new() { Prompt = "Original instruction", Look = captured }, captured.LookId), library.Revision, ct);
        var image = library.Assets[0].Images.Single();
        var stale = library.Copy();
        library = await store.SaveAsync(library with { Assets = [library.Assets[0] with { Images = [image with { LookId = asset.Looks[1].Id }], Looks = [asset.Looks[0] with { Description = "New description", Archived = true }, asset.Looks[1]] }] }, library.Revision, ct);
        var reopened = await CreateFreshStore().LoadAsync(project.Id, ct);
        Assert.Equal(asset.Looks[1].Id, reopened.Assets[0].Images[0].LookId);
        Assert.Equal(image.Id, reopened.Assets[0].Images[0].Id); Assert.Equal(image.FileName, reopened.Assets[0].Images[0].FileName);
        Assert.Equal(captured, reopened.Assets[0].Images[0].Generation!.Look);
        Assert.Equal("Original instruction", reopened.Assets[0].Images[0].Generation!.Prompt);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.SaveAsync(stale, stale.Revision, ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.SaveAsync(library with { Assets = [library.Assets[0] with { Images = [image with { LookId = asset.Looks[0].Id }] }] }, library.Revision, ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.SaveAsync(library with { Assets = [library.Assets[0] with { Looks = [asset.Looks[1]] }] }, library.Revision, ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.SaveAsync(library with { Assets = [library.Assets[0] with { Images = [image with { Generation = image.Generation! with { Look = captured with { Description = "Rewrite" } } }] }] }, library.Revision, ct));
        Assert.Equal(library.Revision, (await store.LoadAsync(project.Id, ct)).Revision);
    }

    [Fact]
    public async Task TrashRestoresLookDefinitionsAndRetainsExistingDefinition()
    {
        var (project, store) = CreateStore(); var asset = LookFixtures.Character(); var ct = Xunit.TestContext.Current.CancellationToken;
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [asset] }, 0, ct);
        await using var bytes = new MemoryStream(Png(32, 32));
        library = await store.AddImageAsync(project.Id, asset.Id, bytes, new("look.png", [], AssetImageOrigin.Imported, LookId: asset.Looks[1].Id), library.Revision, ct);
        var image = library.Assets[0].Images.Single();
        library = await store.DeleteAssetAsync(project.Id, asset.Id, library.Revision, ct);
        Assert.Equal(2, library.Trash.Single().Asset.Looks.Count);
        library = await store.RestoreImagesAsync(project.Id, [library.Trash[0].Id], library.Revision, ct);
        Assert.Equal(asset.Looks[1], library.Assets[0].Looks[1]); Assert.Equal(image.Id, library.Assets[0].Images[0].Id);
        Assert.Equal(asset.Looks[1].Id, library.Assets[0].Images[0].LookId);
        library = (await store.DeleteImageAsync(project.Id, asset.Id, image.Id, library.Revision, ct)).Library;
        var edited = library.Assets[0].Looks[1] with { Description = "Reviewed newer guidance" };
        library = await store.SaveAsync(library with { Assets = [library.Assets[0] with { Looks = [asset.Looks[0], edited] }] }, library.Revision, ct);
        library = await store.RestoreImagesAsync(project.Id, [library.Trash[0].Id], library.Revision, ct);
        Assert.Equal(edited.Description, library.Assets[0].Looks[1].Description);
    }

    [Fact]
    public async Task InvalidOwnershipAndImportIntoArchivedLookDoNotPublish()
    {
        var (project, store) = CreateStore(); var asset = LookFixtures.Character(); var ct = Xunit.TestContext.Current.CancellationToken;
        asset = asset with { Looks = [asset.Looks[0] with { Archived = true }, asset.Looks[1]] };
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [asset] }, 0, ct);
        foreach (var id in new[] { Guid.NewGuid(), asset.Looks[0].Id })
        {
            await using var bytes = new MemoryStream(Png(32, 32));
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.AddImageAsync(project.Id, asset.Id, bytes, new("image.png", [], AssetImageOrigin.Imported, LookId: id), library.Revision, ct));
        }
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.SaveAsync(library with { Assets = [asset with { Category = AssetCategory.Prop }] }, library.Revision, ct));
        Assert.Equal(library.Revision, (await store.LoadAsync(project.Id, ct)).Revision);
    }
}

public sealed class CharacterLookTests
{
    [Fact]
    public void OrderedReferenceContextsAreCopiedAndMembershipChangesRequireRepair()
    {
        var asset = LookFixtures.Character(); var a = LookFixtures.Image(asset.Looks[0].Id); var b = LookFixtures.Image(asset.Looks[1].Id);
        asset = asset with { Images = [a, b] }; var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [asset] };
        var contexts = new List<AssetReferenceLook> { new(new(asset.Id, a.Id), LookPolicy.Capture(asset, a.LookId)), new(new(asset.Id, b.Id), LookPolicy.Capture(asset, b.LookId)) };
        using var streamA = new MemoryStream(); using var streamB = new MemoryStream();
        ReferenceImageSource[] sources = [new(asset.Id, a.Id, streamA), new(asset.Id, b.Id, streamB)];
        var request = ReferenceEditInputs.Capture(new() { SourceAssetId = asset.Id, SourceImageId = a.Id, ReferenceLooks = contexts, Prompt = "Edit", AspectRatio = "1:1" }, sources, 2);
        contexts.Clear(); Assert.Equal(2, request.ReferenceLooks.Count); LookPolicy.ValidateReferences(library, request.ReferenceLooks);
        var edited = asset with { Looks = [asset.Looks[0] with { Description = "Later wording" }, asset.Looks[1]] };
        LookPolicy.ValidateReferences(library with { Assets = [edited] }, request.ReferenceLooks);
        Assert.Equal("Gray hoodie.", request.ReferenceLooks[0].Context.Description);
        Assert.Throws<WorkspaceStoreException>(() => LookPolicy.ValidateTarget(library with { Assets = [edited] }, request.ReferenceLooks[0].Context, true));
        Assert.Throws<WorkspaceStoreException>(() => LookPolicy.ValidateReferences(library with { Assets = [asset with { Images = [a with { LookId = b.LookId }, b] }] }, request.ReferenceLooks));
        Assert.Throws<WorkspaceStoreException>(() => LookPolicy.ValidateReferences(library with { Assets = [asset with { Images = [a] }] }, request.ReferenceLooks));
        Assert.Throws<AiGenerationException>(() => ReferenceEditInputs.Capture(request with { ReferenceLooks = request.ReferenceLooks.Reverse().ToArray() }, sources, 2));
        Assert.Throws<AiGenerationException>(() => ReferenceEditInputs.Capture(request with { ReferenceLooks = [null!] }, sources, 2));
    }

    [Fact]
    public void LegacyLoadsEmptyAndDeepCopiesKeepCollectionsIndependent()
    {
        var legacy = JsonSerializer.Deserialize<ReferenceAsset>("{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"Legacy\"}", AtomicJsonFile.Options)!;
        Assert.Empty(legacy.Looks); Assert.Null(LookFixtures.Image().LookId);
        var asset = LookFixtures.Character(); var evidence = new List<AssetSourceEvidence> { new("Scene", Guid.NewGuid(), "Hoodie") };
        asset = asset with { Looks = [asset.Looks[0] with { Evidence = evidence }, asset.Looks[1]] };
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [asset] }; var copy = library.Copy(); evidence.Clear();
        Assert.Single(copy.Assets[0].Looks[0].Evidence);
        var longName = new string('a', 200); var name = LookPolicy.UniqueName(longName, [], "copy"); Assert.True(name.Length <= 200);
        Assert.NotEqual(name, LookPolicy.UniqueName(longName, [new() { Name = name }], "copy"));
        Assert.True(LookPolicy.Invalid(asset with { Looks = [asset.Looks[0], asset.Looks[1] with { Name = " everyday " }] }));
    }

    [Fact]
    public void ExtractionReviewsNestedLooksAndOnlyMergesWithinCharacter()
    {
        var asset = LookFixtures.Character(); var scene = ScriptBlock.Create(ScriptBlockKind.Scene, "Bedroom");
        var script = ScriptFixtures.Approved([scene, ScriptBlock.Create(ScriptBlockKind.Action, "Juniper wears a hoodie, then Gala armor.")]);
        var library = new AssetLibrary { ProjectId = script.ProjectId, Assets = [asset] };
        var request = new AssetExtractionRequest(script, library, [scene.Id], AiBackend.OpenRouter, "mock");
        var raw = $$"""[{"category":"character","name":"Juniper","description":"Dark eyes.","matchedAssetId":"{{asset.Id}}","evidence":[{"label":"Bedroom","sceneId":"{{scene.Id}}","excerpt":"wears a hoodie"}],"looks":[{"name":"Everyday","description":"A gray hoodie.","matchedLookId":"{{asset.Looks[0].Id}}","evidence":[{"label":"Bedroom","sceneId":"{{scene.Id}}","excerpt":"wears a hoodie"}]},{"name":"Battle worn","description":"Torn armor.","evidence":[{"label":"Bedroom","sceneId":"{{scene.Id}}","excerpt":"Gala armor"}]}]}]""";
        var proposals = AssetExtractor.Parse(raw, request)!; Assert.NotNull(proposals);
        Assert.Equal(ExtractionDecision.Merge, proposals[0].Looks[0].Decision);
        Assert.Equal(script.Id, proposals[0].Looks[0].Evidence[0].ApprovedScriptId);
        proposals[0].Looks[1].Decision = ExtractionDecision.Skip;
        var applied = AssetExtractionApply.Apply(library, proposals, DateTimeOffset.UtcNow);
        Assert.Single(applied.Assets); Assert.Equal(2, applied.Assets[0].Looks.Count);
        Assert.Equal(asset.Looks[0].Id, applied.Assets[0].Looks[0].Id); Assert.Equal("A gray hoodie.", applied.Assets[0].Looks[0].Description);
        Assert.Equal("Gray hoodie.", library.Assets[0].Looks[0].Description);
        Assert.Null(AssetExtractor.Parse(raw.Replace(asset.Looks[0].Id.ToString(), Guid.NewGuid().ToString()), request));
        proposals[0].Looks[0].MatchedLookId = Guid.NewGuid();
        Assert.Throws<WorkspaceStoreException>(() => AssetExtractionApply.Apply(library, proposals, DateTimeOffset.UtcNow));
        Assert.Equal("Gray hoodie.", library.Assets[0].Looks[0].Description);
        Assert.Contains(asset.Looks[0].Id.ToString(), string.Join("\n", AssetExtractor.BuildMessages(request).Select(m => m.Text)));
    }

    [Fact]
    public void EnhancementDistinguishesTargetLookFromSourceContextAndDetectsStaleness()
    {
        var asset = LookFixtures.Character(); var image = LookFixtures.Image(asset.Looks[0].Id);
        var context = new PromptEnhancementContext { ProjectId = Guid.NewGuid(), AssetId = asset.Id, AssetName = asset.Name, IsEdit = true, Prompt = "Put the armor on her. exact_trigger", TargetLook = LookPolicy.Capture(asset, asset.Looks[1].Id), References = [new(new(asset.Id, image.Id), "Image 1", "", null) { Look = LookPolicy.Capture(asset, image.LookId) }], ProtectedTriggers = ["exact_trigger"] };
        var captured = context.Capture(); var fingerprint = context.Fingerprint();
        var messages = PromptEnhancer.BuildMessages(new(context, new(AiBackend.OpenRouter, "mock", "Mock")), "Guide", []);
        using var json = JsonDocument.Parse(messages[1].Text!);
        Assert.Equal("Gala costume", json.RootElement.GetProperty("targetLook").GetProperty("LookName").GetString());
        Assert.Equal("Everyday", json.RootElement.GetProperty("references")[0].GetProperty("sourceLookContext").GetProperty("LookName").GetString());
        context = context with { TargetLook = context.TargetLook! with { Description = "Changed" } };
        Assert.NotEqual(fingerprint, context.Fingerprint()); Assert.Equal("White and gold armor.", captured.TargetLook!.Description);
    }
}

public sealed partial class ShotTests
{
    [Fact]
    public async Task ShotRecoveryKeepsAppearanceIdsAndIndependentTransitions()
    {
        var f = Fixture(); var asset = LookFixtures.Character(); var shot = WithAppearance(asset, false);
        var doc = await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        shot = shot.Copy(); shot.Characters[0] = shot.Characters[0] with { Appearance = new(asset.Id, asset.Looks[0].Id, asset.Looks[1].Id) };
        doc = await f.Shots.SaveAsync(f.Project.Id, [shot], doc.Revision, ct: _ct);
        var savedTransition = doc.Copy();
        doc = await f.Shots.RecoverAsync(f.Project.Id, doc.Recovery[0].Id, doc.Revision, _ct);
        Assert.Null(doc.Shots[0].Characters[0].Appearance!.EndLookId);
        Assert.Equal(asset.Looks[1].Id, savedTransition.Shots[0].Characters[0].Appearance!.EndLookId);
        Assert.Equal(shot.Characters[0].Id, doc.Shots[0].Characters[0].Id);
    }

    private static Shot WithAppearance(ReferenceAsset asset, bool transition = true)
    {
        var shot = Ready(); shot.Dialogue = [new() { Speaker = "JUNIPER", Text = "You called?" }];
        ShotReferences.RetainCharacters(shot);
        shot.Characters[0] = shot.Characters[0] with { Appearance = new(asset.Id, asset.Looks[0].Id, transition ? asset.Looks[1].Id : null) };
        return shot;
    }
    [Fact]
    public void TransitionKeepsOneSubjectWithPhaseSpecificGuidanceAndExactDialogue()
    {
        var asset = LookFixtures.Character(); var start = LookFixtures.Image(asset.Looks[0].Id); var end = LookFixtures.Image(asset.Looks[1].Id) with { PreservationGuidance = "Keep armor seams." };
        asset = asset with { Images = [start, end] }; var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [asset] }; var shot = WithAppearance(asset);
        shot.Images = [new() { AssetId = asset.Id, MediaId = start.Id, LookId = start.LookId, Name = "Hoodie", RepresentsId = shot.Characters[0].Id, Purpose = ShotReferencePurpose.StartingLook }, new() { AssetId = asset.Id, MediaId = end.Id, LookId = end.LookId, Name = "Armor", RepresentsId = shot.Characters[0].Id, Purpose = ShotReferencePurpose.EndingLook }];
        ShotLooks.Validate(shot, library);
        var guidance = ShotReferences.Resolve(shot, library, new()); var appearances = ShotLooks.Capture(shot, library);
        var prompt = H3Policy.Compile(shot, guidance, appearances);
        Assert.Contains("<Subject 1> is JUNIPER from <Picture 1> and <Picture 2>", prompt); Assert.DoesNotContain("<Subject 2>", prompt);
        Assert.Contains("Starting look — Everyday", prompt); Assert.Contains("Ending look — Gala costume", prompt);
        Assert.Contains("<d>[English] You called?</d>", prompt); Assert.Contains("without camera cuts", prompt);
        Assert.Contains("Keep the hood up.", guidance[0].Effective); Assert.DoesNotContain("armor seams", guidance[0].Effective);
        shot.Images[0].Purpose = ShotReferencePurpose.Identity;
        var identity = ShotReferences.Resolve(shot, library, new())[0]; Assert.Equal(asset.PreservationGuidance, identity.Effective);
        Assert.Empty(identity.LookDefault); Assert.Empty(identity.ImageDefault);
        shot.Images[0].PreservationOverride = "An explicit shot override"; Assert.Equal(shot.Images[0].PreservationOverride, ShotReferences.Resolve(shot, library, new())[0].Effective);
        ShotReferences.ChangeSpeaker(shot, shot.Dialogue[0].Id, "MOUSE"); Assert.Equal(appearances[0].CharacterId, shot.Characters[0].Id); Assert.Equal(asset.Looks[1].Id, shot.Characters[0].Appearance!.EndLookId);
        Assert.Equal("White and gold armor.", appearances[0].End!.Description);
    }

    [Fact]
    public void SilentAppearanceNeedsNoImageButMissingMembershipAndArchivedLooksBlockSubmissions()
    {
        var asset = LookFixtures.Character(); var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [asset] }; var shot = WithAppearance(asset, false); shot.Dialogue.Clear();
        var prompt = H3Policy.Compile(shot, [], ShotLooks.Capture(shot, library)); Assert.Contains("Gray hoodie.", prompt); Assert.Single(ShotLooks.Warnings(shot));
        shot.Characters[0] = shot.Characters[0] with { Appearance = new(asset.Id, Guid.NewGuid()) }; Assert.NotNull(ShotLooks.Issue(shot, library));
        shot = WithAppearance(asset); var image = LookFixtures.Image(asset.Looks[0].Id); library = library with { Assets = [asset with { Images = [image] }] };
        shot.Images = [new() { AssetId = asset.Id, MediaId = image.Id, Name = "Juniper", LookId = image.LookId, RepresentsId = shot.Characters[0].Id, Purpose = ShotReferencePurpose.EndingLook }];
        Assert.Contains("another look", ShotLooks.Issue(shot, library)); shot.Images[0].Purpose = ShotReferencePurpose.StartingLook; Assert.Null(ShotLooks.Issue(shot, library));
        library = library with { Assets = [asset with { Images = [image with { LookId = asset.Looks[1].Id }] }] }; Assert.Contains("assignment changed", ShotLooks.Issue(shot, library));
        library = library with { Assets = [asset with { Looks = [asset.Looks[0] with { Archived = true }, asset.Looks[1]], Images = [image] }] }; Assert.Contains("unarchive", ShotLooks.Issue(shot, library));
    }

    [Fact]
    public void PlannerOnlyAcceptsKnownLooksAndPreservesRawInvalidProposals()
    {
        var asset = LookFixtures.Character(); var doc = ScriptFixtures.Document(); var approved = ScriptFixtures.Approved(doc.Blocks, doc.ProjectId);
        var request = new ShotPlanningRequest(approved, new() { ProjectId = doc.ProjectId, Assets = [asset] }, [doc.Blocks[0].Id], 15, "", new(AiBackend.OpenRouter, "mock", "Mock"));
        var shot = WithAppearance(asset); shot.SceneId = doc.Blocks[0].Id; shot.SourceBlockIds = doc.Blocks.Select(b => b.Id).ToList();
        string Raw() => JsonSerializer.Serialize(new[] { shot }, AtomicJsonFile.Options);
        var result = ShotPlanner.Parse(Raw(), request); Assert.Null(result.Error); Assert.Equal(asset.Looks[1].Id, result.Shots[0].Characters[0].Appearance!.EndLookId); Assert.Empty(result.Shots[0].Images);
        shot.Characters[0] = shot.Characters[0] with { Appearance = new(asset.Id, Guid.NewGuid()) }; var raw = Raw(); result = ShotPlanner.Parse(raw, request); Assert.NotNull(result.Error); Assert.Empty(result.Shots); Assert.Equal(raw, result.Raw);
    }

    [Fact]
    public async Task ShotBatchCapturesAppearanceAcrossEditsAndOneMoreAndOwnsArchivedMembership()
    {
        using var f = await QueuedVideoFixture.Create(this); var asset = LookFixtures.Character();
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, _ct);
        f.Shot.Characters = [new(Guid.NewGuid(), "Silent Juniper") { Appearance = new(asset.Id, asset.Looks[0].Id, asset.Looks[1].Id) }];
        var doc = await f.Shots.LoadAsync(f.Project.Id, _ct);
        doc = await f.Shots.SaveAsync(f.Project.Id, [f.Shot], doc.Revision, ct: _ct); var captured = ShotLooks.Capture(f.Shot, library);
        library = await f.Assets.SaveAsync(library with { Assets = [asset with { Looks = [asset.Looks[0] with { Description = "Reviewed hoodie" }, asset.Looks[1]] }] }, library.Revision, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Capture(appearances: captured)); Assert.Empty(f.Graphs);
        var request = await f.Capture(appearances: ShotLooks.Capture(f.Shot, library)); var context = await f.Claim(request);
        await f.Worker.ExecuteAsync(context, request.Snapshot, _ct);
        await f.Jobs.UpdateAsync(request.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        library = await f.Assets.SaveAsync(library with { Assets = [asset with { Looks = [asset.Looks[0] with { Description = "Later hoodie" }, asset.Looks[1]] }] }, library.Revision, _ct);
        var root = (await f.Jobs.ReadAsync(_ct)).Jobs.Single(); await f.Worker.ValidateExtensionAsync(root, request.Snapshot, _ct);
        await f.Jobs.ExtendBatchAsync(request.Id, Guid.NewGuid(), request.OriginTabId, _ct);
        var continuation = (await f.Jobs.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!;
        await f.Worker.ExecuteAsync(f.Context(continuation, false), request.Snapshot, _ct);
        doc = await f.Shots.LoadAsync(f.Project.Id, _ct); Assert.Equal(2, doc.Takes.Count);
        Assert.All(doc.Takes, t => { Assert.Equal("Reviewed hoodie", t.Snapshot.Appearances[0].Start.Description); Assert.DoesNotContain("Later hoodie", t.Snapshot.Prompt); });
        library = await f.Assets.SaveAsync(library with { Assets = [asset with { Looks = [asset.Looks[0] with { Archived = true }, asset.Looks[1]] }] }, library.Revision, _ct);
        await f.Worker.ValidateExtensionAsync(root, request.Snapshot, _ct);
    }
}

public sealed partial class AssetComponentTests
{
    [Fact]
    public void CreateDraftsAreIndependentAndEditTargetDoesNotChangeSourceOrInstruction()
    {
        var asset = LookFixtures.Character(); var image = LookFixtures.Image(asset.Looks[0].Id); asset = asset with { Images = [image] };
        _assets.Library = _assets.Library with { Assets = [asset] }; var page = Page();
        page.Find("#image-prompt").Input("Authored create prompt");
        page.Find("[aria-label='Gallery look']").Change(asset.Looks[0].Id.ToString());
        Assert.Equal("Authored create prompt", page.Find("#image-prompt").GetAttribute("value"));
        page.Find(".media-select").Click();
        page.Find("#image-prompt").Input("Dress her in armor"); var source = page.Find(".compact-image-input img").GetAttribute("src");
        page.Find("[aria-label='Gallery look']").Change(asset.Looks[1].Id.ToString());
        Assert.Equal(source, page.Find(".compact-image-input img").GetAttribute("src")); Assert.Equal("Dress her in armor", page.Find("#image-prompt").GetAttribute("value"));
        Assert.Empty(page.FindAll("#target-look"));
        page.Find("[aria-label='Clear selection']").Click();
        Assert.Equal("Authored create prompt", page.Find("#image-prompt").GetAttribute("value"));
    }
}
