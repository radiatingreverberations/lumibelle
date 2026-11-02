using Bunit;
using lumibelle.Models;
using lumibelle.Services.Production;

namespace Lumibelle.Tests;

// Screenplay cues such as JAN (V.O.) name the character Jan Sherwood. A speaker that is not
// recognized keeps no voice, and the video model then makes one up for each take.
public sealed class SpeakerVoiceMatchingTests
{
    private static (AssetLibrary Library, ReferenceAsset Jan, VoiceReference Voice, List<ShotDialogue> Lines) Jan()
    {
        var (library, riley, voice, _) = CharacterVoiceTests.Fixture();
        var jan = riley with { Name = "Jan Sherwood" };
        library.Assets[0] = jan;
        return (library, jan, voice, [new() { Speaker = "JAN (V.O.)", Text = "Breathe.", Language = "en" }]);
    }
    private static Shot Silent(AssetLibrary library, ReferenceAsset character)
    {
        var shot = new Shot { Images = [new() { AssetId = character.Id, MediaId = Guid.NewGuid(), Name = character.Name }] };
        CharacterVoices.Set(shot, CharacterVoices.Initial(shot, character, library), library);
        return shot;
    }

    [Theory]
    [InlineData("JAN (V.O.)", "Jan Sherwood", true)]
    [InlineData("JAN (O.S.) (CONT'D)", "Jan Sherwood", true)]
    [InlineData("jan sherwood", "Jan Sherwood", true)]
    [InlineData("MIRA (V.O.)", "Jan Sherwood", false)]
    [InlineData("(V.O.)", "Jan Sherwood", false)]
    [InlineData("THE GIRL", "Girl", false)]
    public void ScreenplayExtensionsAreNotPartOfTheName(string speaker, string character, bool names) =>
        Assert.Equal(names, DialogueSpeakers.Names(speaker, character));

    [Fact]
    public void AddingACharacterToAVoiceoverShotSelectsTheirDefaultVoice()
    {
        var (library, jan, voice, lines) = Jan();
        var shot = new Shot { Dialogue = lines };
        var choice = CharacterVoices.Initial(shot, jan, library);
        Assert.Equal(("JAN (V.O.)", true, CharacterVoiceSource.Recording, true), (choice.Speaker, choice.SpeakerConfirmed, choice.Source, choice.FromDefault));
        CharacterVoices.Set(shot, choice, library); CharacterVoices.Validate(shot);
        Assert.Equal(voice.Id, Assert.Single(shot.Voices).VoiceId);
        Assert.Empty(CharacterVoices.Unvoiced(shot, library));
    }

    [Fact]
    public void CopyingFromASilentShotGivesAVoiceoverSpeakerTheDefaultVoice()
    {
        var (library, jan, voice, lines) = Jan();
        var source = Silent(library, jan);
        Assert.Equal(CharacterVoiceSource.None, source.CharacterVoices![0].Source);
        var copy = ReferenceCopies.Into(source, new Shot { Dialogue = lines }, library);
        Assert.Empty(copy.Warnings); CharacterVoices.Validate(copy.Inputs);
        var recording = Assert.Single(copy.Inputs.Voices);
        Assert.Equal(("JAN (V.O.)", voice.Id), (recording.Speaker, recording.VoiceId));
        Assert.Contains(copy.Notes!, n => n.Contains("selected their default voice"));
    }

    [Fact]
    public void AVoiceForJanSpeaksTheJanVoiceoverLines()
    {
        var (library, jan, voice, lines) = Jan();
        var source = new Shot { Dialogue = [new() { Speaker = "JAN", Text = "Hi", Language = "en" }] };
        CharacterVoices.Set(source, CharacterVoices.Initial(source, jan, library), library);
        Assert.Equal("JAN", source.CharacterVoices![0].Speaker);
        var copy = ReferenceCopies.Into(source, new Shot { Dialogue = lines }, library);
        Assert.Empty(copy.Warnings); CharacterVoices.Validate(copy.Inputs);
        var recording = Assert.Single(copy.Inputs.Voices);
        Assert.Equal(("JAN (V.O.)", voice.Id), (recording.Speaker, recording.VoiceId));
    }

