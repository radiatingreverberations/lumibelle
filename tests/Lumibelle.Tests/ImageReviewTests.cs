using Bunit;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

public sealed partial class AssetComponentTests
{
    private (IRenderedComponent<lumibelle.Components.Pages.AssetsStudio> Page, ReferenceAsset Asset) PrepareEdit(int count = 2)
    {
        var source = new AssetImage { Id = Guid.NewGuid(), FileName = "source.png", ContentType = "image/png", Width = 400, Height = 600 };
        var asset = Asset("Mira") with { Images = [source] };
        _assets.Library = _assets.Library with { Assets = [asset] };
        var page = Page(); page.WaitForElement(".reference-actions button"); page.Find(".media-select").Click();
        // Selecting media flushes the previous composer asynchronously before entering Edit.
        page.WaitForAssertion(() => Assert.NotNull(RunButton(page)));
        page.Find("#image-prompt").Input("Change the coat.");
        page.Find("#candidate-count").Change(count.ToString());
        page.WaitForAssertion(() => Assert.False(RunButton(page).HasAttribute("disabled")));
        return (page, asset);
    }
    private static AngleSharp.Dom.IElement RunButton(IRenderedComponent<lumibelle.Components.Pages.AssetsStudio> page) =>
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Generate edited images");
    private AngleSharp.Dom.IElement ReviewButton(string name) => _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == name);

    private Task ClickReview(string name) => ClickInReview(() => ReviewButton(name));
    private async Task ClickReviewSelector(string selector)
    {
        _dialogs.WaitForElement(selector, BunitDefaults.WaitTimeout(10));
        await ClickInReview(() => _dialogs.Find(selector));
        if (selector == "[aria-label='Close image review']")
            _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".image-review-dialog")));
    }
    private Task ClickReviewAsync(string name) => ClickInReview(() => ReviewButton(name));
    private Task ClickInReview(Func<AngleSharp.Dom.IElement> find) => ClickCurrent(_dialogs, find);
    // Background work keeps re-rendering these pages. A stale handler means the DOM lags the
    // renderer and nothing was dispatched, so re-render and try again.
    private static async Task ClickCurrent<TComponent>(IRenderedComponent<TComponent> rendered, Func<AngleSharp.Dom.IElement> find)
        where TComponent : Microsoft.AspNetCore.Components.IComponent
    {
        for (var attempt = 0; ; attempt++)
        {
            try { await rendered.InvokeAsync(() => find().ClickAsync(new())); return; }
            catch (Bunit.Rendering.UnknownEventHandlerIdException) when (attempt < 10) { rendered.Render(); }
        }
    }
    private Task ApplyDialogChanges() => ClickCurrent(_dialogs, () => _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Apply changes"));

    private async Task QueueOneMoreImage()
    {
        await ClickReviewAsync("Generate more…");
        _dialogs.WaitForElement(".repeat-generation-dialog");
        await ClickReviewAsync("Queue 1 image");
    }

    [Fact]
    public async Task RepeatGenerationSeedChoiceLocksOneTakeAndRestoresRandomCount()
    {
        _dialogs = Render<MudBlazor.MudDialogProvider>();
        lumibelle.Components.Assets.RepeatGenerationDialog.Choices? submitted = null;
        Render<lumibelle.Components.Assets.RepeatGenerationDialog>(p => p.Add(c => c.Visible, true)
            .Add(c => c.Kind, "reel").Add(c => c.SourceSeed, 123L)
            .Add(c => c.Confirmed, value => submitted = value));
        _dialogs.WaitForElement("[aria-label='Take count']").Change("3");
        _dialogs.Find("input[type=checkbox]").Change(true);
        Assert.True(_dialogs.Find("[aria-label='Take count']").HasAttribute("disabled"));
        await ClickReviewAsync("Queue 1 reel");
        Assert.Equal(new(1, true), submitted);
        _dialogs.Find("input[type=checkbox]").Change(false);
        Assert.False(_dialogs.Find("[aria-label='Take count']").HasAttribute("disabled"));
        await ClickReviewAsync("Queue 3 reels");
        Assert.Equal(new(3, false), submitted);
    }

    [Fact]
    public async Task GenerateMoreImagesQueuesChosenCountWithoutChangingSavedRequest()
    {
        var (page, _) = PrepareEdit(1); await RunButton(page).ClickAsync(new());
        _dialogs.WaitForElement("[aria-label='View Take 1']");
        page.WaitForAssertion(() => Assert.DoesNotContain(_queue.View.Jobs, j => j.LocksTarget));
        var original = _editor.LastRequest!;
        await ClickReviewAsync("Generate more…");
        _dialogs.WaitForElement("[aria-label='Take count']").Change("3");
        Assert.Single(_queue.View.Jobs.SelectMany(j => j.Batch!.Candidates));
        await ClickReviewAsync("Queue 3 images");
        _dialogs.WaitForElement("[aria-label='View Take 4']");
        var candidates = _queue.View.Jobs.SelectMany(j => j.Batch!.Candidates).ToArray();
        Assert.Equal(4, candidates.Length);
        Assert.Equal(4, candidates.Select(c => c.Seed).Distinct().Count());
        Assert.All(_editor.Requests, request => Assert.Equal(original.Prompt, request.Prompt));
        Assert.Empty(_dialogs.FindAll(".repeat-generation-dialog"));
    }

    [Fact]
    public async Task OneMoreRequiresConfirmationAndCancelKeepsTheBatchUnchanged()
    {
        var (page, _) = PrepareEdit(1); await RunButton(page).ClickAsync(new());
        _dialogs.WaitForElement("[aria-label='View Take 1']");
        page.WaitForAssertion(() => Assert.DoesNotContain(_queue.View.Jobs, j => j.LocksTarget));
        var original = _editor.LastRequest!;
        await ClickReviewAsync("Generate more…");
        _dialogs.WaitForElement(".repeat-generation-dialog");
        Assert.Contains("Mira", _dialogs.Find(".repeat-generation-summary").TextContent);
        Assert.Contains(original.Prompt, _dialogs.Find(".repeat-generation-dialog pre").TextContent);
        Assert.Equal(1, _editor.Calls);
        Assert.Single(_queue.View.Jobs.SelectMany(j => j.Batch!.Candidates));
        await ClickReviewAsync("Cancel");
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".repeat-generation-dialog")));
        Assert.Equal(1, _editor.Calls);
        Assert.Single(_queue.View.Jobs.SelectMany(j => j.Batch!.Candidates));
        await QueueOneMoreImage();
        _dialogs.WaitForElement("[aria-label='View Take 2']");
        Assert.Equal(2, _editor.Calls);
        Assert.NotEqual(original.Seed, _editor.LastRequest!.Seed);
    }

    [Fact]
    public async Task OneMoreQueuesSingleTakesWithoutChangingComparisonOrCountingDiscardsAsMissing()
    {
        var (page, _) = PrepareEdit(); _editor.HoldAfterFirst = true;
        page.Find("#fixed-seed").Input("100");
        var running = RunButton(page).ClickAsync(new());
        await ClickReviewSelector("[aria-label='Compare with Source']");
        await QueueOneMoreImage(); await QueueOneMoreImage();
        Assert.Equal(1, _editor.Calls);
        _dialogs.WaitForAssertion(() => Assert.Equal(new[] { "Take 2", "Take 3", "Take 4" }, _dialogs.FindAll(".review-pending-row strong").Select(e => e.TextContent)));
        await ClickReviewSelector("[aria-label='Discard Take 1']");
        await ClickReview("Undo");
        await ClickReviewSelector("[aria-label='View Take 1']");
        await ClickReviewSelector("[aria-label='Compare with Source']");
        _editor.ReleaseReview(); await running;
        _dialogs.WaitForElement("[aria-label='View Take 4']");
        Assert.Empty(_dialogs.FindAll(".review-pending-row"));
        Assert.Equal(new[] { 2, 1, 1 }, _editor.Requests.Select(r => r.Count));
        Assert.Equal(100, _editor.Requests[0].Seed);
        Assert.Equal(3, _editor.Requests.Select(r => r.Seed).Distinct().Count());
        Assert.Equal("true", _dialogs.Find("[aria-label='View Take 1']").GetAttribute("aria-pressed"));
        Assert.Equal("true", _dialogs.Find("[aria-label='Compare with Source']").GetAttribute("aria-pressed"));
        // Additional requests are single candidates, so the initial four-candidate limit is unchanged.
        await QueueOneMoreImage();
        _dialogs.WaitForElement("[aria-label='View Take 5']");
        Assert.Equal(4, _editor.Requests.Select(r => r.Seed).Distinct().Count());
    }

    [Fact]
    public async Task OneMoreAfterCompletionUsesCapturedInputsAndSettingsAfterSidebarChanges()
    {
        var (page, _) = PrepareEdit(1); await RunButton(page).ClickAsync(new());
        _dialogs.WaitForElement("[aria-label='View Take 1']");
        page.WaitForAssertion(() => Assert.DoesNotContain(_queue.View.Jobs, j => j.LocksTarget));
        var original = _editor.LastRequest!;
        await ClickReviewSelector("[aria-label='Close image review']");
        page.Find("#image-prompt").Input("A different instruction");
        page.Find("#asset-image-workflow").Change("Flux2Klein9bKv");
        page.Find("#fixed-seed").Input("900");
        var settings = (FakeAiSettingsStore)Services.GetRequiredService<IAiSettingsStore>();
        settings.Value = settings.Value with { ComfyUrl = "http://different.invalid", ComfyImageModel = "another model" };
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "Review latest edit").ClickAsync(new()));
        await QueueOneMoreImage();
        _dialogs.WaitForElement("[aria-label='View Take 2']");
        Assert.NotEqual(original.Seed, _editor.LastRequest!.Seed);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(original with { Seed = _editor.LastRequest.Seed }, AtomicJsonFile.Options), System.Text.Json.JsonSerializer.Serialize(_editor.LastRequest, AtomicJsonFile.Options));
        Assert.NotEqual(settings.Value.ComfyUrl, _editor.LastRequest!.SettingsSnapshot!.ComfyUrl);
        Assert.Equal("A different instruction", page.Find("#image-prompt").GetAttribute("value"));
    }

    [Fact]
    public async Task OneMoreAfterCancellationLeavesAbandonedSlotsCancelled()
    {
        var (page, _) = PrepareEdit(); _editor.HoldAfterFirst = true;
        var running = RunButton(page).ClickAsync(new());
        _dialogs.WaitForElement("[aria-label='View Take 1']", BunitDefaults.WaitTimeout(5));
        await QueueOneMoreImage();
        await ClickReview("Cancel remaining"); await running;
        _dialogs.WaitForAssertion(() => Assert.Equal(new[] { "Cancelled", "Cancelled" }, _dialogs.FindAll(".review-pending-row small").Select(e => e.TextContent)));
        _editor.WaitUntilCancelled = true;
        var extra = QueueOneMoreImage();
        _dialogs.WaitForAssertion(() => Assert.Equal(2, _editor.Calls));
        _dialogs.WaitForAssertion(() => Assert.Equal(new[] { "Cancelled", "Cancelled", "Generating…" }, _dialogs.FindAll(".review-pending-row small").Select(e => e.TextContent)));
        await ClickReview("Cancel remaining"); await extra;
        _editor.WaitUntilCancelled = false; _editor.ReleaseReview();
        await QueueOneMoreImage();
        _dialogs.WaitForElement("[aria-label='View Take 5']");
        Assert.Equal(3, _dialogs.FindAll(".review-pending-row").Count);
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".review-status")));
    }

    [Fact]
    public async Task FailedExtraRetainsComparisonAndExplicitNextRequestUsesANewSlot()
    {
        var (page, _) = PrepareEdit(1); await RunButton(page).ClickAsync(new());
        await ClickReviewSelector("[aria-label='Compare with Source']");
        _editor.NoResults = true;
        await QueueOneMoreImage();
        _dialogs.WaitForAssertion(() => Assert.Contains("Not generated", _dialogs.Find(".review-pending-row").TextContent));
        Assert.Contains("provider ended", _dialogs.Find(".review-status").TextContent);
        Assert.Equal("true", _dialogs.Find("[aria-label='Compare with Source']").GetAttribute("aria-pressed"));
        _editor.NoResults = false;
        await QueueOneMoreImage();
        _dialogs.WaitForElement("[aria-label='View Take 3']");
        Assert.Equal("true", _dialogs.Find("[aria-label='Compare with Source']").GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task LatestBatchSupportsAllPairsAndSingleSelectionClearsComparison()
    {
        var (page, asset) = PrepareEdit(); RunButton(page).Click();
        _dialogs.WaitForElement("[aria-label='View Take 2']");
        Assert.Equal(new[] { "Source", "Take 1", "Take 2" }, _dialogs.FindAll(".review-image-select strong").Select(e => e.TextContent));
        Assert.Equal("true", _dialogs.Find("[aria-label='View Take 1']").GetAttribute("aria-pressed"));
        foreach (var primary in new[] { "Source", "Take 1", "Take 2" })
        foreach (var secondary in new[] { "Source", "Take 1", "Take 2" }.Where(s => s != primary))
        {
            await ClickReviewSelector($"[aria-label='View {primary}']");
            Assert.Empty(_dialogs.FindAll(".review-wipe"));
            await ClickReviewSelector($"[aria-label='Compare with {secondary}']");
            Assert.Equal(2, _dialogs.FindAll(".review-viewer img").Count);
            Assert.Equal("true", _dialogs.Find($"[aria-label='Compare with {secondary}']").GetAttribute("aria-pressed"));
            await ClickReview("Side by side"); Assert.Single(_dialogs.FindAll(".review-viewer.side-by-side"));
            await ClickReview("Slider"); Assert.Single(_dialogs.FindAll(".review-wipe"));
            await ClickReviewSelector($"[aria-label='Compare with {secondary}']");
            Assert.Single(_dialogs.FindAll(".review-viewer img"));
        }
        await ClickReviewSelector("[aria-label='Close image review']");
        page.Find("#image-prompt").Input("Another instruction");
        page.Find("#candidate-count").Change("1");
        RunButton(page).Click();
        _dialogs.WaitForAssertion(() => Assert.Equal(2, _dialogs.FindAll(".review-image-row").Count));
        Assert.Equal(4, _assets.Library.Assets[0].Images.Count);
        Assert.Equal("Another instruction", _assets.Library.Assets[0].Images[^1].Generation!.Prompt);
    }

    [Fact]
    public async Task DiscardIsImmediateAndUndoRestoresItsSlotWithoutChangingComparison()
    {
        var (page, asset) = PrepareEdit(); RunButton(page).Click();
        await ClickReviewSelector("[aria-label='Compare with Take 2']");
        await ClickReviewSelector("[aria-label='Discard Take 1']");
        _dialogs.WaitForAssertion(() => Assert.Equal(2, _assets.Library.Assets[0].Images.Count));
        Assert.Single(_assets.Library.Trash);
        Assert.Equal("true", _dialogs.Find("[aria-label='View Take 2']").GetAttribute("aria-pressed"));
        Assert.Empty(_dialogs.FindAll(".review-wipe"));
        Assert.Empty(_dialogs.FindAll(".review-discard-confirm"));
        await ClickReview("Undo");
        _dialogs.WaitForElement("[aria-label='View Take 1']");
        Assert.Equal(new[] { "Source", "Take 1", "Take 2" }, _dialogs.FindAll(".review-image-select strong").Select(e => e.TextContent));
        Assert.Equal("true", _dialogs.Find("[aria-label='View Take 2']").GetAttribute("aria-pressed"));
        await ClickReviewSelector("[aria-label='Discard Take 1']");
        await ClickReviewSelector("[aria-label='Close image review']");
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "Review latest edit").ClickAsync(new()));
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll("[aria-label='View Take 1']")));
        Assert.Single(_assets.Library.Trash);
        await ClickReviewSelector("[aria-label='Discard Take 2']");
        _dialogs.WaitForAssertion(() => Assert.Single(_assets.Library.Assets[0].Images));
        Assert.Equal(asset.Images[0].Id, _assets.Library.Assets[0].Images[0].Id);
        Assert.Equal("true", _dialogs.Find("[aria-label='View Source']").GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task EscapeDuringPendingDiscardClosesWithoutLosingTheSavedResultOrBlockingReopening()
    {
        var (page, _) = PrepareEdit(); RunButton(page).Click();
        _dialogs.WaitForElement("[aria-label='View Take 2']");
        _dialogs.WaitForElement("[aria-label='Discard Take 1']");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _assets.BeforeSave = async () => { entered.TrySetResult(); await release.Task; };
        var discarding = _dialogs.Find("[aria-label='Discard Take 1']").ClickAsync(new());
        await entered.Task;
        var dialog = page.FindComponent<lumibelle.Components.Assets.ImageReviewDialog>().FindComponent<MudBlazor.MudDialog>();
        await page.InvokeAsync(() => dialog.Instance.VisibleChanged.InvokeAsync(false));
        release.TrySetResult(); await discarding;
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".image-review-dialog")));
        Assert.Single(_assets.Library.Trash);
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "Review latest edit").ClickAsync(new()));
        _dialogs.WaitForElement("[aria-label='View Take 2']");
        Assert.Empty(_dialogs.FindAll("[aria-label='View Take 1']"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDiscardKeepsImagesForExplicitRetry(bool conflict)
    {
        var (page, _) = PrepareEdit(); RunButton(page).Click();
        _dialogs.WaitForElement("[aria-label='View Take 2']");
        _dialogs.WaitForElement("[aria-label='Discard Take 1']");
        _assets.DeleteError = conflict ? new WorkspaceConflictException() : new WorkspaceStoreException("Disk full");
        await ClickReviewSelector("[aria-label='Discard Take 1']");
        _dialogs.WaitForElement(".review-discard-error");
        Assert.Equal(3, _assets.Library.Assets[0].Images.Count); Assert.Empty(_assets.Library.Trash);
        _assets.DeleteError = null;
        await ClickReviewSelector("[aria-label='Discard Take 1']");
        _dialogs.WaitForAssertion(() => Assert.Equal(2, _assets.Library.Assets[0].Images.Count));
    }

    [Fact]
    public async Task RemovingEarlierAndSelectedTakesSelectsTheNextStableTake()
    {
        var (page, _) = PrepareEdit(4); RunButton(page).Click();
        // Discarding before later takes arrive leaves no next take to select.
        _dialogs.WaitForElement("[aria-label='View Take 4']", BunitDefaults.WaitTimeout(10));
        await ClickReviewSelector("[aria-label='View Take 2']");
        await ClickReviewSelector("[aria-label='Discard Take 1']");
        await ClickReviewSelector("[aria-label='Discard Take 2']");
        _dialogs.WaitForAssertion(() => Assert.Equal("true", _dialogs.Find("[aria-label='View Take 3']").GetAttribute("aria-pressed")));
    }

    [Fact]
    public async Task FailedPendingSavePreventsDiscardAndPreservesDraft()
    {
        var (page, _) = PrepareEdit(); RunButton(page).Click();
        _dialogs.WaitForElement("[aria-label='View Take 2']");
        _dialogs.WaitForElement("[aria-label='Discard Take 1']");
        _assets.SaveError = new WorkspaceStoreException("Save failed");
        await page.InvokeAsync(() => page.Find("[aria-label='Edit asset details']").Click());
        _dialogs.WaitForElement("#asset-description");
        await _dialogs.InvokeAsync(() => _dialogs.Find("#asset-description").Input("Unsaved description"));
        await ClickReviewSelector("[aria-label='Discard Take 1']");
        _dialogs.WaitForElement(".review-discard-error");
        Assert.Equal(0, _assets.DeleteCalls); Assert.Equal(3, _assets.Library.Assets[0].Images.Count);
        _assets.SaveError = null;
        await ClickReviewSelector("[aria-label='Discard Take 1']");
        _dialogs.WaitForAssertion(() => Assert.Equal(2, _assets.Library.Assets[0].Images.Count));
        Assert.Equal("Unsaved description", _assets.Library.Assets[0].Description);
    }

    [Fact]
    public async Task FirstTakeCanBeDiscardedWhileGeneratingAndCancellationRetainsTrash()
    {
        var (page, _) = PrepareEdit(); _editor.CancelAfterFirst = true;
        var running = RunButton(page).ClickAsync(new());
        await ClickReviewSelector("[aria-label='Discard Take 1']");
        Assert.Single(_assets.Library.Trash);
        Assert.Single(_dialogs.FindAll(".review-pending-row"));
        Assert.Empty(_dialogs.FindAll(".review-pending-row button"));
        await ClickReview("Cancel remaining"); await running;
        _dialogs.WaitForAssertion(() => Assert.Contains("Cancelled", _dialogs.Find(".review-pending-row").TextContent));
        await ClickReview("Undo");
        _dialogs.WaitForElement("[aria-label='View Take 1']");
        Assert.NotEmpty(_dialogs.Find(".review-status").TextContent);
    }

    [Fact]
    public async Task DiscardedCandidatesDoNotCauseFalseIncompleteBatchErrors()
    {
        var (page, _) = PrepareEdit(); _editor.HoldAfterFirst = true;
        var running = RunButton(page).ClickAsync(new());
        await ClickReviewSelector("[aria-label='Discard Take 1']");
        _editor.ReleaseReview(); await running;
        _dialogs.WaitForElement("[aria-label='View Take 2']");
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".review-status")));
        Assert.Empty(_dialogs.FindAll(".review-pending-row"));
        await ClickReview("Undo");
        _dialogs.WaitForAssertion(() => Assert.Equal(new[] { "Source", "Take 1", "Take 2" }, _dialogs.FindAll(".review-image-select strong").Select(e => e.TextContent)));
    }

    [Fact]
    public async Task TrashedSourceCanBeComparedAndRestoredWithoutResettingSelection()
    {
        var (page, asset) = PrepareEdit(); RunButton(page).Click();
        _dialogs.WaitForElement("[aria-label='View Take 2']");
        await ClickReviewSelector("[aria-label='Close image review']");
        var source = asset.Images[0];
        await _assets.DeleteImageAsync(_assets.Library.ProjectId, asset.Id, source.Id, _assets.Library.Revision, Xunit.TestContext.Current.CancellationToken);
        // Reopen through a fresh page as after a reload, using persisted lineage.
        page.Dispose(); page = Page();
        page.WaitForElement(".media-preview[aria-label^=\"Preview image\"]").Click();
        await ClickReviewSelector("[aria-label='Compare with Source']");
        Assert.Contains("/media/trash/", _dialogs.Find(".review-wipe-overlay img").GetAttribute("src"));
        var primary = _dialogs.Find(".review-image-select[aria-pressed='true']").GetAttribute("aria-label");
        _assets.RestoreError = new WorkspaceStoreException("Restore failed");
        await ClickReview("Restore source");
        Assert.Single(_assets.Library.Trash);
        _dialogs.WaitForElement(".review-discard-error");
        _assets.RestoreError = null;
        await ClickReview("Restore source");
        _dialogs.WaitForAssertion(() => Assert.Empty(_assets.Library.Trash));
        Assert.Equal(primary, _dialogs.Find(".review-image-select[aria-pressed='true']").GetAttribute("aria-label"));
        Assert.Single(_dialogs.FindAll(".review-wipe"));
        Assert.DoesNotContain("/media/trash/", _dialogs.Find(".review-wipe-overlay img").GetAttribute("src"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterCandidatesPreserveSelectionAndDoNotReopenDismissedReview(bool close)
    {
        var (page, _) = PrepareEdit(); _editor.HoldAfterFirst = true;
        var running = RunButton(page).ClickAsync(new());
        await ClickReviewSelector("[aria-label='View Source']");
        await ClickReviewSelector("[aria-label='Compare with Take 1']");
        Assert.Single(_dialogs.FindAll(".review-image-list .review-pending-row"));
        if (close)
        {
            await ClickReviewSelector("[aria-label='Close image review']");
            await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "Review latest edit").ClickAsync(new()));
            _dialogs.WaitForElement(".review-live-progress");
            await ClickReviewSelector("[aria-label='Close image review']");
        }
        _editor.ReleaseReview(); await running;
        if (close)
        {
            _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".image-review-dialog")));
            await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "Review latest edit").ClickAsync(new()));
        }
        else
        {
            Assert.Equal("true", _dialogs.Find("[aria-label='View Source']").GetAttribute("aria-pressed"));
            Assert.Equal("true", _dialogs.Find("[aria-label='Compare with Take 1']").GetAttribute("aria-pressed"));
        }
        _dialogs.WaitForElement("[aria-label='View Take 2']");
        Assert.Empty(_dialogs.FindAll(".review-pending-row"));
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".review-live-progress")));
    }

    [Fact]
    public async Task PartialFailureShowsSavedTakeWhileZeroResultDoesNotOpenReview()
    {
        var (page, _) = PrepareEdit(); _editor.FailAfterCandidates = 1; RunButton(page).Click();
        _dialogs.WaitForElement(".review-status");
        Assert.Contains("Mock provider failed", _dialogs.Find(".review-status").TextContent);
        await ClickReviewSelector("[aria-label='Close image review']");
        _editor.NoResults = true; RunButton(page).Click();
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".image-review-dialog")));
        Assert.Equal(2, _assets.Library.Assets[0].Images.Count);
    }

    [Fact]
    public async Task MissingImageShowsUnavailableWithoutSubstitutionAndApprovedTakesCannotBeDiscarded()
    {
        var (page, _) = PrepareEdit(); RunButton(page).Click();
        _dialogs.WaitForElement("[aria-label='View Take 2']");
        _dialogs.WaitForElement(".review-viewer img").Error();
        _dialogs.WaitForElement(".review-unavailable");
        Assert.Equal("true", _dialogs.Find("[aria-label='View Take 1']").GetAttribute("aria-pressed"));
        await ClickReviewSelector("[aria-label='Close image review']");
        await ImageActionAsync(page, "Approve reference");
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "Review latest edit").Click());
        _dialogs.WaitForElement("[aria-label='Discard Take 2']");
        Assert.True(_dialogs.Find("[aria-label='Discard Take 2']").HasAttribute("disabled"));
    }
}
