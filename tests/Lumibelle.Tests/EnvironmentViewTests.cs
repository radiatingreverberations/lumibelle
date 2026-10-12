using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed class EnvironmentViewTests
{
    private static readonly EnvironmentStagingPlan Plan = new("Riley stands at the podium facing across it toward the judge; she steps sideways to the passage and returns.",
        "Static side view includes the podium, Riley and the guard at the passage.", "Facing the judge is inferred from the court scene.", "Passage depth is not established.");
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private static (EnvironmentViewRequest Request, AssetLibrary Library) Fixture(int pictures = 1)
    {
        var environment = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Plaza", Category = AssetCategory.Environment, Description = "Stone plaza with a podium" };
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [environment] };
        var shot = new Shot { EnvironmentAssetId = environment.Id, Duration = 5, Description = "Reverse toward the gate", Images = Enumerable.Range(0, pictures).Select(i => new ShotImageBinding {
            AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Character " + i, Crop = new() { Width = .5 }
        }).ToList() };
        var pool = new List<EnvironmentViewCandidate>();
        for (var n = 0; n < 2; n++) {
            var media = new ReferenceVideoMedia(Guid.NewGuid(), new('a', 64), 100, 640, 360, 120, 24, 5, false);
            var frames = Enumerable.Range(0, 3).Select(i => new ReelKeyframe { Frame = new(media.Id, media.Sha256, i * 24, i),
                Name = $"Reel {n} view {i}", Notes = "Visible gate beyond podium", Crop = new() { Width = .75 } }).ToList();
            var reel = new AssetReferenceReel { AssetId = environment.Id, Media = media, Name = "Circuit " + n, UseGuidance = "Reusable plaza layout", Keyframes = new() { Frames = frames } };
            library.Reels.Add(reel);
            pool.AddRange(frames.Select(f => new EnvironmentViewCandidate($"reel/{reel.Id}/{f.Id}", f.Name!, false, f.Notes, null, reel, f, ReelViewSuggestions.Hash(Png))));
        }
        var request = new EnvironmentViewRequest(1, library.ProjectId, Guid.NewGuid(), Guid.NewGuid(), environment.Id, environment.Name,
            environment.Description, shot, "Face the gate", "EXT. PLAZA", ["Previous shot: looking toward the judge"],
            EnvironmentViews.Context(shot, "Face the gate"), EnvironmentViews.Sources(library, environment.Id), pool,
            pool.Select(c => c.Id).ToArray(), [], Math.Max(1, 9 - pictures), true, []);
        return (request, library);
    }
    [Fact] public void PicksIndividualViewsAcrossReelsWithoutChangingLibraryOrManualReferences()
    {
        var (r, library) = Fixture(); var ids = new[] { r.Pool[3].Id, r.Pool[1].Id };
        var draft = EnvironmentViews.Stage(new(r, ids, [], false), r.Shot, r.DirectingNotes, library);
        Assert.Equal(2, draft.Videos.Count); Assert.Equal(3, ResolvedReferences.For(draft).Pictures.Count);
        Assert.Equal(0, ReferenceVideos.ReelSlotCount(draft)); Assert.Empty(ResolvedReferences.For(draft).Videos);
        Assert.Equal(r.Shot.Images.Single().Crop, draft.Images.Single().Crop);
        Assert.Equal("Reel 1 view 0", draft.Videos[0].Keyframes!.Frames.Single().Name);
        Assert.Equal(.75, draft.Videos[0].Keyframes!.Frames.Single().Crop!.Width);
        Assert.All(draft.Videos, v => { Assert.Equal(r.RunId, v.EnvironmentSelectionId); Assert.False(v.UseSoundtrack); });
        Assert.Empty(r.Shot.Videos); Assert.All(library.Reels, x => Assert.Equal(3, x.Keyframes!.Frames.Count));
    }
    [Fact] public void ChoosingLibraryAndImageIsStagedUntilApplyWithConsistentInferredUsage()
    {
        var (r, library) = Fixture();
        r.Shot.EnvironmentAssetId = null;
        var image = new ShotImageBinding { AssetId = r.AssetId, MediaId = Guid.NewGuid(), Name = "Plaza", InferUsage = true, Use = ShotImageUse.Environment };
        var candidate = new EnvironmentViewCandidate("image/plaza", image.Name, true, "", image, null, null, ReelViewSuggestions.Hash(Png));
        r = r with { Pool = [candidate], Candidates = [candidate.Id], ContextFingerprint = EnvironmentViews.Context(r.Shot, r.DirectingNotes) };
        EnvironmentViews.Validate(r);
        var draft = EnvironmentViews.Stage(new(r, [candidate.Id], [], false), r.Shot, r.DirectingNotes, library);
        Assert.Null(r.Shot.EnvironmentAssetId); Assert.Single(r.Shot.Images);
        Assert.Equal(r.AssetId, draft.EnvironmentAssetId); Assert.Equal(2, draft.Images.Count);
        Assert.True(draft.Images[1].InferUsage); Assert.Null(draft.Images[1].Use); Assert.Null(draft.Images[1].Purpose);
        var changed = r.Shot.Copy(); changed.EnvironmentAssetId = Guid.NewGuid();
        Assert.Throws<WorkspaceStoreException>(() => EnvironmentViews.Stage(new(r, [candidate.Id], [], false), changed, r.DirectingNotes, library));
    }
    [Fact] public void ReferenceSupportRetainsCapturedEvidenceAndQualifiesUnselectedPictures()
    {
        var (r, library) = Fixture();
        var selected = r.Pool[0]; var unselected = r.Pool[1];
        var strategy = new DirectionStrategy { SceneId = Guid.NewGuid(), DramaticProgression = "Isolation becomes resistance" };
        r.Shot.DirectionStrategyId = strategy.Id;
        r.Shot.Direction = new() { Intent = new() { Purpose = "Keep the accusation understandable" } };
        lumibelle.Services.Shots.ShotDirections.Review(r.Shot, strategy);
        r = r with { ContextFingerprint = EnvironmentViews.Context(r.Shot, r.DirectingNotes), DirectionContext = lumibelle.Services.Shots.ShotDirections.Context(r.Shot, strategy) };
        var plan = Plan with { ReferenceSupport = [new("Podium visible", ReferenceSupportStatus.Supported, [selected.Id]), new("Gate beyond podium", ReferenceSupportStatus.Supported, [unselected.Id])] };
        var draft = EnvironmentViews.Stage(new(r, [selected.Id], [], false) { Staging = plan }, r.Shot, r.DirectingNotes, library);
        var staging = draft.EnvironmentStaging!;
        Assert.Equal(ReferenceSupportStatus.Supported, staging.Plan.ReferenceSupport![0].Status);
        Assert.Equal(ReferenceSupportStatus.Inferred, staging.Plan.ReferenceSupport[1].Status);
        Assert.Equal(selected.Sha256, staging.Evidence!.Single(e => e.CandidateId == selected.Id).Sha256);
        Assert.Equal(selected.Keyframe!.Crop, staging.Evidence!.Single(e => e.CandidateId == selected.Id).Crop);
        Assert.Null(EnvironmentViews.StagingIssue(draft, r.DirectingNotes, "", r.DirectionContext));
        strategy.VisualProgression = "Isolate only after the answer";
        Assert.NotNull(EnvironmentViews.StagingIssue(draft, r.DirectingNotes, "", lumibelle.Services.Shots.ShotDirections.Context(draft, strategy)));
        draft.Direction!.Intent.Purpose = "A changed purpose";
        Assert.NotNull(EnvironmentViews.StagingIssue(draft, r.DirectingNotes, ""));
    }
    [Fact] public void PictureTenCannotApplyAndNineExistingPicturesLeaveNoBudget()
    {
        var (r, library) = Fixture(8);
        Assert.Throws<WorkspaceStoreException>(() => EnvironmentViews.Stage(new(r, r.Pool.Take(2).Select(c => c.Id).ToArray(), [], false), r.Shot, r.DirectingNotes, library));
        Assert.Equal(0, EnvironmentViews.Available(Fixture(9).Request.Shot, []));
    }
    [Fact] public void RefModUsesVideoBudgetAndRequiresTwoViewsPerReel()
    {
        var (r, library) = Fixture(9); var ids = r.Pool.Take(2).Select(c => c.Id).ToArray();
        var draft = EnvironmentViews.Stage(new(r, ids, [], true), r.Shot, r.DirectingNotes, library);
        Assert.Equal(9, ResolvedReferences.For(draft).Pictures.Count); Assert.Equal(ReelVisuals.RefMod, draft.Videos.Single().EffectiveVisuals);
        Assert.Throws<WorkspaceStoreException>(() => EnvironmentViews.Stage(new(r, [ids[0]], [], true), r.Shot, r.DirectingNotes, library));
    }
    [Fact] public void CannotSilentlyRemoveCharacterReferencesOrAlterSameReel()
    {
        var (r, library) = Fixture();
        Assert.Throws<WorkspaceStoreException>(() => EnvironmentViews.Stage(new(r, [r.Pool[0].Id], [r.Shot.Images[0].Id], false), r.Shot, r.DirectingNotes, library));
        var prior = new ShotVideoBinding { Media = r.Pool[0].Reel!.Media, Keyframes = new() { Frames = [r.Pool[0].Keyframe!] }, Visuals = ReelVisuals.Keyframes, OwnerCategory = AssetCategory.Environment };
        r.Shot.Videos.Add(prior); r = r with { ContextFingerprint = EnvironmentViews.Context(r.Shot, r.DirectingNotes) };
        Assert.Throws<WorkspaceStoreException>(() => EnvironmentViews.Stage(new(r, [r.Pool[1].Id], [], false), r.Shot, r.DirectingNotes, library));
        var draft = EnvironmentViews.Stage(new(r, [r.Pool[1].Id], [prior.Id], false), r.Shot, r.DirectingNotes, library);
        Assert.Equal(r.Pool[1].Keyframe!.Id, draft.Videos.Single().Keyframes!.Frames.Single().Id);
    }
    [Fact] public void StaleSetupOrSourceCannotApply()
    {
        var (r, library) = Fixture(); var edited = r.Shot.Copy(); edited.Description = "New framing";
        Assert.Throws<WorkspaceStoreException>(() => EnvironmentViews.Stage(new(r, [r.Pool[0].Id], [], false), edited, r.DirectingNotes, library));
        library = library with { Assets = [library.Assets[0] with { Description = "Changed geometry notes" }] };
        Assert.Throws<WorkspaceStoreException>(() => EnvironmentViews.Stage(new(r, [r.Pool[0].Id], [], false), r.Shot, r.DirectingNotes, library));
    }
    [Fact] public void InspectsActualPicturesAndIncludesContinuityWithoutPrescribingReelMotion()
    {
        var (r, _) = Fixture(); var messages = EnvironmentViews.Messages(r, r.Candidates.Select(_ => Png).ToArray());
        Assert.Equal(6, messages[1].Contents.OfType<DataContent>().Count());
        Assert.Contains("Previous shot", messages[1].Text); Assert.Contains("Face the gate", messages[1].Text);
        Assert.Contains("must not become target-shot camera movement", messages[0].Text);
        Assert.Contains("ceiling, not a target", messages[0].Text);
        Assert.Throws<WorkspaceStoreException>(() => EnvironmentViews.Messages(r, [[1], Png, Png, Png, Png, Png]));
    }
    [Fact] public void CapturedRequestsValidateTargetsIdsHashesAndTypedResponses()
    {
        var (r, _) = Fixture(); var messages = EnvironmentViews.Messages(r, r.Candidates.Select(_ => Png).ToArray());
        var model = new TextModelReference(AiBackend.OpenRouter, "vision", "Vision");
        var request = new AiTextJobRequest(2, AiJobKind.EnvironmentViewSelection, model, true, new() { OpenRouterModel = "vision", HasOpenRouterKey = true }, EnvironmentViews.Profile,
            .3f, 42, JsonSerializer.SerializeToElement(r, AtomicJsonFile.Options), messages.Select(AiTextMessage.Capture).ToArray());
        var job = new AiJobHeader { Id = Guid.NewGuid(), Kind = request.Kind, Backend = model.Backend, Target = new(r.ProjectId, ShotId: r.Shot.Id, CompositionId: r.CompositionId),
            ProjectName = "P", TargetName = "Plaza", OriginTabId = Guid.NewGuid(), RequestFingerprint = "test" };
        job.Target.Validate(job.Kind); Assert.Contains("environment-views", job.Target.LockKey(job.Kind));
        Assert.Contains("setupId=", job.ReviewUrl);
        AiTextJobHandler.Read(job, JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options));
        AiTextJobHandler.Read(job, JsonSerializer.SerializeToElement(request with { Profile = "environment-views-v1" }, AtomicJsonFile.Options));
        var reply = JsonSerializer.Serialize(new { summary = "Gate reverse", views = new[] { new { candidateId = r.Candidates[0], reason = "Gate background matches the direction" } }, warnings = Array.Empty<string>() });
        Assert.Null(AiTextResults.Parse(request, reply, "stop").Error);
        Assert.Throws<WorkspaceStoreException>(() => EnvironmentViews.Parse(reply.Replace(r.Candidates[0], "imaginary"), r));
        Assert.Throws<WorkspaceStoreException>(() => AiTextJobHandler.Read(job with { Target = job.Target with { CompositionId = Guid.NewGuid() } }, JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options)));
    }
    [Fact] public void OldShotsKeepTheirShapeAndEnvironmentSurvivesCopiesAndChangesPromptFreshness()
    {
        Assert.DoesNotContain("environmentAssetId", JsonSerializer.Serialize(new Shot(), AtomicJsonFile.Options));
        var (r, _) = Fixture(0); var copied = r.Shot.Copy(); Assert.Equal(r.AssetId, copied.EnvironmentAssetId);
        Assert.Equal(r.AssetId, ProductionPolicy.CoverageCopy(copied).EnvironmentAssetId);
        var before = PromptReferenceFreshness.Fingerprint(copied, []); copied.EnvironmentAssetId = null;
        Assert.NotEqual(before, PromptReferenceFreshness.Fingerprint(copied, []));
        Assert.Equal(17, (int)AiJobKind.ReelViewSuggestion); Assert.Equal(18, (int)AiJobKind.EnvironmentViewSelection);
    }
    [Fact] public void SelectionCanReportMissingCoverageWithoutFillingSlotsAndCannotRepeatCandidates()
    {
        var (r, _) = Fixture();
        var empty = EnvironmentViews.Parse("{\"summary\":\"No reliable reverse view\",\"views\":[],\"warnings\":[\"The platform changes design; add a matching reverse view.\"]}", r);
        Assert.Empty(empty.Views); Assert.Single(empty.Warnings);
        var id = r.Candidates[0];
        var choice = new { candidateId = id, reason = "Readable gate background" };
        Assert.Throws<WorkspaceStoreException>(() => EnvironmentViews.Parse(JsonSerializer.Serialize(new { summary = "Duplicate", views = new[] { choice, choice }, warnings = Array.Empty<string>() }), r));
        var single = EnvironmentViews.Parse(JsonSerializer.Serialize(new { summary = "One clear approved view is sufficient", views = new[] { choice }, warnings = Array.Empty<string>() }), r);
        Assert.Single(single.Views);
    }
    [Fact] public void PictureBudgetIncludesPropsAndContinuityAndReelBindingsHaveTheirOwnLimit()
    {
        var (r, library) = Fixture(3);
        r.Shot.Images[1].Role = "Prop";
        r.Shot.ContinuityFrame = new(Guid.NewGuid(), Guid.NewGuid(), 0, "Previous shot");
        Assert.Equal(5, EnvironmentViews.Available(r.Shot, []));
        r.Shot.Videos = Enumerable.Range(0, 3).Select(_ => new ShotVideoBinding {
            Media = r.Pool[0].Reel!.Media with { Id = Guid.NewGuid() }, Name = "Retained prop reel", Visuals = ReelVisuals.FullReel
        }).ToList();
        r = r with { ContextFingerprint = EnvironmentViews.Context(r.Shot, r.DirectingNotes) };
        var draft = EnvironmentViews.Stage(new(r, [r.Pool[0].Id], [], false), r.Shot, r.DirectingNotes, library);
        Assert.Equal(4, draft.Videos.Count); Assert.Equal(3, ReferenceVideos.ReelSlotCount(draft));
        Assert.Equal(5, ResolvedReferences.For(draft).Pictures.Count);
        Assert.Throws<WorkspaceStoreException>(() => EnvironmentViews.Stage(new(r, r.Pool.Take(2).Select(c => c.Id).ToArray(), [], true), r.Shot, r.DirectingNotes, library));
    }
    [Fact] public void ApprovedImagesAndTheirGuidanceAreCapturedAlongsideReelViewpoints()
    {
        var (r, _) = Fixture(0);
        var image = new ShotImageBinding { AssetId = r.AssetId, MediaId = Guid.NewGuid(), Name = "Approved gate", Notes = "Keep gate shape", Crop = new() { Width = .5 } };
        var candidate = new EnvironmentViewCandidate("image/approved", image.Name, true, image.Notes, image, null, null, ReelViewSuggestions.Hash(Png));
        r = r with { Pool = [candidate, ..r.Pool], Candidates = [candidate.Id, ..r.Candidates] };
        var messages = EnvironmentViews.Messages(r, r.Candidates.Select(_ => Png).ToArray());
        Assert.Contains("approved images when suitable", messages[0].Text);
        using var context = JsonDocument.Parse(messages[1].Text!);
        Assert.True(context.RootElement.GetProperty("candidates")[0].GetProperty("approved").GetBoolean());
        Assert.Contains("Keep gate shape", messages[1].Text);
        Assert.Equal(7, messages[1].Contents.OfType<DataContent>().Count());
        var explicitNone = r.Shot.Copy(); explicitNone.EnvironmentAssetId = null;
        ProductionPolicy.CopyCoverage(r.Shot, explicitNone);
        Assert.Null(explicitNone.EnvironmentAssetId);
    }
    [Fact] public void NewSelectionsRequireStagingButHistoricalResponsesStillLoad()
    {
        var (r, _) = Fixture(); var choice = new { candidateId = r.Candidates[0], reason = "Shows the usable side of the podium and the escape route." };
        var old = JsonSerializer.Serialize(new { summary = "Side view", views = new[] { choice }, warnings = Array.Empty<string>() });
        Assert.Null(EnvironmentViews.Parse(old, r).Staging);
        Assert.Throws<WorkspaceStoreException>(() => EnvironmentViews.Parse(old, r with { Version = 2 }));
        var response = JsonSerializer.Serialize(new { summary = "Side view supports the court blocking", staging = Plan, views = new[] { choice }, warnings = Array.Empty<string>() }, AtomicJsonFile.Options);
        Assert.Equal(Plan, EnvironmentViews.Parse(response, r with { Version = 2 }).Staging);
        var optionalFields = JsonSerializer.Serialize(new { summary = "Side view", staging = new { blocking = Plan.Blocking, camera = Plan.Camera }, views = new[] { choice }, warnings = Array.Empty<string>() });
        var minimalPlan = EnvironmentViews.Parse(optionalFields, r with { Version = 2 }).Staging!;
        Assert.Equal("", minimalPlan.Assumptions); Assert.Equal("", minimalPlan.Concerns);
        Assert.Throws<WorkspaceStoreException>(() => EnvironmentViews.Parse(response.Replace(Plan.Camera, ""), r with { Version = 2 }));
        var messages = EnvironmentViews.Messages(r with { Version = 2 }, r.Candidates.Select(_ => Png).ToArray());
        Assert.Contains("what each person faces", messages[0].Text);
        Assert.Contains("not merely pictures containing the named objects", messages[0].Text);
        Assert.Contains("staging inference, not proof", messages[0].Text);
    }
    [Fact] public void ReviewedStagingAndReasonsReachComposeWithFinalNumbersAndStayOutOfLibraryNotes()
    {
        var (r, library) = Fixture(); var ids = new[] { r.Pool[3].Id, r.Pool[1].Id };
        var application = new EnvironmentViewApplication(r, ids, [], false) { Staging = Plan,
            ViewReasons = [new(ids[0], "Side view shows the podium and escape route."), new(ids[1], "Judge view establishes Riley's facing target.")] };
        var draft = EnvironmentViews.Stage(application, r.Shot, r.DirectingNotes, library);
        Assert.Null(r.Shot.EnvironmentStaging); Assert.NotNull(draft.EnvironmentStaging);
        Assert.Equal(Plan, draft.EnvironmentStaging.Plan);
        Assert.Null(EnvironmentViews.StagingIssue(draft, r.DirectingNotes, r.RevisionNotes));
        Assert.All(library.Reels.SelectMany(v => v.Keyframes!.Frames), f => Assert.Equal("Visible gate beyond podium", f.Notes));
        var resolved = ResolvedReferences.For(draft);
        var composition = new PromptCompositionRequest(r.ProjectId, r.CompositionId, 1, "context", "source", draft, r.SceneContext, r.NearbyShots,
            [new(draft.Images[0].Id, "", "", null)], [], resolved.Pictures.Select(p => new CompositionInput(p.BindingId, "hash")).ToArray(),
            r.DirectingNotes, "", r.RevisionNotes, new(AiBackend.OpenRouter, "vision", "Vision"));
        foreach (var brief in new[] { false, true }) {
            var messages = PromptComposer.BuildMessages(composition, brief ? [] : resolved.Pictures.Select(_ => Png).ToArray(), visualBrief: brief);
            using var context = JsonDocument.Parse(messages[1].Text!);
            var staging = context.RootElement.GetProperty("environmentStaging");
            Assert.Equal(Plan.Blocking, staging.GetProperty("plan").GetProperty("blocking").GetString());
            Assert.Equal(new[] { 2, 3 }, staging.GetProperty("views").EnumerateArray().Select(v => v.GetProperty("picture").GetInt32()));
            Assert.Contains("do not rotate people toward the camera", messages[0].Text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.NotEqual(PromptReferenceFreshness.Fingerprint(r.Shot, composition.Guidance), PromptReferenceFreshness.Fingerprint(draft, composition.Guidance));
        var edited = draft.Copy(); edited.EnvironmentStaging = edited.EnvironmentStaging! with { Plan = Plan with { Blocking = "Different blocking" } };
        Assert.NotEqual(PromptReferenceFreshness.Fingerprint(draft, composition.Guidance), PromptReferenceFreshness.Fingerprint(edited, composition.Guidance));
        Assert.Null(ReferenceCopies.Into(draft, new Shot(), library).Inputs.EnvironmentStaging);
    }
    [Theory]
    [InlineData("action")][InlineData("environment")][InlineData("crop")][InlineData("remove")][InlineData("frame")][InlineData("notes")][InlineData("dialogue")][InlineData("visuals")]
    public void ChangedInputsReportStaleStagingInsteadOfPassingOldBlockingToCompose(string change)
    {
        var (r, library) = Fixture();
        var draft = EnvironmentViews.Stage(new(r, [r.Pool[0].Id], [], false) { Staging = Plan }, r.Shot, r.DirectingNotes, library);
        var notes = r.DirectingNotes;
        switch (change) {
            case "action": draft.Description = "Face away from the podium"; break;
            case "environment": draft.EnvironmentAssetId = Guid.NewGuid(); break;
            case "crop": draft.Videos[0].Keyframes!.Frames[0].Crop = new() { Width = .3 }; break;
            case "remove": draft.Videos.Clear(); break;
            case "frame": var frames = draft.Videos[0].Keyframes!.Frames; frames[0] = frames[0] with { Frame = frames[0].Frame with { Index = 99 } }; break;
            case "notes": notes = "New camera direction"; break;
            case "dialogue": draft.Dialogue.Add(new() { Speaker = "Riley", Text = "Look behind you." }); break;
            case "visuals": draft.Videos[0].Visuals = ReelVisuals.None; break;
        }
        Assert.NotNull(EnvironmentViews.StagingIssue(draft, notes, ""));
        var context = JsonSerializer.Serialize(EnvironmentViews.CompositionStaging(draft, notes, ""));
        Assert.Contains("Needs review", context); Assert.DoesNotContain(Plan.Blocking, context);
    }
    [Fact] public void RefModStagingReasonsUseVideoNumbersInsteadOfInventingPictureLabels()
    {
        var (r, library) = Fixture(0);
        var draft = EnvironmentViews.Stage(new(r, r.Pool.Take(2).Select(c => c.Id).ToArray(), [], true) { Staging = Plan }, r.Shot, r.DirectingNotes, library);
        Assert.Null(EnvironmentViews.StagingIssue(draft, r.DirectingNotes, ""));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(EnvironmentViews.CompositionStaging(draft, r.DirectingNotes, ""), AtomicJsonFile.Options));
        Assert.All(json.RootElement.GetProperty("views").EnumerateArray(), view => {
            Assert.Equal(JsonValueKind.Null, view.GetProperty("picture").ValueKind);
            Assert.Equal(1, view.GetProperty("video").GetInt32());
        });
    }
}
