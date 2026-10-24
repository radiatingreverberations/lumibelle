using System.Text.Json;
using Bunit;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

public sealed partial class AssetComponentTests
{
    [Fact]
    public async Task InvalidExtractionHasSpecificErrorsAndLocalCorrectionSurvivesReopening()
    {
        var script = await Services.GetRequiredService<IScriptStore>().CaptureSourceAsync(_projectId, cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var scene = ScriptStructure.Sections(script!.Blocks).First(s => s.Kind == ScriptBlockKind.Scene);
        var valid = JsonSerializer.Serialize(new[] { new { category = "prop", name = "Brass key", description = "A brass key.",
            evidence = new[] { new { label = scene.Title, sceneId = scene.Id, excerpt = "A brass key" } } } });
        var raw = valid.Replace(scene.Id.ToString(), Guid.NewGuid().ToString());
        _extractor.Result = new([], raw, "Old generic failure");
        var page = Page(); await ExtractionClick(page, "Extract from script"); await ExtractionClick(page, "Find assets");
        page.WaitForAssertion(() => Assert.Contains("Asset 1.evidence[1].sceneId", page.Markup));
        Assert.Empty(page.FindAll(".apply-extraction")); Assert.Empty(_assets.Library.Assets);
        await ExtractionClick(page, "Edit JSON");
        await page.InvokeAsync(() => page.Find("#corrected-extraction-json").Input("[{"));
        await ExtractionClick(page, "Review corrected JSON");
        page.WaitForAssertion(() => Assert.Contains("line 1, column", page.Find("#corrected-extraction-json-error").TextContent));
        await page.InvokeAsync(() => page.Find("#corrected-extraction-json").Input(valid));
        await ExtractionClick(page, "Review corrected JSON");
        page.WaitForElement(".extraction-proposal", BunitDefaults.WaitTimeout(5));
        Assert.Contains("Showing your saved JSON correction", page.Markup);
        Assert.Empty(_assets.Library.Assets); Assert.Equal(1, _extractor.Calls);
        await page.InvokeAsync(() => page.Find("input[aria-label='Asset name']").Input("Author's key"));
        await page.ClickCurrent(() => page.Find(".extraction-footer").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Close"));
        await page.Instance.DisposeAsync(); page.Dispose();
        page = Page(false); await ExtractionClick(page, "Review suggestions");
        page.WaitForAssertion(() => Assert.Equal("Author's key", page.Find("input[aria-label='Asset name']").GetAttribute("value")));
        await page.ClickCurrent(() => page.Find(".apply-extraction"));
        page.WaitForAssertion(() => Assert.Equal("Author's key", Assert.Single(_assets.Library.Assets).Name));
        Assert.Equal(raw, (await _jobs.ReadArtifactAsync<AiTextJobResult>(_extractor.JobId, AiJobArtifact.Result, Xunit.TestContext.Current.CancellationToken))!.Raw);
        Assert.Equal(1, _extractor.Calls);
    }

    [Fact]
    public async Task ExtractionStreamShowsReadableAssetCardsBeforeValidation()
    {
        _extractor.WaitForRelease = true;
        _extractor.Result = new([new() { Category = AssetCategory.Prop, Name = "Brass key", Description = "A key with a curved bow." }], "mock extraction");
        _extractor.PartialResponse = "[{\"category\":\"prop\",\"name\":\"Brass key\",\"description\":\"A key with a curved";
        var page = Page(); await ExtractionClick(page, "Extract from script");
        var dialog = page.Find(".extraction-dialog");
        Assert.Contains("Choose scenes", page.Find(".extraction-workflow [aria-current='step']").TextContent);
        Assert.Empty(_dialogs.FindAll(".ai-assist-dialog"));
        await ExtractionClick(page, "Find assets");
        page.WaitForAssertion(() => {
            Assert.Single(page.FindAll(".extraction-dialog"));
            Assert.Contains("Brass key", page.Find(".streaming-response-preview").TextContent);
            Assert.Contains("Still writing", page.Find(".streaming-response-preview").TextContent);
        });
        Assert.Empty(_assets.Library.Assets); Assert.Empty(page.FindAll(".apply-extraction"));
        Assert.False(page.Find(".extraction-request-details").HasAttribute("open"));
        Assert.Equal("Find assets", page.Find(".extraction-workflow [aria-current='step']").TextContent.Trim().TrimStart('2').Trim());
        Assert.DoesNotContain("Working…", page.Find(".extraction-footer").TextContent);
        Assert.Contains("Cancel request", page.Find(".extraction-footer").TextContent);
        Assert.Empty(page.FindAll(".extraction-body select"));
        _extractor.Release();
        page.WaitForElement(".extraction-proposal", BunitDefaults.WaitTimeout(5));
        Assert.Single(page.FindAll(".extraction-dialog"));
        Assert.Contains("Review & add", page.Find(".extraction-workflow [aria-current='step']").TextContent);
        Assert.Empty(_assets.Library.Assets);
    }
}
