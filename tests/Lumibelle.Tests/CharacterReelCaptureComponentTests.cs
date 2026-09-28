using Bunit;
using AngleSharp.Html.Dom;
using lumibelle.Components.Assets;
using lumibelle.Models;
using lumibelle.Services.Assets;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class CharacterReelCaptureComponentTests
{
    private static ReferenceReelDraft Recipe()
    {
        var draft = ReferenceReelTests.Recipe(); ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterCapture);
        draft.Prompt = "Authored prompt"; draft.UseGuidance = "Authored guidance";
        draft.CheckedInputs = ReferenceReels.InputsFingerprint(draft); return draft;
    }

    [Fact]
    public async Task CaptureControlsChangeOnlyTheirSettingsAndNotifyTheExistingSavePath()
    {
        await using var ui = new BunitContext(); var draft = Recipe(); var saved = new List<ReferenceReelDraft>();
        var view = ui.Render<CharacterReelCaptureOptions>(p => p.Add(x => x.Draft, draft)
            .Add(x => x.Changed, () => saved.Add(draft.Copy())));
        Assert.Equal("Arms", ((IHtmlSelectElement)view.Find("[aria-label='Capture movement']")).Value);
        view.Find("[aria-label='Capture movement']").Change("Knee");
        view.Find("[aria-label='Capture close-up transition']").Change("PushIn");
        Assert.Equal(ReelArticulation.Knee, draft.CaptureArticulation); Assert.Equal(ReelCloseUpTransition.PushIn, draft.CaptureCloseUp);
        Assert.Equal(2, saved.Count); Assert.Null(draft.CheckedInputs);
        Assert.Equal("Authored prompt", draft.Prompt); Assert.Equal("Authored guidance", draft.UseGuidance);
        Assert.Equal(10, draft.Duration); Assert.Equal(ReelVoiceMode.Silent, draft.VoiceMode);
        Assert.Contains("lift and replace one foot", view.Markup); Assert.Contains("without a cut", view.Markup);
        Assert.Contains(ReferenceReels.LongReelKeyframeHint, view.Find(".reel-keyframe-hint").TextContent);
    }

    [Theory]
    [InlineData(5, true, true)] [InlineData(8, true, false)] [InlineData(8.9, true, false)] [InlineData(9, false, false)]
    public async Task ShortDurationDisablesOnlyUnsupportedActionsAndRetainsTheChosenOptions(double seconds, bool movementDisabled, bool transitionDisabled)
    {
        await using var ui = new BunitContext(); var draft = Recipe(); draft.Duration = seconds;
        draft.CaptureArticulation = ReelArticulation.Knee; draft.CaptureCloseUp = ReelCloseUpTransition.PushIn;
        var view = ui.Render<CharacterReelCaptureOptions>(p => p.Add(x => x.Draft, draft));
        Assert.Equal(movementDisabled, view.Find("[aria-label='Capture movement']").HasAttribute("disabled"));
        Assert.Equal(transitionDisabled, view.Find("[aria-label='Capture close-up transition']").HasAttribute("disabled"));
        Assert.Equal(ReelArticulation.Knee, draft.CaptureArticulation); Assert.Equal(ReelCloseUpTransition.PushIn, draft.CaptureCloseUp);
        Assert.Equal("Authored prompt", draft.Prompt);
        if (seconds < 8) Assert.Contains("five held views", view.Markup);
    }

    [Fact]
    public async Task DisabledControlsAndInvalidValuesCannotChangeTheDraft()
    {
        await using var ui = new BunitContext(); var draft = Recipe(); var before = ReferenceReels.Fingerprint(draft); var notifications = 0;
        var view = ui.Render<CharacterReelCaptureOptions>(p => p.Add(x => x.Draft, draft).Add(x => x.Disabled, true)
            .Add(x => x.Changed, () => notifications++));
        Assert.All(view.FindAll("select"), control => Assert.True(control.HasAttribute("disabled")));
        view.Find("[aria-label='Capture movement']").Change("Knee");
        view.Find("[aria-label='Capture close-up transition']").Change("PushIn");
        Assert.Equal(before, ReferenceReels.Fingerprint(draft)); Assert.Equal(0, notifications);
        var enabled = ui.Render<CharacterReelCaptureOptions>(p => p.Add(x => x.Draft, draft).Add(x => x.Changed, () => notifications++));
        enabled.Find("[aria-label='Capture movement']").Change("99");
        enabled.Find("[aria-label='Capture close-up transition']").Change("99");
        Assert.Equal(before, ReferenceReels.Fingerprint(draft)); Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task LegacyAndEnvironmentRecipesDoNotRenderCaptureControlsOrChangeOnOpen()
    {
        await using var ui = new BunitContext(); var legacy = ReferenceReelTests.Recipe();
        var environment = ReferenceReels.NewDraft(new() { Id = Guid.NewGuid(), Category = AssetCategory.Environment, Name = "Study" });
        foreach (var draft in new[] { legacy, environment })
        {
            var before = ReferenceReels.Fingerprint(draft);
            var view = ui.Render<CharacterReelCaptureOptions>(p => p.Add(x => x.Draft, draft));
            Assert.Empty(view.FindAll("select")); Assert.Equal(before, ReferenceReels.Fingerprint(draft));
        }
    }
}