    [Fact]
    public void ASpeakerLeftWithoutAVoiceIsAWarning()
    {
        var (library, jan, _, lines) = Jan();
        library.Assets[0] = jan = jan with { DefaultVoiceId = null };
        // No default voice: None stays, and the copy says so.
        var copy = ReferenceCopies.Into(Silent(library, jan), new Shot { Dialogue = lines }, library);
        Assert.Equal(CharacterVoiceSource.None, Assert.Single(copy.Inputs.CharacterVoices!).Source);
        Assert.Contains("no default voice", Assert.Single(copy.Warnings));
        // A source without Jan at all still warns about the unvoiced line.
        var bare = ReferenceCopies.Into(new Shot(), new Shot { Dialogue = lines }, library);
        Assert.Equal(CharacterVoices.UnvoicedWarning("JAN (V.O.)", jan), Assert.Single(bare.Warnings));
        // An extra who names no character does not.
        Assert.Empty(ReferenceCopies.Into(new Shot(), new Shot { Dialogue = [new() { Speaker = "WAITER", Text = "Sir?" }] }, library).Warnings);
    }

    [Fact]
    public void AnExplicitNoneForASpeakingCharacterIsKeptWithAWarning()
    {
        var (library, jan, _, lines) = Jan();
        var destination = new Shot { Dialogue = lines };
        CharacterVoices.Set(destination, new() { AssetId = jan.Id, CharacterName = jan.Name, Source = CharacterVoiceSource.None }, library);
        var copy = ReferenceCopies.Into(Silent(library, jan), destination, library);
        Assert.Empty(copy.Inputs.Voices);
        Assert.Contains("this shot's voice for them is None", Assert.Single(copy.Warnings));
    }

    private static Shot Vocalizing(AssetLibrary library, ReferenceAsset character, VoiceReference voice, List<ShotDialogue> lines)
    {
        var shot = new Shot { Dialogue = lines };
        var choice = new CharacterVoiceSelection { AssetId = character.Id, CharacterName = character.Name, Speaker = "", SpeakerConfirmed = true };
        CharacterVoices.SelectRecording(choice, voice, false); CharacterVoices.Set(shot, choice, library);
        return shot;
    }

    [Fact]
    public void AVoiceThatOnlyVocalizedSpeaksTheCharactersLinesWhenCopied()
    {
        var (library, jan, voice, lines) = Jan();
        var source = Vocalizing(library, jan, voice, []);
        var copy = ReferenceCopies.Into(source, new Shot { Dialogue = lines }, library);
        Assert.Empty(copy.Warnings); CharacterVoices.Validate(copy.Inputs);
        Assert.Equal(("JAN (V.O.)", true), (copy.Inputs.CharacterVoices![0].Speaker, copy.Inputs.CharacterVoices[0].SpeakerConfirmed));
        Assert.Equal("JAN (V.O.)", Assert.Single(copy.Inputs.Voices).Speaker);
        Assert.Contains(copy.Notes!, n => n.Contains("speaks the JAN (V.O.) lines here"));
    }

    [Fact]
    public void AVocalizationsOnlyVoiceLeavesTheCharactersLinesUnvoiced()
    {
        // Its recording is labelled with the character's name, which must not count as voicing JAN.
        var (library, riley, voice, _) = CharacterVoiceTests.Fixture();
        var jan = riley with { Name = "Jan" }; library.Assets[0] = jan;
        var shot = Vocalizing(library, jan, voice, [new() { Speaker = "JAN", Text = "Where am I?", Language = "en" }]);
        Assert.Equal("Jan", Assert.Single(shot.Voices).Speaker);
        Assert.Equal(("JAN", jan.Id), Assert.Single(CharacterVoices.Unvoiced(shot, library)) is var (speaker, character) ? (speaker, character.Id) : default);
    }
}

[Trait("Category", "Component")]
public sealed class SpeakerVoiceWarningComponentTests : BunitContext
{
    [Fact] public void ManageReferencesWarnsUntilTheVoiceoverSpeakerHasAVoice()
    {
        var (library, riley, _, _) = CharacterVoiceTests.Fixture();
        var jan = riley with { Name = "Jan Sherwood" }; library.Assets[0] = jan;
        var shot = new Shot { Dialogue = [new() { Speaker = "JAN (V.O.)", Text = "Breathe.", Language = "en" }] };
        CharacterVoices.Set(shot, new() { AssetId = jan.Id, CharacterName = jan.Name, Source = CharacterVoiceSource.None }, library);
        var view = Render<lumibelle.Components.Shots.CharacterVoiceEditor>(p => p.Add(c => c.Shot, shot).Add(c => c.Library, library));
        Assert.Equal(CharacterVoices.UnvoicedWarning("JAN (V.O.)", jan), view.Find(".character-voice-missing").TextContent);
        view.Find("select[aria-label='Voice for Jan Sherwood']").Change("default");
        Assert.Empty(view.FindAll(".character-voice-missing"));
        Assert.Equal("JAN (V.O.)", Assert.Single(shot.Voices).Speaker);
    }
}
