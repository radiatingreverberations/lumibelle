using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ShotDirectionTests
{
    private static (ShotPlanningRequest Request, Shot Shot, DirectionStrategy Strategy) Fixture()
    {
        var doc = ScriptFixtures.Document();
        var strategy = new DirectionStrategy { SceneId = doc.Blocks[0].Id, DramaticProgression = "A welcome becomes a test.", VisualProgression = "Shared view, then isolate the mouse.", AudienceInformation = "Delay the caller's identity.", SpatialAnchors = "Mouse faces the door." };
        var shot = new Shot { SceneId = strategy.SceneId, SceneTitle = "Kitchen", SourceBlockIds = [doc.Blocks[0].Id], Description = "Mouse hesitates at the door.", Duration = 4,
            Characters = [new(Guid.NewGuid(), "Mouse")], Dialogue = [new() { Speaker = "Mouse", Text = "You called?" }], DirectionStrategyId = strategy.Id,
            Direction = new() { Intent = new() { Purpose = "Test the welcome", Reason = "The audience must doubt the invitation." }, Staging = new() { Positions = "Beside the door", Facing = "Toward the caller", Camera = "Static side view" },
                Editorial = new() { CutOut = "When the caller responds", Timing = "Wait for hesitation" }, Advanced = new() { Assumptions = "Door location is proposed", LongTakeIdea = "A 25-second exchange could later justify manual extension." } } };
        ShotDirections.Review(shot, strategy);
        var request = new ShotPlanningRequest(new(doc.ProjectId, doc.ProjectId, 1, DateTimeOffset.UtcNow, doc.Blocks), new() { ProjectId = doc.ProjectId }, [strategy.SceneId], 15, "Protect the pause.", new(AiBackend.ClaudeCode, "opus", "Opus")) { CoverageOnly = true };
        return (request, shot, strategy);
    }
    [Theory]
    [InlineData(DirectingStyle.Auto)] [InlineData(DirectingStyle.PrestigeDrama)] [InlineData(DirectingStyle.KineticAction)]
    [InlineData(DirectingStyle.EnsembleClassical)] [InlineData(DirectingStyle.PerformanceComedy)]
    public void BothPathsUseTheSharedStyleAndSingleRequestContract(DirectingStyle style)
    {
        var (request, shot, strategy) = Fixture();
        foreach (var single in new[] { false, true }) {
            var r = request with { Style = style, Strategies = single ? [strategy] : [], SingleShot = single ? new(shot.Id, 0, [], SceneShotSummary.From(shot)) : null };
            var messages = ShotPlanner.BuildMessages(r);
            Assert.Equal(2, messages.Count); Assert.Contains(ShotDirections.StyleGuidance(style), messages[0].Text);
            Assert.Contains("contractVersion:4", messages[0].Text); Assert.Contains("strategy.approach", messages[0].Text); Assert.Contains("rather than classifying its genre", messages[0].Text); Assert.Contains("Authored instructions override", messages[0].Text);
            Assert.Contains("Protect the pause", messages[1].Text); Assert.NotNull(JsonNode.Parse(messages[1].Text));
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void VersionedAndHistoricalCoverageRemainReadable(bool historical)
    {
        var (request, shot, strategy) = Fixture();
        var second = shot.Copy(); second.Title = "Response"; second.Direction = null;
        var raw = historical ? JsonSerializer.Serialize(new[] { shot, second }, AtomicJsonFile.Options)
            : JsonSerializer.Serialize(new { contractVersion = 4, sceneStrategies = new[] { strategy }, shots = new[] { shot, second } }, AtomicJsonFile.Options);
        var parsed = ShotPlanner.Parse(raw, request);
        Assert.Null(parsed.Error); Assert.Equal(2, parsed.Shots.Count);
        if (historical) { Assert.Empty(parsed.Strategies); Assert.NotEmpty(parsed.DirectionNotes); }
        else {
            var saved = Assert.Single(parsed.Strategies); Assert.NotEqual(strategy.Id, saved.Id);
            Assert.Equal(request.Script.Id, saved.SourceId); Assert.Equal(64, saved.SourceFingerprint.Length);
            Assert.All(parsed.Shots, s => Assert.Equal(saved.Id, s.DirectionStrategyId));
        }
        Assert.Contains(parsed.DirectionNotes, n => n.Contains("Supplemental direction"));
        Assert.Contains("25-second", parsed.Shots[0].Direction!.Advanced.LongTakeIdea);
        Assert.Equal(4, parsed.Shots[0].Duration);
    }
    [Fact] public void InvalidSupplementalMetadataDoesNotLoseValidCoverage()
    {
        var (request, shot, _) = Fixture();
        var node = JsonSerializer.SerializeToNode(shot, AtomicJsonFile.Options)!;
        node["direction"] = "not structured";
        var result = ShotPlanner.Parse(new JsonObject { ["sceneStrategies"] = new JsonArray("not structured"), ["shots"] = new JsonArray(node) }.ToJsonString(), request);
        Assert.Null(result.Error); Assert.Single(result.Shots); Assert.Null(result.Shots[0].Direction); Assert.NotEmpty(result.DirectionNotes);
    }
    [Fact] public void LongTakeMetadataNeverRelaxesDurationValidation()
    {
        var (request, shot, strategy) = Fixture(); shot.Duration = 16;
        Assert.NotNull(ShotPlanner.Parse(JsonSerializer.Serialize(new { sceneStrategies = new[] { strategy }, shots = new[] { shot } }, AtomicJsonFile.Options), request).Error);
    }
    [Fact] public void SingleDraftConsumesExistingStrategyWithoutReplacingItsIdentity()
    {
        var (request, shot, strategy) = Fixture();
        request = request with { Strategies = [strategy], SingleShot = new(shot.Id, 0, [], SceneShotSummary.From(shot)) };
        var unrelated = strategy with { Id = Guid.NewGuid(), DramaticProgression = "A different strategy" };
        var result = ShotPlanner.Parse(JsonSerializer.Serialize(new { sceneStrategies = new[] { unrelated }, shots = new[] { shot } }, AtomicJsonFile.Options), request);
        Assert.Null(result.Error); Assert.Equal(strategy.Id, result.Shots[0].DirectionStrategyId);
        Assert.Equal(strategy.DramaticProgression, Assert.Single(result.Strategies).DramaticProgression);
    }
    [Fact] public void ReferenceChangesPreserveIntentAndOpeningStaging()
    {
        var (_, shot, strategy) = Fixture(); shot.EnvironmentAssetId = Guid.NewGuid(); shot.Images.Add(new() { Crop = new() { Width = .5 } });
        var freshness = ShotDirections.Freshness(shot, strategy);
        Assert.True(freshness.Intent); Assert.True(freshness.Staging); Assert.True(freshness.Editorial); Assert.False(freshness.ReferenceSupport);
        Assert.NotNull(ShotDirections.Context(shot, strategy)!.Intent);
    }
    [Fact] public void DialogueChangesDoNotEraseOpeningGeometry()
    {
        var (_, shot, strategy) = Fixture(); shot.Dialogue[0].Text = "Who called?";
        var freshness = ShotDirections.Freshness(shot, strategy);
        Assert.False(freshness.Intent); Assert.True(freshness.Staging); Assert.False(freshness.Editorial); Assert.True(freshness.Continuity);
        var context = ShotDirections.Context(shot, strategy)!;
        Assert.Null(context.Intent); Assert.NotNull(context.ProposedStaging); Assert.Null(context.Editorial);
        Assert.Equal("Test the welcome", shot.Direction!.Intent.Purpose);
    }
    [Fact] public void CastAndDurationAndNeighbourChangesAffectOnlyDependentGroups()
    {
        var (_, shot, strategy) = Fixture();
        shot.Characters.Add(new(Guid.NewGuid(), "Caller"));
        var cast = ShotDirections.Freshness(shot, strategy); Assert.True(cast.Intent); Assert.False(cast.Staging); Assert.False(cast.Continuity); Assert.True(cast.Editorial);
        ShotDirections.Review(shot, strategy); shot.Duration = 8;
        var duration = ShotDirections.Freshness(shot, strategy); Assert.True(duration.Intent); Assert.True(duration.Staging); Assert.False(duration.Editorial);
        ShotDirections.Review(shot, strategy);
        var neighbours = ShotDirections.Freshness(shot, strategy, [new("New arrival", 4, "The caller enters.", [], [])]);
        Assert.True(neighbours.Intent); Assert.True(neighbours.Staging); Assert.False(neighbours.Editorial); Assert.False(neighbours.Continuity);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void StoryAndStrategyChangesExcludeStalePrescriptions(bool changeStrategy)
    {
        var (_, shot, strategy) = Fixture();
        if (changeStrategy) strategy.VisualProgression = "A new visual plan"; else shot.Description = "Mouse departs.";
        var context = ShotDirections.Context(shot, strategy)!;
        Assert.Null(context.Intent); Assert.Null(context.ProposedStaging); Assert.Null(context.Editorial); Assert.Null(context.Advanced);
        Assert.NotEmpty(context.Limitations); Assert.NotNull(shot.Direction);
    }
    [Fact] public void MetadataFreeFingerprintsStayCompatibleAndDirectionChangesAreVisible()
    {
        var (_, shot, strategy) = Fixture();
        var copy = ProductionPolicy.CoverageCopy(shot); Assert.Equal(shot.Direction, copy.Direction); Assert.Equal(strategy.Id, copy.DirectionStrategyId);
        var before = ProductionPolicy.SourceFingerprint(shot); var target = ShotDirections.Target(shot);
        shot.Direction!.Intent.Reason = "New authored reason";
        Assert.NotEqual(before, ProductionPolicy.SourceFingerprint(shot)); Assert.NotEqual(target, ShotDirections.Target(shot));
        shot.Direction = null; shot.DirectionStrategyId = null;
        Assert.Null(ShotDirections.Fingerprint(new(), shot));
    }
    [Fact] public void NeighbourDirectionChangesInvalidateCutsAndContinuityWithoutErasingIntent()
    {
        var (_, shot, strategy) = Fixture(); var neighbour = shot.Copy(); neighbour.Id = Guid.NewGuid();
        var document = new ShotDocument { Shots = [neighbour, shot], DirectionStrategies = [strategy] };
        ShotDirections.Review(shot, strategy, ShotDirections.Neighbours(document, shot));
        Assert.True(ShotDirections.Freshness(shot, strategy, ShotDirections.Neighbours(document, shot)).Editorial);
        neighbour.Direction!.Editorial.CutOut = "Hold until the hand opens";
        var current = ShotDirections.Freshness(shot, strategy, ShotDirections.Neighbours(document, shot));
        Assert.True(current.Intent); Assert.True(current.Staging); Assert.False(current.Editorial); Assert.False(current.Continuity);
    }
    [Fact] public void CopyingReferencesDoesNotTransferDirection()
    {
        var (_, source, _) = Fixture(); var destination = new Shot { Direction = new() { Intent = new() { Purpose = "Keep my purpose" } } };
        var copied = ReferenceCopies.Into(source, destination, new() { ProjectId = Guid.NewGuid() });
        Assert.Equal("Keep my purpose", copied.Inputs.Direction!.Intent.Purpose);
        Assert.Null(copied.Inputs.DirectionStrategyId);
        Assert.NotEqual(source.Direction!.Intent.Purpose, copied.Inputs.Direction.Intent.Purpose);
    }
    [Fact] public void SupportedClaimsNeedCapturedIdentitiesAndEvidence()
    {
        EnvironmentStagingPlan Plan(ReferenceSupportStatus status, params string[] ids) => new("Face the podium.", "Static side view.") { ReferenceSupport = [new("Platform beside podium", status, ids)] };
        EnvironmentViews.ValidateSupport(Plan(ReferenceSupportStatus.Inferred), ["a"]);
        EnvironmentViews.ValidateSupport(Plan(ReferenceSupportStatus.Supported, "a"), ["a"]);
        Assert.Throws<WorkspaceStoreException>(() => EnvironmentViews.ValidateSupport(Plan(ReferenceSupportStatus.Supported), ["a"]));
        Assert.Throws<WorkspaceStoreException>(() => EnvironmentViews.ValidateSupport(Plan(ReferenceSupportStatus.Conflicting, "invented"), ["a"]));
    }
}

public sealed partial class ShotTests
{
    [Fact] public async Task ReviewedStrategiesPersistOnceAndRecoverWithoutRewritingExistingShots()
    {
        var (project, _, store, _) = Fixture();
        var original = Ready(); var oldStrategy = new DirectionStrategy { SceneId = original.SceneId!.Value, DramaticProgression = "Original intent" };
        original.DirectionStrategyId = oldStrategy.Id;
        var saved = await store.SaveDirectedWorkspaceAsync(project.Id, [original], [], [oldStrategy], 0, ct: _ct);
        var next = Ready(); next.SceneId = original.SceneId;
        var strategy = new DirectionStrategy { SceneId = next.SceneId!.Value, DramaticProgression = "New breakdown" }; next.DirectionStrategyId = strategy.Id;
        saved = await store.ApplyDirectedPlanningAsync(project.Id, Guid.NewGuid(), [next], [strategy], saved.Revision, _ct);
        Assert.Equal(2, saved.DirectionStrategies.Count); Assert.Equal(oldStrategy.Id, saved.Shots[0].DirectionStrategyId);
        var loaded = await store.LoadAsync(project.Id, _ct); Assert.Equal(2, loaded.DirectionStrategies.Count);
        var recovered = await store.RecoverAsync(project.Id, saved.Recovery[0].Id, saved.Revision, _ct);
        Assert.Equal(oldStrategy.Id, Assert.Single(recovered.DirectionStrategies).Id); Assert.Single(recovered.Shots);
    }
}
