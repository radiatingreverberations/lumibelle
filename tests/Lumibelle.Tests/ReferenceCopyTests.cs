using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ReferenceCopyTests
{
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, AtomicJsonFile.Options);

    [Fact]
    public void ReelVoiceLinksAreRewrittenAndBothInputsRemainUnchanged()
    {
        var (library, character, _, source) = CharacterVoiceTests.Fixture();
        var reel = source.Videos[0];
        CharacterVoices.Set(source, new()
        {
            AssetId = character.Id, CharacterName = character.Name, Source = CharacterVoiceSource.Reel,
            SourceName = reel.Name, ReelBindingId = reel.Id, ReelMediaId = reel.Media.Id,
            Speaker = "Riley", SpeakerConfirmed = true, Excerpt = new(1, 3)
        }, library);
        var target = new Shot { Title = "Destination", Description = "Keep this direction", Duration = 5,
            Dialogue = ShotCopy.Of(source.Dialogue) };
        var beforeSource = Json(source); var beforeTarget = Json(target);
        var copy = ReferenceCopies.Into(source, target, library);
        CharacterVoices.Validate(copy.Inputs);
        var copiedReel = Assert.Single(copy.Inputs.Videos);
        var copiedVoice = Assert.Single(copy.Inputs.CharacterVoices!);
        Assert.NotEqual(reel.Id, copiedReel.Id);
        Assert.Equal(copiedReel.Id, copiedVoice.ReelBindingId);
        Assert.Equal(reel.Media, copiedReel.Media);
        Assert.True(copiedReel.UseSoundtrack);
        Assert.Equal(new ReelAudioExcerpt(1, 3), copiedReel.AudioExcerpt);
        Assert.Same(copiedVoice, Assert.Single(ResolvedReferences.For(copy.Inputs).Audio).CharacterVoice);
        Assert.Equal(target.Id, copy.Inputs.Id);
        Assert.Equal(target.Title, copy.Inputs.Title);
        Assert.Equal(target.Description, copy.Inputs.Description);
        Assert.Equal(Json(target.Dialogue), Json(copy.Inputs.Dialogue));
        Assert.Equal(beforeSource, Json(source)); Assert.Equal(beforeTarget, Json(target));
    }

    [Fact]
    public void KeyframesGetNewBindingIdsButKeepTheirMediaAndCrop()
    {
        var (library, _, _, source) = CharacterVoiceTests.Fixture();
        var reel = source.Videos[0]; reel.Visuals = ReelVisuals.Keyframes;
        reel.Keyframes = new() { Frames = [new() { Frame = new(reel.Media.Id, reel.Media.Sha256, 24, 1),
            Crop = new() { Width = .5 }, Notes = "Front view" }] };
        var copy = ReferenceCopies.Into(source, new Shot(), library).Inputs;
        var frame = Assert.Single(copy.Videos[0].Keyframes!.Frames);
        Assert.NotEqual(reel.Keyframes.Frames[0].Id, frame.Id);
        Assert.Equal(reel.Keyframes.Frames[0].Frame, frame.Frame);
        Assert.Equal(reel.Keyframes.Frames[0].Crop, frame.Crop);
        frame.Notes = "Changed draft";
        Assert.Equal("Front view", reel.Keyframes.Frames[0].Notes);
    }

    [Fact]
    public void ImageCharacterLinksUseUnambiguousDestinationIdentity()
    {
        var sourceCharacter = new ShotCharacter(Guid.NewGuid(), "Riley");
        var targetCharacter = new ShotCharacter(Guid.NewGuid(), "riley");
        var source = new Shot { Characters = [sourceCharacter], Images = [new() { Name = "Portrait",
            RepresentsId = sourceCharacter.Id, AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Crop = new() { Width = .5 } }] };
        var target = new Shot { Characters = [targetCharacter] };
        var copy = ReferenceCopies.Into(source, target, new AssetLibrary { ProjectId = Guid.NewGuid() }).Inputs;
        var image = Assert.Single(copy.Images);
        Assert.NotEqual(source.Images[0].Id, image.Id);
        Assert.Equal(targetCharacter.Id, image.RepresentsId);
        Assert.Equal(source.Images[0].AssetId, image.AssetId);
        image.Name = "Edited"; Assert.Equal("Portrait", source.Images[0].Name);
    }

    [Fact]
    public void DuplicateNamesDoNotCrashOrGuessACharacterLink()
    {
        var character = new ShotCharacter(Guid.NewGuid(), "Riley");
        var source = new Shot { Characters = [character], Images = [new() { Name = "Portrait", RepresentsId = character.Id }] };
        var target = new Shot { Characters = [new(Guid.NewGuid(), "Riley"), new(Guid.NewGuid(), "RILEY")] };
        var copy = ReferenceCopies.Into(source, target, new AssetLibrary { ProjectId = Guid.NewGuid() });
        Assert.Null(copy.Inputs.Images[0].RepresentsId); Assert.NotEmpty(copy.Warnings);
    }

    [Fact]
    public void ChangedDestinationDialogueRequiresNewSpeakerConfirmation()
    {
        var (library, character, _, source) = CharacterVoiceTests.Fixture();
        CharacterVoices.Set(source, CharacterVoices.Initial(source, character, library), library);
        var target = new Shot { Dialogue = [new() { Speaker = "THE GIRL", Text = "Different dialogue" }] };
        var copy = ReferenceCopies.Into(source, target, library);
        var voice = Assert.Single(copy.Inputs.CharacterVoices!);
        Assert.False(voice.SpeakerConfirmed); Assert.Empty(voice.Speaker);
        Assert.NotEmpty(copy.Warnings);
        Assert.Throws<WorkspaceStoreException>(() => CharacterVoices.Validate(copy.Inputs));
        voice.Speaker = "THE GIRL"; voice.SpeakerConfirmed = true;
        CharacterVoices.Set(copy.Inputs, voice, library);
        CharacterVoices.Validate(copy.Inputs);
        Assert.Equal("THE GIRL", Assert.Single(copy.Inputs.Voices).Speaker);
    }

    [Fact]
    public void ACharacterWithNoLinesInTheDestinationIsCopiedSilent()
    {
        var (library, character, _, source) = CharacterVoiceTests.Fixture();
        CharacterVoices.Set(source, CharacterVoices.Initial(source, character, library), library);
        // No dialogue at all, then only lines for a speaker another voice keeps.
        foreach (var target in new[] { new Shot(), new Shot { Dialogue = [new() { Speaker = "MIRA", Text = "Hi" }],
            CharacterVoices = [], Images = [] } })
        {
            var mira = new CharacterVoiceSelection { AssetId = Guid.NewGuid(), CharacterName = "Mira", Source = CharacterVoiceSource.None };
            var from = ShotCopy.Of(source);
            if (target.Dialogue.Count > 0) { from.Dialogue.Add(new() { Speaker = "MIRA", Text = "Hi" }); from.CharacterVoices!.Add(mira with { Source = CharacterVoiceSource.Recording, Speaker = "MIRA", SpeakerConfirmed = true, SourceName = "Mira voice" }); }
            var copy = ReferenceCopies.Into(from, target, library);
            var riley = copy.Inputs.CharacterVoices!.Single(v => v.AssetId == character.Id);
            Assert.Equal(CharacterVoiceSource.None, riley.Source);
            Assert.Empty(copy.Warnings);
            Assert.Contains("Riley has no lines here, so their voice is None.", copy.Notes!);
            Assert.DoesNotContain(copy.Inputs.Voices, v => v.CharacterAssetId == character.Id);
        }
    }

    [Fact]
    public void AVoiceFollowsItsCharacterToADifferentlyNamedSpeaker()
    {
        var (library, character, _, source) = CharacterVoiceTests.Fixture();
        var choice = CharacterVoices.Initial(source, character, library);
        // By the cast member its pictures represent, even beside another speaker.
        var lux = new ShotCharacter(Guid.NewGuid(), "Lux");
        source.Characters = [lux];
        source.Images = [new() { AssetId = character.Id, MediaId = Guid.NewGuid(), Name = "Riley", RepresentsId = lux.Id }];
        CharacterVoices.Set(source, choice, library);
        var target = new Shot { Characters = [new(Guid.NewGuid(), "Lux")], Dialogue = [new() { Speaker = "LUX", Text = "Hi" }, new() { Speaker = "MIRA", Text = "Hello" }] };
        var copy = ReferenceCopies.Into(source, target, library);
        var voice = Assert.Single(copy.Inputs.CharacterVoices!);
        Assert.Equal(("LUX", true), (voice.Speaker, voice.SpeakerConfirmed));
        Assert.Empty(copy.Warnings);
        Assert.Contains("Riley's voice speaks the LUX lines here.", copy.Notes!);

        // By the words of its name: GUARD is the Noxian Guard.
        source.Images = []; source.Characters = [];
        source.CharacterVoices![0].CharacterName = "Noxian Guard";
        var guard = ReferenceCopies.Into(source, new Shot { Dialogue = [new() { Speaker = "GUARD", Text = "No." }] }, library);
        Assert.Equal("GUARD", Assert.Single(guard.Inputs.CharacterVoices!).Speaker);
        CharacterVoices.Validate(guard.Inputs);
    }

    [Fact]
    public void RecordingExcerptsAndExplicitNoneDoNotFollowNewDefaults()
    {
        var (library, character, original, source) = CharacterVoiceTests.Fixture();
        CharacterVoices.Set(source, CharacterVoices.Initial(source, character, library), library);
        library.Voices[0] = original with { Start = 0, ExcerptDuration = 1 };
        var copy = ReferenceCopies.Into(source, source, library).Inputs;
        Assert.Equal(original.Start, copy.Voices[0].Start);
        Assert.Equal(original.ExcerptDuration, copy.Voices[0].Duration);
        CharacterVoices.Set(source, new() { AssetId = character.Id, CharacterName = character.Name,
            Source = CharacterVoiceSource.None }, library);
        var silent = ReferenceCopies.Into(source, copy, library).Inputs;
        Assert.Equal(CharacterVoiceSource.None, silent.CharacterVoices![0].Source);
        Assert.Empty(ResolvedReferences.For(silent).Audio);
    }

    [Fact]
    public void LegacyVoiceInputsStayLegacyAndReplacingWithEmptyClearsReferencesOnly()
    {
        var (library, _, _, source) = CharacterVoiceTests.Fixture();
        Assert.Null(source.CharacterVoices);
        var copy = ReferenceCopies.Into(source, new Shot(), library).Inputs;
        Assert.Null(copy.CharacterVoices); Assert.True(copy.Videos[0].UseSoundtrack);
        var cleared = ReferenceCopies.Into(new Shot(), copy, library).Inputs;
        Assert.Empty(cleared.Images); Assert.Empty(cleared.Videos); Assert.Empty(cleared.Voices);
        Assert.Equal(copy.Id, cleared.Id);
    }

    [Fact]
    public void MissingReelLinkFailsWithoutPartiallyReplacingTheTarget()
    {
        var (library, character, _, source) = CharacterVoiceTests.Fixture();
        source.CharacterVoices = [new() { AssetId = character.Id, CharacterName = character.Name,
            Source = CharacterVoiceSource.Reel, ReelBindingId = Guid.NewGuid() }];
        var target = new Shot { Images = [new() { Name = "Keep me" }] };
        var before = Json(target);
        Assert.Throws<WorkspaceStoreException>(() => ReferenceCopies.Into(source, target, library));
        Assert.Equal(before, Json(target));
    }
}
