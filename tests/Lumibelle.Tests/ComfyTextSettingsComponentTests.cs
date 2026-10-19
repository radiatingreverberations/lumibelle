using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Services.Story;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed partial class AiSettingsComponentTests
{
    [Fact]
    public void ModelSettingsKeepIndependentDraftsAcrossProvidersAliasesAndFallbackSaves()
    {
        _providers.Models = [new("local/one", "One"), new("local/two", "Two")];
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click();
        page.FindAll(".text-model-expand").Single(b => b.TextContent.Trim() == "One").Click();
        page.Find(".model-generation-settings input[id$='-tokens']").Change("8192");
        page.Find(".model-generation-settings input[id$='-temperature']").Change("0.9");
        page.Find(".model-alias-form input").Input("Unsaved alias");
        page.Find("#ai-text-provider-openrouter").Click(); page.Find("#ai-text-provider-defaults").Click(); page.Find("#timeout").Change("555");
        page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-comfyui").Click();
        Assert.Equal("8192", page.Find(".model-generation-settings input[id$='-tokens']").GetAttribute("value"));
        page.Find(".model-generation-settings").Submit();
        var model = new TextModelReference(AiBackend.ComfyUI, "local/one", "One", _settings.Value.ComfyUrl);
        Assert.Equal(new(8192, .9f), _settings.Value.ComfyTextModels[TextModelPolicy.Key(model)]);
        Assert.Empty(_settings.Value.TextModelAliases); Assert.Equal("Unsaved alias", page.Find(".model-alias-form input").GetAttribute("value"));
        page.Find(".model-generation-settings input[id$='-tokens']").Change("12000");
        page.Find(".model-generation-settings").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();
        Assert.Equal("8192", page.Find(".model-generation-settings input[id$='-tokens']").GetAttribute("value"));
        page.Find(".model-generation-settings").QuerySelectorAll("button").Single(b => b.TextContent.Contains("Use ComfyUI defaults")).Click();
        Assert.Empty(_settings.Value.ComfyTextModels);
        page.Find("#ai-text-provider-defaults").Click(); Assert.Equal("555", page.Find("#timeout").GetAttribute("value"));
    }

    [Fact]
    public void ModelSettingsConflictRetainsDraftUntilExplicitSave()
    {
        _providers.Models = [new("local/one", "One")];
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click(); page.Find(".text-model-expand").Click();
        page.Find(".model-generation-settings input[id$='-tokens']").Change("8192");
        _settings.SaveError = new WorkspaceConflictException(); page.Find(".model-generation-settings").Submit();
        Assert.Empty(_settings.Value.ComfyTextModels); Assert.Equal("8192", page.Find(".model-generation-settings input[id$='-tokens']").GetAttribute("value"));
        _settings.SaveError = null; _settings.Value = _settings.Value with { TimeoutSeconds = 777 };
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Reload saved settings").Click();
        page.Find(".model-generation-settings").Submit(); Assert.Equal(777, _settings.Value.TimeoutSeconds);
        Assert.Equal(8192, Assert.Single(_settings.Value.ComfyTextModels).Value.MaxOutputTokens);
    }
    [Fact]
    public void ComfyGenerationDefaultsRetainDraftsAndSaveSeparatelyFromTimeoutsAndConnections()
    {
        var page = Render<AiSettingsPage>(); var saved = _settings.Value;
        page.Find("#comfy-url").Change("http://unsaved.test:8188");
        page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-defaults").Click(); 
        Assert.Empty(page.FindAll("#temperature, #max-tokens"));
        page.Find("#timeout").Change("500");
        page.Find("#ai-tab-images").Click(); page.Find("#ai-image-defaults").Click(); page.Find("#image-timeout").Change("900");
        page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-comfyui").Click();
        page.Find("#temperature").Change("1.2"); page.Find("#max-tokens").Change("4096");
        page.Find("#ai-text-provider-openrouter").Click();
        Assert.Empty(page.FindAll("#temperature, #max-tokens"));
        page.Find("#ai-text-provider-defaults").Click(); page.Find("#text-timeout-form").Submit();
        page.Find("#ai-tab-images").Click(); page.Find("#image-defaults-form").Submit();
        Assert.Equal(saved.Temperature, _settings.Value.Temperature); Assert.Equal(saved.MaxOutputTokens, _settings.Value.MaxOutputTokens);
        page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-comfyui").Click();
        Assert.Equal("1.2", page.Find("#temperature").GetAttribute("value")); Assert.Equal("4096", page.Find("#max-tokens").GetAttribute("value"));
        page.Find("#comfy-text-settings-form").Submit();
        Assert.Equal(1.2f, _settings.Value.Temperature); Assert.Equal(4096, _settings.Value.MaxOutputTokens);
        Assert.Equal(500, _settings.Value.TimeoutSeconds); Assert.Equal(900, _settings.Value.ImageTimeoutSeconds);
        Assert.Equal(saved.ComfyUrl, _settings.Value.ComfyUrl); Assert.Equal(saved.DefaultBackend, _settings.Value.DefaultBackend);

        page.Find("#ai-text-provider-defaults").Click(); page.Find("#timeout").Change("600");
        page.Find("#ai-text-provider-comfyui").Click(); page.Find("#temperature").Change("1.5");
        page.Find("#comfy-text-settings-form").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();
        Assert.Equal("1.2", page.Find("#temperature").GetAttribute("value"));
        page.Find("#ai-text-provider-defaults").Click(); Assert.Equal("600", page.Find("#timeout").GetAttribute("value"));
        page.Find("#ai-tab-connections").Click(); Assert.Equal("http://unsaved.test:8188", page.Find("#comfy-url").GetAttribute("value"));
    }

    [Fact]
    public void ComfyGenerationConflictPreservesDraftAndRetryKeepsOtherSavedSettings()
    {
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click();
        page.Find("#temperature").Change("1.1"); page.Find("#max-tokens").Change("8192");
        _settings.SaveError = new WorkspaceConflictException(); page.Find("#comfy-text-settings-form").Submit();
        Assert.Equal(2048, _settings.Value.MaxOutputTokens);
        Assert.Equal("8192", page.Find("#max-tokens").GetAttribute("value"));
        _settings.SaveError = null; _settings.Value = _settings.Value with { TimeoutSeconds = 700, OpenRouterConcurrency = 3 };
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Reload saved settings").Click();
        Assert.Equal("1.1", page.Find("#temperature").GetAttribute("value"));
        page.Find("#comfy-text-settings-form").Submit();
        Assert.Equal(8192, _settings.Value.MaxOutputTokens); Assert.Equal(1.1f, _settings.Value.Temperature);
        Assert.Equal(700, _settings.Value.TimeoutSeconds); Assert.Equal(3, _settings.Value.OpenRouterConcurrency);
    }
}
