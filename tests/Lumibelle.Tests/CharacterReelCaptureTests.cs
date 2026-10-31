using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class CharacterReelCaptureTests
{
    private static ReferenceReelDraft Recipe(ReelArticulation movement = ReelArticulation.Arms,
        ReelCloseUpTransition transition = ReelCloseUpTransition.Cut, double seconds = 10)
    {
        var draft = ReferenceReelTests.Recipe();
        ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterCapture);
        draft.CaptureArticulation = movement; draft.CaptureCloseUp = transition; draft.Duration = seconds;
        return draft;
    }

    [Fact]
    public void CaptureDefaultsAreOptInAndLeaveAuthoredTextReferencesAndVoiceDataIntact()
    {
        var draft = ReferenceReelTests.Recipe(voice: ReelVoiceMode.ExistingRecording);
        draft.Prompt = "Authored prompt"; draft.UseGuidance = "Authored guidance"; draft.Instructions = "Use picture 1 for the face.";
        draft.CheckedInputs = ReferenceReels.InputsFingerprint(draft);
        var before = draft.Copy();
        ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterCapture);
        Assert.Equal(10, draft.Duration); Assert.Equal(243, H3Policy.Frames(draft.Duration));
        Assert.Equal(ReelVoiceMode.Silent, draft.VoiceMode);
        Assert.Equal(ReelArticulation.Arms, draft.CaptureArticulation); Assert.Equal(ReelCloseUpTransition.Cut, draft.CaptureCloseUp);
        Assert.Equal(before.Prompt, draft.Prompt); Assert.Equal(before.UseGuidance, draft.UseGuidance);
        Assert.Equal(before.Instructions, draft.Instructions); Assert.Equal(before.Voice, draft.Voice); Assert.Equal(before.Line, draft.Line);
        Assert.Equal(before.Images.Select(i => i.Id), draft.Images.Select(i => i.Id)); Assert.Null(draft.CheckedInputs);
        Assert.Empty(ReferenceReels.Inputs(draft).Voices); Assert.Empty(ReferenceReels.Inputs(draft).Dialogue);
        draft.Duration = 9;
        ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterCapture);
        Assert.Equal(9, draft.Duration); // Choosing the already-selected preset must not reset edits.
        ReferenceReels.SelectCharacterPreset(draft, ReelFraming.BodyToFace);
        Assert.Null(draft.CaptureArticulation); Assert.Null(draft.CaptureCloseUp);
        Assert.Equal(9, draft.Duration); Assert.Equal(before.Prompt, draft.Prompt);
    }

    [Fact]
    public void LegacyWireShapeEnumValuesAndFingerprintsAreUnchanged()
    {
        Assert.Equal(0, (int)ReelFraming.ContinuousTurn); Assert.Equal(3, (int)ReelFraming.Custom);
        Assert.Equal(4, (int)ReelFraming.SideRearFace); Assert.Equal(12, (int)ReelFraming.EnvironmentArc);
        Assert.Equal(13, (int)ReelFraming.CharacterCapture);
        var draft = ReferenceReelTests.Recipe();
        var json = JsonSerializer.SerializeToNode(draft, AtomicJsonFile.Options)!.AsObject();
        Assert.False(json.ContainsKey("captureArticulation")); Assert.False(json.ContainsKey("captureCloseUp"));
        var expected = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(json, AtomicJsonFile.Options)));
        Assert.Equal(expected, ReferenceReels.Fingerprint(draft));
        var reopened = json.Deserialize<ReferenceReelDraft>(AtomicJsonFile.Options)!;
        Assert.Equal(expected, ReferenceReels.Fingerprint(reopened));
        Assert.Equal(5, reopened.Duration); Assert.Equal(ReelFraming.ContinuousTurn, reopened.Framing);
        Assert.Contains("5.167 seconds", ReferenceReels.Preset(reopened).Prompt);
    }

    [Fact]
    public void CaptureOptionsRoundTripAndInvalidateOnlyTheEditedRecipeBaseline()
    {
        var draft = Recipe(); var original = ReferenceReels.Fingerprint(draft);
        var inputs = ReferenceReels.InputsFingerprint(draft); var copy = draft.Copy();
        copy.CaptureArticulation = ReelArticulation.Knee;
        Assert.NotEqual(inputs, ReferenceReels.InputsFingerprint(copy));
        Assert.Equal(original, ReferenceReels.Fingerprint(draft));
        copy = draft.Copy(); copy.CaptureCloseUp = ReelCloseUpTransition.PushIn;
        Assert.NotEqual(inputs, ReferenceReels.InputsFingerprint(copy));
        var reopened = JsonSerializer.Deserialize<ReferenceReelDraft>(JsonSerializer.Serialize(copy, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
        Assert.Equal(copy.CaptureCloseUp, reopened.CaptureCloseUp);
        Assert.Equal(ReferenceReels.Fingerprint(copy), ReferenceReels.Fingerprint(reopened));
    }

    [Theory]
    [InlineData(ReelArticulation.None, ReelCloseUpTransition.Cut, 2)]
    [InlineData(ReelArticulation.Arms, ReelCloseUpTransition.Cut, 2)]
    [InlineData(ReelArticulation.Knee, ReelCloseUpTransition.Cut, 2)]
    [InlineData(ReelArticulation.None, ReelCloseUpTransition.PushIn, 1)]
    [InlineData(ReelArticulation.Arms, ReelCloseUpTransition.PushIn, 1)]
    [InlineData(ReelArticulation.Knee, ReelCloseUpTransition.PushIn, 1)]
    public void TenSecondCoverageUsesOneOrbitThenTheSelectedMovementAndCloseUp(ReelArticulation movement, ReelCloseUpTransition transition, int shots)
    {
        var draft = Recipe(movement, transition); var pair = ReferenceReels.Preset(draft);
        ReferenceReels.ValidatePair(pair, draft);
        var views = ReferenceReels.Views(draft);
        Assert.Contains("360-degree", views); Assert.Contains("front-right three-quarter", views);
        Assert.Contains("right profile", views); Assert.Contains("full back", views); Assert.Contains("left profile", views);
        Assert.Contains("halfway through the orbit", views); Assert.Contains("does not rotate with the camera", views);
        Assert.Contains("not the moving lens", views); Assert.Contains("anatomical sides", views);
        Assert.Equal(shots, Regex.Matches(views, @"\[Shot \d+\]").Count);
        Assert.Contains("10.125 seconds", views); Assert.Contains("10.125 seconds", pair.Prompt);
        Assert.Equal(movement == ReelArticulation.Arms, views.Contains("lifts both arms"));
        Assert.Equal(movement == ReelArticulation.Knee, views.Contains("bends the right knee"));
        if (movement == ReelArticulation.None) Assert.Contains("stop by 8.125 seconds", views);
        else Assert.Contains("From 6.125 to 8.125 seconds keep the camera stationary", views);
        if (transition == ReelCloseUpTransition.Cut) Assert.Contains("[Shot 2] At 00:08.125", views);
        else { Assert.Contains("From 8.125 to 9.125 seconds", views); Assert.Contains("hold a settled face close-up until 10.125 seconds", views); }
        Assert.Contains(views, pair.Prompt); Assert.Contains("partially_preserved", pair.Prompt);
        Assert.Contains("not new evidence", pair.UseGuidance); Assert.Contains("not verified output", pair.UseGuidance);
        Assert.DoesNotContain("<d>", pair.Prompt); Assert.DoesNotContain("<Audio", pair.Prompt);
    }

    [Theory]
    [InlineData(5)] [InlineData(7.9)]
    public void ShorterCaptureUsesFiveHeldViewsWithoutArticulationOrPushIn(double seconds)
    {
        var draft = Recipe(ReelArticulation.Knee, ReelCloseUpTransition.PushIn, seconds);
        var views = ReferenceReels.Views(draft);
        Assert.Equal(5, Regex.Matches(views, @"\[Shot \d+\]").Count);
        Assert.Equal(4, Regex.Matches(views, @"At \d\d:\d\d\.\d{3}").Count);
        Assert.Contains("right-profile full-body", views); Assert.Contains("rear full-body", views); Assert.Contains("left-profile full-body", views);
        Assert.Contains("No articulation", views); Assert.DoesNotContain("bends the right knee", views);
        Assert.DoesNotContain("push the camera", views); Assert.DoesNotContain("360-degree", views);
        Assert.Contains($"until {H3Policy.Seconds(seconds).ToString("0.###", CultureInfo.InvariantCulture)} seconds", views);
        ReferenceReels.ValidatePair(ReferenceReels.Preset(draft), draft);
        Assert.Equal(ReelArticulation.Knee, draft.CaptureArticulation); Assert.Equal(ReelCloseUpTransition.PushIn, draft.CaptureCloseUp);
    }

    [Theory]
    [InlineData(8, false)] [InlineData(8.9, false)] [InlineData(9, true)] [InlineData(10, true)] [InlineData(15, true)]
    public void MovementThresholdUsesRequestedDurationNotRoundedOutputDuration(double seconds, bool movement)
    {
        var draft = Recipe(seconds: seconds); var views = ReferenceReels.Views(draft);
        Assert.Contains("360-degree", views);
        Assert.Equal(movement, views.Contains("lifts both arms"));
        var closeUp = (H3Policy.Frames(seconds) - 48) / 24d;
        Assert.Contains("At " + TimeSpan.FromSeconds(closeUp).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture), views);
        ReferenceReels.ValidatePair(ReferenceReels.Preset(draft), draft);
    }

    [Fact]
    public void CompositionRequiresEnoughTimeAndRejectsUnknownOrMisplacedOptions()
    {
        var draft = Recipe(seconds: 4.9);
        Assert.Contains("at least 5", ReferenceReels.CompositionInstructionsIssue(draft));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateComposition(draft));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Views(draft));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Preset(Recipe() with { CaptureArticulation = (ReelArticulation)99 }));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateProfile(Recipe() with { CaptureCloseUp = (ReelCloseUpTransition)99 }));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateProfile(Recipe() with { Framing = ReelFraming.Custom }));
        var env = ReferenceReels.NewDraft(new() { Id = Guid.NewGuid(), Category = AssetCategory.Environment, Name = "Study" });
        Assert.DoesNotContain(ReelFraming.CharacterCapture, ReferenceReels.Framings(env));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.SelectCharacterPreset(env, ReelFraming.CharacterCapture));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateProfile(env with { CaptureArticulation = ReelArticulation.None }));
        Assert.Throws<ArgumentException>(() => ReferenceReels.Views(ReelFraming.CharacterCapture));
    }

    [Theory]
    [InlineData(ReelVoiceMode.Silent)] [InlineData(ReelVoiceMode.NewVoice)] [InlineData(ReelVoiceMode.ExistingRecording)]
    public void VisionComposerGetsConcreteTimingActualReferencesAndTheChosenVoiceMode(ReelVoiceMode mode)
    {
        var draft = Recipe(ReelArticulation.Knee, ReelCloseUpTransition.PushIn);
        draft.VoiceMode = mode; draft.VoiceDescription = "Warm and lightly raspy.";
        if (mode == ReelVoiceMode.ExistingRecording) draft.Voice = ReferenceReelTests.Recipe(voice: mode).Voice;
        draft.Images[0].PreservationOverride = "Use the face only. Do not copy the pictured outfit.";
        draft.Instructions = "Outfit reference: Picture 2. Keep the glasses.";
        draft.Images.Add(new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), InferUsage = true, Notes = "Body proportions and outfit" });
        var payload = new ReelCompositionRequest(Guid.NewGuid(), draft, ReferenceReels.Fingerprint(draft),
            new(draft.AssetId, "Riley", "", "", null, "General", "", ""), [])
        { ImageGuidance = ShotReferences.Resolve(ReferenceReels.Inputs(draft), new() { ProjectId = Guid.NewGuid() }, new()) };
        var before = ReferenceReels.Fingerprint(draft);
        var messages = ReferenceReels.Messages(payload, [new byte[] { 1 }, new byte[] { 2 }]);
        var rules = messages[0].Text!;
        Assert.Contains("observations of ONE person", rules); Assert.Contains("ONE canonical outfit", rules);
        Assert.Contains("partially_preserved", rules); Assert.Contains("actual <Picture N>", rules);
        Assert.Contains("Do not infer", rules); Assert.Contains("not separate shots", rules);
        Assert.Contains("Return ordinary JSON with exactly prompt and useGuidance", rules);
        using var context = JsonDocument.Parse(messages[1].Text!);
        Assert.Equal(ReferenceReels.Views(draft), context.RootElement.GetProperty("presetViews").GetString());
        Assert.Equal(10.125, context.RootElement.GetProperty("generatedSeconds").GetDouble());
        Assert.Equal(draft.Instructions, context.RootElement.GetProperty("request").GetProperty("draft").GetProperty("instructions").GetString());
        Assert.Contains("Use the face only", context.RootElement.GetProperty("pictures")[0].GetString());
        Assert.Equal(2, messages[1].Contents.OfType<Microsoft.Extensions.AI.DataContent>().Count());
        var pair = ReferenceReels.Preset(draft); ReferenceReels.ValidatePair(pair, draft);
        Assert.Equal(mode == ReelVoiceMode.Silent ? 0 : 1, Regex.Matches(pair.Prompt, "<d>").Count);
        Assert.Equal(mode == ReelVoiceMode.ExistingRecording, pair.Prompt.Contains("<Audio 1>"));
        Assert.Equal(mode != ReelVoiceMode.Silent, pair.Prompt.Contains(draft.VoiceDescription));
        Assert.Equal(before, ReferenceReels.Fingerprint(draft));
    }

    [Theory]
    [InlineData("standard")] [InlineData("turbo4")]
    public void TenSecondRecipeReachesCapturedRequestAndRealWorkflowAt243Frames(string preset)
    {
        var draft = Recipe(); draft.GenerationPreset = preset;
        var pair = ReferenceReels.Preset(draft); draft.Prompt = pair.Prompt; draft.UseGuidance = pair.UseGuidance;
        ReferenceReels.Validate(draft, true);
        var shot = ReferenceReels.Inputs(draft); var settings = new H3Settings(); var size = VideoResolutions.Size(draft);
        var snapshot = new VideoSnapshot(Guid.NewGuid(), 1, shot, draft.Prompt, VideoResolutions.Fingerprint(draft),
            "http://localhost:8188", settings, size.Width, size.Height, H3Policy.Frames(draft.Duration), ReferenceReels.Profile)
        { Reel = new(draft.Copy(), new(draft.AssetId, "Riley", "", "", null, "General", "", "")), OutputPolicy = new(false),
            Preset = H3Presets.Capture(shot, settings), Performance = H3Performance.Capture(H3Presets.NewPerformance(settings)), Sampling = H3Policy.Sampling(shot, settings) };
        var request = new AiVideoJobRequest(3, Guid.NewGuid(), snapshot, [new("image.png", false, 4, new('A', 64))]);
        AiVideoJobPolicy.Validate(request);
        var reopened = JsonSerializer.Deserialize<AiVideoJobRequest>(JsonSerializer.Serialize(request, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
        AiVideoJobPolicy.Validate(reopened);
        Assert.Equal(10, reopened.Snapshot.Reel!.Recipe.Duration);
        Assert.Equal(ReelArticulation.Arms, reopened.Snapshot.Reel.Recipe.CaptureArticulation);
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(reopened.Snapshot, 42, "client", [new("image.png", false)]));
        var inputs = graph.GetProperty("prompt").GetProperty("5").GetProperty("inputs");
        Assert.Equal(243, inputs.GetProperty("length").GetInt32());
        Assert.Equal(pair.Prompt, inputs.GetProperty("prompt").GetString());
        var candidates = AiBatchDefinition.Create(Guid.NewGuid(), 2, 42).Candidates;
        var shared = JsonSerializer.SerializeToElement(ComfyMultiTakeWorkflow.Build(candidates,
            candidate => ComfyH3Video.BuildWorkflow(reopened.Snapshot, candidate.Seed, candidate.Id.ToString("D"), [new("image.png", false)]), "shared-client"))
            .GetProperty("prompt");
        Assert.Single(shared.EnumerateObject(), node => node.Value.GetProperty("class_type").GetString() == "MiniMaxH3ReferenceToVideo");
        Assert.Equal(243, shared.GetProperty("5").GetProperty("inputs").GetProperty("length").GetInt32());
        Assert.Equal(2, shared.EnumerateObject().Count(node => node.Value.GetProperty("class_type").GetString() == "SamplerCustomAdvanced"));
        Assert.Equal(draft.AssetId, AiVideoJobHandler.Target(reopened.Snapshot).AssetId);
        Assert.Null(shot.SceneId); Assert.Null(shot.ApprovedScriptId);
        Assert.Throws<WorkspaceStoreException>(() => AiVideoJobPolicy.Validate(request with { Snapshot = snapshot with { FrameCount = 124 } }));
    }

    [Fact]
    public void DirectionReuseCopiesCaptureOptionsWithoutReplacingTimingOrPromptTexts()
    {
        var source = Recipe(ReelArticulation.Knee, ReelCloseUpTransition.PushIn);
        source.Instructions = "Keep the hands in frame.";
        var target = ReferenceReelTests.Recipe(); target.Duration = 8; target.Prompt = "Authored prompt"; target.UseGuidance = "Authored guidance";
        var images = target.Images.Select(i => i.Id).ToArray(); var voice = target.VoiceMode;
        ReferenceReels.ReuseDirections(target, source);
        Assert.Equal(ReelFraming.CharacterCapture, target.Framing); Assert.Equal(source.CaptureArticulation, target.CaptureArticulation);
        Assert.Equal(source.CaptureCloseUp, target.CaptureCloseUp); Assert.Equal(8, target.Duration); Assert.Equal(voice, target.VoiceMode);
        Assert.Equal("Authored prompt", target.Prompt); Assert.Equal("Authored guidance", target.UseGuidance);
        Assert.Equal(images, target.Images.Select(i => i.Id)); Assert.Contains("No articulation", ReferenceReels.Views(target));
        ReferenceReels.ReuseDirections(target, ReferenceReelTests.Recipe());
        Assert.Null(target.CaptureArticulation); Assert.Null(target.CaptureCloseUp); ReferenceReels.ValidateProfile(target);
        Assert.True(ReferenceReels.ShowKeyframeHint(source)); Assert.False(ReferenceReels.ShowKeyframeHint(target));
    }
}

