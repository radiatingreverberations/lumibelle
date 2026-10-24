using Bunit;
using lumibelle.Components.AI;
using lumibelle.Components.Script;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class StreamingResponsePreviewComponentTests : BunitContext
{
    [Fact]
    public void AssetCardsDecodePartialDescriptionsAndKeepSourceIdsOutOfTheReadingView()
    {
        var cut = Render<StreamingResponsePreview>(p => p.Add(c => c.ArrayLabel, "Assets").Add(c => c.Raw, """
            [{"category":"character","name":"Eira","description":"Short dark hair.","looks":[{"name":"Raincoat","description":"A blue coat."}],"evidence":[{"label":"The gate","sceneId":"11111111-1111-1111-1111-111111111111","excerpt":"Eira takes the key."}],"matchedAssetId":"22222222-2222-2222-2222-222222222222"},{"category":"prop","name":"Brass key","description":"A key with a curved
            """));
        Assert.Equal(2, cut.FindAll(".streaming-preview-card").Count);
        Assert.Equal(new[] { "Character", "Prop" }, cut.FindAll(".streaming-asset-category").Select(c => c.TextContent));
        Assert.Contains("Assets", cut.Find(".streaming-preview-group h4").TextContent);
        Assert.Contains("Raincoat", cut.Find(".streaming-asset-looks").TextContent);
        Assert.Contains("A key with a curved", cut.Find(".is-writing").TextContent);
        Assert.False(cut.Find(".streaming-asset-evidence").HasAttribute("open"));
        Assert.DoesNotContain("11111111-1111", cut.Markup);
        Assert.DoesNotContain("22222222-2222", cut.Markup);
        Assert.Empty(cut.FindAll("button"));
        cut.Render(p => p.Add(c => c.ArrayLabel, "Suggestions"));
        Assert.Contains("Suggestions", cut.Find(".streaming-preview-group h4").TextContent);
    }

    [Fact]
    public void EmptyStreamCanShowAUsefulWaitingStateUntilTheFirstSuggestionArrives()
    {
        var cut = Render<StreamingResponsePreview>(p => p.Add(c => c.Title, "Asset suggestions arriving").Add(c => c.EmptyMessage, "Suggestions appear as they arrive."));
        Assert.Contains("Suggestions appear as they arrive", cut.Find(".streaming-response-empty").TextContent);
        cut.Render(p => p.Add(c => c.Raw, "[{\"category\":\"prop\",\"name\":\"Key"));
        Assert.Empty(cut.FindAll(".streaming-response-empty"));
        Assert.Contains("Key", cut.Find(".streaming-preview-card").TextContent);
    }

    [Fact]
    public void CardsUpdateInPlaceWhileTheCurrentItemIsStillWriting()
    {
        var cut = Render<StreamingResponsePreview>(p => p.Add(c => c.Raw, "{\"beats\":[\"Eira finds a key.\",\"A river"));
        Assert.Equal(2, cut.FindAll(".streaming-preview-card").Count);
        Assert.Contains("Received", cut.Find(".streaming-preview-card").TextContent);
        Assert.Contains("Still writing", cut.Find(".is-writing").TextContent);
        Assert.Contains("not yet checked", cut.Markup);
        cut.Render(p => p.Add(c => c.Raw, "{\"beats\":[\"Eira finds a key.\",\"A river leads to a gate.\"]}"));
        Assert.Equal(2, cut.FindAll(".streaming-preview-card").Count);
        Assert.Empty(cut.FindAll(".is-writing"));
        Assert.Contains("A river leads to a gate.", cut.Markup);
    }

    [Fact]
    public void SceneCardsRenderScreenplayTextAndEscapeMarkupFromTheModel()
    {
        var cut = Render<StreamingResponsePreview>(p => p.Add(c => c.Raw,
            "{\"scenes\":[{\"heading\":\"EXT. RIVER - DAY\",\"blocks\":[{\"kind\":\"Action\",\"spans\":[{\"text\":\"<script>alert('key')</script>\"}]},{\"kind\":\"Character\",\"spans\":[{\"text\":\"EIRA\"}]},{\"kind\":\"Dialogue\",\"spans\":[{\"text\":\"The gate is open"));
        Assert.Equal("EXT. RIVER - DAY", cut.Find("article header strong").TextContent);
        Assert.Equal("EIRA", cut.Find(".is-character").TextContent);
        Assert.Contains("The gate is open", cut.Find(".is-dialogue").TextContent);
        Assert.Empty(cut.FindAll("script"));
        Assert.Contains("<script>", cut.Find(".streaming-script-block").TextContent);
        Assert.DoesNotContain("spans", cut.Markup);
    }

    [Fact]
    public void PlanningUsesTheSharedPreviewBeforeThereIsACompleteBrief()
    {
        var cut = Render<SequencePlanningReview>(p => p.Add(c => c.Run, new AssistantRun {
            Target = ScriptStructure.Capture(ScriptFixtures.Document(), ScriptScope.Document, null),
            Status = AssistantRunStatus.Running, Output = "{\"beats\":[\"Eira finds a key" }));
        Assert.Contains("Eira finds a key", cut.Find("[aria-label='Live response preview']").TextContent);
        Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public void NonJsonTextHasNoMisleadingStructuredPreview()
    {
        var cut = Render<StreamingResponsePreview>(p => p.Add(c => c.Raw, "Thinking about the scene…"));
        Assert.Empty(cut.FindAll("section"));
    }

    [Fact]
    public void RepeatedPropertiesAndMalformedTailsCannotBreakThePreview()
    {
        var cut = Render<StreamingResponsePreview>(p => p.Add(c => c.Raw, "{\"beats\":[\"A key.\"],\"beats\":[\"A gate.\"], broken"));
        Assert.Equal(2, cut.FindAll(".streaming-preview-card").Count);
        Assert.Contains("A key.", cut.Markup); Assert.Contains("A gate.", cut.Markup);
    }
}
