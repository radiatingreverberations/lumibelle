using Bunit;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using System.Text.Json;

namespace Lumibelle.Tests;

public sealed partial class AssetComponentTests
{
    [Fact]
    public async Task QueuedImageLeavesItsComposerClearAndItsRequestListedAfterNavigation()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _queue.SetPausedAsync(AiBackend.ComfyUI, true, ct);
        var (page, asset) = await PrepareEdit(2);
        await Generate(page);
        page.WaitForElement(".ai-queue-wait");
        await page.InvokeAsync(() =>
        {
            Assert.Empty(page.FindAll(".enhancement-job-actions"));
            Assert.False(page.Find("#image-prompt").HasAttribute("disabled")); Assert.Equal("", page.Find("#image-prompt").GetAttribute("value"));
        });
        Assert.Equal(0, _editor.Calls);
        var original = Assert.Single(_queue.View.Jobs);
        var request = (await _jobs.ReadSnapshotAsync(original.Id, ct)).Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal("Change the coat.", request.Prompt); Assert.Null(request.Edit!.Seed);
        Assert.Equal(2, original.Batch!.Candidates.Select(c => c.Seed).Distinct().Count());
        await page.Instance.DisposeAsync(); page.Dispose();
        page = Page(); page.WaitForElement(".ai-queue-wait");
        await page.InvokeAsync(() =>
        {
            Assert.Single(page.FindAll(".asset-image-request"));
            Assert.NotEqual("Change the coat.", page.Find("#image-prompt").GetAttribute("value"));
            Assert.False(page.Find("#image-prompt").HasAttribute("disabled"));
            Assert.Empty(_dialogs.FindAll(".image-review-dialog"));
        });
        await _queue.SetPausedAsync(AiBackend.ComfyUI, false, ct);
        await _assets.Until(() => Assert.Equal(3, _assets.Library.Assets.Single(a => a.Id == asset.Id).Images.Count));
        await _queue.Until(() => Assert.DoesNotContain(_queue.View.Jobs, j => j.LocksTarget));
        // Once the page shows the finished edit, it has not opened its review on its own.
        page.WaitForAssertion(() => Assert.Contains(page.FindAll("button"), b => b.TextContent.Trim() == "Review latest edit"));
        await page.InvokeAsync(() => Assert.Empty(_dialogs.FindAll(".image-review-dialog")));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "Review latest edit").ClickAsync(new()));
        _dialogs.WaitForElement("[aria-label='View Take 2']");
        Assert.Equal(1, _editor.Calls);
    }

    [Fact]
    public async Task OtherAssetRemainsEditableWhileImageRequestWaits()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _queue.SetPausedAsync(AiBackend.ComfyUI, true, ct);
        var first = Asset("Juniper", "Character portrait"); var second = Asset("Bedroom", "Bedroom reference");
        _assets.Library = _assets.Library with { Assets = [first, second] };
        var page = Page(); page.WaitForElement("#image-prompt");
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "Generate images").ClickAsync(new()));
        page.WaitForElement(".ai-queue-wait");
        await page.InvokeAsync(() => page.FindAll(".asset-choice").Single(b => b.TextContent.Contains("Bedroom")).ClickAsync(new()));
        page.WaitForAssertion(() => Assert.False(page.Find("#image-prompt").HasAttribute("disabled")));
        await page.InvokeAsync(() => page.Find("#image-prompt").Input("Authored bedroom prompt"));
        await page.InvokeAsync(() => page.FindAll(".asset-choice").Single(b => b.TextContent.Contains("Juniper")).ClickAsync(new()));
        page.WaitForElement(".ai-queue-wait"); await page.InvokeAsync(() => Assert.Equal("", page.Find("#image-prompt").GetAttribute("value")));
        await page.InvokeAsync(() => page.FindAll(".asset-choice").Single(b => b.TextContent.Contains("Bedroom")).ClickAsync(new()));
        await page.InvokeAsync(() => Assert.Equal("Authored bedroom prompt", page.Find("#image-prompt").GetAttribute("value")));
    }

    [Fact]
    public async Task TheSameAssetQueuesSeveralRequestsUpToItsLimit()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _queue.SetPausedAsync(AiBackend.ComfyUI, true, ct);
        _assets.Library = _assets.Library with { Assets = [Asset("Juniper", "Character portrait")] };
        var page = Page(); page.WaitForElement("#image-prompt");
        // The queue keeps rendering the page from other threads. bUnit parses the page's DOM lazily on first read, so a read from the
        // test thread that overlaps a render can cache the old DOM, whose prompt names an input handler the render already replaced.
        // Read and type on the renderer's dispatcher instead, where no render can overlap.
        for (var i = 1; i <= lumibelle.Models.AiJobLocks.MaxActiveImageBatchesPerAsset; i++)
        {
            await page.InvokeAsync(() => page.Find("#image-prompt").Input($"Variation {i}"));
            await page.ClickCurrent(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "Generate images"));
            page.WaitForAssertion(() => Assert.Equal(i, page.FindAll(".asset-image-request").Count));
            await page.InvokeAsync(() => Assert.Equal("", page.Find("#image-prompt").GetAttribute("value")));
        }
        var prompts = new List<string>();
        foreach (var job in _queue.View.Jobs.OrderBy(j => j.CreatedUtc))
            prompts.Add((await _jobs.ReadSnapshotAsync(job.Id, ct)).Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!.Prompt);
        Assert.Equal(["Variation 1", "Variation 2", "Variation 3", "Variation 4"], prompts);
        // Several queued requests stay one line each, in queue order, under one paused-queue hint. A row names its prompt once
        // the page has read the saved request.
        page.WaitForAssertion(() => Assert.Equal(Enumerable.Range(1, 4).Select(i => $"Queued · position {i} · “Variation {i}”"),
            page.FindAll(".asset-image-request.is-compact .asset-image-request-prompt").Select(p => p.GetAttribute("title"))));
        await page.InvokeAsync(() =>
        {
            Assert.Empty(page.FindAll(".asset-image-request .ai-queue-wait"));
            Assert.Equal("4 queued · This provider queue is paused.", page.Find(".asset-image-requests-summary").TextContent);
            page.Find("#image-prompt").Input("One too many");
            Assert.True(page.FindAll("button").Single(b => b.TextContent.Trim() == "Generate images").HasAttribute("disabled"));
            Assert.Contains(lumibelle.Models.AiJobLocks.ImageLimitMessage, page.Markup);
        });
        await page.InvokeAsync(() => page.FindAll(".asset-image-request")[0].QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Cancel").ClickAsync(new()));
        page.WaitForAssertion(() => Assert.False(page.FindAll("button").Single(b => b.TextContent.Trim() == "Generate images").HasAttribute("disabled")));
    }
}
