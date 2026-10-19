using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

public sealed partial class AiModelComponentTests
{
    [Fact]
    public void ProviderVisitsPreserveSearchPaginationExpandedDraftsAndUrlContext()
    {
        var project = Guid.NewGuid();
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/settings/ai?tab=text&provider=openrouter&returnUrl=%2Fprojects%2F{project}%2Fassets%3Fview%3Dgrid%23focus");
        _providers.Models = Enumerable.Range(0, 61).Select(i => new AiModel($"model/{i:D2}", $"Model {i:D2}")).ToArray();
        var page = Render<AiSettingsPage>();
        Assert.Equal(new[] { AiBackend.OpenRouter }, _providers.CheckedBackends);
        Assert.Equal("true", page.Find("#ai-text-provider-openrouter").GetAttribute("aria-selected"));
        Assert.Equal($"/projects/{project}/assets?view=grid#focus", page.Find(".writing-back").GetAttribute("href"));
        Button(page, "Next").Click(); page.Find(".text-model-expand").Click();
        page.Find(".model-alias-form input").Input("Draft on page two");
        page.Find("#ai-text-provider-comfyui").Click();
        Assert.Equal(2, _providers.CheckCalls); page.Find("#model-search").Input("Model 60");
        page.Find(".text-model-expand").Click(); page.Find(".model-alias-form input").Input("Local draft");
        page.Find("#ai-tab-images").Click(); page.Find("#ai-tab-text").Click();
        Assert.Equal("Local draft", page.Find(".model-alias-form input").GetAttribute("value"));
        page.Find("#ai-text-provider-openrouter").Click();
        Assert.Contains("Page 2 of 3", page.Markup); Assert.Equal("Draft on page two", page.Find(".model-alias-form input").GetAttribute("value"));
        Assert.Equal(2, _providers.CheckCalls); Assert.Equal(0, _providers.VerificationCalls); Assert.Equal(0, _settings.SaveCalls);
        Assert.DoesNotContain("provider=", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Contains("returnUrl=", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Empty(page.FindAll("#ai-text-comfyui"));
        page.Find("#ai-text-provider-openrouter").KeyDown("Home");
        Assert.Equal("true", page.Find("#ai-text-provider-comfyui").GetAttribute("aria-selected"));
        page.Find("#ai-text-provider-comfyui").KeyDown("ArrowDown");
        Assert.Equal("true", page.Find("#ai-text-provider-vllm").GetAttribute("aria-selected"));
        page.Find("#ai-text-provider-vllm").KeyDown("ArrowDown");
        Assert.Equal("true", page.Find("#ai-text-provider-openrouter").GetAttribute("aria-selected"));
        page.Find("#ai-text-provider-openrouter").KeyDown("End");
        Assert.Equal("true", page.Find("#ai-text-provider-defaults").GetAttribute("aria-selected"));
        Assert.NotNull(page.Find("#ai-text-defaults #global-text-default")); Assert.Empty(page.FindAll("#model-search"));
        page.Find("#ai-text-provider-defaults").KeyDown("ArrowUp");
        Assert.Equal("true", page.Find("#ai-text-provider-claudecode").GetAttribute("aria-selected"));
        Assert.DoesNotContain("provider=", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public async Task SlowHiddenCatalogFinishesWithoutReloadingAndFailureKeepsLastData()
    {
        var result = new TaskCompletionSource<AiConnectionCheck>(TaskCreationOptions.RunContinuationsAsynchronously);
        _providers.CheckResult = backend => backend == AiBackend.OpenRouter ? result.Task : Task.FromResult(new AiConnectionCheck(true, "Ready", []));
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-openrouter").Click();
        page.WaitForAssertion(() => Assert.Contains("Loading OpenRouter", page.Markup));
        page.Find("#ai-text-provider-comfyui").Click();
        await page.InvokeAsync(() => result.SetResult(new(true, "Ready", [new(Cloud.Model, Cloud.Name)])));
        await page.InvokeAsync(() => page.Find("#ai-text-provider-openrouter").Click());
        page.WaitForAssertion(() => Assert.Contains(Cloud.Name, page.Markup));
        Assert.Equal(1, _providers.CheckedBackends.Count(b => b == AiBackend.OpenRouter));
        _providers.CheckResult = _ => Task.FromResult(new AiConnectionCheck(false, "Provider offline", []));
        page.Find("button[aria-label='Refresh OpenRouter models']").Click();
        Assert.Contains("may be out of date", page.Markup); Assert.Single(page.FindAll(".text-model-row"));
        Assert.Equal(1, page.FindAll(".field-error").Count(e => e.TextContent.Contains("Provider offline")));
        Assert.True(page.Find(".model-star").HasAttribute("disabled")); Assert.Single(page.FindAll("time"));
        Assert.Equal(0, _providers.VerificationCalls);
    }

    [Fact]
    public async Task ConnectionSaveInvalidatesAnInFlightCatalogAndLoadsTheNewServer()
    {
        var pending = new TaskCompletionSource<AiConnectionCheck>(TaskCreationOptions.RunContinuationsAsynchronously);
        _providers.CheckResult = _ => pending.Task;
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click();
        page.Find("#ai-tab-connections").Click(); page.Find("#comfy-url").Change("http://new-server:8188");
        page.Find("#connection-comfyui-form").Submit(); page.Find("#ai-tab-text").Click();
        _providers.CheckResult = _ => Task.FromResult(new AiConnectionCheck(true, "New server", [new("new-model", "New model")], "v2"));
        await page.InvokeAsync(() => pending.SetResult(new(true, "Old server", [new("old-model", "Old model")], "v1")));
        page.WaitForAssertion(() => Assert.Contains("New model", page.Markup));
        Assert.DoesNotContain("Old model", page.Markup); Assert.Equal(2, _providers.CheckCalls);
        Assert.Equal("http://new-server:8188", _settings.Value.ComfyUrl);
    }

    [Fact]
    public void AliasesSaveClearSearchAndSurviveConflictAndUnstarring()
    {
        _providers.Models = [new(Cloud.Model, Cloud.Name)];
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-openrouter").Click();
        page.Find(".model-star").Click(); Button(page, "Star anyway").Click(); page.Find(".text-model-expand").Click();
        page.Find(".model-alias-form input").Input("Cloud writer");
        _settings.SaveError = new WorkspaceConflictException(); page.Find(".model-alias-form").Submit();
        Assert.Empty(_settings.Value.TextModelAliases); Assert.Equal("Cloud writer", page.Find(".model-alias-form input").GetAttribute("value"));
        _settings.SaveError = null; Button(page, "Reload saved settings").Click(); page.Find(".model-alias-form").Submit();
        Assert.Equal("Cloud writer", page.Find(".text-model-expand").TextContent);
        Assert.Equal(Cloud.Model, _settings.Value.StarredTextModels.Single().Model);
        Assert.Contains(Cloud.Name, page.Find(".text-model-details").TextContent); Assert.Contains(Cloud.Model, page.Find(".text-model-details").TextContent);
        page.Find("#model-search").Input("Cloud writer"); Assert.Single(page.FindAll(".text-model-row"));
        page.Find("#model-search").Input(Cloud.Model); Assert.Single(page.FindAll(".text-model-row"));
        page.Find(".model-star").Click(); Assert.Single(_settings.Value.TextModelAliases);
        page.Find(".model-star").Click(); Button(page, "Star anyway").Click(); Assert.Single(_settings.Value.TextModelAliases);
        page.Find(".model-alias-form input").Input("Unsaved alias"); Button(page, "Cancel alias").Click();
        Assert.Equal("Cloud writer", page.Find(".model-alias-form input").GetAttribute("value"));
        page.Find(".model-alias-form input").Input(""); page.Find(".model-alias-form").Submit();
        Assert.Empty(_settings.Value.TextModelAliases); Assert.Equal(Cloud.Name, page.Find(".text-model-expand").TextContent);
    }

    [Fact]
    public void SingleDefaultPickerRetainsUnstarredCurrentAndRejectsUnavailableOrFailedSaves()
    {
        _settings.Value = _settings.Value with { StarredTextModels = [Cloud] };
        _providers.Models = [new(Cloud.Model, Cloud.Name)];
        var initial = _settings.Value; var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-defaults").Click();
        Assert.Equal(2, page.FindAll("#global-text-default option").Count);
        Assert.DoesNotContain("Set default", page.Markup);
        _providers.Success = false; page.Find("#global-text-default").Change(TextModelPolicy.Key(Cloud));
        Assert.Equal(initial.DefaultBackend, _settings.Value.DefaultBackend);
        Assert.Equal(TextModelPolicy.Key(TextModelPolicy.Default(initial)), page.Find("#global-text-default").GetAttribute("value"));
        _providers.Success = true; _settings.SaveError = new WorkspaceConflictException();
        page.Find("#global-text-default").Change(TextModelPolicy.Key(Cloud)); Assert.Equal(initial.DefaultBackend, _settings.Value.DefaultBackend);
        _settings.SaveError = null; Button(page, "Reload saved settings").Click();
        page.Find("#global-text-default").Change(TextModelPolicy.Key(Cloud));
        Assert.Equal(AiBackend.OpenRouter, _settings.Value.DefaultBackend); Assert.Equal(Cloud.Model, _settings.Value.OpenRouterModel);
        Assert.Equal(0, _providers.VerificationCalls);
    }

    [Fact]
    public void PricesAndCapabilitiesDisplayAndNumericSortsApplyBeforePagination()
    {
        var models = Enumerable.Range(1, 30).Select(i => new AiModel($"model/{i:D2}", $"Model {i:D2}", SupportsImages: i == 30,
            Catalog: new("Description", i * 1000, 1024, i == 30, new((31 - i) / 1000000m, i / 1000000m, new Dictionary<string, decimal?>())))).ToList();
        models.Add(new("free/model", "Zero model", Catalog: new(Pricing: new(0, 0, new Dictionary<string, decimal?>()))));
        models.Add(new("unknown/model", "A unknown"));
        models.Add(new("variable/model", "B variable", Catalog: new(Pricing: new(0, 0, new Dictionary<string, decimal?>(), Variable: true))));
        _providers.Models = models;
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-openrouter").Click();
        Assert.Equal("A unknown", page.Find(".text-model-expand").TextContent);
        page.Find("#model-sort").Change("input"); Assert.Equal("Zero model", page.Find(".text-model-expand").TextContent);
        Assert.Contains("Free text", page.Find(".text-model-row").TextContent);
        Assert.Equal("Model 30", page.FindAll(".text-model-expand")[1].TextContent);
        Assert.Contains("Image input", page.FindAll(".text-model-row")[1].TextContent); Assert.Contains("Reasoning", page.FindAll(".text-model-row")[1].TextContent);
        Button(page, "Next").Click(); Assert.Equal("B variable", page.FindAll(".text-model-expand").Last().TextContent);
        page.Find("#model-sort").Change("output"); Assert.Equal("Model 01", page.FindAll(".text-model-expand")[1].TextContent);
        page.Find("#model-sort").Change("context"); Assert.Equal("Model 30", page.Find(".text-model-expand").TextContent);
        page.Find(".text-model-expand").Click(); Assert.Contains("Description", page.Find(".text-model-details").TextContent);
        Assert.Contains("Maximum output", page.Find(".text-model-details").TextContent);
        Assert.Equal("https://openrouter.ai/model/30", page.Find(".text-model-details a").GetAttribute("href"));
        page.FindAll(".text-model-expand")[1].Click(); Assert.Single(page.FindAll(".text-model-details"));
    }

    [Fact]
    public void ColumnHeadersToggleDirectionsRetainThemAndKeepUnknownValuesLast()
    {
        var early = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _providers.Models = [new("a", "A", Catalog: new(ContextLength: 1000, Pricing: new(0, 0, new Dictionary<string, decimal?>()), AddedUtc: early, PopularityOrder: 10)),
            new("b", "B", Catalog: new(ContextLength: 9000, Pricing: new(0.000001m, 0.000002m, new Dictionary<string, decimal?>()), AddedUtc: early.AddYears(1), PopularityOrder: 1)),
            new("unknown", "Unknown"), new("variable", "Variable", Catalog: new(Pricing: new(0, 0, new Dictionary<string, decimal?>(), Variable: true)))];
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-openrouter").Click();
        Assert.Equal(new[] { "name", "input", "output", "context", "added", "popularity" }, page.FindAll("[role=columnheader]").Select(e => e.GetAttribute("data-column")));
        foreach (var key in new[] { "input", "output", "context", "added", "popularity" })
        {
            var selector = $"[role=columnheader][data-column={key}]";
            page.Find(selector + " button").Click();
            var first = page.Find(".text-model-expand").TextContent;
            var direction = page.Find(selector).GetAttribute("aria-sort");
            page.Find(selector + " button").Click();
            Assert.NotEqual(first, page.Find(".text-model-expand").TextContent);
            Assert.NotEqual(direction, page.Find(selector).GetAttribute("aria-sort"));
            Assert.Equal(new[] { "Unknown", "Variable" }, page.FindAll(".text-model-expand").Skip(2).Select(e => e.TextContent));
        }
        Assert.Equal("descending", page.Find("[data-column=popularity][role=columnheader]").GetAttribute("aria-sort"));
        page.Find("#ai-text-provider-comfyui").Click(); page.Find("#ai-text-provider-openrouter").Click();
        Assert.Equal("descending", page.Find("[data-column=popularity][role=columnheader]").GetAttribute("aria-sort"));
        Assert.Contains("2025-01-01", page.Markup); Assert.Contains("#10", page.Markup);
        page.Find("[data-column=name][role=columnheader] button").Click();
        page.Find("[data-column=name][role=columnheader] button").Click();
        Assert.Equal("Variable", page.Find(".text-model-expand").TextContent);
    }

    [Fact]
    public void LocalColumnsUseSavedBenchmarksAndSortKnownValuesBeforeUntestedModels()
    {
        var now = DateTimeOffset.UtcNow;
        var fast = Local with { Model = "fast.safetensors", Name = "Fast" };
        _providers.Models = [new(Local.Model, "Local"), new(fast.Model, fast.Name), new("untested", "Untested")];
        ComfyTextModelBenchmark Bench(double speed, long peak) => new(now, "GPU", 0, 20L << 30, 1L << 30, peak, null, null, 256, 256, speed, true, false);
        _settings.Value = _settings.Value with { ComfyTextModelVerifications = [new(Local.ComfyUrl!, "0.34.0", Local.Model, now.AddDays(-1), [Bench(5, 8L << 30)]),
            new(Local.ComfyUrl!, "0.34.0", fast.Model, now, [Bench(15, 4L << 30)])] };
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click();
        page.Find("[role=columnheader][data-column=speed] button").Click(); Assert.Equal("Fast", page.Find(".text-model-expand").TextContent);
        Assert.Contains("15.0 tokens/s", page.Find(".text-model-row").TextContent); Assert.Contains("~3.0 GiB", page.Find(".text-model-row").TextContent);
        page.Find("[role=columnheader][data-column=speed] button").Click(); Assert.Equal("Local", page.Find(".text-model-expand").TextContent);
        page.Find("[role=columnheader][data-column=vram] button").Click(); Assert.Equal("Fast", page.Find(".text-model-expand").TextContent);
        page.Find("[role=columnheader][data-column=tested] button").Click(); Assert.Equal("Fast", page.Find(".text-model-expand").TextContent);
    }

    [Fact]
    public void CompactPickersUseAliasesWithoutChangingModelIdentity()
    {
        AvailableModels(); _settings.Value = _settings.Value with { TextModelAliases = new() { [TextModelPolicy.Key(Cloud)] = "My cloud writer" } };
        var project = Guid.NewGuid(); var states = new List<TextModelSelectionState>(); var picker = Picker(project, TextAssistantStudio.Story, states); var dialog = PickerDialog(picker);
        Assert.Contains("My cloud writer", dialog.Find("select").TextContent); dialog.Find("select").Change(TextModelPolicy.Key(Cloud));
        Assert.Equal(Cloud, states.Last().Model); Assert.Contains("My cloud writer", picker.Markup); Assert.Empty(_preferences.Values);
    }
}
