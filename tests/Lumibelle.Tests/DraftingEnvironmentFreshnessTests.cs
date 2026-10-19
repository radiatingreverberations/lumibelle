using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData("crop")]
    [InlineData("guidance")]
    [InlineData("unselected")]
    public async Task ComposeAndViewSelectionRecheckAcceptedDraftingEvidenceAgainstLiveAssets(string change)
    {
        var f = Fixture(); var media = new ReelPictureMedia();
        var assets = new FileAssetStore(f.Files, _clock, referenceVideos: media);
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Plaza", Category = AssetCategory.Environment, Description = "Stone plaza" };
        var library = await assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [owner] }, 0, _ct);
        var reel = new AssetReferenceReel { AssetId = owner.Id, Name = "Podium views",
            Media = new(Guid.NewGuid(), new('A', 64), 100, 32, 16, 72, 24, 3, false) };
        library = await assets.SaveReelAsync(f.Project.Id, reel, library.Revision, _ct);
        var frames = new ReelKeyframeSet { Frames = [new() { Name = "Podium side", Frame = new(reel.Media.Id, reel.Media.Sha256, 24, 1), Crop = new() { Width = .5 } }] };
        library = await assets.SaveKeyframesAsync(f.Project.Id, reel, frames, library.Revision, _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var settings = new FakeAiSettingsStore { Value = new() { DefaultBackend = AiBackend.OpenRouter, OpenRouterModel = "test/model", HasOpenRouterKey = true } };
        var production = new FileProductionStore(f.Files, f.Shots, assets, projects, _clock, new FileAiJobStore(Path.Combine(_root, "jobs"), _clock));
        var capture = new AiTextJobCapture(settings, projects, assets, null!, null!, production: production, shots: f.Shots, referenceVideos: media);
        var pool = await capture.CaptureEnvironmentSourcesAsync(f.Project.Id, owner.Id, _ct);
        var setup = new DraftingEnvironmentSetup { AssetId = owner.Id, Views = pool.Candidates.ToList(), Constraints = "Preserve scale" };
        var shot = new Shot { Title = "Hidden reaction", Description = "Riley waits behind the podium.", Duration = 5, EnvironmentAssetId = owner.Id,
            EnvironmentInspection = setup, Direction = new() { Intent = new() { Purpose = "Hide the reaction until the answer" }, Staging = new() { Camera = "Side view" } },
            DraftingEnvironment = new(DraftingEnvironments.Snapshot(Guid.NewGuid(), setup, pool), new() { Summary = "OLD ACCEPTED ASSESSMENT",
                Claims = [new("Standing side is visible", ReferenceSupportStatus.Supported, [pool.Candidates[0].Id])] }) };
        ShotDirections.Review(shot, null);
        await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var c = (await production.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        var model = new TextModelReference(AiBackend.OpenRouter, "test/model", "Test");
        async Task<PromptCompositionRequest> Compose() => (await capture.ComposeAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, c.Id, c.Version, model, false, _ct))
            .Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Payload<PromptCompositionRequest>();
        var initial = (await Compose()).DirectionContext!;
        Assert.True(initial.Environment is not null, string.Join("\n", initial.Limitations));
        var initialFingerprint = (await Compose()).ContextFingerprint;
        library = await assets.LoadAsync(f.Project.Id, _ct);
        if (change == "crop") {
            var changed = ShotCopy.Of(library.Reels[0].Keyframes!); changed.Frames[0].Crop = new() { Width = .75 };
            await assets.SaveKeyframesAsync(f.Project.Id, library.Reels[0], changed, library.Revision, _ct);
        } else if (change == "guidance") {
            await assets.SaveAsync(library with { Assets = [library.Assets[0] with { PreservationGuidance = "The raised structure is the magistrate's platform." }] }, library.Revision, _ct);
        } else {
            using var png = new MemoryStream(AssetStoreTests.Png(32, 16));
            await assets.AddImageAsync(f.Project.Id, owner.Id, png, new("unrelated.png", [], AssetImageOrigin.Imported), library.Revision, _ct);
        }
        var composed = await Compose();
        Assert.Equal(change == "unselected", composed.ContextFingerprint == initialFingerprint);
        var selected = await capture.CaptureEnvironmentViewsAsync(f.Project.Id, c.Id, c.Version, false, false, _ct);
        foreach (var context in new[] { composed.DirectionContext!, selected.Request.DirectionContext! }) {
            Assert.Equal("Hide the reaction until the answer", context.Intent!.Purpose);
            if (change == "unselected") { Assert.NotNull(context.Environment); Assert.NotNull(context.ProposedStaging); }
            else {
                Assert.Null(context.Environment); Assert.Null(context.ProposedStaging); Assert.Null(context.Advanced);
                Assert.Contains(context.Limitations, text => text.Contains("Environment evidence changed"));
            }
        }
        Assert.Equal("OLD ACCEPTED ASSESSMENT", (await f.Shots.LoadAsync(f.Project.Id, _ct)).Shots[0].DraftingEnvironment!.Assessment.Summary);
    }
}
