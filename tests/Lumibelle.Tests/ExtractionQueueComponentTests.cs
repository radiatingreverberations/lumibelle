using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

public sealed partial class AssetComponentTests
{
    // awaitHandler: false when the test holds back something the click's handlers wait for, such as saved review decisions.
    // The clicks are then only dispatched, and the caller waits for what they show.
    private async Task ExtractionClick(IRenderedComponent<AssetsStudio> page, string text, bool awaitHandler = true) {
        Task Press(AngleSharp.Dom.IElement button) { if (awaitHandler) return button.ClickAsync(new()); button.Click(); return Task.CompletedTask; }
        if (text == "Extract from script") { await page.InvokeAsync(() => page.Find(".assist-trigger[aria-label='Extract assets from script']").ClickAsync(new())); return; }
        if (text is "Queued · View extraction" or "Working · View extraction") text = "View request";
        if (text is "Review latest extraction" or "Review suggestions") text = "Review assets";
        if (text is "View request" or "Review assets") { await page.InvokeAsync(() => page.Find(".asset-library-extraction .request-action-button").ClickAsync(new())); return; }
        await page.InvokeAsync(() => Press(page.FindAll("button").Concat(_dialogs.FindAll("button")).First(b => b.TextContent.Trim() == text)));
        if (text == "Find assets") {
            await _queue.Until(() => Assert.NotEmpty(_queue.View.Jobs));
            page.WaitForElement(".asset-library-extraction .request-action-button", BunitDefaults.WaitTimeout(5));
            await page.InvokeAsync(() => page.FindAll(".extraction-dialog").Count == 0 ? Press(page.Find(".asset-library-extraction .request-action-button")) : Task.CompletedTask);
        }
    }
    private async Task<IRenderedComponent<AssetsStudio>> ReviewableExtraction()
    {
        _extractor.Result = new([new() { Name = "Mira", Description = "A quiet traveller.", Category = AssetCategory.Character }], "mock extraction");
        var page = Page(); await ExtractionClick(page, "Extract from script"); await ExtractionClick(page, "Find assets");
        page.WaitForElement(".extraction-proposal", BunitDefaults.WaitTimeout(5)); return page;
    }
    [Fact]
    public async Task FailedReviewPublicationKeepsDecisionsAndBlocksImportUntilExplicitRetry()
    {
        var page = await ReviewableExtraction(); var reviews = (TestReviewDraftStore)Services.GetRequiredService<IAiJobReviewStore>();
        reviews.SaveError = new WorkspaceStoreException("Review disk full");
        await page.InvokeAsync(() => page.Find("input[aria-label='Asset name']").Input("Kept author decision"));
        await ExtractionClick(page, "Add to library");
        page.WaitForAssertion(() => Assert.Contains("Review disk full", page.Markup));
        Assert.Empty(_assets.Library.Assets); Assert.Equal("Kept author decision", page.Find("input[aria-label='Asset name']").GetAttribute("value"));
        await page.InvokeAsync(() => page.Find(".extraction-dialog .preview-close").ClickAsync(new()));
        Assert.NotEmpty(page.FindAll(".extraction-dialog"));
        reviews.SaveError = null; await ExtractionClick(page, "Retry saving review"); await ExtractionClick(page, "Add to library");
        page.WaitForAssertion(() => Assert.Equal("Kept author decision", Assert.Single(_assets.Library.Assets).Name));
        Assert.Equal(1, _extractor.Calls); Assert.Equal(_extractor.JobId, Assert.Single(_assets.Library.ExtractionReviews).Id);
    }
    [Fact]
    public async Task ReopeningAnExtractionRetainsEditedDecisionsAndDoesNotRepeatTheProvider()
    {
        var page = await ReviewableExtraction();
        await page.InvokeAsync(() => page.Find("input[aria-label='Asset name']").Input("Named in review"));
        await page.InvokeAsync(() => page.Find(".extraction-dialog .preview-close").ClickAsync(new()));
        await page.Instance.DisposeAsync(); page.Dispose();
        page = Page(false); await ExtractionClick(page, "Review suggestions");
        page.WaitForAssertion(() => Assert.Equal("Named in review", page.Find("input[aria-label='Asset name']").GetAttribute("value")));
        Assert.Equal(1, _extractor.Calls); Assert.Empty(_assets.Library.Assets);
        await ExtractionClick(page, "Add to library"); Assert.Equal("Named in review", Assert.Single(_assets.Library.Assets).Name);
    }
    [Fact]
    public async Task ProposalsWaitForSavedDecisionsSoEarlyEditsAreSaved()
    {
        var reviews = (TestReviewDraftStore)Services.GetRequiredService<IAiJobReviewStore>();
        var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); reviews.LoadGate = loading.Task;
        _extractor.Result = new([new() { Name = "Mira", Description = "A quiet traveller.", Category = AssetCategory.Character }], "mock extraction");
        // A request that finishes quickly is read while its clicks are still handled, so they wait for the decisions held back here.
        var page = Page(); await ExtractionClick(page, "Extract from script"); await ExtractionClick(page, "Find assets", awaitHandler: false);
        await reviews.LoadStarted.WaitAsync(BunitDefaults.WaitTimeout(5), Xunit.TestContext.Current.CancellationToken);
        // Any render while they load, such as a progress update, must not show proposals yet: an edit made before
        // the saved decisions arrive would be mistaken for them and never saved.
        await page.InvokeAsync(() => { page.Render(); Assert.Empty(page.FindAll(".extraction-proposal")); });
        loading.SetResult();
        page.WaitForElement(".extraction-proposal", BunitDefaults.WaitTimeout(5));
        await page.InvokeAsync(() => page.Find("input[aria-label='Asset name']").Input("Named early"));
        await page.InvokeAsync(() => page.Find(".extraction-dialog .preview-close").ClickAsync(new()));
        await page.Instance.DisposeAsync(); page.Dispose();
        page = Page(false); await ExtractionClick(page, "Review suggestions");
        page.WaitForAssertion(() => Assert.Equal("Named early", page.Find("input[aria-label='Asset name']").GetAttribute("value")));
    }
    [Fact]
    public async Task SkippingSuggestionsChangesTheApplyActionWithoutCreatingAssets()
    {
        var page = await ReviewableExtraction();
        Assert.Contains("Awaiting your review", page.Markup);
        Assert.Contains("1 asset to add", page.Markup);
        Assert.Equal("Add to library", page.Find(".apply-extraction").TextContent.Trim());
        // The review can still settle after its proposal appears. A stale handler means the page's DOM
        // lags the renderer, so re-render to refresh it and try again.
        for (var attempt = 0; ; attempt++)
        {
            try { await page.InvokeAsync(() => page.Find("select[aria-label='Decision for Mira']").Change("Skip")); break; }
            catch (Bunit.Rendering.UnknownEventHandlerIdException) when (attempt < 10) { page.Render(); }
        }
        page.WaitForAssertion(() => Assert.Equal("Finish review", page.Find(".apply-extraction").TextContent.Trim()));
        Assert.Contains("1 skipped", page.Markup);
        Assert.Empty(_assets.Library.Assets);
        await ExtractionClick(page, "Finish review");
        page.WaitForAssertion(() => Assert.Single(_assets.Library.ExtractionReviews));
        Assert.Empty(_assets.Library.Assets);
    }
    [Fact]
    public async Task ClosingQueuedExtractionLeavesItQueuedAndCancelIsExplicit()
    {
        await _queue.SetPausedAsync(AiBackend.OpenRouter, true, Xunit.TestContext.Current.CancellationToken);
        var page = Page(); await ExtractionClick(page, "Extract from script"); await ExtractionClick(page, "Find assets");
        page.WaitForAssertion(() => Assert.Contains("Queued in Lumibelle", page.Markup));
        await page.InvokeAsync(() => page.Find(".extraction-dialog .preview-close").ClickAsync(new()));
        Assert.Equal(0, _extractor.Calls); Assert.False(_queue.View.Jobs.Single().CancelRequested);
        await ExtractionClick(page, "Queued · View extraction"); await ExtractionClick(page, "Cancel request");
        page.WaitForAssertion(() => Assert.Contains("cancelled", page.Markup));
        Assert.Equal(AiJobState.Cancelled, _queue.View.Jobs.Single().State); Assert.Equal(0, _extractor.Calls);
    }
}
