using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using lumibelle.Components.Shots;

namespace Lumibelle.Tests;

public sealed class DraftingEnvironmentTests
{
    private static readonly byte[] Picture = [1, 2, 3];
    private static (ShotPlanningRequest Request, DraftingEnvironmentSetup Setup, EnvironmentSourcePool Pool, Shot Shot) Fixture()
    {
        var script = ScriptFixtures.Document(); var asset = Guid.NewGuid();
        var candidate = new EnvironmentViewCandidate("image/" + Guid.NewGuid(), "Podium side", true, "Visible standing side", new() { AssetId = asset, MediaId = Guid.NewGuid() }, null, null, ReelViewSuggestions.Hash(Picture));
        var setup = new DraftingEnvironmentSetup { AssetId = asset, Constraints = "Riley is shorter than the podium. Do not invent a platform.", Views = [candidate] };
        var pool = new EnvironmentSourcePool(script.ProjectId, asset, "Plaza", "Stone courtyard", "source", [candidate], [], new Dictionary<string, byte[]> { [candidate.Id] = Picture });
        var environment = DraftingEnvironments.Snapshot(script.Blocks[0].Id, setup, pool);
        var request = new ShotPlanningRequest(new(script.ProjectId, script.ProjectId, 1, DateTimeOffset.UtcNow, script.Blocks), new() { ProjectId = script.ProjectId }, [script.Blocks[0].Id], 15, "Protect reactions", new(AiBackend.ClaudeCode, "opus", "Opus")) { CoverageOnly = true, Environments = [environment] };
        var shot = new Shot { SceneId = script.Blocks[0].Id, SourceBlockIds = [script.Blocks[0].Id], Description = "Mouse hesitates at the door", Duration = 4, Direction = new() { Intent = new() { Purpose = "Delay the answer" }, Staging = new() { Camera = "Side view" } } };
        return (request, setup, pool, shot);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void BothDraftPathsInspectTheSetWithoutAttachingGenerationReferences(bool single)
    {
        var (r, _, _, shot) = Fixture();
        if (single) r = r with { SingleShot = new(shot.Id, 0, [], SceneShotSummary.From(shot)) };
        var messages = ShotPlanner.BuildMessages(r, new Dictionary<string, byte[]> { [ReelViewSuggestions.Hash(Picture)] = Picture });
        Assert.Contains("contractVersion:5", messages[0].Text); Assert.Contains("Additional environment view needed", messages[0].Text);
        Assert.Contains("Riley is shorter", messages[1].Text);
        ShotPlanner.ValidateInspection(r, [Picture]);
        var strategy = new { sceneId = shot.SceneId, environmentAssessment = new { summary = "Preserve scale", claims = new[] { new EnvironmentSupportClaim("Standing side", ReferenceSupportStatus.Supported, [r.Environments[0].Views[0].Id]) } } };
        var parsed = ShotPlanner.Parse(JsonSerializer.Serialize(new { sceneStrategies = new[] { strategy }, shots = new[] { shot } }, AtomicJsonFile.Options), r);
        Assert.Null(parsed.Error); var result = Assert.Single(parsed.Shots);
        Assert.Equal(r.Environments[0].AssetId, result.EnvironmentAssetId); Assert.Empty(result.Images); Assert.Empty(result.Videos);
        Assert.Equal("Preserve scale", result.DraftingEnvironment!.Assessment.Summary);
    }
    [Fact] public void ClaimsCannotCiteAvailableButUnattachedPictures()
    {
        var (r, _, _, shot) = Fixture();
        var strategy = new { sceneId = shot.SceneId, environmentAssessment = new { claims = new[] { new EnvironmentSupportClaim("A platform exists", ReferenceSupportStatus.Supported, ["image/not-attached"]) } } };
        Assert.Contains("not attached", ShotPlanner.Parse(JsonSerializer.Serialize(new { sceneStrategies = new[] { strategy }, shots = new[] { shot } }, AtomicJsonFile.Options), r).Error);
        Assert.Throws<WorkspaceStoreException>(() => ShotPlanner.ValidateInspection(r, [[9]]));
    }
    [Fact] public void TextOnlyPilotCannotClaimToHaveSeenItsMetadataCandidates()
    {
        var (r, _, _, shot) = Fixture(); r = r with { InspectionImageHashes = [] };
        var strategy = new { sceneId = shot.SceneId, environmentAssessment = new { claims = new[] { new EnvironmentSupportClaim("Standing side", ReferenceSupportStatus.Supported, [r.Environments[0].Views[0].Id]) } } };
        Assert.NotNull(ShotPlanner.Parse(JsonSerializer.Serialize(new { sceneStrategies = new[] { strategy }, shots = new[] { shot } }, AtomicJsonFile.Options), r).Error);
        ShotPlanner.ValidateInspection(r, []);
        var parsed = ShotPlanner.Parse(JsonSerializer.Serialize(new { shots = new[] { shot } }, AtomicJsonFile.Options), r);
        Assert.Null(parsed.Error);
        Assert.Empty(parsed.Shots[0].DraftingEnvironment!.InspectedViews);
        Assert.Single(parsed.Shots[0].DraftingEnvironment!.Snapshot.Views); // Metadata remains captured, explicitly uninspected.
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void CapturedHashCitationResolvesOnlyToItsUniquelyAttachedSource(bool single)
    {
        var (r, _, _, shot) = Fixture();
        if (single) r = r with { SingleShot = new(shot.Id, 0, [], SceneShotSummary.From(shot)) };
        var view = r.Environments[0].Views[0];
        var alias = "image/" + view.Sha256;
        var strategy = new { sceneId = shot.SceneId, environmentAssessment = new { claims = new[] { new EnvironmentSupportClaim("Standing side", ReferenceSupportStatus.Supported, [alias]) } } };
        var raw = JsonSerializer.Serialize(new { sceneStrategies = new[] { strategy }, shots = new[] { shot } }, AtomicJsonFile.Options);
        var parsed = ShotPlanner.Parse(raw, r);
        Assert.Null(parsed.Error); Assert.Equal(raw, parsed.Raw);
        Assert.Equal(view.Id, Assert.Single(Assert.Single(parsed.Shots).DraftingEnvironment!.Assessment.Claims).CandidateIds.Single());
        Assert.Contains(parsed.DirectionNotes, note => note.Contains("media hash"));
        Assert.NotNull(ShotPlanner.Parse(raw, r with { InspectionImageHashes = [] }).Error);
        var duplicate = view with { Id = "image/" + Guid.NewGuid() };
        Assert.NotNull(ShotPlanner.Parse(raw, r with { Environments = [r.Environments[0] with { Views = [view, duplicate] }] }).Error);
        Assert.NotNull(ShotPlanner.Parse(raw.Replace(alias, "reel/other/" + view.Sha256), r).Error);
    }
    [Fact] public void IdenticalAttachmentsAreDeduplicatedAcrossScenesWithExplicitMappings()
    {
        var (r, _, _, _) = Fixture(); var second = r.Environments[0] with { SceneId = Guid.NewGuid() }; r = r with { Environments = [r.Environments[0], second] };
        var messages = ShotPlanner.BuildMessages(r, new Dictionary<string, byte[]> { [ReelViewSuggestions.Hash(Picture)] = Picture });
        Assert.Single(messages[1].Contents.OfType<Microsoft.Extensions.AI.DataContent>());
        Assert.Equal(2, JsonNode.Parse(messages[1].Text)!["sceneEnvironments"]!.AsArray().Count);
    }
    [Fact] public void OverrideWithoutSelectionNeverFallsBackToScenePicturesOrConstraints()
    {
        var (_, setup, _, shot) = Fixture(); var scene = new SceneReferenceSetup { Environment = setup };
        Assert.Same(setup, DraftingEnvironments.Effective(shot, scene));
        shot.EnvironmentAssetId = Guid.NewGuid(); var effective = DraftingEnvironments.Effective(shot, scene)!;
        Assert.Equal(shot.EnvironmentAssetId, effective.AssetId); Assert.Empty(effective.Views); Assert.Empty(effective.Constraints);
        shot.EnvironmentInspectionDisabled = true; Assert.Null(DraftingEnvironments.Effective(shot, scene));
    }
    [Fact] public void SemanticSetupChangesPreserveIntentAndCapturedHistory()
    {
        var (r, setup, _, shot) = Fixture(); shot.EnvironmentAssetId = setup.AssetId;
        shot.DraftingEnvironment = new(r.Environments[0], new() { Summary = "Keep the podium tall" });
        var scene = new SceneReferenceSetup { SceneId = shot.SceneId!.Value, Environment = setup };
        var document = new ShotDocument { ProjectId = r.Script.ProjectId, Shots = [shot], SceneSetups = [scene] };
        ShotDirections.Review(shot, null);
        Assert.True(ShotDirections.Freshness(document, shot).Staging);
        setup.Version = Guid.NewGuid(); Assert.True(ShotDirections.Freshness(document, shot).Staging);
        setup.Constraints = "A platform is now approved.";
        var freshness = ShotDirections.Freshness(document, shot);
        Assert.True(freshness.Intent); Assert.False(freshness.Staging); Assert.False(freshness.ReferenceSupport);
        Assert.Contains("Riley is shorter", shot.DraftingEnvironment.Snapshot.Constraints);
        Assert.Null(ShotDirections.Context(document, shot)!.Environment);
    }
    [Fact] public void ChangedOrMissingSelectedSourcesRequireReviewButNewUnselectedSourcesDoNot()
    {
        var (r, setup, pool, _) = Fixture();
        Assert.Equal(r.Environments[0].Fingerprint, DraftingEnvironments.Snapshot(r.SceneIds[0], setup, pool with { Candidates = [.. pool.Candidates, pool.Candidates[0] with { Id = "another" }] }).Fingerprint);
        Assert.Throws<WorkspaceStoreException>(() => DraftingEnvironments.Snapshot(r.SceneIds[0], setup, pool with { Candidates = [] }));
        Assert.Throws<WorkspaceStoreException>(() => DraftingEnvironments.Snapshot(r.SceneIds[0], setup, pool with { Candidates = [pool.Candidates[0] with { Sha256 = new string('a', 64) }] }));
    }
    [Fact] public void LiveEvidenceChecksOnlyInspectedSourcesAndSupportsCopiedIdentities()
    {
        var (r, _, pool, _) = Fixture();
        var view = pool.Candidates[0];
        // Use real candidate/resource identities rather than treating library membership as inspection.
        view = view with { Id = "image/" + view.Image!.MediaId };
        var accepted = new AcceptedDraftingEnvironment(r.Environments[0] with { Views = [view] }, new());
        pool = pool with { Candidates = [view] };
        Assert.Null(DraftingEnvironments.LiveSourceIssue(accepted, pool));
        Assert.Null(DraftingEnvironments.LiveSourceIssue(accepted, pool with { Candidates = [view, view with { Id = "unselected", Sha256 = "changed" }] }));
        Assert.NotNull(DraftingEnvironments.LiveSourceIssue(accepted, pool with { Guidance = "A newly authored landmark note" }));
        Assert.NotNull(DraftingEnvironments.LiveSourceIssue(accepted, pool with { Candidates = [] }));
        Assert.NotNull(DraftingEnvironments.LiveSourceIssue(accepted, pool with { Candidates = [view with { Notes = "Changed landmark" }] }));
        Assert.NotNull(DraftingEnvironments.LiveSourceIssue(accepted, pool with { Candidates = [view with { Image = view.Image! with { Crop = new() { Width = .5 } } }] }));
        Assert.Null(DraftingEnvironments.LiveSourceIssue(accepted with { InspectionImageHashes = [] }, pool with { Candidates = [] }));
        var mappedAsset = Guid.NewGuid(); var mappedImage = Guid.NewGuid();
        accepted.Resources = new() { [accepted.Snapshot.AssetId] = mappedAsset, [view.Image.MediaId] = mappedImage };
        Assert.Null(DraftingEnvironments.LiveSourceIssue(accepted, pool with { AssetId = mappedAsset,
            Candidates = [view with { Id = "image/" + mappedImage, Image = view.Image with { AssetId = mappedAsset, MediaId = mappedImage } }] }));
    }
    [Fact] public void EditedClaimsCannotAcquireEvidenceOutsideTheExactInspectionManifest()
    {
        var (r, _, _, _) = Fixture(); var view = r.Environments[0].Views[0];
        var unseen = view with { Id = "metadata-only", Sha256 = "uninspected" };
        var accepted = new AcceptedDraftingEnvironment(r.Environments[0] with { Views = [view, unseen] }, new() {
            Claims = [new("The bench is not identified", ReferenceSupportStatus.Inferred, [view.Id])]
        }) { InspectionImageHashes = [view.Sha256] };
        DraftingEnvironments.ValidateAssessment(accepted);
        accepted.Assessment.Claims[0] = accepted.Assessment.Claims[0] with { CandidateIds = [unseen.Id] };
        Assert.Throws<WorkspaceStoreException>(() => DraftingEnvironments.ValidateAssessment(accepted));
        accepted.Assessment.Claims[0] = new("Bench confirmed", ReferenceSupportStatus.Supported, []);
        Assert.Throws<WorkspaceStoreException>(() => DraftingEnvironments.ValidateAssessment(accepted));
        accepted.Assessment.Claims.Clear(); DraftingEnvironments.ValidateAssessment(accepted);
    }
    [Fact] public async Task BoundedRoundsConsiderEverySourceAndRejectForeignWinners()
    {
        var pool = Enumerable.Range(0, 65).Select(i => i.ToString()).ToArray(); var inspected = new HashSet<string>(); var calls = new List<string[]>();
        var result = await EnvironmentInspectionRunner.RunAsync<string>(pool, (batch, final, round, ct) => {
            Assert.InRange(batch.Count, 1, 24); calls.Add(batch.ToArray());
            return Task.FromResult(new EnvironmentInspectionBatch<string>(batch, [batch[0]], final, "done"));
        }, inspected, TestContext.Current.CancellationToken);
        Assert.Equal("done", result); Assert.Equal(65, inspected.Count); Assert.Equal(4, calls.Count);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => EnvironmentInspectionRunner.RunAsync<string>(pool,
            (batch, final, round, ct) => Task.FromResult(new EnvironmentInspectionBatch<string>(batch, ["foreign"], final, "bad")), new HashSet<string>(), TestContext.Current.CancellationToken));
    }
    [Fact] public void SelectionBatchCannotReturnCandidatesSeenInAnotherBatch()
    {
        var (_, _, pool, _) = Fixture(); var r = new SceneEnvironmentSelectionRequest(pool.ProjectId, Guid.NewGuid(), null, "Scene", "", "", pool.AssetId, pool.AssetName, pool.Guidance, pool.Candidates, false);
        Assert.Throws<WorkspaceStoreException>(() => DraftingEnvironments.ParseSelection("{\"summary\":\"x\",\"views\":[{\"candidateId\":\"other-batch\",\"reason\":\"nice\"}],\"warnings\":[]}", r));
    }
    [Fact] public void CompleteCapacityIncludesAllScenesTextPicturesAndReply()
    {
        var (r, _, _, _) = Fixture();
        var messages = ShotPlanner.BuildMessages(r, new Dictionary<string, byte[]> { [ReelViewSuggestions.Hash(Picture)] = Picture }).Select(AiTextMessage.Capture).ToArray();
        var request = new AiTextJobRequest(3, AiJobKind.ShotPlanning, r.Selection, false, new() { MaxOutputTokens = 2048 }, "v5", .3f, 0, JsonSerializer.SerializeToElement(r, AtomicJsonFile.Options), messages);
        DraftingEnvironments.ValidateCapacity(request, 100000);
        Assert.Throws<WorkspaceStoreException>(() => DraftingEnvironments.ValidateCapacity(request, 2048));
        Assert.Throws<WorkspaceStoreException>(() => ShotPlanner.ValidateInspection(r with { InspectionImageHashes = ["uncaptured"] }, [Picture]));
        var singleCost = ComfyTextCapacity.TextTokens(string.Join("\n", messages.SelectMany(m => m.Parts).Select(p => p.Text ?? ""))) + 1536 + 2048;
        DraftingEnvironments.ValidateCapacity(request, singleCost);
        var scene = ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. SECOND COURTYARD — DAY");
        byte[] anotherPicture = [4, 5, 6];
        var view = r.Environments[0].Views[0] with { Id = "second-view", Sha256 = ReelViewSuggestions.Hash(anotherPicture) };
        var environment = r.Environments[0] with { SceneId = scene.Id, Views = [view] };
        var multi = r with { SceneIds = [.. r.SceneIds, scene.Id], Script = r.Script with { Blocks = [.. r.Script.Blocks, scene, ScriptBlock.Create(ScriptBlockKind.Action, "A pursuit changes direction.")] }, Environments = [.. r.Environments, environment] };
        var multiMessages = ShotPlanner.BuildMessages(multi, new Dictionary<string, byte[]> { [ReelViewSuggestions.Hash(Picture)] = Picture, [view.Sha256] = anotherPicture }).Select(AiTextMessage.Capture).ToArray();
        Assert.Equal(2, multiMessages.SelectMany(m => m.Parts).Count(p => p.Image is not null));
        Assert.Throws<WorkspaceStoreException>(() => DraftingEnvironments.ValidateCapacity(request with { Task = JsonSerializer.SerializeToElement(multi, AtomicJsonFile.Options), Messages = multiMessages }, singleCost));
    }
    [Fact] public async Task CanceledInspectionRetainsItsPartialLedgerWithoutCompleting()
    {
        using var cancel = new CancellationTokenSource(); var inspected = new HashSet<string>();
        var pool = Enumerable.Range(0, 50).Select(i => i.ToString()).ToArray(); var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EnvironmentInspectionRunner.RunAsync<string>(pool, (batch, final, round, ct) => {
            if (++calls == 2) { cancel.Cancel(); ct.ThrowIfCancellationRequested(); }
            return Task.FromResult(new EnvironmentInspectionBatch<string>(batch, [batch[0]], final, "done"));
        }, inspected, cancel.Token));
        Assert.Equal(24, inspected.Count);
    }
    [Fact] public void HistoricalTargetFingerprintsAndNoEnvironmentPayloadsStayStable()
    {
        var shot = new Shot { Title = "Existing", Description = "Holds" };
        var expected = ShotDirections.Hash(new { shot.Title, shot.Description, shot.Duration, shot.SceneId, shot.SourceBlockIds, shot.SourceExcerpt, shot.Dialogue, shot.Characters, shot.Atmosphere, shot.Music, shot.Direction, shot.DirectionStrategyId });
        Assert.Equal(expected, ShotDirections.Target(shot));
        Assert.DoesNotContain("draftingEnvironment", JsonSerializer.Serialize(shot, AtomicJsonFile.Options));
        shot.EnvironmentInspectionDisabled = true; Assert.NotEqual(expected, ShotDirections.Target(shot));
    }
    [Fact] public void AssessmentTextListsRemainReviewableAndDoNotDiscardSceneStrategy()
    {
        var (r, _, _, shot) = Fixture();
        var response = new { sceneStrategies = new[] { new { sceneId = shot.SceneId, dramaticProgression = "Reveal the obstruction", environmentAssessment = new { summary = "The podium blocks the face", assumptions = new[] { "Reaction angle proposed", "Chair placement unknown" }, missingCoverage = new[] { "Reverse needed" }, claims = Array.Empty<EnvironmentSupportClaim>() } } }, shots = new[] { shot } };
        var parsed = ShotPlanner.Parse(JsonSerializer.Serialize(response, AtomicJsonFile.Options), r);
        Assert.Null(parsed.Error); Assert.Single(parsed.Strategies);
        Assert.Contains("Chair placement unknown", parsed.Shots[0].DraftingEnvironment!.Assessment.Assumptions);
    }
    [Fact] public void RedraftingWithAnotherSetDoesNotLeakItsEarlierAssessmentOrBlocking()
    {
        var (r, _, _, shot) = Fixture();
        var previous = new AcceptedDraftingEnvironment(r.Environments[0] with { AssetId = Guid.NewGuid(), Constraints = "OLD SET PLATFORM" }, new() { Summary = "OLD SET GEOMETRY" });
        var current = SceneShotSummary.From(shot) with { DirectionContext = new(null, new() { Purpose = "Keep the surprise" }, new() { Positions = "OLD SET BLOCKING" }, null, null, []) { Environment = previous } };
        r = r with { SingleShot = new(shot.Id, 1, [current], current) };
        var text = ShotPlanner.BuildMessages(r, new Dictionary<string, byte[]> { [ReelViewSuggestions.Hash(Picture)] = Picture })[1].Text;
        Assert.DoesNotContain("OLD SET", text); Assert.Contains("Keep the surprise", text);
    }
}
