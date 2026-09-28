using System.Text.Json;
using Bunit;
using lumibelle.Components.Shots;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class CharacterVoiceTests
{
    internal static (AssetLibrary Library, ReferenceAsset Character, VoiceReference Voice, Shot Shot) Fixture()
    {
        var character = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Riley", Category = AssetCategory.Character };
        var voice = new VoiceReference { AssetId = character.Id, Name = "Elementalist voice", Duration = 10, Start = 2, ExcerptDuration = 5 };
        character = character with { DefaultVoiceId = voice.Id };
        var reel = new AssetReferenceReel { AssetId = character.Id, Name = "Riley casual", Media = new(Guid.NewGuid(), new('A', 64), 100, 32, 32, 120, 24, 5, true) };
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [character], Voices = [voice], Reels = [reel] };
        var shot = new Shot { Description = "Riley speaks", Duration = 5, Dialogue = [new() { Speaker = "Riley", Text = "Hello", Language = "en" }],
            Videos = [new() { Name = reel.Name, Media = reel.Media, OwnerAssetId = character.Id, UseSoundtrack = true }] };
        return (library, character, voice, shot);
    }

    [Fact] public void DefaultVoiceReplacesCompetingReelAudioAndKeepsItsCapturedExcerpt()
    {
        var (library, character, voice, shot) = Fixture();
        var choice = CharacterVoices.Initial(shot, character, library);
        CharacterVoices.Set(shot, choice, library);
        Assert.False(shot.Videos[0].UseSoundtrack);
        Assert.Equal(voice.Id, Assert.Single(shot.Voices).VoiceId);
        Assert.Equal(2, shot.Voices[0].Start); Assert.Equal(5, shot.Voices[0].Duration);
        var audio = Assert.Single(ResolvedReferences.For(shot).Audio);
        Assert.Equal("Elementalist voice", audio.SourceName); Assert.Equal("Riley", audio.Speaker);
        var saved = ShotCopy.Of(shot);
        var setup = ShotCopy.Of(new ProductionComposition { Shot = shot });
        Assert.Equal(voice.Id, Assert.Single(setup.Shot.CharacterVoices!).Recording!.VoiceId);
        Assert.True(setup.Shot.CharacterVoices![0].FromDefault);
        library.Assets[0] = character with { DefaultVoiceId = null }; library.Voices[0] = voice with { Start = 0, ExcerptDuration = 1 };
        Assert.Equal(voice.Id, saved.Voices[0].VoiceId); Assert.Equal(2, saved.Voices[0].Start);
        H3Policy.Validate(saved, ready: true, requireScene: false);
    }

    [Fact] public void MoreLooksKeepOneVoiceAndNoneStaysExplicit()
    {
        var (library, character, _, shot) = Fixture();
        CharacterVoices.Set(shot, CharacterVoices.Initial(shot, character, library), library);
        shot.Videos.Add(shot.Videos[0] with { Id = Guid.NewGuid(), Media = shot.Videos[0].Media with { Id = Guid.NewGuid() }, UseSoundtrack = true });
        CharacterVoices.Set(shot, shot.CharacterVoices![0], library);
        Assert.Single(ResolvedReferences.For(shot).Audio); Assert.All(shot.Videos, v => Assert.False(v.UseSoundtrack));
        var none = new CharacterVoiceSelection { AssetId = character.Id, CharacterName = character.Name, Source = CharacterVoiceSource.None };
        CharacterVoices.Set(shot, none, library); Assert.Empty(ResolvedReferences.For(shot).Audio);
        H3Policy.Validate(shot);
        Assert.Equal(CharacterVoiceSource.None, ShotCopy.Of(shot).CharacterVoices![0].Source);
    }

    [Fact] public void MissingDefaultAndAmbiguousSpeakerRequireExplicitDecisions()
    {
        var (library, character, _, shot) = Fixture();
        var noDefault = character with { DefaultVoiceId = null };
        CharacterVoices.Set(shot, CharacterVoices.Initial(shot, noDefault, library), library);
        Assert.Throws<WorkspaceStoreException>(() => H3Policy.Validate(shot));
        shot.Dialogue[0].Speaker = "THE GIRL";
        var ambiguous = CharacterVoices.Initial(shot, character, library);
        Assert.False(ambiguous.SpeakerConfirmed); Assert.Equal(CharacterVoiceSource.Unselected, ambiguous.Source);
        shot.Videos[0].Speaker = "THE GIRL";
        Assert.Equal("THE GIRL", CharacterVoices.Initial(shot, character, library).Speaker); // Explicit mapping is reusable.
        shot.Videos[0].Speaker = null; shot.Dialogue.Clear();
        Assert.Equal(CharacterVoiceSource.None, CharacterVoices.Initial(shot, character, library).Source);
    }

    [Fact] public void ReelAudioOverrideUsesExactlyOneSourceAndReplacementRequiresRepair()
    {
        var (library, character, _, shot) = Fixture();
        CharacterVoices.Set(shot, CharacterVoices.Initial(shot, character, library), library);
        var reel = shot.Videos[0];
        var choice = new CharacterVoiceSelection { AssetId = character.Id, CharacterName = character.Name, Source = CharacterVoiceSource.Reel,
            SourceName = reel.Name, ReelBindingId = reel.Id, ReelMediaId = reel.Media.Id, Speaker = "Riley", SpeakerConfirmed = true, Excerpt = new(1, 3) };
        CharacterVoices.Set(shot, choice, library); H3Policy.Validate(shot); ReferenceVideos.Validate(shot);
        Assert.Empty(shot.Voices); Assert.True(reel.UseSoundtrack);
        Assert.Equal(new[] { VideoInputKind.Video, VideoInputKind.VideoSoundtrack }, ResolvedReferences.For(shot).InputOrder().Select(i => i.Kind));
        reel.Visuals = ReelVisuals.None;
        Assert.Equal(VideoInputKind.Audio, Assert.Single(ResolvedReferences.For(shot).InputOrder()).Kind);
        Assert.Equal(new ReelAudioExcerpt(1, 3), reel.AudioExcerpt);
        reel.Media = reel.Media with { Id = Guid.NewGuid() };
        CharacterVoices.Set(shot, choice, library);
        Assert.False(reel.UseSoundtrack); Assert.Throws<WorkspaceStoreException>(() => H3Policy.Validate(shot));
    }

    [Fact] public void LegacyOpeningAndFreshRecipesDoNotRewriteCapturedSettings()
    {
        var (library, character, voice, shot) = Fixture();
        var fingerprint = H3Policy.Fingerprint(shot);
        var json = JsonSerializer.Serialize(shot, AtomicJsonFile.Options);
        Assert.DoesNotContain("characterVoices", json);
        CharacterVoices.Validate(shot); _ = ResolvedReferences.For(shot); _ = CharacterVoices.Owners(shot, library);
        Assert.Equal(fingerprint, H3Policy.Fingerprint(shot)); Assert.True(shot.Videos[0].UseSoundtrack);
        var fresh = ReferenceReels.NewDraft(character, library: library);
        Assert.Equal(ReelVoiceMode.ExistingRecording, fresh.VoiceMode); Assert.Equal(voice.Start, fresh.Voice!.Start);
        var captured = fresh.Copy(); library.Assets[0] = character with { DefaultVoiceId = null };
        Assert.Equal(ReelVoiceMode.NewVoice, ReferenceReels.NewDraft(library.Assets[0], library: library).VoiceMode);
        Assert.Equal(voice.Id, captured.Voice!.VoiceId);
    }

    [Fact] public void EditingExcerptKeepsOtherCharacterNumberingAndRejectsCompetingSpeaker()
    {
        var (library, character, voice, shot) = Fixture();
        CharacterVoices.Set(shot, CharacterVoices.Initial(shot, character, library), library);
        var second = character with { Id = Guid.NewGuid(), Name = "Mira", DefaultVoiceId = null };
        var other = voice with { Id = Guid.NewGuid(), AssetId = second.Id, Name = "Mira voice" };
        library.Assets.Add(second); library.Voices.Add(other); shot.Dialogue.Add(new() { Speaker = "Mira", Text = "Hi" });
        var c = CharacterVoices.Initial(shot, second, library); CharacterVoices.SelectRecording(c, other, false); CharacterVoices.Set(shot, c, library);
        var edit = ShotCopy.Of(shot.CharacterVoices![0]); edit.Recording!.Start = 1; CharacterVoices.Set(shot, edit, library);
        Assert.Equal(new Guid?[] { character.Id, second.Id }, ResolvedReferences.For(shot).Audio.Select(a => a.CharacterAssetId));
        c.Speaker = "Riley"; CharacterVoices.Set(shot, c, library);
        Assert.Throws<WorkspaceStoreException>(() => H3Policy.Validate(shot));
    }
}

