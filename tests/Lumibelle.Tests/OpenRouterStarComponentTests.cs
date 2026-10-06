using Bunit;
using lumibelle.Components;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Lumibelle.Tests;

public sealed partial class AiModelComponentTests
{
    [Fact]
    public void UntestedStarsNeedAChoiceAndCancelMakesNoChangesOrRequests()
    {
        _providers.Models = [new(Cloud.Model, Cloud.Name)];
        var original = _settings.Value;
        var page = Render<AiSettingsPage>();
        page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-openrouter").Click();
        Assert.Contains("Not tested", page.Markup);
        page.Find(".model-star").Click();
        Assert.Contains("Catalog discovery does not test generation", page.Find(".model-star-warning").TextContent);
        Assert.Empty(_settings.Value.StarredTextModels); Assert.Equal(0, _settings.SaveCalls);
        page.Find(".model-star-warning").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();
        Assert.Empty(page.FindAll(".model-star-warning")); Assert.Equal(original, _settings.Value);
        page.Find(".model-star").Click(); Button(page, "Star anyway").Click();
        Assert.Single(_settings.Value.StarredTextModels); Assert.Equal(Cloud.Model, _settings.Value.StarredTextModels[0].Model);
        Assert.Equal(original.DefaultBackend, _settings.Value.DefaultBackend);
        Assert.Empty(Services.GetRequiredService<AiJobCoordinator>().View.Jobs);
        page.Find(".model-star").Click();
        Assert.Empty(page.FindAll(".model-star-warning")); Assert.Empty(_settings.Value.StarredTextModels);
        page.Find(".model-star").Click(); Assert.Single(page.FindAll(".model-star-warning"));
    }

    [Fact]
    public async Task TestShortcutOpensReviewWithoutGeneratingAndSuccessfulBenchmarkRemovesWarning()
    {
        var host = Render<MudDialogProvider>();
        _providers.Models = [new(Cloud.Model, Cloud.Name)];
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-openrouter").Click();
        page.Find(".model-star").Click(); Button(page, "Test model").Click();
        var dialog = host.FindComponent<ComfyModelDialog>();
        Assert.Equal("2048", dialog.Find("#advanced-test-tokens").GetAttribute("value"));
        Assert.Equal("8192", dialog.Find("#advanced-test-tokens").GetAttribute("max"));
        Assert.Contains("about 80 words", dialog.Find("#advanced-test-prompt").GetAttribute("value"));
        Assert.Empty(_settings.Value.StarredTextModels);
        Assert.Empty(Services.GetRequiredService<AiJobCoordinator>().View.Jobs);
        await dialog.InvokeAsync(() => Button(dialog, "Run benchmark · 2,048 total tokens").ClickAsync(new()));
        dialog.WaitForAssertion(() => Assert.Contains("Test result saved.", dialog.Markup));
        await dialog.InvokeAsync(() => Button(dialog, "Close").Click());
        page.WaitForAssertion(() => Assert.Contains("Benchmark completed", page.Markup));
        page.WaitForAssertion(() => Assert.False(page.Find(".model-star").HasAttribute("disabled")));
        // The finished benchmark can still render the page, so click and read on the renderer's dispatcher; see BunitClicks.ClickCurrent.
        await page.ClickCurrent(() => page.Find(".model-star"));
        await _settings.Until(() => Assert.Single(_settings.Value.StarredTextModels));
        await page.InvokeAsync(() => Assert.Empty(page.FindAll(".model-star-warning")));
        await page.ClickCurrent(() => page.Find(".model-star")); await page.ClickCurrent(() => page.Find(".model-star"));
        await page.InvokeAsync(() => Assert.Empty(page.FindAll(".model-star-warning"))); Assert.Single(_settings.Value.StarredTextModels);
    }
}
