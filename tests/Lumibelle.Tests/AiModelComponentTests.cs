using AngleSharp.Dom;
using Bunit;
using lumibelle.Components;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Lumibelle.Tests;
[Trait("Category", "Component")]
public sealed partial class AiModelComponentTests : BunitContext
{
    private readonly ModelTestQueueFixture _modelQueue = new();
    protected override void Dispose(bool disposing) { if (disposing) _modelQueue.Dispose(); base.Dispose(disposing); }
    private readonly FakeAiSettingsStore _settings = new();
    private readonly FakeProviders _providers = new();
    private readonly FakeProjectAiPreferencesStore _preferences = new();
    private static readonly TextModelReference Cloud = new(AiBackend.OpenRouter, "test/cloud", "Cloud model");
    private static readonly TextModelReference Local = new(AiBackend.ComfyUI, "local.safetensors", "Local model", "http://127.0.0.1:8188");
    public AiModelComponentTests()
    {
        _modelQueue.Register(Services);
        Services.AddMudServices(); JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IAiSettingsStore>(_settings); Services.AddSingleton<IAiProviderRegistry>(_providers);
        Services.AddSingleton<IProjectAiPreferencesStore>(_preferences);
        Services.AddSingleton<lumibelle.Services.Assets.IReferenceImageGenerator>(new FakeReferenceImageGenerator());
        Services.AddSingleton<lumibelle.Services.Assets.IReferenceImageEditor>(new FakeReferenceImageEditor());
        Services.AddSingleton<lumibelle.Services.Assets.IComfyLoraCatalog>(new FakeLoraCatalog());
        Services.AddSingleton<lumibelle.Services.Shots.IVideoGenerator>(new Lumibelle.Testing.MockVideoGenerator());
        Render<MudPopoverProvider>(); _modelQueue.Start(Services);
    }
    private static IElement Button<T>(IRenderedComponent<T> cut, string name) where T : IComponent => cut.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(button => button.TextContent.Trim() == name);
    private IRenderedComponent<TextModelPicker> Picker(Guid id, TextAssistantStudio studio, List<TextModelSelectionState>? states = null, bool disabled = false) =>
        Render<TextModelPicker>(p => p.Add(picker => picker.ProjectId, id).Add(picker => picker.Studio, studio)
            .Add(picker => picker.Disabled, disabled).Add(picker => picker.SelectionChanged, state => states?.Add(state)));
    private IRenderedComponent<MudDialogProvider>? _pickerDialogs;
    private IRenderedComponent<MudDialogProvider> PickerDialog(IRenderedComponent<TextModelPicker> picker) {
        _pickerDialogs ??= Render<MudDialogProvider>();
        picker.InvokeAsync(picker.Instance.OpenAsync).GetAwaiter().GetResult();
        _pickerDialogs.WaitForElement("select[id^=text-model-]", BunitDefaults.WaitTimeout(10)); return _pickerDialogs;
    }
    private void AvailableModels()
    {
        _settings.Value = _settings.Value with { StarredTextModels = [Cloud, Local],
            ComfyTextModelVerifications = [new(Local.ComfyUrl!, "0.34.0", Local.Model, DateTimeOffset.UtcNow)] };
        _providers.Models = [new(Cloud.Model, Cloud.Name), new(Local.Model, Local.Name), new(_settings.Value.ComfyModel, "Default")];
    }
    [Fact]
    public async Task OpeningModelOptionsDuringInitialLoadStillChecksEveryConfiguredProvider()
    {
        AvailableModels();
        var initial = new TaskCompletionSource<AiConnectionCheck>(TaskCreationOptions.RunContinuationsAsynchronously);
        var available = new AiConnectionCheck(true, "Available", _providers.Models!, "0.34.0");
        _providers.CheckResult = _ => _providers.CheckCalls == 1 ? initial.Task : Task.FromResult(available);
        _pickerDialogs = Render<MudDialogProvider>();
        var picker = Picker(Guid.NewGuid(), TextAssistantStudio.Story);
        picker.WaitForAssertion(() => Assert.Equal(1, _providers.CheckCalls));
        var opening = picker.InvokeAsync(picker.Instance.OpenAsync);
        initial.SetResult(available);
        await opening;
        _pickerDialogs.WaitForAssertion(() =>
        {
            Assert.Contains(AiBackend.OpenRouter, _providers.CheckedBackends);
            var option = _pickerDialogs.FindAll("option").Single(o => o.TextContent.Contains("Cloud model"));
            Assert.False(option.HasAttribute("disabled"));
        });
    }
    [Fact]
    public async Task ChangedDefaultIsDisplayedAndStopsSubmissionUntilTheAuthorRetries()
    {
        AvailableModels(); var id = Guid.NewGuid(); var states = new List<TextModelSelectionState>();
        var picker = Picker(id, TextAssistantStudio.Story, states);
        _pickerDialogs = Render<MudDialogProvider>();
        await _preferences.SetTextDefaultAsync(id, Cloud, 0, Xunit.TestContext.Current.CancellationToken);
        var ready = await picker.InvokeAsync(picker.Instance.PrepareSubmitAsync);
        Assert.False(ready); Assert.Equal(Cloud.Model, states.Last().Model.Model);
        _pickerDialogs.WaitForAssertion(() => Assert.Contains("model changed", _pickerDialogs.Markup));
        Assert.True(await picker.InvokeAsync(picker.Instance.PrepareSubmitAsync));
    }
    [Fact]
    public async Task DisabledPickerLoadsProjectDefaultWithoutChangingTheQueuedRequest()
    {
        AvailableModels(); var project = Guid.NewGuid();
        await _preferences.SetTextDefaultAsync(project, Local, 0, Xunit.TestContext.Current.CancellationToken);
        var states = new List<TextModelSelectionState>(); var picker = Picker(project, TextAssistantStudio.PromptEnhancement, states, disabled: true);
        picker.WaitForAssertion(() => Assert.Equal(Local, states.Last().Model));
        Assert.True(picker.Find(".model-chip").HasAttribute("disabled")); Assert.Equal(0, _providers.VerificationCalls);
    }
    [Fact]
    public void CatalogLoadsOnFirstVisitAndSupportsSearchStarsAndPagination()
    {
        _providers.Models = Enumerable.Range(0, 61).Select(i => new AiModel($"model/{i:D2}", $"Model {i:D2}")).ToList();
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click();
        Assert.Equal(1, _providers.CheckCalls); Assert.Equal(0, _providers.VerificationCalls);
        Assert.Contains(_settings.Value.ComfyModel, page.Markup);
        page.Find("#ai-text-provider-openrouter").Click();
        Assert.Equal(25, page.FindAll(".text-model-row").Count);
        Button(page, "Next").Click(); Assert.Contains("Page 2 of 3", page.Markup);
        page.Find("#model-search").Input("Model 60");
        Assert.Single(page.FindAll(".text-model-row"));
        page.Find(".model-star").Click(); Button(page, "Star anyway").Click(); Assert.Single(_settings.Value.StarredTextModels);
        Assert.Equal("true", page.Find(".model-star").GetAttribute("aria-pressed"));
        page.Find("#model-search").Input(""); Button(page, "★ Starred 1").Click();
        Assert.Single(page.FindAll(".text-model-row"));
        page.Find("#ai-tab-text").KeyDown("ArrowLeft"); Assert.Equal("true", page.Find("#ai-tab-connections").GetAttribute("aria-selected"));
        page.Find("#ai-tab-connections").KeyDown("End"); Assert.Equal("true", page.Find("#ai-tab-loras").GetAttribute("aria-selected"));
        page.Find("#ai-tab-loras").KeyDown("ArrowLeft"); Assert.Equal("true", page.Find("#ai-tab-video").GetAttribute("aria-selected"));
        page.Find("#ai-tab-loras").KeyDown("Home"); Assert.Equal("true", page.Find("#ai-tab-connections").GetAttribute("aria-selected"));
        page.Find("#ai-tab-connections").KeyDown("ArrowLeft"); Assert.Equal("true", page.Find("#ai-tab-loras").GetAttribute("aria-selected"));
        page.Find("#ai-tab-loras").KeyDown("ArrowRight"); Assert.Equal("true", page.Find("#ai-tab-connections").GetAttribute("aria-selected"));
    }
    [Fact]
    public void VideoCatalogTogglePreservesExplicitChoicesAndOnlySaveWritesSettings()
    {
        var generator=(Lumibelle.Testing.MockVideoGenerator)Services.GetRequiredService<lumibelle.Services.Shots.IVideoGenerator>();
        H3Settings? saved=null;var fail=false;
        var page=Render<lumibelle.Components.Shots.VideoSettingsPanel>(p=>p.Add(c=>c.Settings,_settings.Value)
            .Add(c=>c.SaveSettings, draft=> { if(fail) throw new WorkspaceConflictException();saved=draft;return Task.FromResult(true); }));
        const string file="custom/renamed-h3-encoder.safetensors";
        var encoder="select[aria-label='H3 Encoder']";
        const string attention="select[aria-label='H3 dense attention']";
        page.Find(attention).Change("Sage");
        Assert.Empty(page.FindAll(".video-performance-settings input[type=checkbox]"));
        Assert.Equal(lumibelle.Services.Shots.H3Presets.Keys.Length, page.FindAll(".video-preset-setup").Count);
        Assert.Equal(H3AttentionBackend.ServerDefault,_settings.Value.H3.Performance.Attention);
        Assert.Equal(0,generator.CheckCalls);
        Button(page,"Refresh video models").Click();Assert.Equal(1,generator.CheckCalls);
        Assert.DoesNotContain(file,page.Find(encoder).TextContent);
        page.Find("input[type=checkbox]").Change(true);Assert.Contains(file,page.Find(encoder).TextContent);
        page.Find(encoder).Change(file);Assert.Null(saved);
        page.Find("input[type=checkbox]").Change(false);Assert.Contains(file,page.Find(encoder).TextContent);
        Assert.Equal(file,page.Find(encoder).GetAttribute("value"));Assert.Null(saved);
        fail=true;page.FindAll("form").Single(f => f.Closest("[hidden]") is null).Submit();Assert.Null(saved);Assert.Contains("Another tab",page.Markup);
        Assert.Equal("Sage",page.Find(attention).QuerySelector("option[selected]")!.GetAttribute("value"));
        Assert.Equal(file,page.Find(encoder).GetAttribute("value"));
        fail=false;page.FindAll("form").Single(f => f.Closest("[hidden]") is null).Submit();Assert.Equal(file,saved!.Encoder);
        Assert.Equal(H3AttentionBackend.Sage,saved.Performance.Attention);Assert.False(saved.Performance.SolAttention);
        Assert.Equal(H3ArchiveCompression.Fast,saved.Performance.ArchiveCompression);
        Assert.Equal(H3AttentionBackend.ServerDefault,_settings.Value.H3.Performance.Attention);
        Assert.NotEqual(file,_settings.Value.H3.Encoder);
        Button(page,"Cancel").Click();Assert.Equal(_settings.Value.H3.Encoder,page.Find(encoder).GetAttribute("value"));
        Assert.Equal("ServerDefault",page.Find(attention).QuerySelector("option[selected]")!.GetAttribute("value"));
        page.Render(p=>p.Add(c=>c.Settings,_settings.Value with { H3=saved }).Add(c=>c.Busy,true));
        Button(page,"Cancel");Assert.True(page.Find("input[type=checkbox]").HasAttribute("disabled"));
        Assert.Equal(0,generator.SubmitCount);
    }
    [Fact]
    public void StarsAndDefaultsNeverSaveOtherTabDrafts()
    {
        _providers.Models = [new(Cloud.Model, Cloud.Name)];
        var original = _settings.Value;
        var page = Render<AiSettingsPage>();
        page.Find("#ai-tab-connections").Click(); page.Find("#comfy-url").Change("invalid draft URL"); page.Find("#ai-provider-openrouter").Click(); page.Find("#openrouter-key").Change("unsaved-key");
        page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-comfyui").Click(); page.Find("#temperature").Change("1.2");
        page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-openrouter").Click();
        page.Find("button[aria-label='Star Cloud model']").Click(); Button(page, "Star anyway").Click(); page.Find("#ai-text-provider-defaults").Click(); page.Find("#global-text-default").Change(TextModelPolicy.Key(Cloud)); page.Find("#ai-text-provider-openrouter").Click();
        Assert.Equal(Cloud.Model, _settings.Value.OpenRouterModel); Assert.Equal(AiBackend.OpenRouter, _settings.Value.DefaultBackend);
        Assert.Equal(original.Temperature, _settings.Value.Temperature); Assert.Equal(original.ComfyUrl, _settings.Value.ComfyUrl); Assert.Equal("test-only-key", _settings.Key);
        page.Find("button[aria-label='Unstar Cloud model']").Click(); Assert.Equal(Cloud.Model, _settings.Value.OpenRouterModel);
        page.Find("#ai-tab-connections").Click(); Assert.Equal("invalid draft URL", page.Find("#comfy-url").GetAttribute("value")); Assert.Equal("unsaved-key", page.Find("#openrouter-key").GetAttribute("value"));
        page.Find("#connection-comfyui-form").QuerySelectorAll("button").Single(button => button.TextContent == "Cancel").Click();
        Assert.Equal(original.ComfyUrl, page.Find("#comfy-url").GetAttribute("value")); Assert.Equal("unsaved-key", page.Find("#openrouter-key").GetAttribute("value"));
        page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-comfyui").Click(); page.Find("#comfy-text-settings-form").Submit();
        Assert.Equal(1.2f, _settings.Value.Temperature); Assert.Equal("test-only-key", _settings.Key);
    }
    [Fact]
    public void FailedAndConflictingStarWritesCanBeRetriedWithoutLosingDrafts()
    {
        _providers.Models = [new(Cloud.Model, Cloud.Name)];
        var page = Render<AiSettingsPage>();
        page.Find("#ai-tab-text").Click(); page.Find("#max-tokens").Change("4096");
        page.Find("#ai-text-provider-openrouter").Click(); _settings.SaveError = new WorkspaceConflictException();
        page.Find("button[aria-label='Star Cloud model']").Click(); Button(page, "Star anyway").Click(); Assert.Empty(_settings.Value.StarredTextModels);
        Assert.Contains("Another tab", page.Markup);
        _settings.SaveError = null; _settings.Value = _settings.Value with { Temperature = 1.1f };
        Button(page, "Reload saved settings").Click(); page.Find("#ai-text-provider-openrouter").Click();
        page.Find("button[aria-label='Star Cloud model']").Click(); Button(page, "Star anyway").Click(); Assert.Single(_settings.Value.StarredTextModels);
        Assert.Equal(1.1f, _settings.Value.Temperature); Assert.Equal(2048, _settings.Value.MaxOutputTokens);
        page.Find("#ai-text-provider-comfyui").Click(); Assert.Equal("4096", page.Find("#max-tokens").GetAttribute("value"));
    }
    [Fact]
    public async Task PendingStarWriteDisablesDuplicateActions()
    {
        _providers.Models = [new(Cloud.Model, Cloud.Name)];
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _settings.BeforeSave = () => release.Task;
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-openrouter").Click();
        page.Find("button[aria-label='Star Cloud model']").Click();
        var action = Button(page, "Star anyway").ClickAsync(new());
        try { page.WaitForAssertion(() => Assert.Contains("Saving", page.Find(".settings-feedback").TextContent)); Assert.True(page.Find("button[aria-label='Star Cloud model']").HasAttribute("disabled")); Assert.Equal(1, _settings.SaveCalls); }
        finally { release.TrySetResult(); }
        await action; Assert.Single(_settings.Value.StarredTextModels);
    }
    [Fact]
    public async Task BenchmarkDialogSavesImmediatelyAndRetriesPersistenceWithoutRegeneration()
    {
        var host = Render<MudDialogProvider>();
        _providers.Models = [new(Local.Model, Local.Name)];
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click();
        page.FindAll(".text-model-row").Single(row => row.TextContent.Contains(Local.Name)).QuerySelector(".text-model-expand")!.Click(); Button(page, "Details & test").Click();
        var dialog = host.FindComponent<ComfyModelDialog>(); _settings.SaveError = new WorkspaceStoreException("Disk unavailable");
        await dialog.InvokeAsync(() => Button(dialog, "Test selected model · 256 tokens").ClickAsync(new()));
        dialog.WaitForAssertion(() => Assert.Contains("could not be saved", dialog.Markup), BunitDefaults.WaitTimeout(5));
        Assert.Empty(_settings.Value.ComfyTextModelVerifications); Assert.Equal(1, _providers.VerificationCalls);
        // The save failure can surface before the dialog finishes enqueueing; Retry stays disabled until then.
        dialog.WaitForAssertion(() => Assert.False(Button(dialog, "Retry saving test result").HasAttribute("disabled")));
        _settings.SaveError = null; await dialog.InvokeAsync(() => Button(dialog, "Retry saving test result").ClickAsync(new()));
        dialog.WaitForAssertion(() => Assert.Contains("Test result saved.", dialog.Markup), BunitDefaults.WaitTimeout(5));
        Assert.Single(_settings.Value.ComfyTextModelVerifications); Assert.Equal(1, _providers.VerificationCalls);
        dialog.WaitForAssertion(() => Assert.Equal(AiJobState.Completed, Services.GetRequiredService<AiJobCoordinator>().View.Jobs.Single().State), BunitDefaults.WaitTimeout(5));
        await dialog.InvokeAsync(() => Button(dialog, "Close").Click());
        page.WaitForAssertion(() => Assert.False(page.Find("button[aria-label='Star Local model']").HasAttribute("disabled")));
        page.Find("button[aria-label='Star Local model']").Click(); page.WaitForAssertion(() => Assert.Single(_settings.Value.StarredTextModels));
        page.Find("#ai-text-provider-defaults").Click(); page.Find("#global-text-default").Change(TextModelPolicy.Key(Local)); Assert.Equal(Local.Model, _settings.Value.ComfyModel);
    }
    [Fact]
    public async Task ReopeningAModelShowsItsEarlierTestWithoutAnnouncingANewSave()
    {
        var host = Render<MudDialogProvider>();
        _providers.Models = [new(Local.Model, Local.Name)];
        IRenderedComponent<ComfyModelDialog> Open(IRenderedComponent<AiSettingsPage> settings)
        {
            settings.Find("#ai-tab-text").Click();
            settings.FindAll(".text-model-row").Single(row => row.TextContent.Contains(Local.Name)).QuerySelector(".text-model-expand")!.Click();
            Button(settings, "Details & test").Click();
            return host.FindComponents<ComfyModelDialog>().Last();
        }
        var page = Render<AiSettingsPage>(); var dialog = Open(page);
        await dialog.InvokeAsync(() => Button(dialog, "Test selected model · 256 tokens").ClickAsync(new()));
        page.WaitForAssertion(() => Assert.Contains("Model test saved.", page.Markup), BunitDefaults.WaitTimeout(5));
        dialog.WaitForAssertion(() => Assert.Equal(AiJobState.Completed, Services.GetRequiredService<AiJobCoordinator>().View.Jobs.Single().State), BunitDefaults.WaitTimeout(5));
        await dialog.InvokeAsync(() => Button(dialog, "Close").Click());
        // A later visit shows the finished test again; it is not a new save.
        var later = Render<AiSettingsPage>(); var reopened = Open(later);
        reopened.WaitForAssertion(() => Assert.Contains("Test result saved.", reopened.Markup), BunitDefaults.WaitTimeout(5));
        Assert.DoesNotContain("Model test saved.", later.Markup);
        Assert.Equal(1, _providers.VerificationCalls);
    }
    [Fact]
    public async Task BenchmarkSaveMergesConcurrentSettingsAndInvalidatesOldCatalog()
    {
        var host = Render<MudDialogProvider>();
        _settings.Value = _settings.Value with { ComfyModel = Local.Model };
        _providers.Models = [new(Local.Model, Local.Name)];
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click(); page.Find(".text-model-expand").Click(); Button(page, "Details & test").Click();
        _settings.Value = _settings.Value with { Revision = 5, ComfyUrl = "http://other-server:8188", Temperature = 1.1f };
        var dialog = host.FindComponent<ComfyModelDialog>(); Button(dialog, "Test selected model · 256 tokens").Click();
        dialog.WaitForAssertion(() => Assert.Single(_settings.Value.ComfyTextModelVerifications));
        Assert.Equal("http://other-server:8188", _settings.Value.ComfyUrl); Assert.Equal(1.1f, _settings.Value.Temperature);
        Assert.Equal(Local.ComfyUrl, _settings.Value.ComfyTextModelVerifications[0].ComfyUrl);
        await dialog.InvokeAsync(() => Button(dialog, "Close").Click());
        // The settings write precedes the background job's completion notification.
        page.WaitForAssertion(() =>
        {
            Assert.Contains("Test this model", page.Markup);
            Assert.All(page.FindAll(".model-star"), star => Assert.True(star.HasAttribute("disabled")));
        });
    }

