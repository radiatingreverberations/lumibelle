using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed class AssetPickingTests
{
    [Fact]
    public void CatalogueIncludesLooksReelsAndDefaultRecordingButNoImageDescriptions()
    {
        var f = Fixture(); var catalogue = AssetPickCatalog.Capture(f.Library);
        var image = Assert.Single(catalogue.Candidates, c => c.SourceId == f.Image.Id);
        // Retired image descriptions never reach picking, even when an older library still holds one.
        Assert.Equal("", image.Description);
        Assert.Equal(f.Image.LookId, image.LookId); Assert.True(image.Approved);
        Assert.Single(catalogue.Assets.Single(a => a.Id == f.Hero.Id).Looks);
        var reel = Assert.Single(catalogue.Candidates, c => c.SourceId == f.Reel.Id);
        Assert.Contains("back view", reel.Description); Assert.Equal(2, reel.KeyframeCount);
        Assert.Contains("RefMod", reel.VisualModes); Assert.Equal("Keyframes", reel.PreferredVisualMode);
        var voice = Assert.Single(catalogue.Candidates, c => c.SourceId == f.Voice.Id);
        Assert.True(voice.DefaultVoice); Assert.Equal(1, voice.ExcerptStart); Assert.Equal(5, voice.ExcerptDuration);
    }

    [Fact]
    public void ArchivedLookMediaAndOrphanMediaAreNotCandidates()
    {
        var f = Fixture(); var archived = new CharacterLook { Name = "Old outfit", Archived = true };
        var hidden = Image(archived.Id); var orphan = f.Reel with { Id = Guid.NewGuid(), AssetId = Guid.NewGuid() };
        f.Library.Assets[0] = f.Hero with { Looks = [.. f.Hero.Looks, archived], Images = [f.Image, hidden] };
        f.Library.Reels.Add(orphan);
        var catalogue = AssetPickCatalog.Capture(f.Library);
        Assert.DoesNotContain(catalogue.Candidates, c => c.SourceId == hidden.Id || c.SourceId == orphan.Id);
        Assert.DoesNotContain(catalogue.Assets.SelectMany(a => a.Looks), l => l.Id == archived.Id);
    }

    [Fact]
    public void CatalogueOrderAndLibraryRevisionAreNotSelectionChanges()
    {
        var f = Fixture(); var first = AssetPickCatalog.Hash(AssetPickCatalog.Capture(f.Library));
        f.Library.Assets.Reverse(); f.Library.Reels.Reverse(); f.Library.Voices.Reverse();
        var reordered = f.Library with { Revision = 99 };
        Assert.Equal(first, AssetPickCatalog.Hash(AssetPickCatalog.Capture(reordered)));
    }

    [Fact]
    public void CatalogueDoesNotRetainMutableTagLists()
    {
        var f = Fixture(); var captured = AssetPickCatalog.Capture(f.Library); var before = Json(captured);
        f.Image.Tags.Add("later change");
        Assert.Equal(before, Json(captured));
        Assert.NotEqual(before, Json(AssetPickCatalog.Capture(f.Library)));
    }

    [Fact]
    public void RequestIncludesShotPromptAndDescriptionsButNoMediaOrStoragePaths()
    {
        var f = Fixture(); var request = Request(f);
        var messages = AssetPicker.BuildMessages(request);
        Assert.Equal(2, messages.Count);
        Assert.All(messages.SelectMany(m => m.Contents), c => Assert.IsType<TextContent>(c));
        var text = messages[1].Text;
        Assert.Contains("Blue coat", text); Assert.Contains("Move through the courtyard", text);
        Assert.DoesNotContain("Full-body front view", text);
        Assert.Contains("Stay wide", text); Assert.Contains("Riley", text);
        Assert.DoesNotContain("private-image.png", text); Assert.DoesNotContain("private-voice.wav", text);
        Assert.DoesNotContain("image_url", text); Assert.DoesNotContain("data:image", text);
        Assert.Contains("text only", messages[0].Text);
    }

    [Fact]
    public void LargeCatalogueFailsRatherThanSilentlyOmittingCandidates()
    {
        var f = Fixture();
        f.Library.Assets[0] = f.Hero with { Images = Enumerable.Range(0, 70)
            .Select(_ => Image() with { PreservationGuidance = new string('x', 12000) }).ToList() };
        var error = Assert.Throws<WorkspaceStoreException>(() => AssetPicker.BuildMessages(Request(f)));
        Assert.Contains("Nothing was truncated or sent", error.Message);
    }

    [Fact]
    public void ParsesOnlyKnownIdsAndAvailableModes()
    {
        var f = Fixture(); var request = Request(f);
        var choices = new[] { Pick("image", f.EnvironmentImage.Id), Pick("reel", f.Reel.Id, "Keyframes") };
        var result = AssetPicker.Parse(Response(choices), request);
        Assert.Equal(choices, result.Selections.ToArray());
    }

    [Fact]
    public void EmptySelectionCanReportAnUnmetNeed()
    {
        var f = Fixture();
        var result = AssetPicker.Parse("{\"version\":1,\"selections\":[],\"missing\":[\"No suitable courtyard\"],\"summary\":\"Need a new asset\"}", Request(f));
        Assert.Empty(result.Selections); Assert.Single(result.Missing);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"version\":\"1\",\"selections\":[],\"missing\":[],\"summary\":\"\"}")]
    [InlineData("{\"version\":2,\"selections\":[],\"missing\":[],\"summary\":\"\"}")]
    [InlineData("{\"version\":1,\"version\":1,\"selections\":[],\"missing\":[],\"summary\":\"\"}")]
    [InlineData("{\"version\":1,\"selections\":[],\"missing\":[],\"summary\":\"\",\"operations\":[]}")]
    public void MalformedOrUnexpectedResponseCannotProduceASelection(string raw)
    {
        var f = Fixture(); Assert.Throws<WorkspaceStoreException>(() => AssetPicker.Parse(raw, Request(f)));
    }

    [Fact]
    public void UnknownAndDuplicateCandidatesAreRejected()
    {
        var f = Fixture(); var request = Request(f); var item = Pick("image", f.Image.Id);
        Assert.Throws<WorkspaceStoreException>(() => AssetPicker.Parse(Response([Pick("image", Guid.NewGuid())]), request));
        Assert.Throws<WorkspaceStoreException>(() => AssetPicker.Parse(Response([item, item]), request));
    }

    [Fact]
    public void ImagesCannotSupplyAudioAndAudioOnlyReelsMustEnableAudio()
    {
        var f = Fixture(); var request = Request(f);
        Assert.Throws<WorkspaceStoreException>(() => AssetPicker.ValidateSelection(request, [Pick("image", f.Image.Id, speaker: "Riley")]));
        Assert.Throws<WorkspaceStoreException>(() => AssetPicker.ValidateSelection(request, [Pick("reel", f.Reel.Id, "None")]));
        Assert.Throws<WorkspaceStoreException>(() => AssetPicker.ValidateSelection(request, [Pick("voice", f.Voice.Id)]));
    }

    [Fact]
    public void VoicesMustMapToExistingSpeakersAndCannotCompete()
    {
        var f = Fixture(); var request = Request(f);
        Assert.Throws<WorkspaceStoreException>(() => AssetPicker.ValidateSelection(request, [Pick("voice", f.Voice.Id, speaker: "Someone else")]));
        Assert.Throws<WorkspaceStoreException>(() => AssetPicker.ValidateSelection(request,
            [Pick("voice", f.Voice.Id, speaker: "Riley"), Pick("reel", f.Reel.Id, "None", "Riley")]));
    }

    [Fact]
    public void ReelsWithoutSavedFramesDoNotAdvertiseKeyframesOrRefMods()
    {
        var f = Fixture(); f.Library.Reels[0] = f.Reel with { Keyframes = null };
        var request = Request(f); var reel = request.Catalogue.Candidates.Single(c => c.Kind == "Reel");
        Assert.DoesNotContain("Keyframes", reel.VisualModes); Assert.DoesNotContain("RefMod", reel.VisualModes);
        Assert.Equal("FullReel", reel.PreferredVisualMode);
        Assert.Throws<WorkspaceStoreException>(() => AssetPicker.ValidateSelection(request, [Pick("reel", f.Reel.Id, "RefMod")]));
    }

    [Fact]
    public void SilentMediaCannotEnableASoundtrack()
    {
        var f = Fixture(); f.Library.Reels[0] = f.Reel with { Media = f.Reel.Media with { HasAudio = false } };
        var request = Request(f);
        Assert.Throws<WorkspaceStoreException>(() => AssetPicker.ValidateSelection(request, [Pick("reel", f.Reel.Id, "FullReel", "Riley")]));
    }

    [Fact]
    public void VisualSelectionDoesNotImplicitlyChooseTheDefaultVoice()
    {
        var f = Fixture(); var request = Request(f);
        var plan = Plan(f, request, [Pick("image", f.Image.Id)]);
        Assert.Single(plan.Inputs.Images); Assert.Empty(plan.Inputs.Voices);
        Assert.Equal(CharacterVoiceSource.None, Assert.Single(plan.Inputs.CharacterVoices!).Source);
        Assert.Empty(ResolvedReferences.For(plan.Inputs).Audio);
    }

    [Fact]
    public void ExplicitDefaultRecordingUsesItsSavedExcerptAndCanonicalSpeaker()
    {
        var f = Fixture(); var request = Request(f);
        var plan = Plan(f, request, [Pick("image", f.Image.Id), Pick("voice", f.Voice.Id, speaker: "riley")]);
        var voice = Assert.Single(plan.Inputs.Voices);
        Assert.Equal(f.Voice.Id, voice.VoiceId); Assert.Equal("Riley", voice.Speaker);
        Assert.Equal(f.Voice.Start, voice.Start); Assert.Equal(f.Voice.ExcerptDuration, voice.Duration);
        var choice = Assert.Single(plan.Inputs.CharacterVoices!);
        Assert.Equal(CharacterVoiceSource.Recording, choice.Source); Assert.True(choice.FromDefault);
        H3Policy.Validate(plan.Inputs);
    }

    [Fact]
    public void ReelAudioAndVisualModeAreCapturedIndependently()
    {
        var f = Fixture(); var request = Request(f);
        var plan = Plan(f, request, [Pick("reel", f.Reel.Id, "Keyframes", "Riley")]);
        var reel = Assert.Single(plan.Inputs.Videos);
        Assert.True(reel.UseSoundtrack); Assert.Equal(ReelVisuals.Keyframes, reel.EffectiveVisuals);
        Assert.Equal(f.Reel.Id, plan.ReelSources[reel.Id]);
        Assert.Equal(2, ResolvedReferences.For(plan.Inputs).Pictures.Count);
        Assert.Single(ResolvedReferences.For(plan.Inputs).Audio);
        Assert.Equal(reel.Id, Assert.Single(plan.Inputs.CharacterVoices!).ReelBindingId);
    }

    [Fact]
    public void RefModSelectionStagesExistingFramesWithoutBuildingOrInventingAHandle()
    {
        var f = Fixture(); var request = Request(f);
        var plan = Plan(f, request, [Pick("reel", f.Reel.Id, "RefMod")]);
        var reel = Assert.Single(plan.Inputs.Videos);
        Assert.Equal(ReelVisuals.RefMod, reel.EffectiveVisuals); Assert.Null(reel.RefMod);
        Assert.Equal(Json(f.Reel.Keyframes), Json(reel.Keyframes));
        Assert.Empty(ResolvedReferences.For(plan.Inputs).Pictures);
        Assert.Single(ResolvedReferences.For(plan.Inputs).Videos);
        Assert.Throws<WorkspaceStoreException>(() => ReelRefMods.ValidateBinding(reel, true));
    }

    [Fact]
    public void AddModePreservesExistingImageCropRoleAndGuidance()
    {
        var f = Fixture(); var existing = Binding(f.Hero, f.Image);
        existing.Crop = new() { X = .1, Y = .2, Width = .5, Height = .5 };
        existing.PreservationOverride = "Keep this crop"; existing.AiUseHint = "Existing use";
        f.Shot.Images.Add(existing); var before = Json(existing); var request = Request(f);
        var plan = Plan(f, request, [Pick("image", f.Image.Id), Pick("image", f.EnvironmentImage.Id)]);
        Assert.Equal(before, Json(plan.Inputs.Images[0])); Assert.Equal(2, plan.Inputs.Images.Count);
        Assert.Equal(before, Json(existing)); Assert.Single(f.Shot.Images);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddModePreservesExistingVoiceIncludingExplicitNone(bool recording)
    {
        var f = Fixture(); f.Shot.Images.Add(Binding(f.Hero, f.Image));
        var choice = new CharacterVoiceSelection { AssetId = f.Hero.Id, CharacterName = "Riley", Speaker = "Riley", SpeakerConfirmed = true, Source = CharacterVoiceSource.None };
        if (recording) CharacterVoices.SelectRecording(choice, f.Voice, false);
        CharacterVoices.Set(f.Shot, choice, f.Library);
        var before = Json(f.Shot.CharacterVoices); var voices = Json(f.Shot.Voices); var request = Request(f);
        var plan = Plan(f, request, [Pick("voice", f.Voice.Id, speaker: "Riley")]);
        Assert.Equal(before, Json(plan.Inputs.CharacterVoices)); Assert.Equal(voices, Json(plan.Inputs.Voices));
        Assert.Contains(plan.Notices, n => n.Contains("Kept the current voice"));
    }

    [Fact]
    public void AddModeDoesNotChangeAnAlreadySelectedReelModeOrSoundtrack()
    {
        var f = Fixture(); f.Shot.Videos.Add(ReelBinding(f.Reel, ReelVisuals.Keyframes));
        var before = Json(f.Shot.Videos); var request = Request(f);
        var plan = Plan(f, request, [Pick("reel", f.Reel.Id, "FullReel", "Riley")]);
        Assert.Equal(before, Json(plan.Inputs.Videos)); Assert.Empty(ResolvedReferences.For(plan.Inputs).Audio);
    }

    [Fact]
    public void NewVoiceDoesNotNormalizeMutedLegacyReelMetadata()
    {
        var f = Fixture(); var reel = ReelBinding(f.Reel, ReelVisuals.Keyframes);
        reel.OwnerAssetId = null; reel.Speaker = "Retained inactive mapping";
        f.Shot.Videos.Add(reel); var before = Json(f.Shot.Videos); var request = Request(f);
        var plan = Plan(f, request, [Pick("voice", f.Voice.Id, speaker: "Riley")]);
        Assert.Equal(before, Json(plan.Inputs.Videos)); Assert.Single(plan.Inputs.Voices);
    }

    [Fact]
    public void ReplacementChangesOnlyReferencesAndNeverMutatesItsInput()
    {
        var f = Fixture(); f.Shot.Images.Add(Binding(f.Hero, f.Image));
        var request = Request(f, replace: true); var original = Json(f.Shot);
        var direction = AssetPickCatalog.DirectionFingerprint(f.Shot, request.Prompt, request.DirectingNotes);
        var plan = Plan(f, request, [Pick("image", f.EnvironmentImage.Id)]);
        Assert.Equal(f.EnvironmentImage.Id, Assert.Single(plan.Inputs.Images).MediaId);
        Assert.Empty(plan.Inputs.Videos); Assert.Empty(plan.Inputs.Voices);
        Assert.Equal(direction, AssetPickCatalog.DirectionFingerprint(plan.Inputs, request.Prompt, request.DirectingNotes));
        Assert.Equal(original, Json(f.Shot)); Assert.Equal(original, Json(request.Shot));
    }

    [Fact]
    public void RepeatedPreviewPlansProduceTheSameBindingIdentities()
    {
        var f = Fixture(); var request = Request(f); var selected = new[] { Pick("image", f.Image.Id), Pick("reel", f.Reel.Id, "RefMod") };
        Assert.Equal(Json(Plan(f, request, selected)), Json(Plan(f, request, selected)));
    }

    [Fact]
    public void BudgetCountsRetainedPicturesAsWellAsSuggestions()
    {
        var f = Fixture(); var extras = Enumerable.Range(0, 9).Select(_ => Image()).ToList();
        f.Library.Assets[1] = f.Environment with { Images = [f.EnvironmentImage, .. extras] };
        f.Shot.Images = extras.Select(i => Binding(f.Environment, i)).ToList();
        var request = Request(f);
        Assert.Throws<WorkspaceStoreException>(() => Plan(f, request, [Pick("image", f.Image.Id)]));
        Assert.Equal(9, f.Shot.Images.Count);
    }

    [Fact]
    public void AudioTimeBudgetAlsoAppliesWhenThereAreNoReels()
    {
        var shot = new Shot { Voices = Enumerable.Range(0, 3).Select(i => new ShotVoiceBinding {
            VoiceId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Speaker = "Speaker " + i, Start = 0, Duration = 6
        }).ToList() };
        Assert.Throws<WorkspaceStoreException>(() => AssetPickPlan.ValidateBudget(shot));
    }

    [Fact]
    public void FullReelVideoTimeAndTotalSourceBudgetsAreEnforced()
    {
        var f = Fixture(); var first = ReelBinding(f.Reel with { Media = f.Reel.Media with { Duration = 8 } }, ReelVisuals.FullReel);
        var second = ShotCopy.Of(first); second.Id = Guid.NewGuid(); second.Media = second.Media with { Id = Guid.NewGuid() };
        Assert.Throws<WorkspaceStoreException>(() => AssetPickPlan.ValidateBudget(new Shot { Videos = [first, second] }));
        var shot = new Shot { Images = Enumerable.Range(0, 9).Select(_ => Binding(f.Hero, Image())).ToList(),
            Videos = [ReelBinding(f.Reel, ReelVisuals.RefMod)],
            Voices = Enumerable.Range(0, 3).Select(i => new ShotVoiceBinding { VoiceId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Speaker = "Voice " + i, Duration = 3 }).ToList() };
        Assert.Throws<WorkspaceStoreException>(() => AssetPickPlan.ValidateBudget(shot));
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("direction")]
    [InlineData("shot")]
    [InlineData("references")]
    public void ChangedShotContextCannotBeOverwritten(string change)
    {
        var f = Fixture(); var request = Request(f); var current = f.Shot.Copy();
        var prompt = request.Prompt; var direction = request.DirectingNotes;
        if (change == "prompt") prompt += " changed";
        if (change == "direction") direction += " changed";
        if (change == "shot") current.Description += " changed";
        if (change == "references") current.Images.Add(Binding(f.Hero, f.Image));
        Assert.Throws<WorkspaceStoreException>(() => AssetPickPlan.Create(request, [Pick("image", f.Image.Id)], current, prompt, direction, f.Library));
    }

    [Fact]
    public void ChangedGuidanceAndRemovedMediaInvalidateTheCatalogue()
    {
        var f = Fixture(); var request = Request(f);
        f.Library.Assets[0] = f.Hero with { Images = [f.Image with { PreservationGuidance = "Different outfit" }] };
        Assert.Throws<WorkspaceStoreException>(() => Plan(f, request, [Pick("image", f.Image.Id)]));
        f.Library.Assets[0] = f.Hero; f.Library.Voices.Clear();
        Assert.Throws<WorkspaceStoreException>(() => Plan(f, request, [Pick("image", f.Image.Id)]));
    }

    [Fact]
    public void CrossProjectAndTamperedRequestsAreRejected()
    {
        var f = Fixture(); var request = Request(f);
        Assert.Throws<WorkspaceStoreException>(() => AssetPicker.ValidateRequest(request with { Prompt = "Tampered" }));
        Assert.Throws<WorkspaceStoreException>(() => AssetPickCatalog.RequireCurrent(request, f.Library with { ProjectId = Guid.NewGuid() }));
    }

    [Fact]
    public void NewJobKindPreservesExistingEnumValuesAndUsesExactShotTarget()
    {
        Assert.Equal(0, (int)AiJobKind.ScriptAssistant); Assert.Equal(14, (int)AiJobKind.ContentProbe);
        Assert.Equal(15, (int)AiJobKind.AssetPicking);
        var target = new AiJobTarget(Guid.NewGuid(), ShotId: Guid.NewGuid()); target.Validate(AiJobKind.AssetPicking);
        Assert.EndsWith("/asset-picker", target.LockKey(AiJobKind.AssetPicking));
        Assert.Throws<WorkspaceStoreException>(() => (target with { AssetId = Guid.NewGuid() }).Validate(AiJobKind.AssetPicking));
        Assert.Throws<WorkspaceStoreException>(() => (target with { ShotId = null }).Validate(AiJobKind.AssetPicking));
    }

    [Fact]
    public void QueuedTextSnapshotRoundTripsAndRejectsImagePartsOrDifferentTargets()
    {
        var f = Fixture(); var request = Request(f); var snapshot = Snapshot(request); var header = Header(request);
        var read = AiTextJobHandler.Read(header, JsonSerializer.SerializeToElement(snapshot, AtomicJsonFile.Options));
        Assert.False(read.InspectsImages); Assert.Equal(request.ContextFingerprint, read.Payload<AssetPickRequest>().ContextFingerprint);
        var bad = snapshot with { Messages = [.. snapshot.Messages, new("user", [new(Image: [1], MediaType: "image/png")])] };
        Assert.Throws<WorkspaceStoreException>(() => AiTextJobHandler.Read(header, JsonSerializer.SerializeToElement(bad, AtomicJsonFile.Options)));
        Assert.Throws<WorkspaceStoreException>(() => AiTextJobHandler.Read(header with { Target = header.Target with { ShotId = Guid.NewGuid() } }, JsonSerializer.SerializeToElement(snapshot, AtomicJsonFile.Options)));
        Assert.Contains("view=References", header.ReviewUrl);
    }

    [Theory]
    [InlineData("length")]
    [InlineData(null)]
    public void IncompleteHostedOutputCannotBecomeAnApplicableSelection(string? finish)
    {
        var f = Fixture(); var request = Request(f); var raw = Response([Pick("image", f.Image.Id)]);
        var result = AiTextResults.Parse(Snapshot(request), raw, finish);
        Assert.NotNull(result.Error); Assert.Null(result.Value); Assert.Equal(raw, result.Raw);
    }

    [Fact]
    public void CompleteOutputUsesTheRegisteredAssetSelectionParser()
    {
        var f = Fixture(); var request = Request(f); var raw = Response([Pick("image", f.Image.Id)]);
        var result = AiTextResults.Parse(Snapshot(request), raw, "stop");
        Assert.Null(result.Error); Assert.Single(result.Read<AssetPickResult>()!.Selections);
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, AtomicJsonFile.Options);
    private static string Response(IReadOnlyList<AssetPickItem> selections) => Json(new { version = 1, selections, missing = Array.Empty<string>(), summary = "Suitable references" });
    private static AssetPickItem Pick(string kind, Guid id, string? mode = null, string? speaker = null) => new(AssetPickCatalog.Id(kind, id), mode, speaker, "", "Matches the described shot");
    private static AssetPickRequest Request(TestFixture f, bool replace = false)
    {
        var shot = f.Shot.Copy(); var catalogue = AssetPickCatalog.Capture(f.Library);
        const string prompt = "Move through the courtyard"; const string direction = "Stay wide";
        return new(1, f.Library.ProjectId, shot, prompt, direction, "", replace, catalogue,
            AssetPickCatalog.ContextFingerprint(f.Library.ProjectId, shot, prompt, direction), AssetPickCatalog.Hash(catalogue));
    }
    private static AssetPickDraft Plan(TestFixture f, AssetPickRequest request, IReadOnlyList<AssetPickItem> picks) =>
        AssetPickPlan.Create(request, picks, f.Shot, request.Prompt, request.DirectingNotes, f.Library);
    private static AiTextJobRequest Snapshot(AssetPickRequest request) => new(2, AiJobKind.AssetPicking,
        new(AiBackend.OpenRouter, "test-model", "Test model"), false, new(), AssetPicker.Profile, .2f, 1,
        JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options), AssetPicker.BuildMessages(request).Select(AiTextMessage.Capture).ToArray());
    private static AiJobHeader Header(AssetPickRequest request) => new() {
        Id = Guid.NewGuid(), Kind = AiJobKind.AssetPicking, Backend = AiBackend.OpenRouter,
        Target = new(request.ProjectId, ShotId: request.Shot.Id), ProjectName = "Test", TargetName = "References",
        OriginTabId = Guid.NewGuid(), RequestFingerprint = new string('A', 64)
    };
    private static AssetImage Image(Guid? look = null) => new() { Id = Guid.NewGuid(), FileName = "private-image.png",
        ContentType = "image/png", Width = 832, Height = 480, Name = "Blue outfit", LookId = look,
        VisualDescription = "Full-body front view, blue coat, brown hair", IsReference = true, Tags = ["full body"], CreatedUtc = DateTimeOffset.UnixEpoch };
    private static ShotImageBinding Binding(ReferenceAsset owner, AssetImage image) => new() {
        AssetId = owner.Id, MediaId = image.Id, Kind = ShotImageKind.AssetImage, Name = image.Name ?? "Image"
    };
    private static ShotVideoBinding ReelBinding(AssetReferenceReel reel, ReelVisuals mode) => new() {
        Media = reel.Media, Name = reel.Name, Description = reel.UseGuidance, OwnerAssetId = reel.AssetId,
        OwnerCategory = AssetCategory.Character, Visuals = mode, Keyframes = reel.Keyframes is null ? null : ShotCopy.Of(reel.Keyframes)
    };
    private static TestFixture Fixture()
    {
        var look = new CharacterLook { Name = "Blue outfit", Description = "Blue coat" };
        var image = Image(look.Id); var environmentImage = Image() with { Name = "Courtyard", VisualDescription = "Stone courtyard with a fountain" };
        var heroId = Guid.NewGuid(); var voice = new VoiceReference { AssetId = heroId, Name = "Riley clear voice", FileName = "private-voice.wav", ContentType = "audio/wav", Duration = 12, Start = 1, ExcerptDuration = 5 };
        var hero = new ReferenceAsset { Id = heroId, Name = "Riley", Category = AssetCategory.Character, Description = "Brown-haired lead", Images = [image], Looks = [look], DefaultVoiceId = voice.Id };
        var environment = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Courtyard", Category = AssetCategory.Environment, Images = [environmentImage] };
        var media = new ReferenceVideoMedia(Guid.NewGuid(), new string('A', 64), 1000, 832, 480, 120, 24, 5, true);
        var reel = new AssetReferenceReel { AssetId = heroId, Name = "Riley blue outfit reel", LookId = look.Id, Media = media, UseGuidance = "Blue outfit from multiple views", Keyframes = new() { Frames = [
            new() { Frame = new(media.Id, media.Sha256, 0, 0), Notes = "front view" },
            new() { Frame = new(media.Id, media.Sha256, 24, 1), Notes = "back view" }
        ] } };
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [hero, environment], Reels = [reel], Voices = [voice] };
        var shot = new Shot { Title = "Arrival", Description = "Riley enters the courtyard", Duration = 5,
            Dialogue = [new() { Speaker = "Riley", Language = "English", Text = "Hello." }], Characters = [new(Guid.NewGuid(), "Riley")] };
        return new(library, shot, hero, environment, image, environmentImage, reel, voice);
    }
    private sealed record TestFixture(AssetLibrary Library, Shot Shot, ReferenceAsset Hero, ReferenceAsset Environment,
        AssetImage Image, AssetImage EnvironmentImage, AssetReferenceReel Reel, VoiceReference Voice);
}
