using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class PromptReferenceFreshnessTests
{
    private static ReferenceVideoMedia Media() => new(Guid.NewGuid(), new('A', 64), 100, 640, 640, 141, 24, 141d / 24, true);
    private static Shot Example()
    {
        var media = Media();
        return new() {
            Duration = 5, Description = "Hold on the character.",
            Images = [new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Face", Notes = "Keep glasses" },
                new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Outfit" }],
            Voices = [new() { AssetId = Guid.NewGuid(), VoiceId = Guid.NewGuid(), Speaker = "骆歆", Start = 1, Duration = 4 }],
            Videos = [new() { Media = media, Name = "Angles", Visuals = ReelVisuals.RefMod,
                Keyframes = new() { Frames = [new() { Frame = new(media.Id, media.Sha256, 0, 0) },
                    new() { Frame = new(media.Id, media.Sha256, 24, 1) }] },
                RefMod = new(new("trial", new('B', 64), new('C', 64), 640, 640, "video-vae.safetensors", [new('D', 64), new('E', 64)]),
                    "http://server-a:8188", "visual-ref.safetensors", Guid.NewGuid()) }]
        };
    }
    private static ShotReferenceGuidance[] Guidance(Shot s) => s.Images.Select(i => new ShotReferenceGuidance(i.Id, "Keep identity", "Keep the outfit", i.PreservationOverride)).ToArray();
    private static string Fingerprint(Shot s) => PromptReferenceFreshness.Fingerprint(s, Guidance(s));
    private static ProductionComposition Reviewed(Shot shot) {
        var revision = new CompositionPromptRevision(Guid.NewGuid(), DateTimeOffset.UtcNow, "Reviewed prompt", "Use the identity", "context", "source") {
            ReferenceFingerprint = Fingerprint(shot)
        };
        return new() { ShotId = shot.Id, Shot = shot, Prompt = revision.Prompt, History = [revision], AcceptedRevisionId = revision.Id };
    }

    [Fact] public void AcceptedDirectionFingerprintTracksStrategyChangesWithoutChangingReferences()
    {
        var shot = Example();
        var strategy = new DirectionStrategy { SceneId = Guid.NewGuid(), DramaticProgression = "Trust becomes doubt" };
        shot.DirectionStrategyId = strategy.Id; shot.Direction = new() { Intent = new() { Purpose = "Show suspicion" } };
        ShotDirections.Review(shot, strategy);
        var document = new ShotDocument { Shots = [shot], DirectionStrategies = [strategy] };
        var composition = Reviewed(shot);
        composition.History[0] = composition.History[0] with { DirectionFingerprint = ShotDirections.Fingerprint(document, shot) };
        Assert.Equal(PromptReferenceState.Current, PromptReferenceFreshness.Compare(composition, Fingerprint(shot), ShotDirections.Fingerprint(document, shot)).State);
        Assert.Equal(PromptReferenceState.NeedsReview, PromptReferenceFreshness.Compare(composition, Fingerprint(shot), null).State);
        // Reference-only callers retain their historical comparison contract.
        Assert.Equal(PromptReferenceState.Current, PromptReferenceFreshness.Compare(composition, Fingerprint(shot)).State);
        strategy.VisualProgression = "Isolate only after the letter is read";
        Assert.Equal(PromptReferenceState.NeedsReview, PromptReferenceFreshness.Compare(composition, Fingerprint(shot), ShotDirections.Fingerprint(document, shot)).State);
    }

    [Theory]
    [InlineData("replace-image")][InlineData("image-order")][InlineData("crop")]
    [InlineData("image-guidance")][InlineData("image-role")][InlineData("image-name")]
    [InlineData("frame-order")][InlineData("frame-crop")][InlineData("frame-index")][InlineData("frame-notes")]
    [InlineData("reel-description")][InlineData("visual-mode")][InlineData("refmod-pixels")][InlineData("refmod-canvas")]
    [InlineData("voice-source")][InlineData("voice-start")][InlineData("voice-duration")][InlineData("speaker")]
    [InlineData("remove-voice")][InlineData("add-picture")]
    public void MeaningfulReferenceEditsFlagTheReviewedPrompt(string change)
    {
        var original = Example(); var c = Reviewed(original); var next = original.Copy();
        switch (change) {
            case "replace-image": next.Images[0].MediaId = Guid.NewGuid(); break;
            case "image-order": next.Images.Reverse(); break;
            case "crop": next.Images[0].Crop = new() { Width = .5 }; break;
            case "image-guidance": next.Images[0].PreservationOverride = "Keep only the face"; break;
            case "image-role": next.Images[0].Use = ShotImageUse.FirstFrame; break;
            case "image-name": next.Images[0].Name = "Different subject"; break;
            case "frame-order": next.Videos[0].Keyframes!.Frames.Reverse(); break;
            case "frame-crop": next.Videos[0].Keyframes!.Frames[0].Crop = new() { Height = .5 }; break;
            case "frame-index": next.Videos[0].Keyframes!.Frames[0] = next.Videos[0].Keyframes!.Frames[0] with { Frame = new(next.Videos[0].Media.Id, new('A', 64), 2, 2d / 24) }; break;
            case "frame-notes": next.Videos[0].Keyframes!.Frames[0].Notes = "Rear view only"; break;
            case "reel-description": next.Videos[0].Description = "Use the room, not the person"; break;
            case "visual-mode": next.Videos[0].Visuals = ReelVisuals.Keyframes; break;
            case "refmod-pixels": next.Videos[0].RefMod = next.Videos[0].RefMod! with { Recipe = next.Videos[0].RefMod!.Recipe with { FrameHashes = [new('F', 64), new('E', 64)] } }; break;
            case "refmod-canvas": next.Videos[0].RefMod = next.Videos[0].RefMod! with { Recipe = next.Videos[0].RefMod!.Recipe with { Width = 768 } }; break;
            case "voice-source": next.Voices[0].VoiceId = Guid.NewGuid(); break;
            case "voice-start": next.Voices[0].Start++; break;
            case "voice-duration": next.Voices[0].Duration++; break;
            case "speaker": next.Voices[0].Speaker = "Other speaker"; break;
            case "remove-voice": next.Voices.Clear(); break;
            case "add-picture": next.Images.Add(new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid() }); break;
        }
        Assert.Equal(PromptReferenceState.ReferencesChanged, PromptReferenceFreshness.Compare(c, Fingerprint(next)).State);
        Assert.Equal(Fingerprint(original), c.Accepted!.ReferenceFingerprint);
    }

    [Fact]
    public void NoOpSaveSerializationAndNewBindingIdentitiesDoNotChangeContent()
    {
        var original = Example(); var c = Reviewed(original); var copy = original.Copy();
        foreach (var image in copy.Images) image.Id = Guid.NewGuid();
        copy.Videos[0].Id = Guid.NewGuid();
        copy.Videos[0].Keyframes = copy.Videos[0].Keyframes! with { Version = 9,
            Frames = copy.Videos[0].Keyframes!.Frames.Select(f => f with { Id = Guid.NewGuid() }).ToList() };
        copy.Images[0].Crop = new(); // Null and the full-image rectangle are equivalent.
        c.Version += 100; c.Seed = 900; c.TakeCount = 4;
        Assert.Equal(Fingerprint(original), Fingerprint(copy));
        Assert.Equal(PromptReferenceState.Current, PromptReferenceFreshness.Compare(c, Fingerprint(copy)).State);
    }

    [Fact]
    public void RevertingReferencesClearsTheFlagWithoutRecomposing()
    {
        var original = Example(); var c = Reviewed(original); var next = original.Copy(); next.Images.Reverse();
        Assert.True(PromptReferenceFreshness.Compare(c, Fingerprint(next)).NeedsAttention);
        next.Images.Reverse();
        Assert.False(PromptReferenceFreshness.Compare(c, Fingerprint(next)).NeedsAttention);
    }

    [Fact]
    public void RemoteRefModReceiptsVaeAndRecipeBookkeepingAreNotPromptInputs()
    {
        var s = Example(); var before = Fingerprint(s); var mod = s.Videos[0].RefMod!;
        s.Videos[0].RefMod = mod with { BuildId = Guid.NewGuid(), ComfyUrl = "http://other-server:8188", FileName = "rebuilt.safetensors",
            Recipe = mod.Recipe with { Key = new('F', 64), Selection = new('1', 64), VaeName = "compatible-vae.safetensors" } };
        Assert.Equal(before, Fingerprint(s));
    }

    [Fact]
    public void GenerationProfilesResolutionSeedAndUnrelatedStoryMetadataDoNotAffectTheReferenceFlag()
    {
        var s = Example(); var before = Fingerprint(s);
        s.NativeResolution = true; s.Resolution = VideoResolution.Native; s.UpscalePreview = true;
        s.GenerationPreset = "hyperflow"; s.Turbo = true; s.TurboSteps = 8; s.SaveLosslessFrames = true;
        s.Title = "Renamed shot"; s.Description = "A new action"; s.SelectedTakeId = Guid.NewGuid(); s.ApprovedScriptId = Guid.NewGuid();
        Assert.Equal(before, Fingerprint(s));
    }

    [Fact]
    public void EffectiveGuidanceNotUnusedDefaultsDeterminesFreshness()
    {
        var s = Example(); s.Images[0].PreservationOverride = "Use only the eyes";
        var g = Guidance(s); var before = PromptReferenceFreshness.Fingerprint(s, g);
        g[0] = g[0] with { AssetDefault = "A changed but overridden asset default", ImageDefault = "Unused" };
        Assert.Equal(before, PromptReferenceFreshness.Fingerprint(s, g));
        g[1] = g[1] with { ImageDefault = "New active image guidance" };
        Assert.NotEqual(before, PromptReferenceFreshness.Fingerprint(s, g));
    }

    [Fact]
    public void DisabledReelDetailsAndUnusedKeyframesAreIgnored()
    {
        var s = Example(); s.Videos[0].Visuals = ReelVisuals.FullReel;
        var before = Fingerprint(s);
        s.Videos[0].Keyframes!.Frames.Reverse(); s.Videos[0].AudioExcerpt = new(2, 1); s.Videos[0].Speaker = "Unused";
        Assert.Equal(before, Fingerprint(s));
        s.Videos[0].UseSoundtrack = true;
        Assert.NotEqual(before, Fingerprint(s));
    }

    [Fact]
    public void CharacterVoiceDefaultProvenanceDoesNotInvalidateButEffectiveSpeakerDoes()
    {
        var s = Example(); var owner = Guid.NewGuid(); s.Voices[0].CharacterAssetId = owner;
        s.CharacterVoices = [new() { AssetId = owner, Source = CharacterVoiceSource.Recording, SourceName = "Original recording", Speaker = "骆歆", FromDefault = true }];
        var before = Fingerprint(s); s.CharacterVoices[0].FromDefault = false; s.CharacterVoices[0].SpeakerConfirmed = true;
        Assert.Equal(before, Fingerprint(s));
        s.CharacterVoices[0].Speaker = "Guard";
        Assert.NotEqual(before, Fingerprint(s));
    }

    [Fact]
    public void DraftsAndMissingPromptsAreNotMarkedCurrentByAJobOrSave()
    {
        var s = Example(); var c = Reviewed(s);
        c.Prompt = " "; Assert.Equal(PromptReferenceState.Missing, PromptReferenceFreshness.Compare(c, Fingerprint(s)).State);
        c.Prompt = "New draft"; Assert.Equal(PromptReferenceState.NeedsReview, PromptReferenceFreshness.Compare(c, Fingerprint(s)).State);
        c.ReviewJobId = Guid.NewGuid(); c.AppliedJobId = Guid.NewGuid(); c.Version++;
        Assert.Equal(PromptReferenceState.NeedsReview, PromptReferenceFreshness.Compare(c, Fingerprint(s)).State);
        c.AcceptedRevisionId = null;
        Assert.Equal(PromptReferenceState.NeedsReview, PromptReferenceFreshness.Compare(c, Fingerprint(s)).State);
        Assert.Equal(PromptReferenceState.Missing, PromptReferenceFreshness.Compare(null, null).State);
    }

    [Fact]
    public void UnknownBaselineIsNotMisrepresentedAsChanged()
    {
        var s = Example(); var c = Reviewed(s);
        c.History[0] = c.History[0] with { ReferenceFingerprint = null };
        Assert.Equal(PromptReferenceState.NeedsReview, PromptReferenceFreshness.Compare(c, Fingerprint(s)).State);
        c.History[0] = c.History[0] with { ReferenceFingerprint = "future-format" };
        Assert.Equal(PromptReferenceState.NeedsReview, PromptReferenceFreshness.Compare(c, Fingerprint(s)).State);
    }

    [Fact]
    public void OptionalRevisionSignatureRoundTripsAndNullKeepsLegacySerialization()
    {
        var c = Reviewed(Example()); var accepted = c.Accepted!;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(accepted, AtomicJsonFile.Options);
        var read = JsonSerializer.Deserialize<CompositionPromptRevision>(bytes, AtomicJsonFile.Options)!;
        Assert.Equal(accepted, read);
        var legacy = JsonSerializer.Serialize(accepted with { ReferenceFingerprint = null }, AtomicJsonFile.Options);
        Assert.DoesNotContain("referenceFingerprint", legacy);
        Assert.Null(JsonSerializer.Deserialize<CompositionPromptRevision>(legacy, AtomicJsonFile.Options)!.ReferenceFingerprint);
        var content = ShotProductionContent.From(c); var other = new ProductionComposition(); content.Apply(other);
        Assert.Equal(accepted.ReferenceFingerprint, other.Accepted!.ReferenceFingerprint);
    }

    [Theory]
    [InlineData(PromptReferenceState.Current, "attention", false)]
    [InlineData(PromptReferenceState.Missing, "attention", true)]
    [InlineData(PromptReferenceState.ReferencesChanged, "changed", true)]
    [InlineData(PromptReferenceState.NeedsReview, "changed", false)]
    [InlineData(PromptReferenceState.NeedsReview, "review", true)]
    [InlineData(PromptReferenceState.Missing, "missing", true)]
    [InlineData(PromptReferenceState.Current, "all", true)]
    [InlineData(PromptReferenceState.Checking, "all", true)]
    [InlineData(PromptReferenceState.Checking, "missing", true)]
    [InlineData(PromptReferenceState.Checking, "attention", true)]
    public void FiltersKeepMissingChangedAndUnreviewedSeparate(PromptReferenceState state, string filter, bool matches)
        => Assert.Equal(matches, new PromptReferenceCheck(state, "test").Matches(filter));

    [Fact]
    public void CheckingNeitherFlagsNorApprovesAPrompt()
    {
        Assert.False(PromptReferenceCheck.Checking.NeedsAttention);
        Assert.NotEqual(PromptReferenceState.Current, PromptReferenceCheck.Checking.State);
        Assert.Equal("Checking…", PromptReferenceCheck.Checking.Label);
    }
}
