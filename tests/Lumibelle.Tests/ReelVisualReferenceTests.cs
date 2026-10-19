using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed class ReelVisualReferenceTests
{
    private readonly CancellationToken _ct = Xunit.TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ReelFraming.ContinuousTurn)] [InlineData(ReelFraming.BodyToFace)]
    [InlineData(ReelFraming.CharacterCapture)] [InlineData(ReelFraming.CharacterNeutralTurntable)]
    [InlineData(ReelFraming.CharacterVoiceReference)]
    [InlineData(ReelFraming.EnvironmentFullSet)] [InlineData(ReelFraming.EnvironmentWalkthrough)]
    public async Task PresetsSupportVideoOnlyReferencesWithoutInventingPictures(ReelFraming framing)
    {
        using var f = new RefModOnDemandFixture();
        var accepted = await f.Preparation.CaptureAsync(f.Project.Id, f.Shot, _ct);
        var environment = framing is ReelFraming.EnvironmentFullSet or ReelFraming.EnvironmentWalkthrough;
        var owner = f.Owner with { Category = environment ? AssetCategory.Environment : AssetCategory.Character };
        foreach (var mode in new[] { ReelVisuals.RefMod, ReelVisuals.FullReel })
        {
            var draft = ReferenceReels.NewDraft(owner); draft.Framing = framing; draft.Instructions = "From the doorway along the wall to the desk.";
            draft.KeyframeReels = ShotCopy.Of(accepted.Videos);
            draft.KeyframeReels[0].Visuals = mode; draft.KeyframeReels[0].UseSoundtrack = false;
            var pair = ReferenceReels.Preset(draft);
            draft.Prompt = pair.Prompt; draft.UseGuidance = pair.UseGuidance;
            ReferenceReels.Validate(draft, true);
            Assert.True(ReferenceReels.HasVisualReferences(draft)); Assert.Equal(0, ReferenceReels.PictureCount(draft));
            Assert.Contains("<Video 1>", pair.Prompt); Assert.DoesNotContain("<Picture", pair.Prompt);
            Assert.DoesNotContain("<Audio", pair.Prompt);
            var before = ReferenceReels.Fingerprint(draft);
            Assert.Equal(before, ReferenceReels.Fingerprint(ShotCopy.Of(draft)));
            var invalid = draft.Copy(); invalid.KeyframeReels![0].UseSoundtrack = true;
            Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(invalid));
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MixedCompositionUsesVideoNumbersAndRetainedInspectionFrames(bool environment)
    {
        using var f = new RefModOnDemandFixture();
        var accepted = await f.Preparation.CaptureAsync(f.Project.Id, f.Shot, _ct);
        var owner = f.Owner with { Category = environment ? AssetCategory.Environment : AssetCategory.Character };
        var draft = ReferenceReels.NewDraft(owner); draft.VoiceMode = ReelVoiceMode.Silent;
        var full = ShotCopy.Of(accepted.Videos[0]); full.Id = Guid.NewGuid(); full.Visuals = ReelVisuals.FullReel;
        full.Media = full.Media with { Id = Guid.NewGuid() }; full.RefMod = null; full.Keyframes = null;
        draft.KeyframeReels = [accepted.Videos[0], full];
        draft.Images = [new() { AssetId = owner.Id, MediaId = Guid.NewGuid(), Name = "Opening" }];
        foreach (var reel in draft.KeyframeReels) reel.UseSoundtrack = false;
        var shot = ReferenceReels.Inputs(draft);
        var frames = await f.Store.InspectionAsync(f.Project.Id, shot, _ct);
        Assert.All(frames, frame => Assert.Equal(2, frame.VideoNumber));
        var request = new ReelCompositionRequest(f.Project.Id, draft, ReferenceReels.Fingerprint(draft), LookPolicy.Capture(owner, null), []);
        var messages = ReferenceReels.Messages(request, [AssetStoreTests.Png(16, 16)], frames);
        Assert.Equal(4, messages[1].Contents.OfType<DataContent>().Count());
        using var metadata = JsonDocument.Parse(messages[1].Contents.OfType<TextContent>().Last().Text);
        var references = metadata.RootElement.GetProperty("videoReferences");
        Assert.Equal("<Video 1>", references.GetProperty("videos")[0].GetProperty("label").GetString());
        Assert.Equal("<Video 2>", references.GetProperty("videos")[1].GetProperty("label").GetString());
        Assert.Equal(2, references.GetProperty("sparseInspectionAttachments")[0].GetProperty("attachment").GetInt32());
        Assert.Contains("not additional Picture", messages[0].Text);
        var prompt = ReferenceReels.Preset(draft).Prompt;
        Assert.Contains("<Picture 1>", prompt); Assert.Contains("<Video 1>", prompt); Assert.Contains("<Video 2>", prompt);
        Assert.DoesNotContain("<Picture 2>", prompt);
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Messages(request, [], frames.Reverse().ToArray()));
        var corrupted = frames.ToArray(); corrupted[0] = corrupted[0] with { Png = [1, 2, 3] };
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Messages(request, [], corrupted));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Messages(request, []));
    }
}

