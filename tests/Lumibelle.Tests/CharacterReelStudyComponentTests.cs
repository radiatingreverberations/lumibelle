using Bunit;
using lumibelle.Components.Assets;
using lumibelle.Models;
using lumibelle.Services.Assets;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class CharacterReelStudyComponentTests
{
    [Theory]
    [MemberData(nameof(CharacterReelStudyTests.Modes), MemberType = typeof(CharacterReelStudyTests))]
    public async Task StudyPanelShowsItsOwnPurposeAndDoesNotOfferUnrelatedArticulation(ReelFraming mode)
    {
        await using var ui = new BunitContext(); var draft = ReferenceReelTests.Recipe();
        ReferenceReels.SelectCharacterPreset(draft, mode);
        draft.Prompt = "Authored prompt"; draft.UseGuidance = "Authored guidance";
        var before = ReferenceReels.Fingerprint(draft); var notifications = 0;
        var view = ui.Render<CharacterReelCaptureOptions>(p => p.Add(x => x.Draft, draft)
            .Add(x => x.Changed, () => notifications++));
        var preset = ReferenceReels.CapturePreset(draft)!;
        Assert.Equal(preset.Label, view.Find(".capture-mode").TextContent);
        Assert.Contains(preset.Purpose, view.Find(".capture-purpose").TextContent);
        Assert.Equal(ReferenceReels.CharacterCaptureSummary(draft), view.Find(".capture-summary").TextContent);
        Assert.Empty(view.FindAll("select"));
        Assert.Equal(before, ReferenceReels.Fingerprint(draft)); Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task ShorterNeutralStudyExplainsHeldViewFallbackWithoutChangingTheMode()
    {
        await using var ui = new BunitContext(); var draft = ReferenceReelTests.Recipe();
        ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterNeutralTurntable); draft.Duration = 7;
        var view = ui.Render<CharacterReelCaptureOptions>(p => p.Add(x => x.Draft, draft));
        Assert.Contains("five held principal views", view.Find("[role=status]").TextContent);
        Assert.Contains("not a continuous orbit", view.Find("[role=status]").TextContent);
        Assert.Equal(ReelFraming.CharacterNeutralTurntable, draft.Framing); Assert.Equal(7, draft.Duration);
        Assert.Empty(view.FindAll("select"));
    }

    [Fact]
    public async Task CombinedCaptureStillOffersItsOriginalMovementAndCloseUpChoices()
    {
        await using var ui = new BunitContext(); var draft = ReferenceReelTests.Recipe();
        ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterFacePriority);
        ReferenceReels.SelectCharacterPreset(draft, ReelFraming.CharacterCapture);
        var view = ui.Render<CharacterReelCaptureOptions>(p => p.Add(x => x.Draft, draft));
        Assert.Equal(2, view.FindAll("select").Count); Assert.Empty(view.FindAll(".capture-purpose"));
        view.Find("[aria-label='Capture movement']").Change("Knee");
        view.Find("[aria-label='Capture close-up transition']").Change("PushIn");
        Assert.Equal(ReelArticulation.Knee, draft.CaptureArticulation); Assert.Equal(ReelCloseUpTransition.PushIn, draft.CaptureCloseUp);
        Assert.Contains("without a cut", view.Markup);
    }
}
