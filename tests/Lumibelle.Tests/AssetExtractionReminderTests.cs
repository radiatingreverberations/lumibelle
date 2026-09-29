using Bunit;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

public sealed partial class AssetComponentTests
{
    [Fact]
    public async Task ExtractionReminderCanBeIgnoredAndRestoredWithoutReviewingScenes()
    {
        var positions = Services.GetRequiredService<WorkspacePositions>();
        await positions.LoadAsync(_projectId, "assets");
        var page = Page();
        page.WaitForElement(".asset-library-extraction .request-notice-badge");
        Assert.Empty(page.FindAll(".asset-library .coverage-summary"));
        var button = page.Find(".asset-library-extraction .request-action-button");
        Assert.Equal("Extract assets from script", button.GetAttribute("aria-label"));
        Assert.Contains("extraction review", page.Find($"#{button.GetAttribute("aria-describedby")}").TextContent);

        await ExtractionClick(page, "Extract from script");
        _dialogs.WaitForElement(".extraction-reminder-choice input");
        Assert.Contains("0 /", _dialogs.Find(".coverage-summary").TextContent);
        await _dialogs.Find(".extraction-reminder-choice input").ChangeAsync(new() { Value = true });
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".request-notice-badge")));
        Assert.True(positions.Get(_projectId, "assets", "ignoreExtractionReminders", false));
        Assert.False(positions.Get(Guid.NewGuid(), "assets", "ignoreExtractionReminders", false));
        Assert.Empty(_assets.Library.ExtractionReviews);
        Assert.Equal(0, _extractor.Calls);

        await _dialogs.FindAll(".ai-assist-dialog .mud-dialog-actions button").Single(b => b.TextContent.Trim() == "Close").ClickAsync(new());
        await page.Instance.DisposeAsync(); page.Dispose();
        page = Page(false);
        page.WaitForElement(".asset-library-extraction .request-action-button");
        Assert.Empty(page.FindAll(".request-notice-badge"));
        await ExtractionClick(page, "Extract from script");
        _dialogs.WaitForAssertion(() => Assert.True(_dialogs.Find(".extraction-reminder-choice input").HasAttribute("checked")));
        await _dialogs.Find(".extraction-reminder-choice input").ChangeAsync(new() { Value = false });
        page.WaitForElement(".asset-library-extraction .request-notice-badge");
        Assert.Empty(_assets.Library.ExtractionReviews);
        Assert.Equal(0, _extractor.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractionReminderOnlyMarksScenesChangedSinceTheirReview(bool changed)
    {
        var scripts = (FakeScriptStore)Services.GetRequiredService<IScriptStore>();
        var source = (await scripts.CaptureSourceAsync(_projectId, cancellationToken: Xunit.TestContext.Current.CancellationToken))!;
        _assets.Library = _assets.Library with { ExtractionReviews = [new(Guid.NewGuid(), DateTimeOffset.UtcNow, source.Id,
            ExtractionCoverage.Scenes(source), new(0, 0, 0, 0, 0, 0), "reviewed")] };
        if (changed) scripts.Document.Blocks.Add(ScriptBlock.Create(ScriptBlockKind.Action, "A new prop arrives."));
        var page = Page();
        page.WaitForElement(".asset-library-extraction .request-action-button");
        Assert.Equal(changed ? 1 : 0, page.FindAll(".asset-library-extraction .request-notice-badge").Count);
    }

    [Fact]
    public void ExtractionReminderDoesNotFlagAnEmptyScript()
    {
        var scripts = (FakeScriptStore)Services.GetRequiredService<IScriptStore>();
        scripts.Document = scripts.Document with { Blocks = [] };
        var page = Page();
        page.WaitForElement(".asset-library-extraction .request-action-button");
        Assert.Empty(page.FindAll(".request-notice-badge"));
    }
}
