using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ReferenceReelTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public void SimilarReelKeepsCapturedContentAndSettingsWithoutOldJobIdentity(bool moved, bool archived)
    {
        var source = Recipe(); var look = new CharacterLook { Id = Guid.NewGuid(), Name = "Costume", Archived = archived };
        source.LookId = look.Id; source.Prompt = "Captured prompt"; source.UseGuidance = "Captured guidance";
        source.PendingJobId = Guid.NewGuid(); source.ResolvedJobs = [Guid.NewGuid()]; source.CheckedInputs = "checked";
        var owner = new ReferenceAsset { Id = moved ? Guid.NewGuid() : source.AssetId, Name = "Mira", Category = AssetCategory.Character, Looks = [look] };
        var before = ReferenceReels.Fingerprint(source);
        var draft = ReferenceReels.SimilarDraft(source, owner);
        Assert.NotEqual(source.Id, draft.Id); Assert.Equal(0, draft.Revision); Assert.Equal(owner.Id, draft.AssetId);
        Assert.Equal(moved || archived ? null : source.LookId, draft.LookId);
        Assert.Equal(source.Name, draft.Name); Assert.Equal(source.Prompt, draft.Prompt); Assert.Equal(source.UseGuidance, draft.UseGuidance);
        Assert.Equal(source.Images, draft.Images); Assert.Equal(source.Duration, draft.Duration); Assert.Equal(source.Framing, draft.Framing);
        Assert.Equal(source.GenerationPreset, draft.GenerationPreset); Assert.Equal(source.VoiceMode, draft.VoiceMode);
        Assert.Null(draft.PendingJobId); Assert.Null(draft.CheckedInputs); Assert.Empty(draft.ResolvedJobs);
        Assert.Equal(before, ReferenceReels.Fingerprint(source));
    }

    [Fact]
    public void OptionalVoiceDescriptionPreservesOldRecipesAndParticipatesInNewBaselines()
    {
        var draft = Recipe();
        var json = JsonSerializer.SerializeToNode(draft, AtomicJsonFile.Options)!.AsObject();
        Assert.False(json.ContainsKey("voiceDescription"));
        var baseline = ReferenceReels.Fingerprint(draft);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(json, AtomicJsonFile.Options))), baseline);
        Assert.Equal(baseline, ReferenceReels.Fingerprint(json.Deserialize<ReferenceReelDraft>(AtomicJsonFile.Options)!));
        var inputBaseline = ReferenceReels.InputsFingerprint(draft);
        draft.VoiceDescription = "Warm, lightly raspy — posé.\nA relaxed pace.";
        var copy = draft.Copy();
        Assert.Equal(draft.VoiceDescription, copy.VoiceDescription);
        Assert.NotEqual(inputBaseline, ReferenceReels.InputsFingerprint(copy));
        copy.VoiceDescription = "Bright and brisk.";
        Assert.NotEqual(ReferenceReels.Fingerprint(draft), ReferenceReels.Fingerprint(copy));
        Assert.StartsWith("Warm", draft.VoiceDescription);
        draft.VoiceDescription = null;
        Assert.Equal(baseline, ReferenceReels.Fingerprint(draft));
    }

    [Theory]
    [InlineData(ReelVoiceMode.NewVoice)]
    [InlineData(ReelVoiceMode.ExistingRecording)]
    [InlineData(ReelVoiceMode.Silent)]
    public void VoiceDirectionReachesCompositionWithoutOverridingSpeechMode(ReelVoiceMode mode)
    {
        var draft = Recipe(ReelFraming.SideRearFace, mode);
        draft.VoiceDescription = "Warm, lightly raspy — posé.\nA relaxed pace.";
        var original = draft.Copy();
        var request = new ReelCompositionRequest(Guid.NewGuid(), draft, ReferenceReels.Fingerprint(draft),
            new(draft.AssetId, "Riley", "", "", null, "General", "", ""), []);
        var messages = ReferenceReels.Messages(request, []);
        using var context = JsonDocument.Parse(messages[1].Text!);
        var captured = context.RootElement.GetProperty("request").GetProperty("draft");
        var pair = ReferenceReels.Preset(draft);
        ReferenceReels.ValidatePair(pair, draft);
        if (mode == ReelVoiceMode.Silent)
        {
            Assert.False(captured.TryGetProperty("voiceDescription", out _));
            Assert.DoesNotContain(draft.VoiceDescription, pair.Prompt);
            Assert.DoesNotContain(draft.VoiceDescription, pair.UseGuidance);
            Assert.DoesNotContain("<d>", pair.Prompt);
        }
        else
        {
            Assert.Equal(draft.VoiceDescription, captured.GetProperty("voiceDescription").GetString());
            Assert.Contains(draft.VoiceDescription, pair.Prompt);
            Assert.Contains(draft.VoiceDescription, pair.UseGuidance);
            Assert.Contains($"Riley (S1) says: <d>[English] {draft.Line}</d>", pair.Prompt);
            Assert.Equal(mode == ReelVoiceMode.ExistingRecording, pair.Prompt.Contains("<Audio 1>"));
        }
        Assert.Contains("preserve the recording's voice identity", messages[0].Text);
        Assert.Equal(ReferenceReels.Fingerprint(original), ReferenceReels.Fingerprint(draft));
        Assert.Empty(draft.Prompt);
        Assert.Empty(draft.UseGuidance);
    }

    [Fact]
    public void VoiceDescriptionHasAnActionableLengthLimit()
    {
        var draft = Recipe(); draft.VoiceDescription = new('x', 2001);
        Assert.Contains("voice description", Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(draft)).Message);
    }

    [Fact]
    public void CompleteJsonRetainsBothTextsAndTreatsDurationAsAReviewNote()
    {
        var draft = Recipe();
        var pair = ReferenceReels.Preset(draft);
        pair = pair with { Prompt = pair.Prompt.Replace("5.167 seconds", "5 seconds") };
        var raw = JsonSerializer.Serialize(pair, AtomicJsonFile.Options);
        var payload = new ReelCompositionRequest(Guid.NewGuid(), draft, ReferenceReels.Fingerprint(draft),
            new(draft.AssetId, "Riley", "", "", null, "General", "", ""), []);
        var request = new AiTextJobRequest(2, AiJobKind.ReelComposition, new(AiBackend.OpenRouter, "mock", "Mock"),
            true, new(), ReferenceReels.Profile, .5f, 1, JsonSerializer.SerializeToElement(payload, AtomicJsonFile.Options), []);
        var result = AiTextResults.Parse(request, raw, "stop");
        Assert.Null(result.Error);
        Assert.Contains("5.167 seconds", ReferenceReels.PromptWarning(pair, draft));
        Assert.Equal(pair, result.Read<ReelPromptPair>());
        Assert.Equal(raw, result.Raw);
        Assert.Equal(pair, ReferenceReels.ParsePair(raw));
        Assert.Equal(pair, ReferenceReels.Parse(raw, draft));
        Assert.Equal("", draft.Prompt);
        Assert.Null(AiTextResults.Parse(request, raw, "length").Value);
        Assert.Null(AiTextResults.Parse(request, "{\"prompt\":\"incomplete\"}", "stop").Value);
    }

    [Fact]
    public void PresetUsesTheSameGuidanceAsReferenceSelectionAndAi()
    {
        var draft = Recipe(); var image = draft.Images[0]; image.Notes = "";
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [new() {
            Id = image.AssetId, Name = "Riley", Category = AssetCategory.Character, PreservationGuidance = "Keep the freckles."
        }] };
        Assert.Contains("Keep the freckles.", ReferenceReels.Preset(draft, library).Prompt);
        image.PreservationOverride = "Use only the hands and keep the blue gloves.";
        var pair = ReferenceReels.Preset(draft, library);
        Assert.Contains(image.PreservationOverride, pair.Prompt); Assert.Contains(image.PreservationOverride, pair.UseGuidance);
        Assert.DoesNotContain("Keep the freckles.", pair.Prompt);
        image.PreservationOverride = "";
        Assert.DoesNotContain("Keep the freckles.", ReferenceReels.Preset(draft, library).Prompt);
    }
    [Fact]
    public void CustomHasNoPresetTextButCanReceiveAValidAuthoredPair()
    {
        var draft = Recipe(ReelFraming.Custom);
        Assert.Equal(new ReelPromptPair("", ""), ReferenceReels.Preset(draft));
        var authored = ReferenceReels.Preset(draft with { Framing = ReelFraming.ThreeAngles });
        ReferenceReels.ValidatePair(authored, draft);
        var request = new ReelCompositionRequest(Guid.NewGuid(), draft, ReferenceReels.Fingerprint(draft), new(draft.AssetId, "Riley", "", "", null, "General", "", ""), []);
        var messages = ReferenceReels.Messages(request, []);
        Assert.Contains("Custom means no prescribed framing", messages[0].Text);
        Assert.Contains("\"framing\": \"Custom\"", messages[1].Text);
    }
    [Fact]
    public void CoveragePresetSharesItsViewsWithAiAndKeepsExistingIdentities()
    {
        Assert.Equal(3, (int)ReelFraming.Custom);
        var draft = Recipe(ReelFraming.SideRearFace);
        var pair = ReferenceReels.Preset(draft);
        Assert.Contains(ReferenceReels.Views(draft.Framing), pair.Prompt);
        Assert.Contains("shoulder height", pair.Prompt);
        Assert.Contains("first 30%", pair.Prompt); Assert.Contains("next 40%", pair.Prompt); Assert.Contains("final 30%", pair.Prompt);
        Assert.Contains("rear appearance", pair.UseGuidance); Assert.Contains("not verified output", pair.UseGuidance);
        var request = new ReelCompositionRequest(Guid.NewGuid(), draft, ReferenceReels.Fingerprint(draft), new(draft.AssetId, "Riley", "", "", null, "General", "", ""), []);
        using var context = JsonDocument.Parse(ReferenceReels.Messages(request, [])[1].Text!);
        Assert.Equal(ReferenceReels.Views(draft.Framing), context.RootElement.GetProperty("presetViews").GetString());
        Assert.Empty(draft.Prompt);
    }
    public static ReferenceReelDraft Recipe(ReelFraming framing = ReelFraming.ContinuousTurn, ReelVoiceMode voice = ReelVoiceMode.NewVoice) => new()
    {
        AssetId = Guid.NewGuid(), Speaker = "Riley", Name = "Riley reference", Framing = framing, VoiceMode = voice,
        Images = [new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Notes = "facial identity", InferUsage = true }],
        Voice = voice == ReelVoiceMode.ExistingRecording ? new() { AssetId = Guid.NewGuid(), VoiceId = Guid.NewGuid(), Speaker = "Riley", Duration = 3 } : null
    };
    [Theory]
    [InlineData(ReelFraming.ContinuousTurn, ReelVoiceMode.NewVoice)]
    [InlineData(ReelFraming.ContinuousTurn, ReelVoiceMode.ExistingRecording)]
    [InlineData(ReelFraming.ContinuousTurn, ReelVoiceMode.Silent)]
    [InlineData(ReelFraming.BodyToFace, ReelVoiceMode.NewVoice)]
    [InlineData(ReelFraming.BodyToFace, ReelVoiceMode.ExistingRecording)]
    [InlineData(ReelFraming.BodyToFace, ReelVoiceMode.Silent)]
    [InlineData(ReelFraming.ThreeAngles, ReelVoiceMode.NewVoice)]
    [InlineData(ReelFraming.ThreeAngles, ReelVoiceMode.ExistingRecording)]
    [InlineData(ReelFraming.ThreeAngles, ReelVoiceMode.Silent)]
    [InlineData(ReelFraming.SideRearFace, ReelVoiceMode.NewVoice)]
    [InlineData(ReelFraming.SideRearFace, ReelVoiceMode.ExistingRecording)]
    [InlineData(ReelFraming.SideRearFace, ReelVoiceMode.Silent)]
    public void PresetsProduceValidPairsWithoutSceneProvenance(ReelFraming framing, ReelVoiceMode voice)
    {
        var draft = Recipe(framing, voice); var pair = ReferenceReels.Preset(draft); draft.Prompt = pair.Prompt; draft.UseGuidance = pair.UseGuidance;
        ReferenceReels.Validate(draft, true);
        var inputs = ReferenceReels.Inputs(draft);
        Assert.Null(inputs.SceneId); Assert.Null(inputs.ApprovedScriptId); Assert.Contains("5.167 seconds", pair.Prompt);
        Assert.Equal(voice == ReelVoiceMode.Silent ? 0 : 1, System.Text.RegularExpressions.Regex.Matches(pair.Prompt, "<d>").Count);
        Assert.Equal(voice == ReelVoiceMode.ExistingRecording, pair.Prompt.Contains("<Audio 1>"));
        if (framing != ReelFraming.ContinuousTurn) Assert.Throws<WorkspaceStoreException>(() => ProductionPolicy.ValidatePrompt(pair.Prompt, inputs));
    }
    [Theory]
    [InlineData("heading")] [InlineData("duration")] [InlineData("reference")] [InlineData("dialogue")] [InlineData("speaker")] [InlineData("order")]
    public void ReelTemplateFindingsAreAdvisory(string defect)
    {
        var draft = Recipe(ReelFraming.ThreeAngles); var pair = ReferenceReels.Preset(draft);
        pair = pair with { Prompt = defect switch {
            "heading" => pair.Prompt.Replace("summary:", "summary"), "duration" => pair.Prompt.Replace("5.167", "5.5"),
            "reference" => pair.Prompt.Replace("<Picture 1>", "<Picture 2>"), "dialogue" => pair.Prompt.Replace(draft.Line, "Invented words."),
            "speaker" => pair.Prompt.Replace("Riley (S1)", "Other (S1)"), _ => pair.Prompt.Replace("[Shot 2]", "[Shot 4]") } };
        ReferenceReels.ValidatePair(pair, draft);
        Assert.NotNull(ReferenceReels.PromptWarning(pair, draft));
    }
    [Theory]
    [InlineData("standard")] [InlineData("turbo4")]
    [InlineData("larry")] [InlineData("pdd")]
    public void CutsReachTheRealWorkflowWithoutHiddenCoverage(string preset)
    {
        var draft = Recipe(ReelFraming.ThreeAngles); var pair = ReferenceReels.Preset(draft); draft.Prompt = pair.Prompt; draft.UseGuidance = pair.UseGuidance;
        draft.GenerationPreset = preset;
        var shot = ReferenceReels.Inputs(draft); var settings = new H3Settings(); var size = H3Policy.Size(draft.Aspect, false);
        var snapshot = new VideoSnapshot(Guid.NewGuid(), 1, shot, draft.Prompt, H3Policy.Fingerprint(shot), "http://localhost:8188", settings, size.Width, size.Height, H3Policy.Frames(draft.Duration), ReferenceReels.Profile)
        { Reel = new(draft, new(draft.AssetId, "Riley", "", "", null, "General", "", "")), OutputPolicy = new(false), Preset = H3Presets.Capture(shot, settings), Performance = H3Performance.Capture(H3Presets.NewPerformance(settings)), Sampling = H3Policy.Sampling(shot, settings) };
        var request = new AiVideoJobRequest(3, Guid.NewGuid(), snapshot, [new("image.png", false, 4, new('A', 64))]);
        AiVideoJobPolicy.Validate(request);
        var workflow = JsonSerializer.Serialize(ComfyH3Video.BuildWorkflow(snapshot, 42, "client", [new("image.png", false)]));
        Assert.Contains("[Shot 3]", workflow);
        Assert.Throws<WorkspaceStoreException>(() => AiVideoJobPolicy.Validate(request with { Version = 2 }));
        Assert.Equal(draft.AssetId, AiVideoJobHandler.Target(snapshot).AssetId); Assert.Null(AiVideoJobHandler.Target(snapshot).ShotId);
    }
    [Fact]
    public void RecipeCopiesAndBaselinesTrackOnlyTheirOwnInputs()
    {
        var draft = Recipe(); var copy = draft.Copy(); copy.Images[0].Notes = "hands";
        Assert.Equal("facial identity", draft.Images[0].Notes);
        Assert.NotEqual(ReferenceReels.Fingerprint(draft), ReferenceReels.Fingerprint(copy));
        copy = draft.Copy(); copy.Revision = 5; copy.PendingJobId = Guid.NewGuid(); copy.ResolvedJobs.Add(Guid.NewGuid());
        Assert.Equal(ReferenceReels.Fingerprint(draft), ReferenceReels.Fingerprint(copy));
        copy.Duration = 8; Assert.NotEqual(ReferenceReels.InputsFingerprint(draft), ReferenceReels.InputsFingerprint(copy));
    }
}