public sealed partial class ShotTests
{
    [Theory] [InlineData(ReelVisuals.Keyframes)] [InlineData(ReelVisuals.FullReel)]
    public void DefaultVoiceCompositionAndGraphExcludeTheOtherReelSoundtrack(ReelVisuals mode)
    {
        var (library, character, _, shot) = CharacterVoiceTests.Fixture();
        shot.ApprovedScriptId = Guid.NewGuid(); shot.SceneId = Guid.NewGuid();
        var reel = shot.Videos[0]; reel.Visuals = mode;
        if (mode == ReelVisuals.Keyframes) reel.Keyframes = new() { Frames = [new() { Frame = new(reel.Media.Id, reel.Media.Sha256, 0, 0) }] };
        CharacterVoices.Set(shot, CharacterVoices.Initial(shot, character, library), library);
        var resolved = ResolvedReferences.For(shot);
        var inputs = resolved.InputOrder().Select((i, n) => new PreparedVideoInput($"ref-{n}{AiVideoJobPolicy.Extension(i.Kind)}", i.Kind == VideoInputKind.Audio) { Kind = i.Kind, VideoIndex = i.VideoIndex }).ToArray();
        var graph = JsonSerializer.Serialize(ComfyH3Video.BuildWorkflow(ReferenceSnapshot(shot), 42, "voice-test", inputs));
        Assert.Contains("ref_audios.ref_audio_0", graph); Assert.DoesNotContain("ref_video_audios.ref_video_audio_0", graph);
        if (mode == ReelVisuals.Keyframes) { Assert.DoesNotContain("LoadVideo", graph); Assert.DoesNotContain("ref_videos.ref_video_", graph); }
        var request = new PromptCompositionRequest(Guid.NewGuid(), Guid.NewGuid(), 1, "context", "source", shot, "Scene", [], [], [],
            resolved.Pictures.Select(p => new CompositionInput(p.BindingId, "hash")).ToArray(), "", "", "", new(AiBackend.OpenRouter, "vision", "Vision"));
        var messages = PromptComposer.BuildMessages(request, resolved.Pictures.Select(_ => new byte[] { 1 }).ToArray());
        Assert.Equal(2, messages.Count);
        using var context = JsonDocument.Parse(messages[1].Text!);
        var voice = Assert.Single(context.RootElement.GetProperty("voices").EnumerateArray());
        Assert.Equal("Elementalist voice", voice.GetProperty("sourceName").GetString()); Assert.Equal(1, voice.GetProperty("audio").GetInt32());
        Assert.Equal("Riley", voice.GetProperty("speaker").GetString());
        Assert.Empty(context.RootElement.GetProperty("reelAudio").EnumerateArray());
    }
}

