using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

internal sealed record AutomaticReelVoiceFixture(AssetLibrary Library, ReferenceAsset Character,
    VoiceReference Recording, AssetReferenceReel Reel, ShotVideoBinding Binding, Shot Shot)
{
    internal static AutomaticReelVoiceFixture Create(ReelVoiceMode mode = ReelVoiceMode.ExistingRecording, bool generated = true)
    {
        var character = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Riley", Category = AssetCategory.Character };
        var recording = new VoiceReference { AssetId = character.Id, Name = "Original voice", Duration = 12,
            Start = 0, ExcerptDuration = 1 };
        var project = Guid.NewGuid();
        var media = new ReferenceVideoMedia(Guid.NewGuid(), new('A', 64), 1000, 640, 640, 124, 24, 124 / 24d, true);
        var frames = new ReelKeyframeSet { Frames = [
            new() { Frame = new(media.Id, media.Sha256, 0, 0) },
            new() { Frame = new(media.Id, media.Sha256, 60, 2.5) }] };
        var recipe = new ReferenceReelDraft { AssetId = character.Id, Speaker = "Old reel speaker", VoiceMode = mode,
            Voice = new() { AssetId = recording.AssetId, VoiceId = recording.Id, Start = 2, Duration = 4, Speaker = "Old reel speaker" },
            Line = "These are reference words, not target dialogue." };
        var provenance = new VideoSnapshot(project, 1, new Shot { Title = "Character reel", Duration = 5 }, "Captured reel prompt", new('B', 64),
            "http://localhost:8188", new(), 640, 640, 124);
        var reel = new AssetReferenceReel { AssetId = character.Id, Name = "Riley angles", Media = media, Keyframes = frames,
            Generation = generated ? new(recipe, Guid.NewGuid(), Guid.NewGuid(), 1, 42, provenance) : null };
        var binding = new ShotVideoBinding { OwnerAssetId = character.Id, OwnerCategory = AssetCategory.Character,
            Name = reel.Name, Media = media, Visuals = ReelVisuals.Keyframes, Keyframes = ShotCopy.Of(frames),
            AudioExcerpt = new(0, 5) };
        var shot = new Shot { Title = "A greeting", Description = "Riley answers the phone.", Duration = 5,
            ApprovedScriptId = Guid.NewGuid(), SceneId = Guid.NewGuid(), Videos = [binding],
            Dialogue = [new() { Speaker = character.Name, Language = "English", Text = "Hello, how are you?" }] };
        var library = new AssetLibrary { ProjectId = project, Assets = [character], Voices = [recording], Reels = [reel] };
        return new(library, character, recording, reel, binding, shot);
    }
    internal ReelVoiceDefaultResult Apply() => ReelVoiceDefaults.Apply(Shot, Binding, Library);
    internal void UseRefMod()
    {
        Binding.Visuals = ReelVisuals.RefMod;
        var hashes = Binding.Keyframes!.Frames.Select((_, i) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("angle-" + i)))).ToArray();
        var recipe = ReelRefMods.Recipe(Binding, 640, 640, new H3Settings().VideoVae, hashes);
        var id = Guid.NewGuid();
        Binding.RefMod = new(recipe, "http://localhost:8188", ReelRefMods.BuildStem(Library.ProjectId, id), id);
    }
}

public sealed class AutomaticReelVoiceTests
{
    private static string Saved(Shot shot) => JsonSerializer.Serialize(shot, AtomicJsonFile.Options);

