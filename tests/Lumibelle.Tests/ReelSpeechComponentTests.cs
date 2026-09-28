using Bunit;
using lumibelle.Components.Assets;
using lumibelle.Models;
using lumibelle.Services.Assets;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class ReelSpeechComponentTests
{
    [Fact]
    public async Task OpeningAnOldRecipeDoesNotSelectAPresetOrChangeItsFingerprint()
    {
        await using var ui = new BunitContext(); var draft = ReferenceReelTests.Recipe();
        var before = ReferenceReels.Fingerprint(draft); var notifications = 0;
        var view = ui.Render<CharacterReelSpeechOptions>(p => p.Add(x => x.Draft, draft).Add(x => x.Changed, () => notifications++));
        Assert.Equal(before, ReferenceReels.Fingerprint(draft)); Assert.Null(draft.Speech); Assert.Equal(0, notifications);
        Assert.Equal("CustomText", view.Find("[aria-label='Sample passage']").GetAttribute("value"));
        Assert.Empty(view.FindAll("[aria-label='Suggested passage']"));
        Assert.Equal(13, view.Find("[aria-label='Spoken language']").QuerySelectorAll("option").Length);
        Assert.Contains("English", view.Markup);
    }

    [Fact]
    public async Task LanguageLengthAndModeChangesOnlyUpdateSuggestionsUntilExplicitApply()
    {
        await using var ui = new BunitContext(); var draft = ReferenceReelTests.Recipe(); draft.Duration = 10;
        draft.Prompt = "Keep authored prompt"; draft.UseGuidance = "Keep authored guidance";
        var words = draft.Line; var notifications = 0;
        var view = ui.Render<CharacterReelSpeechOptions>(p => p.Add(x => x.Draft, draft).Add(x => x.Changed, () => notifications++));
        view.Find("[aria-label='Sample passage']").Change("ConversationalRange");
        view.Find("[aria-label='Spoken language']").Change("French");
        view.Find("[aria-label='Passage length']").Change("Short");
        Assert.Equal(words, draft.Line); Assert.Equal("French", draft.Language); Assert.Equal(3, notifications);
        var preview = view.Find("[aria-label='Suggested passage']").TextContent;
        Assert.StartsWith("Ah, te voilà", preview);
        view.Find("button").Click();
        Assert.Equal(preview, draft.Line); Assert.Equal(4, notifications);
        Assert.Equal("Keep authored prompt", draft.Prompt); Assert.Equal("Keep authored guidance", draft.UseGuidance);
        Assert.True(view.Find("button").HasAttribute("disabled"));
        view.Find("[aria-label='Exact dialogue']").Input("C'est mon propre texte.");
        Assert.Equal("C'est mon propre texte.", draft.Line); Assert.Equal(5, notifications);
        view.Find("[aria-label='Sample passage']").Change("CustomText");
        Assert.Equal("C'est mon propre texte.", draft.Line);
    }

    [Fact]
    public async Task AChangedDurationRecomputesSuggestionWithoutRewritingTheLine()
    {
        await using var ui = new BunitContext(); var draft = ReferenceReelTests.Recipe(); draft.Speech = new();
        var view = ui.Render<CharacterReelSpeechOptions>(p => p.Add(x => x.Draft, draft));
        var first = view.Find("[aria-label='Suggested passage']").TextContent;
        var words = draft.Line;
        draft.Duration = 15;
        // The input event renders the current recipe, as a parent duration render does.
        view.Find("[aria-label='Passage length']").Change("Automatic");
        Assert.NotEqual(first, view.Find("[aria-label='Suggested passage']").TextContent);
        Assert.Equal(words, draft.Line); Assert.Contains(lumibelle.Services.Shots.H3Policy.Seconds(15).ToString("0.###"), view.Markup);
    }

    [Fact]
    public async Task OtherLanguagesAndLegacyLabelsArePreservedWithoutEnglishFallback()
    {
        await using var ui = new BunitContext(); var draft = ReferenceReelTests.Recipe(); draft.Speech = new();
        draft.Language = "Finnish"; draft.Line = "Oma teksti.";
        var view = ui.Render<CharacterReelSpeechOptions>(p => p.Add(x => x.Draft, draft));
        Assert.Equal("Finnish", view.Find("[aria-label='Other spoken language']").GetAttribute("value"));
        Assert.Empty(view.FindAll("[aria-label='Suggested passage']"));
        Assert.Empty(view.FindAll("button"));
        view.Find("[aria-label='Other spoken language']").Input("Icelandic");
        Assert.Equal("Icelandic", draft.Language); Assert.Equal("Oma teksti.", draft.Line);
        view.Find("[aria-label='Spoken language']").Change("Swedish");
        Assert.Contains("Experimental", view.Find(".speech-support").TextContent);
        Assert.Equal("Oma teksti.", draft.Line);
        view.Find("[aria-label='Spoken language']").Change("other");
        Assert.Equal("Swedish", draft.Language); Assert.Equal("Oma teksti.", draft.Line);
    }

    [Fact]
    public async Task ArabicTextUsesAutomaticDirectionAndRemainsPlainDialogue()
    {
        await using var ui = new BunitContext(); var draft = ReferenceReelTests.Recipe(); draft.Speech = new(); draft.Language = "Arabic";
        var view = ui.Render<CharacterReelSpeechOptions>(p => p.Add(x => x.Draft, draft));
        Assert.Equal("auto", view.Find("[aria-label='Suggested passage']").GetAttribute("dir"));
        Assert.Equal("auto", view.Find("[aria-label='Exact dialogue']").GetAttribute("dir"));
        Assert.Equal("ar", view.Find("[aria-label='Exact dialogue']").GetAttribute("lang"));
        view.Find("button").Click(); Assert.StartsWith("أهلًا", draft.Line);
    }

    [Fact]
    public async Task DisabledControlsAndSilentModeDoNotMutateRememberedSpeech()
    {
        await using var ui = new BunitContext(); var draft = ReferenceReelTests.Recipe(); draft.Speech = new();
        var before = ReferenceReels.Fingerprint(draft); var notifications = 0;
        var view = ui.Render<CharacterReelSpeechOptions>(p => p.Add(x => x.Draft, draft).Add(x => x.Disabled, true).Add(x => x.Changed, () => notifications++));
        Assert.All(view.FindAll("input,textarea,select,button"), node => Assert.True(node.HasAttribute("disabled")));
        view.Find("[aria-label='Spoken language']").Change("Spanish");
        view.Find("[aria-label='Exact dialogue']").Input("Must not replace.");
        Assert.Equal(before, ReferenceReels.Fingerprint(draft)); Assert.Equal(0, notifications);
        draft.VoiceMode = ReelVoiceMode.Silent;
        var silent = ui.Render<CharacterReelSpeechOptions>(p => p.Add(x => x.Draft, draft));
        Assert.Empty(silent.FindAll("input,textarea,select,button")); Assert.NotNull(draft.Speech);
    }
}