public sealed partial class AssetStoreTests
{
    [Theory]
    [InlineData("duration")] [InlineData("movement")] [InlineData("transition")]
    public async Task CharacterCaptureSettingsSurviveSaveAndBlockStaleAutomaticComposition(string change)
    {
        var ct = TestContext.Current.CancellationToken; var (project, store) = CreateStore();
        var owner = Asset("Riley") with { Category = AssetCategory.Character };
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [owner] }, 0, ct);
        using var image = new MemoryStream(Png(32, 32));
        library = await store.AddImageAsync(project.Id, owner.Id, image, new("face.png", [], AssetImageOrigin.Imported), library.Revision, ct);
        var draft = ReferenceReels.NewDraft(owner); ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterCapture);
        draft.Images = [new() { AssetId = owner.Id, MediaId = library.Assets.Single().Images.Single().Id, InferUsage = true }];
        var jobId = Guid.NewGuid(); draft.PendingJobId = jobId;
        draft = await store.SaveDraftAsync(project.Id, draft, 0, ct);
        var pair = ReferenceReels.Preset(draft);
        var inputs = await ProductionInputs.CaptureAsync(project.Id, ReferenceReels.Inputs(draft), store, ct);
        var request = new ReelCompositionRequest(project.Id, draft.Copy(), ReferenceReels.Fingerprint(draft), LookPolicy.Capture(owner, null), inputs.Select(i => i.Identity).ToArray())
        { ImageGuidance = ShotReferences.Resolve(ReferenceReels.Inputs(draft), library, new()) };
        var job = new AiJobHeader { Id = jobId, Kind = AiJobKind.ReelComposition, Backend = AiBackend.OpenRouter,
            Target = new(project.Id, owner.Id, ReelId: draft.Id), ProjectName = "Test", TargetName = "Reel", OriginTabId = Guid.NewGuid(), RequestFingerprint = "test", State = AiJobState.Completed };
        if (change == "duration") draft.Duration = 9;
        else if (change == "movement") draft.CaptureArticulation = ReelArticulation.Knee;
        else draft.CaptureCloseUp = ReelCloseUpTransition.PushIn;
        await store.SaveDraftAsync(project.Id, draft, draft.Revision, ct);
        Assert.False(await store.ApplyPairAsync(job, request, pair, true, ct));
        var saved = (await store.LoadAsync(project.Id, ct)).ReelDrafts.Single(d => d.Id == draft.Id);
        Assert.Equal(draft.Duration, saved.Duration); Assert.Equal(draft.CaptureArticulation, saved.CaptureArticulation);
        Assert.Equal(draft.CaptureCloseUp, saved.CaptureCloseUp); Assert.Empty(saved.Prompt); Assert.Empty(saved.UseGuidance);
        Assert.Equal(10, request.Draft.Duration); Assert.Equal(ReelArticulation.Arms, request.Draft.CaptureArticulation);
        Assert.Equal(ReelCloseUpTransition.Cut, request.Draft.CaptureCloseUp);
    }
}