    [Fact]
    public void ProjectDefaultIsSharedAndTemporaryChoicesDoNotPersist()
    {
        AvailableModels(); var id = Guid.NewGuid(); var states = new List<TextModelSelectionState>();
        var picker = Picker(id, TextAssistantStudio.Story, states); var dialog = PickerDialog(picker);
        dialog.Find("select").Change(TextModelPolicy.Key(Cloud));
        Assert.Empty(_preferences.Values); Assert.Equal(TextModelSelectionSource.RequestOverride, states.Last().Source);
        Button(dialog, "Set as project default").Click(); Assert.Equal(Cloud, _preferences.Values[id].TextDefault);
        _settings.Value = _settings.Value with { StarredTextModels = [] };
        var otherStates = new List<TextModelSelectionState>(); var other = Picker(id, TextAssistantStudio.AssetExtraction, otherStates);
        Assert.Equal(Cloud, otherStates.Last().Model); Assert.Contains(Cloud.Name, other.Markup); Assert.Equal(0, _settings.SaveCalls);
    }
    [Fact]
    public async Task DefaultSaveFailurePreservesTemporaryChoiceAndGlobalChangesRefresh()
    {
        AvailableModels(); var id = Guid.NewGuid(); var states = new List<TextModelSelectionState>();
        var picker = Picker(id, TextAssistantStudio.Story, states); var dialog = PickerDialog(picker);
        dialog.Find("select").Change(TextModelPolicy.Key(Cloud)); _preferences.SaveError = new WorkspaceStoreException("Disk unavailable");
        Button(dialog, "Set as project default").Click(); Assert.Equal(Cloud, states.Last().Model); Assert.Empty(_preferences.Values);
        Assert.Contains("Disk unavailable", dialog.Markup);
        _preferences.SaveError = null; dialog.Find("select").Change("default");
        _settings.Value = _settings.Value with { DefaultBackend = AiBackend.OpenRouter, OpenRouterModel = Cloud.Model };
        await picker.InvokeAsync(picker.Instance.RefreshModel); Assert.Equal(Cloud.Model, states.Last().Model.Model);
    }
    [Fact]
    public void UnavailableAndOutdatedSelectionsBlockAssistanceAndSwitchingCanBeDisabled()
    {
        AvailableModels(); var id = Guid.NewGuid();
        _preferences.Values[id] = new() { ProjectId = id, TextDefault = Local }; _providers.BackendVersion = "new version";
        var states = new List<TextModelSelectionState>(); var picker = Picker(id, TextAssistantStudio.Story, states); var dialog = PickerDialog(picker);
        Assert.False(states.Last().Ready); Assert.Contains("Test this model", dialog.Markup);
        _providers.Success = false; Button(dialog, "Refresh availability").Click(); Assert.False(states.Last().Ready);
        Assert.Contains("Not configured", dialog.Markup); picker.Render(p => p.Add(c => c.Disabled, true));
        Assert.True(picker.Find(".model-chip").HasAttribute("disabled")); Assert.Equal(Local, states.Last().Model);
    }
}
