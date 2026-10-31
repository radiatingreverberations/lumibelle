using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class EnvironmentReelTests
{
    private static ReferenceAsset Owner() => new() { Id = Guid.NewGuid(), Category = AssetCategory.Environment,
        Name = "Study", Description = "A desk beneath the window.", PreservationGuidance = "Keep the brick wall opposite the desk." };
    [Fact]
    public void DefaultsAreEmptySilentWideAndDoNotChangeCharacterRecipes()
    {
        var owner = Owner(); var draft = ReferenceReels.NewDraft(owner);
        Assert.Equal(ReelFraming.EnvironmentFullSet, draft.Framing); Assert.Equal("16:9", draft.Aspect);
        Assert.Equal(15, draft.Duration); Assert.Equal(362 / 24d, H3Policy.Seconds(draft.Duration));
        Assert.Equal(ReelCameraDirection.Right, draft.CameraDirection);
        Assert.Empty(draft.Images); Assert.Empty(draft.Prompt); Assert.Empty(draft.UseGuidance);
        Assert.Equal(ReelVoiceMode.Silent, draft.VoiceMode); Assert.Null(draft.Voice); Assert.Empty(draft.Line); Assert.Null(draft.LookId);
        var character = ReferenceReels.NewDraft(owner with { Category = AssetCategory.Character });
        var old = new ReferenceReelDraft { Id = character.Id, AssetId = owner.Id, Name = owner.Name + " reference", Speaker = owner.Name };
        Assert.True(character.SaveLosslessFrames);
        Assert.Equal(ReferenceReels.Fingerprint(old), ReferenceReels.Fingerprint(character with { SaveLosslessFrames = null }));
        Assert.Equal(0, (int)ReelFraming.ContinuousTurn); Assert.Equal(3, (int)ReelFraming.Custom); Assert.Equal(4, (int)ReelFraming.SideRearFace);
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.NewDraft(owner with { Category = AssetCategory.Reference }));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SilentReelInstructionsRequestExplicitSoundSectionsAndRetainReviewNotes(bool environment)
    {
        var owner = Owner() with { Category = environment ? AssetCategory.Environment : AssetCategory.Character };
        var draft = ReferenceReels.NewDraft(owner); draft.VoiceMode = ReelVoiceMode.Silent;
        draft.Images = [new() { AssetId = owner.Id, MediaId = Guid.NewGuid() }];
        var payload = new ReelCompositionRequest(Guid.NewGuid(), draft, ReferenceReels.Fingerprint(draft), LookPolicy.Capture(owner, null), []);
        var instructions = ReferenceReels.Messages(payload, [new byte[] { 1 }])[0].Text!;
        Assert.Contains("No non-diegetic music.", instructions);
        Assert.Contains("Do not leave either sound section empty", instructions);
        var pair = ReferenceReels.Preset(draft);
        var broken = pair with { Prompt = pair.Prompt.Replace("No non-diegetic music.", "") };
        var request = new AiTextJobRequest(2, AiJobKind.ReelComposition, new(AiBackend.OpenRouter, "mock", "Mock"),
            true, new(), draft.PresetVersion, .5f, 1, JsonSerializer.SerializeToElement(payload, AtomicJsonFile.Options), []);
        var raw = JsonSerializer.Serialize(broken, AtomicJsonFile.Options);
        var result = AiTextResults.Parse(request, raw, "stop");
        Assert.Null(result.Error);
        Assert.Equal("Complete the non_diegetic_music section.", ReferenceReels.PromptWarning(broken, draft));
        Assert.Equal(broken, result.Read<ReelPromptPair>()); Assert.Equal(raw, result.Raw);
        Assert.Null(AiTextResults.Parse(request, JsonSerializer.Serialize(pair, AtomicJsonFile.Options), "stop").Error);
    }
    [Theory]
    [InlineData(ReelFraming.EnvironmentFullSet, 1)] [InlineData(ReelFraming.EnvironmentFullSet, 2)]
    [InlineData(ReelFraming.EnvironmentFeatureStudy, 1)] [InlineData(ReelFraming.EnvironmentPath, 2)]
    public void EnvironmentPairsCaptureViewsCropsAndContextWithNoAudio(ReelFraming framing, int count)
    {
        var owner = Owner(); var draft = ReferenceReels.NewDraft(owner);
        draft.Framing = framing; draft.Instructions = "Travel from the doorway toward the desk — café.\nKeep the window visible.";
        draft.Images = Enumerable.Range(1, count).Select(n => new ShotImageBinding { AssetId = owner.Id, MediaId = Guid.NewGuid(),
            Notes = $"View {n}", PreservationOverride = $"Preserve brickwork {n}", InferUsage = true, Crop = new() { X = 0, Y = 0, Width = .5, Height = 1 } }).ToList();
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [owner] };
        var pair = ReferenceReels.Preset(draft, library); draft.Prompt = pair.Prompt; draft.UseGuidance = pair.UseGuidance;
        ReferenceReels.Validate(draft, true);
        Assert.Contains("15.083 seconds", pair.Prompt); Assert.Contains(ReferenceReels.Views(draft), pair.Prompt);
        Assert.Contains("brick wall", pair.Prompt); Assert.Contains("brick wall", pair.UseGuidance);
        Assert.Contains("Complete unseen areas plausibly", pair.Prompt); Assert.Contains("not verified output", pair.UseGuidance);
        Assert.DoesNotContain("<d>", pair.Prompt); Assert.DoesNotContain("<Audio", pair.Prompt); Assert.DoesNotContain("(S1)", pair.Prompt);
        var inputs = ReferenceReels.Inputs(draft); Assert.Empty(inputs.Dialogue); Assert.Empty(inputs.Voices);
        Assert.Null(inputs.SceneId); Assert.Null(inputs.ApprovedScriptId); Assert.Equal(draft.Images[0].Crop, inputs.Images[0].Crop);
        var request = new ReelCompositionRequest(Guid.NewGuid(), draft, ReferenceReels.Fingerprint(draft), LookPolicy.Capture(owner, null), [])
            { ImageGuidance = ShotReferences.Resolve(inputs, library, new()) };
        var messages = ReferenceReels.Messages(request, [new byte[] { 1, 2 }]);
        using var context = JsonDocument.Parse(messages[1].Text!);
        Assert.Equal(owner.Description, context.RootElement.GetProperty("environment").GetProperty("visualNotes").GetString());
        Assert.Contains("unseen areas", messages[0].Text); Assert.DoesNotContain("character reference", messages[0].Text);
        Assert.Equal(count, context.RootElement.GetProperty("draft").GetProperty("images").GetArrayLength());
        Assert.Contains(draft.Instructions, context.RootElement.GetProperty("draft").GetProperty("instructions").GetString());
        var settings = new H3Settings(); var size = H3Policy.Size(draft.Aspect, false);
        var snapshot = new VideoSnapshot(request.ProjectId, 0, inputs, pair.Prompt, H3Policy.Fingerprint(inputs), "http://localhost:8188", settings,
            size.Width, size.Height, H3Policy.Frames(draft.Duration), draft.PresetVersion) {
            Reel = new(draft.Copy(), request.Character) { Owner = owner }, OutputPolicy = new(inputs.SaveLosslessFrames),
            Preset = H3Presets.Capture(inputs, settings), Performance = H3Performance.Capture(H3Presets.NewPerformance(settings)), Sampling = H3Policy.Sampling(inputs, settings) };
        var captured = new AiVideoJobRequest(3, Guid.NewGuid(), snapshot, Enumerable.Range(1, count).Select(n => new AiVideoInput($"image-{n}.png", false, 4, new('A', 64))).ToArray());
        AiVideoJobPolicy.Validate(captured);
        Assert.Throws<WorkspaceStoreException>(() => AiVideoJobPolicy.Validate(captured with { Snapshot = snapshot with { Profile = ReferenceReels.Profile } }));
        var graph = JsonSerializer.Serialize(ComfyH3Video.BuildWorkflow(snapshot, 42, "client", captured.Inputs.Select(i => new PreparedVideoInput(i.FileName, false)).ToArray()));
        Assert.Contains("environment reference reel", graph); Assert.DoesNotContain("LoadAudio", graph);
        var bad = pair with { Prompt = pair.Prompt.Replace("15.083 seconds", "15 seconds") };
        ReferenceReels.ValidatePair(bad, draft);
        Assert.Contains("15.083 seconds", ReferenceReels.PromptWarning(bad, draft));
    }
    [Fact]
    public void PathRequiresCompositionInstructionsButManualGenerationIsIndependent()
    {
        var draft = ReferenceReels.NewDraft(Owner()); draft.Images = [new() { AssetId = draft.AssetId, MediaId = Guid.NewGuid(), InferUsage = true }];
        var authored = ReferenceReels.Preset(draft);
        draft.Framing = ReelFraming.EnvironmentPath;
        Assert.Contains("custom camera path", Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateComposition(draft)).Message);
        draft.Prompt = authored.Prompt; draft.UseGuidance = authored.UseGuidance;
        ReferenceReels.Validate(draft, true);
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(draft with { VoiceMode = ReelVoiceMode.NewVoice }));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(draft with { LookId = Guid.NewGuid() }));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(draft with { Framing = ReelFraming.ThreeAngles }));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateOwner(draft, Owner() with { Id = draft.AssetId, Category = AssetCategory.Character }));
    }

    public static IEnumerable<object[]> PresetsAndDirections() => ReferenceReels.EnvironmentPresets
        .SelectMany(p => Enum.GetValues<ReelCameraDirection>().Select(d => new object[] { p.Framing, d }));

    [Theory]
    [InlineData(ReelCameraDirection.Right, "clockwise")]
    [InlineData(ReelCameraDirection.Left, "counterclockwise")]
    public void FullSetAndFeatureStudyCoverAllSidesWithDifferentFraming(ReelCameraDirection direction, string name)
    {
        var draft = ReferenceReels.NewDraft(Owner()); draft.CameraDirection = direction;
        var full = ReferenceReels.Views(draft);
        Assert.Contains($"Travel {name}, as seen from above", full);
        Assert.Contains("complete 360° perimeter circuit", full);
        Assert.Contains("looking inward across the set", full);
        Assert.Contains("outside the central acting area", full);
        Assert.Contains("wide views of backgrounds and staging space", full);
        Assert.Contains("reverse background behind the opening camera", full);
        Assert.Contains("all three other sides", full);
        Assert.Contains("Do not approach the centre, zoom in, spin in place", full);
        Assert.Contains("approximately 360° from the opening position", full);
        ReferenceReels.SelectCameraPreset(draft, ReelFraming.EnvironmentFeatureStudy);
        var feature = ReferenceReels.Views(draft);
        Assert.Contains("complete 360° orbit", feature);
        Assert.Contains("whole feature and its immediate surroundings framed", feature);
        Assert.Contains("not coverage of the entire environment", feature);
        Assert.Contains("back of the feature", feature);
        Assert.Contains("all three other angles", feature);
        Assert.Contains("strong parallax", feature);
        Assert.Contains(name, feature);
        Assert.Equal(direction == ReelCameraDirection.Left ? "Counterclockwise" : "Clockwise", ReferenceReels.CameraDirectionLabel(draft, direction));
    }

    [Fact]
    public void WalkthroughAndCustomFollowAuthoredRoutesWithoutPrescribingAnOrbit()
    {
        var draft = ReferenceReels.NewDraft(Owner());
        ReferenceReels.SelectCameraPreset(draft, ReelFraming.EnvironmentWalkthrough);
        var walk = ReferenceReels.Views(draft);
        Assert.Contains("revealing how the spaces join", walk);
        Assert.Contains("author's destination", walk);
        Assert.Contains("hold the final view completely still until 15.083 seconds", walk);
        ReferenceReels.SelectCameraPreset(draft, ReelFraming.EnvironmentPath);
        draft.Instructions = "Move gently from the doorway to the rug at slow speed.";
        var custom = ReferenceReels.Views(draft);
        Assert.Contains("extent and speed they specify", custom);
        Assert.Contains("arriving at the specified destination", custom);
        Assert.DoesNotContain("360°", custom);
        Assert.DoesNotContain("fast speed", custom);
    }

    [Theory, MemberData(nameof(PresetsAndDirections))]
    public void EveryPresetProducesOneCapturedReelWithTheSameCameraPlan(ReelFraming framing, ReelCameraDirection direction)
    {
        var owner = Owner(); var draft = ReferenceReels.NewDraft(owner);
        ReferenceReels.SelectCameraPreset(draft, framing); draft.CameraDirection = direction;
        draft.Images = [new() { AssetId = owner.Id, MediaId = Guid.NewGuid(), InferUsage = true }];
        draft.Instructions = "Use the desk as the feature. Travel from the doorway along the wall to the desk. Keep the brick wall.";
        var preset = ReferenceReels.CameraPreset(draft)!;
        Assert.Equal(preset.Seconds, draft.Duration);
        var views = ReferenceReels.Views(draft);
        var pair = ReferenceReels.Preset(draft);
        ReferenceReels.ValidatePair(pair, draft);
        Assert.Contains(views, pair.Prompt);
        var shots = System.Text.RegularExpressions.Regex.Matches(views, @"\[Shot (\d+)\]").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "1" }, shots);
        var request = new ReelCompositionRequest(Guid.NewGuid(), draft, ReferenceReels.Fingerprint(draft), LookPolicy.Capture(owner, null), []);
        var messages = ReferenceReels.Messages(request, []);
        using var context = JsonDocument.Parse(messages[1].Text!);
        Assert.Equal(views, context.RootElement.GetProperty("presetViews").GetString());
        Assert.Contains("within this one reel", messages[0].Text);
        Assert.Contains("resolve conflicts", messages[0].Text);
        Assert.Contains("outside the central acting area", messages[0].Text);
        Assert.Contains("not a verified reconstruction", messages[0].Text);
        Assert.Contains("one [Shot 1] section", messages[0].Text);
        Assert.DoesNotContain("subsequent cuts", messages[0].Text);
        Assert.Contains("Preserve physical positions and relationships within the environment while screen positions change", pair.Prompt);
        Assert.Contains("Preserve physical positions and relationships within the environment while screen positions change", messages[0].Text);
        Assert.Contains("physical relationships for retention and use guidance", messages[0].Text);
        Assert.DoesNotContain("first picture establishes the opening composition", messages[0].Text);
        Assert.DoesNotContain("Establishes the opening composition only", pair.Prompt);
        Assert.Contains("not a requirement to keep its exact framing", pair.Prompt);
        Assert.Empty(ReferenceReels.Inputs(draft).Voices);
        draft.Prompt = pair.Prompt; draft.UseGuidance = pair.UseGuidance;
        var inputs = ReferenceReels.Inputs(draft); var settings = new H3Settings(); var size = H3Policy.Size(draft.Aspect, false);
        var snapshot = new VideoSnapshot(request.ProjectId, 0, inputs, pair.Prompt, H3Policy.Fingerprint(inputs), "http://localhost:8188", settings,
            size.Width, size.Height, H3Policy.Frames(draft.Duration), draft.PresetVersion) {
            Reel = new(draft.Copy(), request.Character) { Owner = owner }, OutputPolicy = new(true),
            Preset = H3Presets.Capture(inputs, settings), Performance = H3Performance.Capture(H3Presets.NewPerformance(settings)), Sampling = H3Policy.Sampling(inputs, settings) };
        var captured = new AiVideoJobRequest(3, Guid.NewGuid(), snapshot, [new("image-1.png", false, 4, new('A', 64))]);
        AiVideoJobPolicy.Validate(captured);
        var graph = ComfyH3Video.BuildWorkflow(snapshot, 42, "client", [new("image-1.png", false)]);
        var graphJson = JsonSerializer.Serialize(graph);
        Assert.DoesNotContain("LoadAudio", graphJson); Assert.DoesNotContain("LoadVideo", graphJson);
        using var graphDoc = JsonDocument.Parse(graphJson);
        Assert.Equal(H3Policy.Frames(draft.Duration), graphDoc.RootElement.GetProperty("prompt").GetProperty("5").GetProperty("inputs").GetProperty("length").GetInt32());
    }

    [Theory]
    [InlineData(2)] [InlineData(6)] [InlineData(10)] [InlineData(15)]
    public void TimingsUseActualDurationAndHoldsFitEvenShortReels(double duration)
    {
        var draft = ReferenceReels.NewDraft(Owner()); draft.Duration = duration;
        var total = H3Policy.Frames(duration) / 24d;
        foreach (var framing in new[] { ReelFraming.EnvironmentFullSet, ReelFraming.EnvironmentFeatureStudy }) {
            draft.Framing = framing;
            var intervals = System.Text.RegularExpressions.Regex.Matches(ReferenceReels.Views(draft), @"[Ff]rom (\d+(?:\.\d+)?) to (\d+(?:\.\d+)?) seconds")
                .Select(m => (Start: double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), End: double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
            Assert.Equal(8, intervals.Length);
            Assert.Equal(0, intervals[0].Start);
            Assert.InRange(Math.Abs(intervals[^1].End - total), 0, .0005);
            for (var i = 0; i < intervals.Length; i++) {
                Assert.True(intervals[i].End > intervals[i].Start);
                Assert.InRange(Math.Abs(intervals[i].Start * 24 - Math.Round(intervals[i].Start * 24)), 0, .012);
                if (i > 0) Assert.Equal(intervals[i - 1].End, intervals[i].Start);
            }
            Assert.InRange(intervals[^1].End - intervals[^1].Start, .001, 1.001);
        }
        foreach (var framing in new[] { ReelFraming.EnvironmentWalkthrough, ReelFraming.EnvironmentPath }) {
            draft.Framing = framing;
            Assert.Contains($"until {total.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} seconds", ReferenceReels.Views(draft));
        }
    }

    [Fact]
    public void SelectionReuseAndReloadPreserveAuthoredInputs()
    {
        var draft = ReferenceReels.NewDraft(Owner()); draft.Prompt = "My prompt"; draft.UseGuidance = "My guidance";
        draft.Instructions = "Around the desk"; draft.CameraDirection = ReelCameraDirection.Left; draft.Duration = 6;
        draft.Images = [new() { AssetId = draft.AssetId, MediaId = Guid.NewGuid(), Crop = new() { X = 0, Y = 0, Width = .5, Height = 1 } }];
        draft.CheckedInputs = ReferenceReels.InputsFingerprint(draft);
        var before = draft.Copy();
        ReferenceReels.SelectCameraPreset(draft, ReelFraming.EnvironmentFeatureStudy);
        Assert.Equal(15, draft.Duration); Assert.Null(draft.CheckedInputs);
        Assert.Equal(before.Prompt, draft.Prompt); Assert.Equal(before.UseGuidance, draft.UseGuidance);
        Assert.Equal(before.Instructions, draft.Instructions); Assert.Equal(before.Images, draft.Images);
        Assert.Equal(ReelCameraDirection.Left, draft.CameraDirection);
        var source = ReferenceReels.NewDraft(Owner()); source.Instructions = "Saved directions";
        draft.Duration = 6;
        ReferenceReels.ReuseDirections(draft, source);
        Assert.Equal(source.Framing, draft.Framing); Assert.Equal(source.CameraDirection, draft.CameraDirection);
        Assert.Equal(6, draft.Duration); Assert.Equal(before.Prompt, draft.Prompt); Assert.Equal(before.Images, draft.Images);
        Assert.Equal(source.Instructions, draft.Instructions);
        var reload = JsonSerializer.Deserialize<ReferenceReelDraft>(JsonSerializer.Serialize(draft, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
        Assert.Equal(ReferenceReels.Fingerprint(draft), ReferenceReels.Fingerprint(reload));
        Assert.Equal(ReferenceReels.Views(draft), ReferenceReels.Views(reload));
        Assert.NotNull(ReferenceReels.DurationAdvice(draft)); Assert.False(ReferenceReels.ShowKeyframeHint(draft));
        draft.Duration = 10; Assert.True(ReferenceReels.ShowKeyframeHint(draft));
    }

    [Theory]
    [InlineData(ReelFraming.EnvironmentFeatureStudy)] [InlineData(ReelFraming.EnvironmentWalkthrough)] [InlineData(ReelFraming.EnvironmentPath)]
    public void FeatureAndRoutePresetsRequireDirectionsForComposition(ReelFraming framing)
    {
        var draft = ReferenceReels.NewDraft(Owner()); ReferenceReels.SelectCameraPreset(draft, framing);
        Assert.NotNull(ReferenceReels.CompositionInstructionsIssue(draft));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateComposition(draft));
        draft.Instructions = "From the doorway, along the wall, to the desk.";
        ReferenceReels.ValidateComposition(draft);
        draft.CameraDirection = (ReelCameraDirection)99;
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidateProfile(draft));
    }

    [Theory]
    [InlineData(ReelFraming.EnvironmentTurn, 5)] [InlineData(ReelFraming.EnvironmentPan, 6)]
    [InlineData(ReelFraming.EnvironmentHalfTurn, 8)] [InlineData(ReelFraming.EnvironmentHeldViews, 9)]
    [InlineData(ReelFraming.EnvironmentPullBack, 10)] [InlineData(ReelFraming.EnvironmentApproach, 11)] [InlineData(ReelFraming.EnvironmentArc, 12)]
    public void RemovedPresetsRetainHistoryButRequireExplicitReplacement(ReelFraming removed, int storedId)
    {
        var draft = ReferenceReels.NewDraft(Owner());
        draft.Images = [new() { AssetId = draft.AssetId, MediaId = Guid.NewGuid(), Crop = new() { X = 0, Y = 0, Width = .5, Height = 1 } }];
        var pair = ReferenceReels.Preset(draft); draft.Prompt = pair.Prompt; draft.UseGuidance = pair.UseGuidance;
        draft.Framing = removed; draft.Instructions = "My saved directions";
        var before = ReferenceReels.Fingerprint(draft);
        var reload = JsonSerializer.Deserialize<ReferenceReelDraft>(JsonSerializer.Serialize(draft, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
        Assert.Equal(storedId, (int)reload.Framing);
        Assert.Equal(before, ReferenceReels.Fingerprint(reload));
        ReferenceReels.Validate(reload); ReferenceReels.ValidatePair(pair, reload);
        Assert.True(ReferenceReels.NeedsEnvironmentReplacement(reload));
        Assert.Equal(ReferenceReels.EnvironmentReplacementRequired, ReferenceReels.CompositionInstructionsIssue(reload));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Preset(reload));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Views(reload));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Views(removed));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.Validate(reload, true));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.SelectCameraPreset(reload, removed));
        ReferenceReels.SelectCameraPreset(reload, ReelFraming.EnvironmentFullSet);
        Assert.False(ReferenceReels.NeedsEnvironmentReplacement(reload));
        Assert.Equal(draft.Prompt, reload.Prompt); Assert.Equal(draft.UseGuidance, reload.UseGuidance);
        Assert.Equal(draft.Instructions, reload.Instructions); Assert.Equal(draft.Images, reload.Images);
        Assert.Equal(before, ReferenceReels.Fingerprint(draft));
        ReferenceReels.Validate(reload, true);
    }

    [Fact]
    public void OnlyFourCoveragePresetsAreAvailableAndStoredIdsAreStable()
    {
        Assert.Equal(new[] { ReelFraming.EnvironmentFullSet, ReelFraming.EnvironmentFeatureStudy, ReelFraming.EnvironmentWalkthrough, ReelFraming.EnvironmentPath }, ReferenceReels.Framings(ReferenceReels.NewDraft(Owner())));
        Assert.Equal(7, (int)ReelFraming.EnvironmentPath); Assert.Equal(21, (int)ReelFraming.PropOrbit); Assert.Equal(27, (int)ReelFraming.PropCustom);
        Assert.Equal(28, (int)ReelFraming.EnvironmentFullSet); Assert.Equal(29, (int)ReelFraming.EnvironmentFeatureStudy); Assert.Equal(30, (int)ReelFraming.EnvironmentWalkthrough);
    }

    [Theory]
    [InlineData(AssetCategory.Environment)] [InlineData(AssetCategory.Character)] [InlineData(AssetCategory.Prop)]
    public void ImageEntryPointCreatesAnIndependentRecipeWithTheChosenImage(AssetCategory category)
    {
        var image = new AssetImage { Id = Guid.NewGuid(), Name = "Opening view", FileName = "view.png", ContentType = "image/png", Width = 64, Height = 64 };
        var owner = Owner() with { Category = category, Images = [image] };
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [owner] };
        var first = ReferenceReels.DraftFromImage(owner, image.Id, library);
        var second = ReferenceReels.DraftFromImage(owner, image.Id, library);
        Assert.NotEqual(first.Id, second.Id);
        var binding = Assert.Single(first.Images);
        Assert.Equal(owner.Id, binding.AssetId); Assert.Equal(image.Id, binding.MediaId);
        Assert.Equal(ShotImageKind.AssetImage, binding.Kind); Assert.True(binding.InferUsage);
        Assert.Empty(first.Prompt); Assert.Empty(first.UseGuidance);
        ReferenceReels.Validate(first);
        var unnamedOwner = owner with { Images = [image with { Name = null }] };
        var unnamed = ReferenceReels.DraftFromImage(unnamedOwner, image.Id, library);
        Assert.Equal(owner.Name, unnamed.Images[0].Name);
        ReferenceReels.Validate(unnamed);
        if (category == AssetCategory.Environment) Assert.Equal(ReelFraming.EnvironmentFullSet, first.Framing);
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.DraftFromImage(owner, Guid.NewGuid(), library));
    }
}