public sealed partial class ShotTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RefModOnlyReelCapturesCompositionAndGenerationWithoutPictureUploads(bool environment)
    {
        var f = Fixture(); var media = new ReelPictureMedia(); var settings = new FakeAiSettingsStore();
        var assets = new FileAssetStore(f.Files, _clock, referenceVideos: media);
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Reference owner", Category = environment ? AssetCategory.Environment : AssetCategory.Character,
            DefaultReelVisuals = ReelVisuals.RefMod };
        await assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [owner] }, 0, _ct);
        using var source = new RefModOnDemandFixture();
        await assets.SaveReelAsync(f.Project.Id, source.Reel with { AssetId = owner.Id }, (await assets.LoadAsync(f.Project.Id, _ct)).Revision, _ct);
        var draft = ReferenceReels.NewDraft(owner); draft.VoiceMode = ReelVoiceMode.Silent;
        draft.KeyframeReels = ShotCopy.Of(source.Shot.Videos);
        draft.KeyframeReels[0].UseSoundtrack = false; draft.KeyframeReels[0].OwnerAssetId = owner.Id;
        var store = new ReelRefModStore(f.Files);
        var preparation = new ReelRefModPreparation(assets, media, store, settings);
        draft.KeyframeReels = (await preparation.CaptureAsync(f.Project.Id, ReferenceReels.Inputs(draft), _ct)).Videos;
        var pair = ReferenceReels.Preset(draft); draft.Prompt = pair.Prompt; draft.UseGuidance = pair.UseGuidance;
        var composeId = Guid.NewGuid(); draft.PendingJobId = composeId;
        draft = await assets.SaveDraftAsync(f.Project.Id, draft, 0, _ct);
        var fingerprint = ReferenceReels.Fingerprint(draft);
        // Later asset defaults do not reinterpret a saved recipe or its captured sources.
        var library = await assets.LoadAsync(f.Project.Id, _ct);
        library.Assets[0] = owner with { DefaultReelVisuals = ReelVisuals.FullReel };
        await assets.SaveAsync(library, library.Revision, _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var compose = new AiTextJobCapture(settings, projects, assets, null!, null!, referenceVideos: media, refmods: store);
        var text = await compose.ComposeReelAsync(composeId, Guid.NewGuid(), f.Project.Id, draft.Id, draft.Revision,
            new(AiBackend.OpenRouter, "vision", "Vision"), false, _ct);
        var textRequest = text.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Empty(textRequest.Payload<ReelCompositionRequest>().Images);
        Assert.Equal(3, textRequest.Messages.Sum(m => m.Parts.Count(p => p.Image is not null)));
        var header = new AiJobHeader { Id = composeId, Kind = text.Kind, Backend = text.Backend, Target = text.Target,
            ProjectName = "Test", TargetName = "Reel", OriginTabId = Guid.NewGuid(), RequestFingerprint = "test" };
        AiTextJobHandler.Read(header, text.Snapshot);
        var corrupted = textRequest with { Messages = textRequest.Messages.Select(m => m with { Parts = m.Parts.Where(p => p.Image is null).ToArray() }).ToArray() };
        Assert.Throws<WorkspaceStoreException>(() => AiTextJobHandler.Read(header, JsonSerializer.SerializeToElement(corrupted, AtomicJsonFile.Options)));

        var generator = new Lumibelle.Testing.MockVideoGenerator(assets, f.Shots, media);
        var capture = new AiReelCapture(assets, assets, settings, generator, projects, new FakeProjectAiPreferencesStore(), referenceVideos: media);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => capture.CaptureAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, draft.Id, draft.Revision, 1, _ct));
        generator.Catalog = (await generator.CheckAsync(settings.Value, _ct)) with { RefModIssue = null };
        var submission = await capture.CaptureAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, draft.Id, draft.Revision, 1, _ct);
        var captured = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Empty(captured.Inputs);
        var binding = Assert.Single(captured.Snapshot.Shot.Videos);
        Assert.Equal(ReelVisuals.RefMod, binding.EffectiveVisuals);
        Assert.Equal(draft.KeyframeReels![0].RefMod!.Recipe.Key, binding.RefMod!.Recipe.Key);
        Assert.Equal(fingerprint, ReferenceReels.Fingerprint(captured.Snapshot.Reel!.Recipe));
        var graph = JsonSerializer.Serialize(ComfyH3Video.BuildWorkflow(captured.Snapshot, 42, "test", []));
        using var workflow = JsonDocument.Parse(graph);
        var refmods = workflow.RootElement.GetProperty("prompt").GetProperty("refmods");
        Assert.Equal(ComfyRefModClient.LoadNode, refmods.GetProperty("class_type").GetString());
        using var stack = JsonDocument.Parse(refmods.GetProperty("inputs").GetProperty("stack_state").GetString()!);
        Assert.Equal(binding.RefMod.FileName, Assert.Single(stack.RootElement.GetProperty("picks").EnumerateArray()).GetProperty("visual").GetProperty("file").GetString());
        Assert.DoesNotContain("LoadVideo", graph); Assert.DoesNotContain("LoadImage", graph);
        Assert.DoesNotContain("LoadAudio", graph);
    }
}