    [Theory]
    [InlineData(ReelVisuals.Keyframes)] [InlineData(ReelVisuals.FullReel)] [InlineData(ReelVisuals.RefMod)]
    public void OriginalRecordingAndCapturedExcerptFollowTheCharacterAcrossVisualModes(ReelVisuals mode)
    {
        var f = AutomaticReelVoiceFixture.Create();
        if (mode == ReelVisuals.RefMod) f.UseRefMod(); else f.Binding.Visuals = mode;
        var source = JsonSerializer.Serialize(f.Reel, AtomicJsonFile.Options);
        var dialogue = JsonSerializer.Serialize(f.Shot.Dialogue);
        var result = f.Apply();
        Assert.True(result.Handled); Assert.Contains("original recording", result.Notice!);
        var voice = Assert.Single(f.Shot.Voices);
        Assert.Equal(f.Recording.Id, voice.VoiceId);
        Assert.Equal(2, voice.Start); Assert.Equal(4, voice.Duration);
        Assert.Equal("Riley", voice.Speaker); Assert.Equal(f.Character.Id, voice.CharacterAssetId);
        Assert.False(f.Binding.UseSoundtrack); Assert.Single(ResolvedReferences.For(f.Shot).Audio);
        Assert.Equal(source, JsonSerializer.Serialize(f.Reel, AtomicJsonFile.Options));
        Assert.Equal(dialogue, JsonSerializer.Serialize(f.Shot.Dialogue));
        H3Policy.Validate(f.Shot, ready: true);
        Assert.Equal("Riley", Assert.Single(f.Shot.CharacterVoices!).Speaker);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void NewVoiceOrImportedReelUsesOneIndependentReelExcerpt(bool generated)
    {
        var f = AutomaticReelVoiceFixture.Create(ReelVoiceMode.NewVoice, generated); f.UseRefMod();
        Assert.True(f.Apply().Handled);
        var choice = Assert.Single(f.Shot.CharacterVoices!);
        Assert.Equal(CharacterVoiceSource.Reel, choice.Source);
        Assert.Equal(f.Binding.Id, choice.ReelBindingId); Assert.Equal(f.Binding.Media.Id, choice.ReelMediaId);
        Assert.Equal(new ReelAudioExcerpt(0, 5), choice.Excerpt); Assert.Empty(f.Shot.Voices);
        var audio = Assert.Single(ResolvedReferences.For(f.Shot).Audio);
        Assert.Equal("Riley", audio.Speaker); Assert.Equal(1, audio.Number);
        Assert.Equal(VideoInputKind.Audio, Assert.Single(ReferenceVideos.InputOrder(f.Shot)).Kind);
        H3Policy.Validate(f.Shot, ready: true);
    }

    [Theory]
    [InlineData(CharacterVoiceSource.None)] [InlineData(CharacterVoiceSource.Recording)] [InlineData(CharacterVoiceSource.Reel)]
    public void ExplicitVoiceAndNoneAreNotChangedByAnotherVisualSelection(CharacterVoiceSource selected)
    {
        var f = AutomaticReelVoiceFixture.Create();
        var choice = CharacterVoices.Initial(f.Shot, f.Character, f.Library);
        if (selected == CharacterVoiceSource.Recording) CharacterVoices.SelectRecording(choice, f.Recording, false);
        else if (selected == CharacterVoiceSource.Reel)
        {
            choice.Source = selected; choice.SourceName = f.Reel.Name; choice.ReelBindingId = f.Binding.Id;
            choice.ReelMediaId = f.Binding.Media.Id; choice.Excerpt = new(1, 3);
        }
        else choice.Source = selected;
        CharacterVoices.Set(f.Shot, choice, f.Library);
        var before = Saved(f.Shot);
        Assert.True(f.Apply().Handled); Assert.Equal(before, Saved(f.Shot));
    }

    [Fact]
    public void SavedDefaultWinsEvenWhenItsAssetDefaultChanges()
    {
        var f = AutomaticReelVoiceFixture.Create();
        var choice = CharacterVoices.Initial(f.Shot, f.Character, f.Library);
        CharacterVoices.SelectRecording(choice, f.Recording, true); CharacterVoices.Set(f.Shot, choice, f.Library);
        f.Library.Assets[0] = f.Character with { DefaultVoiceId = Guid.NewGuid() };
        var before = Saved(f.Shot); f.Apply(); Assert.Equal(before, Saved(f.Shot));
        Assert.True(Assert.Single(f.Shot.CharacterVoices!).FromDefault);
        Assert.Equal(1, Assert.Single(f.Shot.Voices).Duration); // Not replaced by the reel's four-second excerpt.
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ActiveLegacyAudioIsPreservedWithoutConvertingTheShot(bool soundtrack)
    {
        var f = AutomaticReelVoiceFixture.Create();
        if (soundtrack) { f.Binding.UseSoundtrack = true; f.Binding.Speaker = "Riley"; }
        else f.Shot.Voices.Add(new() { AssetId = f.Character.Id, VoiceId = f.Recording.Id, Speaker = "Riley", Duration = 1 });
        var before = Saved(f.Shot);
        Assert.True(f.Apply().Handled); Assert.Equal(before, Saved(f.Shot)); Assert.Null(f.Shot.CharacterVoices);
    }

    [Fact]
    public void LegacySpeakerOwnedElsewhereIsNotRemovedByAutomaticSelection()
    {
        var f = AutomaticReelVoiceFixture.Create();
        f.Shot.Voices.Add(new() { AssetId = Guid.NewGuid(), VoiceId = Guid.NewGuid(), Speaker = "Riley", Duration = 2 });
        var before = Saved(f.Shot); Assert.True(f.Apply().Handled); Assert.Equal(before, Saved(f.Shot));
    }

    [Fact]
    public void UnselectedVoiceCanBeFilledButAmbiguousSpeakerStillRequiresConfirmation()
    {
        var f = AutomaticReelVoiceFixture.Create();
        f.Shot.Dialogue[0].Speaker = "THE GIRL";
        CharacterVoices.Set(f.Shot, CharacterVoices.Initial(f.Shot, f.Character, f.Library), f.Library);
        Assert.Equal(CharacterVoiceSource.Unselected, f.Shot.CharacterVoices![0].Source);
        var result = f.Apply(); var choice = Assert.Single(f.Shot.CharacterVoices!);
        Assert.Equal(CharacterVoiceSource.Recording, choice.Source); Assert.False(choice.SpeakerConfirmed);
        Assert.Contains("no speaker was guessed", result.Notice!);
        Assert.Throws<WorkspaceStoreException>(() => CharacterVoices.Validate(f.Shot));
        choice.Speaker = "THE GIRL"; choice.SpeakerConfirmed = true; CharacterVoices.Set(f.Shot, choice, f.Library);
        H3Policy.Validate(f.Shot, ready: true);
    }

    [Theory] [InlineData("silent")] [InlineData("environment")] [InlineData("no-dialogue")] [InlineData("non-speaker")]
    public void SilentSourcesAndNonSpeakingCharactersDoNotGainAudio(string reason)
    {
        var f = AutomaticReelVoiceFixture.Create(reason == "silent" ? ReelVoiceMode.Silent : ReelVoiceMode.NewVoice);
        if (reason == "environment") f.Library.Assets[0] = f.Character with { Category = AssetCategory.Environment };
        if (reason == "no-dialogue") f.Shot.Dialogue.Clear();
        if (reason == "non-speaker")
        {
            f.Library.Assets.Add(new() { Id = Guid.NewGuid(), Name = "Mira", Category = AssetCategory.Character });
            f.Shot.Dialogue[0].Speaker = "Mira";
        }
        var before = Saved(f.Shot); Assert.False(f.Apply().Handled); Assert.Equal(before, Saved(f.Shot));
        Assert.Empty(ResolvedReferences.For(f.Shot).Audio);
    }

    [Fact]
    public void OriginalRecordingStillWorksWhenGeneratedReelHasNoSoundtrack()
    {
        var f = AutomaticReelVoiceFixture.Create(); var media = f.Reel.Media with { HasAudio = false };
        f.Library.Reels[0] = f.Reel with { Media = media }; f.Binding.Media = media;
        f.Apply(); Assert.Equal(f.Recording.Id, Assert.Single(f.Shot.Voices).VoiceId); Assert.False(f.Binding.UseSoundtrack);
    }

    [Fact]
    public void MissingOriginalIsVisibleAndNeverReplacedWithGeneratedOrDefaultAudio()
    {
        var f = AutomaticReelVoiceFixture.Create(); f.Library.Voices.Clear();
        var substitute = f.Recording with { Id = Guid.NewGuid(), Name = "Different default" };
        f.Library.Voices.Add(substitute); f.Library.Assets[0] = f.Character with { DefaultVoiceId = substitute.Id };
        var result = f.Apply();
        Assert.True(result.Handled); Assert.Contains("unavailable", result.Notice!);
        Assert.Equal(f.Recording.Id, Assert.Single(f.Shot.Voices).VoiceId);
        Assert.False(f.Binding.UseSoundtrack); Assert.False(f.Shot.CharacterVoices![0].FromDefault);
        Assert.DoesNotContain(f.Library.Voices, v => v.Matches(f.Shot.Voices[0]));
    }

    [Fact]
    public void CapturedRecordingDoesNotFollowNewReelDraftOrNewDefaultExcerpt()
    {
        var f = AutomaticReelVoiceFixture.Create();
        f.Library.ReelDrafts.Add(f.Reel.Generation!.Recipe with { Voice = f.Reel.Generation.Recipe.Voice! with { VoiceId = Guid.NewGuid() } });
        f.Library.Voices[0] = f.Recording with { Start = 6, ExcerptDuration = 2 };
        f.Apply();
        var selected = Assert.Single(f.Shot.Voices);
        Assert.Equal(f.Recording.Id, selected.VoiceId); Assert.Equal(2, selected.Start); Assert.Equal(4, selected.Duration);
    }

    [Fact]
    public void MovedRecordingUsesItsExistingIdentityAlias()
    {
        var f = AutomaticReelVoiceFixture.Create();
        f.Library.Voices[0] = f.Recording with { AssetId = Guid.NewGuid(), PreviousAssetIds = [f.Recording.AssetId] };
        f.Apply(); var voice = Assert.Single(f.Shot.Voices);
        Assert.True(f.Library.Voices[0].Matches(voice)); Assert.Equal(f.Character.Id, voice.CharacterAssetId);
    }

    [Fact]
    public void AddingMoreLooksDoesNotDuplicateTheVoiceAndReusesItsAudioLabel()
    {
        var f = AutomaticReelVoiceFixture.Create(); f.Apply();
        var another = f.Binding with { Id = Guid.NewGuid(), Media = f.Binding.Media with { Id = Guid.NewGuid() }, Keyframes = null, Visuals = ReelVisuals.FullReel };
        f.Shot.Videos.Add(another);
        Assert.True(ReelVoiceDefaults.Apply(f.Shot, another, f.Library).Handled);
        Assert.Single(f.Shot.Voices); Assert.Single(ResolvedReferences.For(f.Shot).Audio);
        Assert.Contains("<Audio 1>", ReelVoiceDefaults.Summary(f.Shot, another, f.Library));
        Assert.Contains("<Audio 1>", ReelVoiceDefaults.Usage(f.Shot, another, f.Library));
        Assert.DoesNotContain("Soundtrack is disabled", ReelVoiceDefaults.Usage(f.Shot, another, f.Library));
    }

    [Fact]
    public void SourceChangesAreNotInferredFromAnUnrelatedOrStaleReel()
    {
        var f = AutomaticReelVoiceFixture.Create();
        f.Library.Reels[0] = f.Reel with { Media = f.Reel.Media with { Sha256 = new('F', 64) } };
        var before = Saved(f.Shot); Assert.False(f.Apply().Handled); Assert.Equal(before, Saved(f.Shot));
    }

    [Fact]
    public void ExistingLimitsStillRejectTooManyAudioInputsInsteadOfDroppingOne()
    {
        var f = AutomaticReelVoiceFixture.Create();
        for (var i = 0; i < 3; i++) f.Shot.Voices.Add(new() { AssetId = Guid.NewGuid(), VoiceId = Guid.NewGuid(), Speaker = "Other " + i, Duration = 1 });
        f.Apply(); Assert.Equal(4, ResolvedReferences.For(f.Shot).Audio.Count);
        Assert.Throws<WorkspaceStoreException>(() => H3Policy.Validate(f.Shot));
    }

    [Theory] [InlineData(ReelVoiceMode.ExistingRecording)] [InlineData(ReelVoiceMode.NewVoice)]
    public void RefModGraphAndPromptCompositionReceiveOneSeparatelyMappedAudioInput(ReelVoiceMode mode)
    {
        var f = AutomaticReelVoiceFixture.Create(mode); f.UseRefMod(); f.Apply();
        var settings = new H3Settings(); var size = VideoResolutions.Size(f.Shot);
        var prompt = H3Policy.Compile(f.Shot);
        var snapshot = new VideoSnapshot(f.Library.ProjectId, 1, f.Shot, prompt, H3Policy.Fingerprint(f.Shot),
            "http://localhost:8188", settings, size.Width, size.Height, H3Policy.Frames(5)) { Sampling = H3Policy.Sampling(f.Shot, settings) };
        var input = Assert.Single(ReferenceVideos.InputOrder(f.Shot));
        var inputs = new[] { new PreparedVideoInput("captured-voice.wav", true) { Kind = input.Kind, VideoIndex = input.VideoIndex } };
        var candidates = AiBatchDefinition.Create(Guid.NewGuid(), 2, 42).Candidates;
        var graph = JsonSerializer.SerializeToElement(ComfyMultiTakeWorkflow.Build(candidates,
            c => ComfyH3Video.BuildWorkflow(snapshot, c.Seed, c.Id.ToString("D"), inputs), "client"), AtomicJsonFile.Options).GetProperty("prompt");
        using var media = JsonDocument.Parse(graph.GetProperty("refmedia").GetProperty("inputs").GetProperty("media_state").GetString()!);
        var audio = Assert.Single(media.RootElement.EnumerateArray());
        Assert.Equal("audio", audio.GetProperty("kind").GetString()); Assert.False(audio.GetProperty("has_audio").GetBoolean());
        Assert.Equal("captured-voice.wav", audio.GetProperty("file").GetString());
        using var stack = JsonDocument.Parse(graph.GetProperty("refmods").GetProperty("inputs").GetProperty("stack_state").GetString()!);
        Assert.False(Assert.Single(stack.RootElement.GetProperty("picks").EnumerateArray()).TryGetProperty("audio", out _));
        Assert.Contains("<Audio 1>", prompt); Assert.Contains("Riley (S1)", prompt);
        var request = new PromptCompositionRequest(f.Library.ProjectId, Guid.NewGuid(), 1, "context", "source", f.Shot,
            "scene", [], [], [], [], "", "", "", new(AiBackend.OpenRouter, "vision", "Vision"));
        var inspection = f.Binding.RefMod!.Recipe.FrameHashes.Select((hash, i) =>
            new RefModInspectionFrame(1, i + 1, f.Binding.Name, "Angle", hash, Encoding.UTF8.GetBytes("angle-" + i))).ToArray();
        var messages = PromptComposer.BuildMessages(request, [], inspection);
        using var payload = JsonDocument.Parse(messages[1].Contents.OfType<Microsoft.Extensions.AI.TextContent>().First().Text);
        var sources = payload.RootElement.GetProperty(mode == ReelVoiceMode.ExistingRecording ? "voices" : "reelAudio");
        var mapped = Assert.Single(sources.EnumerateArray());
        Assert.Equal(1, mapped.GetProperty("audio").GetInt32()); Assert.Equal("Riley", mapped.GetProperty("speaker").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("sparseVisualReferences").GetArrayLength());
        Assert.DoesNotContain(f.Reel.Generation!.Recipe.Line, prompt);
    }
}