public sealed partial class AssetStoreTests
{
    [Fact]
    public async Task RemovedEnvironmentRecipesRemainEditableAndCapturedReelsRemainUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, store) = CreateStore(); var owner = Asset("Plaza") with { Category = AssetCategory.Environment };
        await store.SaveAsync(new() { ProjectId = project.Id, Assets = [owner] }, 0, ct);
        var recipe = ReferenceReels.NewDraft(owner);
        recipe.Framing = ReelFraming.EnvironmentArc; recipe.Instructions = "Around the podium";
        recipe.Prompt = "Captured old prompt"; recipe.UseGuidance = "Captured old guidance";
        recipe = await store.SaveDraftAsync(project.Id, recipe, 0, ct);
        var snapshot = new VideoSnapshot(project.Id, 0, ReferenceReels.Inputs(recipe), recipe.Prompt, "", "", new(), 64, 64, 362, ReferenceReels.EnvironmentProfile);
        var reel = new AssetReferenceReel { AssetId = owner.Id, Media = new(Guid.NewGuid(), new('A', 64), 15, 64, 64, 362, 24, 362 / 24d, false),
            Generation = new(recipe.Copy(), Guid.NewGuid(), Guid.NewGuid(), 1, 42, snapshot) };
        await store.PublishReelAsync(project.Id, reel, ct);
        var captured = JsonSerializer.Serialize(reel.Generation, AtomicJsonFile.Options);
        recipe.Instructions = "Keep this unfinished edit";
        recipe = await store.SaveDraftAsync(project.Id, recipe, recipe.Revision, ct);
        ReferenceReels.SelectCameraPreset(recipe, ReelFraming.EnvironmentFullSet);
        await store.SaveDraftAsync(project.Id, recipe, recipe.Revision, ct);
        var library = await store.LoadAsync(project.Id, ct);
        Assert.Equal(captured, JsonSerializer.Serialize(Assert.Single(library.Reels).Generation, AtomicJsonFile.Options));
        var saved = Assert.Single(library.ReelDrafts);
        Assert.Equal(ReelFraming.EnvironmentFullSet, saved.Framing);
        Assert.Equal("Captured old prompt", saved.Prompt); Assert.Equal("Captured old guidance", saved.UseGuidance);
        Assert.Equal("Keep this unfinished edit", saved.Instructions);
    }

    [Theory]
    [InlineData(AssetCategory.Character, ReelVoiceMode.NewVoice)]
    [InlineData(AssetCategory.Character, ReelVoiceMode.ExistingRecording)]
    [InlineData(AssetCategory.Character, ReelVoiceMode.Silent)]
    [InlineData(AssetCategory.Environment, ReelVoiceMode.Silent)]
    [InlineData(AssetCategory.Prop, ReelVoiceMode.Silent)]
    public async Task ClearedReelDraftSavesSeparatelyWithoutChangingThePreviousRecipe(AssetCategory category, ReelVoiceMode voice)
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, store) = CreateStore(); var owner = Asset("Reference owner") with { Category = category };
        await store.SaveAsync(new() { ProjectId = project.Id, Assets = [owner] }, 0, ct);
        var draft = ReferenceReels.NewDraft(owner) with {
            VoiceMode = voice, Name = "Authored reel", Instructions = "Keep the silhouette", Prompt = "Authored prompt", UseGuidance = "Authored guidance",
            Duration = 8, Aspect = "16:9", GenerationPreset = "turbo4", PendingJobId = Guid.NewGuid(), ResolvedJobs = [Guid.NewGuid()]
        };
        draft = await store.SaveDraftAsync(project.Id, draft, 0, ct);
        var original = ReferenceReels.Fingerprint(draft);
        var blank = await store.SaveDraftAsync(project.Id, ReferenceReels.ClearDraftContent(draft), 0, ct);
        var reopened = await store.LoadAsync(project.Id, ct);
        Assert.Equal(blank.Id, reopened.ReelDrafts.Last().Id);
        Assert.NotEqual(draft.Id, blank.Id);
        Assert.Equal(original, ReferenceReels.Fingerprint(reopened.ReelDrafts.Single(d => d.Id == draft.Id)));
        Assert.Equal("", blank.Name); Assert.Equal("", blank.Prompt); Assert.Equal("", blank.UseGuidance); Assert.Equal("", blank.Instructions);
        Assert.Equal("", blank.Line); Assert.Equal("", blank.Speaker); Assert.Null(blank.Voice); Assert.Empty(blank.Images);
        Assert.Null(blank.KeyframeReels); Assert.Null(blank.PendingJobId); Assert.Empty(blank.ResolvedJobs);
        Assert.Equal(8, blank.Duration); Assert.Equal("16:9", blank.Aspect); Assert.Equal("turbo4", blank.GenerationPreset);
    }


    [Theory]
    [InlineData("success")] [InlineData("environment")] [InlineData("prop")] [InlineData("edited")] [InlineData("newer")]
    [InlineData("waiting")] [InlineData("cancelled")] [InlineData("failed")] [InlineData("partial")] [InlineData("one-more")]
    public async Task SuccessfulReelsStartFreshOnlyForTheUnchangedCompletedDraft(string scenario)
    {
        var ct = TestContext.Current.CancellationToken; var (project, store) = CreateStore();
        var owner = Asset("Riley") with { Category = scenario switch { "environment" => AssetCategory.Environment, "prop" => AssetCategory.Prop, _ => AssetCategory.Character } };
        await store.SaveAsync(new() { ProjectId = project.Id, Assets = [owner] }, 0, ct);
        var recipe = ReferenceReels.NewDraft(owner); recipe.Images = ReferenceReelTests.Recipe().Images;
        recipe.Prompt = "Captured prompt"; recipe.UseGuidance = "Captured guidance"; recipe.Instructions = "Keep this camera route.";
        recipe = await store.SaveDraftAsync(project.Id, recipe, 0, ct);
        var fingerprint = ReferenceReels.Fingerprint(recipe); var jobId = Guid.NewGuid(); var batch = AiBatchDefinition.Create(jobId, 2);
        var job = new AiJobHeader { Id = jobId, Kind = AiJobKind.ReelVideo, Backend = AiBackend.ComfyUI, Batch = batch,
            Target = new(project.Id, owner.Id, ReelId: recipe.Id), ProjectName = "Test", TargetName = "Reel", OriginTabId = Guid.NewGuid(), RequestFingerprint = "test", State = AiJobState.Completed };
        var snapshot = new VideoSnapshot(project.Id, recipe.Revision, ReferenceReels.Inputs(recipe), recipe.Prompt, "", "", new(), 64, 64, 124);
        foreach (var candidate in batch.Candidates.Take(scenario == "partial" ? 1 : 2))
            await store.PublishReelAsync(project.Id, new() { Id = candidate.Id, AssetId = owner.Id,
                Media = new(Guid.NewGuid(), new('A', 64), 5, 64, 64, 124, 24, 124 / 24d, false),
                Generation = new(recipe.Copy(), jobId, jobId, candidate.Number, candidate.Seed, snapshot) }, ct);
        if (scenario == "edited") await store.SaveDraftAsync(project.Id, recipe with { Instructions = "A newer camera route" }, recipe.Revision, ct);
        if (scenario == "newer") await store.SaveDraftAsync(project.Id, ReferenceReels.NewDraft(owner) with { Instructions = "My next draft" }, 0, ct);
        if (scenario == "waiting") job = job with { State = AiJobState.Waiting };
        if (scenario == "failed") job = job with { State = AiJobState.NeedsAttention };
        if (scenario == "cancelled") job = job with { CancelRequested = true };
        if (scenario == "one-more") job = job with { Id = Guid.NewGuid() };
        var fresh = await store.StartFreshAfterSuccessAsync(job, recipe, ct);
        var saved = await store.LoadAsync(project.Id, ct);
        Assert.Equal(fingerprint, ReferenceReels.Fingerprint(recipe));
        Assert.All(saved.Reels, reel => Assert.Equal(fingerprint, ReferenceReels.Fingerprint(reel.Generation!.Recipe)));
        if (scenario is "success" or "environment" or "prop") {
            Assert.NotNull(fresh); Assert.NotEqual(recipe.Id, fresh.Id); Assert.Null(fresh.LookId);
            Assert.Empty(fresh.Prompt); Assert.Empty(fresh.UseGuidance); Assert.Empty(fresh.Images); Assert.Empty(fresh.Instructions);
            Assert.Equal(scenario == "environment" ? "16:9" : "1:1", fresh.Aspect);
            Assert.Equal(recipe.PresetVersion, fresh.PresetVersion);
            Assert.Equal(fresh.Id, saved.ReelDrafts.Last().Id);
            Assert.Null(await store.StartFreshAfterSuccessAsync(job, recipe, ct));
            Assert.Equal(saved.Revision, (await store.LoadAsync(project.Id, ct)).Revision);
        } else {
            Assert.Null(fresh);
            Assert.Equal(scenario == "newer" ? 2 : 1, saved.ReelDrafts.Count);
            Assert.Equal(scenario == "edited" ? "A newer camera route" : recipe.Instructions, saved.ReelDrafts.First().Instructions);
        }
    }

    [Fact]
    public async Task PropsKeepReelsAndRecipesThatReferenceAssetsCannotHold()
    {
        var ct = TestContext.Current.CancellationToken; var (project, store) = CreateStore();
        var prop = Asset("Armchair") with { Category = AssetCategory.Prop }; var reference = Asset("Mood board") with { Category = AssetCategory.Reference };
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [prop, reference] }, 0, ct);
        AssetReferenceReel Imported(Guid owner) => new() { AssetId = owner, Name = "Imported turn", UseGuidance = "Shows every side.",
            Media = new(Guid.NewGuid(), new('A', 64), 5, 64, 64, 124, 24, 124 / 24d, false) };
        library = await store.SaveReelAsync(project.Id, Imported(prop.Id), library.Revision, ct);
        Assert.Equal(prop.Id, Assert.Single(library.Reels).AssetId);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.SaveReelAsync(project.Id, Imported(reference.Id), library.Revision, ct));
        var draft = await store.SaveDraftAsync(project.Id, ReferenceReels.NewDraft(prop), 0, ct);
        Assert.Equal(ReferenceReels.PropProfile, Assert.Single((await store.LoadAsync(project.Id, ct)).ReelDrafts).PresetVersion);
        var mismatched = ReferenceReels.NewDraft(prop with { Category = AssetCategory.Environment });
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.SaveDraftAsync(project.Id, mismatched, 0, ct));
        Assert.Equal(draft.Id, Assert.Single((await store.LoadAsync(project.Id, ct)).ReelDrafts).Id);
    }

    [Fact]
    public async Task ReelDetailsMergeUnrelatedEditsAndRejectCompetingCorrections()
    {
        var ct = TestContext.Current.CancellationToken; var (project, store) = CreateStore(); var owner = Asset("Riley");
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [owner] }, 0, ct);
        var baseline = new AssetReferenceReel { AssetId = owner.Id, Name = "Original", UseGuidance = "Original guidance", Media = new(Guid.NewGuid(), new('A', 64), 5, 64, 64, 124, 24, 124 / 24d, false) };
        library = await store.SaveReelAsync(project.Id, baseline, library.Revision, ct);
        library = await store.EditReelDetailsAsync(project.Id, baseline, baseline.Name, "Corrected after watching", library.Revision, ct);
        library = await store.EditReelDetailsAsync(project.Id, baseline, "Renamed reel", baseline.UseGuidance, library.Revision, ct);
        var saved = Assert.Single(library.Reels);
        Assert.Equal("Renamed reel", saved.Name); Assert.Equal("Corrected after watching", saved.UseGuidance);
        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.EditReelDetailsAsync(project.Id, baseline, "Renamed reel", "A competing correction", library.Revision, ct));
        Assert.Contains("use guidance changed elsewhere", error.Message);
        Assert.Equal(saved, Assert.Single((await store.LoadAsync(project.Id, ct)).Reels));
    }
    [Fact]
    public async Task ReelLookDraftsAndConcurrentPublicationsRemainIndependent()
    {
        var ct = TestContext.Current.CancellationToken; var (project, store) = CreateStore();
        var look = new CharacterLook { Name = "Evening" }; var owner = Asset("Riley") with { Looks = [look] };
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [owner] }, 0, ct);
        var general = new ReferenceReelDraft { AssetId = owner.Id, Speaker = owner.Name };
        var specific = general.Copy(); specific.Id = Guid.NewGuid(); specific.LookId = look.Id;
        await Task.WhenAll(store.SaveDraftAsync(project.Id, general, 0, ct), store.SaveDraftAsync(project.Id, specific, 0, ct));
        AssetReferenceReel Candidate(int number) => new() { AssetId = owner.Id, LookId = look.Id,
            Media = new(Guid.NewGuid(), new('A', 64), 8, 64, 64, 124, 24, 124 / 24d, false),
            Generation = new(specific, Guid.NewGuid(), Guid.NewGuid(), number, number, new(project.Id, 0, ReferenceReels.Inputs(specific), "", "", "", new(), 64, 64, 124)) };
        var first = Candidate(1); var second = Candidate(2);
        await Task.WhenAll(store.PublishReelAsync(project.Id, first, ct), store.PublishReelAsync(project.Id, second, ct));
        library = await store.LoadAsync(project.Id, ct);
        Assert.Equal(2, library.Reels.Count); Assert.Equal(2, library.ReelDrafts.Count);
        var copy = library.Copy(); copy.ReelDrafts[0].UseGuidance = "changed only in the copy";
        Assert.All(library.ReelDrafts, d => Assert.Empty(d.UseGuidance));
        library.Assets[0] = owner with { Looks = [look with { Archived = true }] };
        library = await store.SaveAsync(library, library.Revision, ct);
        library = await store.SaveReelAsync(project.Id, first with { UseGuidance = "Corrected after viewing." }, library.Revision, ct);
        Assert.Equal(2, library.Reels.Count);
        var stale = library.Revision; library = await store.TrashReelAsync(project.Id, first.Id, stale, ct);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.SaveReelAsync(project.Id, second, stale, ct));
        library = await store.RestoreReelAsync(project.Id, first.Id, library.Revision, ct);
        Assert.Equal(first.Media.Id, library.Reels.Single(r => r.Id == first.Id).Media.Id);
    }
    [Theory]
    [InlineData("initial", false)] [InlineData("initial", true)]
    [InlineData("revision", false)] [InlineData("revision", true)]
    [InlineData("manual", false)] [InlineData("manual", true)]
    [InlineData("cancel", false)] [InlineData("cancel", true)]
    [InlineData("replaced", false)] [InlineData("replaced", true)]
    [InlineData("image", false)] [InlineData("image", true)]
    [InlineData("guidance", false)] [InlineData("guidance", true)]
    [InlineData("render", false)] [InlineData("render", true)]
    [InlineData("warning", false)] [InlineData("warning", true)]
    [InlineData("historical", false)] [InlineData("historical", true)]
    public async Task ReelPairApplicationChecksCapturedTargetAndIsIdempotent(string scenario, bool environment)
    {
        var ct = TestContext.Current.CancellationToken; var (project, store) = CreateStore(); var owner = Asset("Riley") with { Category = environment ? AssetCategory.Environment : AssetCategory.Character };
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [owner] }, 0, ct);
        using var image = new MemoryStream(Png(32, 32));
        library = await store.AddImageAsync(project.Id, owner.Id, image, new("face.png", [], AssetImageOrigin.Imported), library.Revision, ct);
        var draft = environment ? ReferenceReels.NewDraft(owner) : ReferenceReelTests.Recipe(); var jobId = Guid.NewGuid(); draft.AssetId = owner.Id; draft.PendingJobId = jobId;
        draft.Images = [new() { AssetId = owner.Id, MediaId = library.Assets.Single().Images.Single().Id, InferUsage = true }];
        var pair = ReferenceReels.Preset(draft);
        if (scenario is "warning" or "historical") pair = pair with { Prompt = pair.Prompt.Replace("<Picture 1>", "Picture 1") };
        if (scenario == "revision") { draft.Prompt = pair.Prompt; draft.UseGuidance = pair.UseGuidance; }
        draft = await store.SaveDraftAsync(project.Id, draft, 0, ct);
        var inputs = await ProductionInputs.CaptureAsync(project.Id, ReferenceReels.Inputs(draft), store, ct);
        var request = new ReelCompositionRequest(project.Id, draft.Copy(), ReferenceReels.Fingerprint(draft), LookPolicy.Capture(owner, null), inputs.Select(i => i.Identity).ToArray())
        { ImageGuidance = ShotReferences.Resolve(ReferenceReels.Inputs(draft), library, new()) };
        var job = new AiJobHeader { Id = jobId, Kind = AiJobKind.ReelComposition, Backend = AiBackend.OpenRouter,
            Target = new(project.Id, owner.Id, ReelId: draft.Id), ProjectName = "Test", TargetName = "Reel", OriginTabId = Guid.NewGuid(), RequestFingerprint = "test", State = AiJobState.Completed };
        if (scenario == "manual") { draft.UseGuidance = "My manual writing 👋"; await store.SaveDraftAsync(project.Id, draft, draft.Revision, ct); }
        if (scenario == "replaced") { draft.PendingJobId = Guid.NewGuid(); await store.SaveDraftAsync(project.Id, draft, draft.Revision, ct); }
        if (scenario == "cancel") job = job with { CancelRequested = true };
        if (scenario == "historical") job = job with { State = AiJobState.NeedsAttention, Recovery = AiJobRecovery.GenerateAgain };
        // Render settings do not shape the composed text, so changing them never blocks the pair.
        if (scenario == "render") { draft.GenerationPreset = "hyperflow"; draft.Resolution = VideoResolution.Quick; draft.SaveLosslessFrames = false; await store.SaveDraftAsync(project.Id, draft, draft.Revision, ct); }
        if (scenario is "image" or "guidance") {
            library = await store.LoadAsync(project.Id, ct);
            if (scenario == "image") await store.DeleteImageAsync(project.Id, owner.Id, draft.Images[0].MediaId, library.Revision, ct);
            else { library.Assets[0] = library.Assets[0] with { PreservationGuidance = "Changed identity notes" }; await store.SaveAsync(library, library.Revision, ct); }
        }
        if (scenario == "image") await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.ApplyPairAsync(job, request, pair, true, ct));
        else Assert.Equal(scenario is "initial" or "render", await store.ApplyPairAsync(job, request, pair, true, ct));
        if (scenario == "revision") Assert.True(await store.ApplyPairAsync(job, request, pair, false, ct));
        if (scenario is "warning" or "historical") {
            Assert.Empty((await store.LoadAsync(project.Id, ct)).ReelDrafts.Single().Prompt);
            Assert.True(await store.ApplyPairAsync(job, request, pair, false, ct));
            var accepted = (await store.LoadAsync(project.Id, ct)).ReelDrafts.Single();
            Assert.Equal(pair.Prompt, accepted.Prompt); ReferenceReels.Validate(accepted, true);
            Assert.False(await store.ApplyPairAsync(job, request, pair, false, ct));
        }
        if (scenario == "manual") Assert.Equal("My manual writing 👋", (await store.LoadAsync(project.Id, ct)).ReelDrafts.Single().UseGuidance);
        if (scenario is "manual" or "guidance") {
            // Changes are named for the author, and the pair can then be applied anyway.
            var changed = await Assert.ThrowsAsync<AssistedInputsChangedException>(() => store.ApplyPairAsync(job, request, pair, false, ct));
            Assert.Contains(scenario == "manual" ? ReferenceReels.PairTextEdited : "the character’s look or notes changed", changed.Changes);
            if (scenario == "manual") Assert.Single(changed.Changes);
            Assert.True(await store.ApplyPairAsync(job, request, pair, false, ct, acceptChangedInputs: true));
            var saved = (await store.LoadAsync(project.Id, ct)).ReelDrafts.Single();
            Assert.Equal(pair.UseGuidance, saved.UseGuidance);
            // Replaced text was checked against these inputs; a pair written for other inputs was not.
            Assert.Equal(scenario == "manual" ? ReferenceReels.InputsFingerprint(saved) : null, saved.CheckedInputs);
        }
        if (scenario == "replaced")
            Assert.False(await store.ApplyPairAsync(job, request, pair, false, ct, acceptChangedInputs: true));
        if (scenario is "initial" or "revision" or "render") {
            Assert.False(await store.ApplyPairAsync(job, request, pair, false, ct));
            var saved = (await store.LoadAsync(project.Id, ct)).ReelDrafts.Single();
            Assert.Equal(pair.Prompt, saved.Prompt); Assert.Equal(pair.UseGuidance, saved.UseGuidance); Assert.Single(saved.ResolvedJobs);
        }
    }
    [Fact]
    public async Task ReferenceReelsSurviveMetadataSavesAndCharacterRecovery()
    {
        var ct = TestContext.Current.CancellationToken; var (project, store) = CreateStore(); var owner = Asset("Riley");
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [owner] }, 0, ct);
        var reel = new AssetReferenceReel { AssetId = owner.Id, Media = new(Guid.NewGuid(), new('A', 64), 5, 64, 64, 124, 24, 124 / 24d, false), CreatedUtc = DateTimeOffset.UtcNow };
        library = await store.SaveReelAsync(project.Id, reel, library.Revision, ct);
        var draft = ReferenceReelTests.Recipe(); draft.AssetId = owner.Id;
        draft = await store.SaveDraftAsync(project.Id, draft, 0, ct);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.SaveDraftAsync(project.Id, draft, 0, ct));
        library = await store.LoadAsync(project.Id, ct);
        library = await store.SaveAsync(library with { Reels = [], ReelDrafts = [] }, library.Revision, ct);
        Assert.Single(library.Reels); Assert.Single(library.ReelDrafts);
        library = await store.DeleteAssetAsync(project.Id, owner.Id, library.Revision, ct);
        Assert.Empty(library.Reels); Assert.Single(library.ReelTrash);
        library = await store.RestoreReelAsync(project.Id, reel.Id, library.Revision, ct);
        Assert.Equal(owner.Id, Assert.Single(library.Assets).Id); Assert.Equal(reel, Assert.Single(library.Reels));
        Assert.Single(library.ReelDrafts);
    }
    [Fact]
    public async Task ReelPublicationIsIdempotentEvenAfterDeletion()
    {
        var ct = TestContext.Current.CancellationToken; var (project, store) = CreateStore(); var owner = Asset("Riley");
        await store.SaveAsync(new() { ProjectId = project.Id, Assets = [owner] }, 0, ct);
        var recipe = ReferenceReelTests.Recipe(); recipe.AssetId = owner.Id;
        var snapshot = new VideoSnapshot(project.Id, 0, ReferenceReels.Inputs(recipe), "", "", "", new(), 64, 64, 124);
        var reel = new AssetReferenceReel { AssetId = owner.Id, Media = new(Guid.NewGuid(), new('A', 64), 5, 64, 64, 124, 24, 124 / 24d, false),
            Generation = new(recipe, Guid.NewGuid(), Guid.NewGuid(), 1, 42, snapshot) };
        var library = await store.PublishReelAsync(project.Id, reel, ct);
        var revision = library.Revision;
        Assert.Equal(revision, (await store.PublishReelAsync(project.Id, reel, ct)).Revision);
        library = await store.TrashReelAsync(project.Id, reel.Id, revision, ct);
        library = await store.PublishReelAsync(project.Id, reel, ct);
        Assert.Empty(library.Reels); Assert.Single(library.ReelTrash);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.PublishReelAsync(project.Id, reel with { Name = "Different result" }, ct));
    }
}
