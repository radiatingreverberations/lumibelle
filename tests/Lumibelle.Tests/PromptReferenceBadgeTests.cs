using Bunit;
using lumibelle.Components.Shots;
using lumibelle.Services.Production;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class PromptReferenceBadgeTests : BunitContext
{
    [Theory]
    [InlineData(PromptReferenceState.Missing, "Prompt missing")]
    [InlineData(PromptReferenceState.ReferencesChanged, "References changed")]
    [InlineData(PromptReferenceState.NeedsReview, "Review prompt")]
    public void VisibleFlagsIncludeTextAndExplanation(PromptReferenceState state, string label)
    {
        var ui = Render<PromptReferenceBadge>(p => p.Add(c => c.Check, new(state, "A useful explanation")));
        var badge = ui.Find("[data-prompt-state]");
        Assert.Equal(label, badge.TextContent); Assert.Equal(state.ToString(), badge.GetAttribute("data-prompt-state"));
        Assert.Equal("A useful explanation", badge.GetAttribute("title"));
    }
    [Fact]
    public void ARevertedOrReviewedSelectionRemovesTheFlag()
    {
        var ui = Render<PromptReferenceBadge>(p => p.Add(c => c.Check, new(PromptReferenceState.ReferencesChanged, "Changed")));
        Assert.Single(ui.FindAll("[data-prompt-state]"));
        ui.Render(p => p.Add(c => c.Check, new(PromptReferenceState.Current, "Same references")));
        Assert.Empty(ui.FindAll("[data-prompt-state]"));
    }
    [Fact]
    public void ALoadingStatusShowsNoFlag()
    {
        var ui = Render<PromptReferenceBadge>(p => p.Add(c => c.Check, PromptReferenceCheck.Checking));
        Assert.Empty(ui.FindAll("[data-prompt-state]"));
    }
}