[Trait("Category", "Component")]
public sealed class CharacterVoiceComponentTests : BunitContext
{
    [Fact] public void EachCharacterSummaryShowsItsOwnResolvedAudioNumber()
    {
        var (library, character, voice, shot) = CharacterVoiceTests.Fixture();
        var second = character with { Id = Guid.NewGuid(), Name = "Mira", DefaultVoiceId = null };
        var recording = voice with { Id = Guid.NewGuid(), AssetId = second.Id, Name = "Mira voice" };
        library = library with { Assets = [.. library.Assets, second], Voices = [.. library.Voices, recording] };
        shot.Dialogue.Add(new() { Speaker = second.Name, Text = "Hello" });
        CharacterVoices.Set(shot, CharacterVoices.Initial(shot, character, library), library);
        var choice = CharacterVoices.Initial(shot, second, library);
        CharacterVoices.SelectRecording(choice, recording, false); CharacterVoices.Set(shot, choice, library);
        var view = Render<CharacterVoiceEditor>(p => p.Add(c => c.Shot, shot).Add(c => c.Library, library));
        Assert.Contains("<Audio 1>", view.Find($"[data-character-voice='{character.Id}'] .character-voice-summary").TextContent);
        Assert.Contains("<Audio 2>", view.Find($"[data-character-voice='{second.Id}'] .character-voice-summary").TextContent);
    }

    [Fact] public void VoiceControlsAreEnabledAndLegacyDraftIsUntouchedUntilExplicitChoice()
    {
        var (library, character, voice, shot) = CharacterVoiceTests.Fixture();
        var original = JsonSerializer.Serialize(shot); var changes = 0;
        var view = Render<CharacterVoiceEditor>(p => p.Add(c => c.Shot, shot).Add(c => c.Library, library).Add(c => c.Changed, () => changes++));
        Assert.Equal(original, JsonSerializer.Serialize(shot));
        Assert.False(view.Find("fieldset").HasAttribute("disabled"));
        view.Find($"select[aria-label='Voice for {character.Name}']").Change("default");
        Assert.Equal(voice.Id, Assert.Single(shot.Voices).VoiceId); Assert.False(shot.Videos[0].UseSoundtrack);
        view.Find("input[aria-label='Audio start for Riley']").Change("1"); Assert.Equal(1, shot.Voices[0].Start);
        view.Find("select[aria-label='Voice for Riley']").Change("none"); Assert.Empty(shot.Voices); Assert.Equal(3, changes);
    }
}
