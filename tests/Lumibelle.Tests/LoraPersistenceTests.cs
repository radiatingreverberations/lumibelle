using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using TestContext = Xunit.TestContext;

namespace Lumibelle.Tests;

public sealed partial class AiSettingsStoreTests
{
    [Fact]
    public async Task LoraLibraryReopensWithoutAffectingCredentialsAndRejectsInvalidWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = await Store.SaveAsync(new(), "secret", cancellationToken: ct);
        var definition = LoraTests.Definition();
        var saved = await Store.SaveAsync(original with { LoraLibrary = [definition] }, cancellationToken: ct);
        var reopened = Assert.Single((await Store.LoadAsync(ct)).LoraLibrary);
        Assert.Equal(definition.Reference, reopened.Reference); Assert.Equal(definition.DefaultStrength, reopened.DefaultStrength);
        Assert.Equal(definition.TriggerText, reopened.TriggerText); Assert.Empty(reopened.Tags);
        Assert.Equal("secret", await Store.ReadOpenRouterKeyAsync(ct));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.SaveAsync(original with { LoraLibrary = [] }, cancellationToken: ct));
        foreach (var invalid in new IReadOnlyList<LoraDefinition>[] { [definition, definition], [definition with { DefaultStrength = float.NaN }],
            [definition with { Reference = definition.Reference with { FileName = "krea2_identity_edit_v1_2.safetensors" } }] })
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.SaveAsync(saved with { LoraLibrary = invalid }, cancellationToken: ct));
        Assert.Equal(saved.Revision, (await Store.LoadAsync(ct)).Revision);
        Assert.Empty(JsonSerializer.Deserialize<AiSettings>("{}")!.LoraLibrary);
    }
}

public sealed partial class AssetStoreTests
{
    [Fact]
    public async Task LoraSelectionsAndHistorySurviveReopeningAssetRecreationAndRediscard()
    {
        var ct = TestContext.Current.CancellationToken; var (project, store) = CreateStore();
        var krea = LoraTests.Definition().Reference; var klein = LoraTests.Definition(ImageWorkflow.Flux2Klein9bKv, "klein/style.safetensors").Reference;
        var selections = new List<LoraSelection> { new(krea, .7f) };
        var asset = Asset("Character") with { Loras = new Dictionary<ImageWorkflow, IReadOnlyList<LoraSelection>>
        { [ImageWorkflow.Krea2] = selections, [ImageWorkflow.Flux2Klein9bKv] = [new(klein, -.5f, false)] } };
        var saved = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [asset, Asset("Other asset")] }, 0, ct);
        selections.Clear();
        Assert.Single(saved.Assets[0].Loras[ImageWorkflow.Krea2]);
        Assert.Empty(saved.Assets[1].Loras);
        var applied = new List<AppliedLora> { new(krea, .7f) };
        using var bytes = new MemoryStream(Png(4, 3));
        saved = await store.AddImageAsync(project.Id, asset.Id, bytes, new("take.png", [], AssetImageOrigin.Generated, new() { Loras = applied }), saved.Revision, ct);
        applied.Clear();
        Assert.Single(saved.Assets[0].Images[0].Generation!.Loras);
        saved = await store.DeleteAssetAsync(project.Id, asset.Id, saved.Revision, ct);
        var trash = Assert.Single(saved.Trash);
        saved = await store.RestoreImagesAsync(project.Id, [trash.Id], saved.Revision, ct);
        var restored = saved.Assets.Single(a => a.Id == asset.Id);
        Assert.Equal(.7f, Assert.Single(restored.Loras[ImageWorkflow.Krea2]).Strength);
        Assert.False(Assert.Single(restored.Loras[ImageWorkflow.Flux2Klein9bKv]).Enabled);
        Assert.Equal(new AppliedLora(krea, .7f), Assert.Single(restored.Images[0].Generation!.Loras));
        var reopened = await store.LoadAsync(project.Id, ct);
        Assert.Equal(restored.Loras[ImageWorkflow.Krea2], reopened.Assets.Single(a => a.Id == asset.Id).Loras[ImageWorkflow.Krea2]);
        var other = CreateStore("Other project"); Assert.Empty((await other.Store.LoadAsync(other.Project.Id, ct)).Assets);
        Assert.Empty(JsonSerializer.Deserialize<ReferenceAsset>("""{"Id":"a647f52b-11ca-4cd9-8566-753d8acba050","Name":"Legacy"}""")!.Loras);
    }

    [Fact]
    public async Task InvalidLoraPreferencesAndAppliedMetadataLeaveManifestUntouched()
    {
        var ct = TestContext.Current.CancellationToken; var (project, store) = CreateStore(); var asset = Asset("Character");
        var saved = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [asset] }, 0, ct);
        var selection = new LoraSelection(LoraTests.Definition().Reference);
        var invalid = asset with { Loras = new Dictionary<ImageWorkflow, IReadOnlyList<LoraSelection>> { [ImageWorkflow.Krea2] = [selection, selection] } };
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.SaveAsync(saved with { Assets = [invalid] }, saved.Revision, ct));
        using var bytes = new MemoryStream(Png(3, 4));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.AddImageAsync(project.Id, asset.Id, bytes, new("bad.png", [], AssetImageOrigin.Generated,
            new() { Loras = [new(selection.Reference, float.PositiveInfinity)] }), saved.Revision, ct));
        Assert.Equal(saved.Revision, (await store.LoadAsync(project.Id, ct)).Revision);
    }
}

public sealed partial class AiSettingsComponentTests
{
    [Fact]
    public void LargeLoraLibraryPagesAndFiltersSavedEntriesBeforeCheckingConnections()
    {
        _settings.Value = _settings.Value with { LoraLibrary = Enumerable.Range(1, 40).Select(i => LoraTests.Definition(
            i % 2 == 0 ? ImageWorkflow.Krea2 : ImageWorkflow.Flux2Klein9bKv, $"nested/checkpoint{i:0000}.safetensors", $"LoRA {i:0000}")).ToArray() };
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-loras").Click();
        Assert.Equal(15, page.FindAll(".lora-library .lora-row").Count);
        Assert.Empty(((FakeLoraCatalog)Services.GetRequiredService<IComfyLoraCatalog>()).Requests);
        page.Find("#lora-library-workflow").Change("Flux2Klein9bKv");
        page.FindAll(".lora-library button").Single(b => b.TextContent == "Next").Click();
        Assert.Equal(5, page.FindAll(".lora-library .lora-row").Count);
        page.Find("#lora-library-search").Input("checkpoint0035");
        Assert.Single(page.FindAll(".lora-library .lora-row"));
        page.FindAll(".lora-library button").Single(b => b.TextContent == "Remove LoRA 0035").Click();
        Assert.Empty(page.FindAll(".lora-library .lora-row"));
        page.FindAll(".lora-library button").Single(b => b.TextContent == "Cancel LoRA changes").Click();
        Assert.Single(page.FindAll(".lora-library .lora-row")); Assert.Equal(40, _settings.Value.LoraLibrary.Count);
    }
    [Theory]
    [InlineData(LoraWorkflow.Krea2)] [InlineData(LoraWorkflow.MiniMaxH3Ref2VA)]
    public void LoraRegistrationDraftSurvivesTabsFailuresAndConflictReloadWithoutSavingOtherForms(LoraWorkflow workflow)
    {
        var page = Render<AiSettingsPage>();
        page.Find("#ai-tab-connections").Click(); page.Find("#comfy-url").Change("http://unsaved.invalid");
        page.Find("#ai-tab-images").Click(); page.Find("#comfy-image-model").Change("unsaved-model");
        page.Find("#ai-tab-loras").Click();
        Assert.Empty(page.FindAll("#lora-library-search, #lora-library-workflow"));
        Assert.Contains("No LoRAs registered yet", page.Markup);
        page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(b => b.TextContent == "Refresh installed LoRAs").Click();
        var catalog = (FakeLoraCatalog)Services.GetRequiredService<IComfyLoraCatalog>();
        Assert.Equal(new AiSettings().ComfyUrl, Assert.Single(catalog.Requests).ComfyUrl);
        page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(b => b.TextContent == "Add registration").Click();
        page.Find("#lora-file").Change("character.safetensors"); page.Find("#lora-name").Input("Character"); page.Find("#lora-trigger").Input("mouse trigger");
        page.Find("#lora-workflow").Change(workflow.ToString());
        page.Find("#lora-default-strength").Change("0.75");
        page.Find("#lora-tags").Input(" SFW, sfw, character ");
        page.Find("#ai-tab-text").Click(); page.Find("#ai-tab-loras").Click(); Assert.Equal("Character", page.Find("#lora-name").GetAttribute("value"));
        Assert.Equal(" SFW, sfw, character ", page.Find("#lora-tags").GetAttribute("value"));
        _settings.SaveError = new WorkspaceConflictException();
        page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(b => b.TextContent == "Save LoRA library").Click();
        Assert.Empty(_settings.Value.LoraLibrary); Assert.Contains("Another tab changed", page.Markup);
        Assert.Equal("Character", page.Find("#lora-name").GetAttribute("value"));
        _settings.SaveError = null;
        page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(b => b.TextContent == "Save LoRA library").Click();
        var definition = Assert.Single(_settings.Value.LoraLibrary); Assert.Equal(.75f, definition.DefaultStrength); Assert.Equal("mouse trigger", definition.TriggerText);
        Assert.Equal(workflow, definition.Reference.Workflow);
        Assert.Equal(new[] { "sfw", "character" }, definition.Tags);
        page.Find("#lora-library-search").Input("sfw"); Assert.Single(page.FindAll(".lora-library .lora-row"));
        page.Find("#lora-library-search").Input("");
        Assert.Single(page.FindAll("#lora-library-search")); Assert.Single(page.FindAll("#lora-library-workflow"));
        Assert.Equal(new AiSettings().ComfyUrl, _settings.Value.ComfyUrl); Assert.Equal(new AiSettings().ComfyImageModel, _settings.Value.ComfyImageModel);
    }
}

public sealed partial class AssetComponentTests
{
    [Fact]
    public async Task LoraModalValidatesDraftsAndCancelLeavesAppliedSelectionsUnchanged()
    {
        var settings = (FakeAiSettingsStore)Services.GetRequiredService<IAiSettingsStore>();
        var definition = LoraTests.Definition();
        settings.Value = settings.Value with { LoraLibrary = [definition] };
        _assets.Library = _assets.Library with { Assets = [Asset("Character") with
        {
            Description = "A portrait",
            Loras = new Dictionary<ImageWorkflow, IReadOnlyList<LoraSelection>> { [ImageWorkflow.Krea2] = [new(definition.Reference)] }
        }, Asset("Other") with { Description = "A landscape" }] };
        var page = Page();
        await page.Find(".image-loras-button").ClickAsync(new());
        _dialogs.Find("input[aria-label='Strength for Character']").Input("999");
        Assert.Contains("Enter valid strengths", _dialogs.Markup);
        Assert.True(_dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Apply changes").HasAttribute("disabled"));
        _dialogs.Find("input[aria-label='Strength for Character']").Input("0.6");
        await _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").ClickAsync(new());
        await page.Find(".image-loras-button").ClickAsync(new());
        Assert.Equal("1", _dialogs.Find("input[aria-label='Strength for Character']").GetAttribute("value"));
        _dialogs.Find("input[aria-label='Strength for Character']").Input("0.6");
        await ApplyDialogChanges();
        Assert.Equal("LoRAs, 1 active", page.Find(".image-loras-button").GetAttribute("aria-label"));
        await page.Find(".image-loras-button").ClickAsync(new());
        Assert.Equal("0.6", _dialogs.Find("input[aria-label='Strength for Character']").GetAttribute("value"));
        _dialogs.Find(".lora-row input[type=checkbox]").Change(false);
        await ApplyDialogChanges();
        Assert.Equal("LoRAs, 0 active", page.Find(".image-loras-button").GetAttribute("aria-label"));
        Assert.False(page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(b => b.TextContent == "Generate images").HasAttribute("disabled"));
    }

    [Fact]
    public async Task SearchableLoraPickerRefreshesPerOpeningAndPreservesInvalidStrengthDrafts()
    {
        var settings = (FakeAiSettingsStore)Services.GetRequiredService<IAiSettingsStore>();
        var character = LoraTests.Definition(name: "Mouse") with { Tags = ["character"] };
        var ink = LoraTests.Definition(file: "styles/ink.safetensors", name: "Ink") with { Tags = ["drawing"] };
        settings.Value = settings.Value with { LoraLibrary = [character, ink] };
        _assets.Library = _assets.Library with { Assets = [Asset("Character")] };
        var page = Page(); page.Find("#image-prompt").Input("A portrait");
        await page.Find(".image-loras-button").ClickAsync(new());
        var catalog = (FakeLoraCatalog)Services.GetRequiredService<IComfyLoraCatalog>();
        var requests = catalog.Requests.Count;
        var picker = _dialogs.FindComponent<MudBlazor.MudAutocomplete<LoraDefinition>>().Instance;
        await page.InvokeAsync(picker.OpenMenuAsync);
        foreach (var search in new[] { "mouse", "character.safetensors", "CHARACTER" })
            Assert.Equal(character, Assert.Single(await page.InvokeAsync(async () => (await picker.SearchFunc!(search, default)!)!)));
        Assert.Equal(ink, Assert.Single(await page.InvokeAsync(async () => (await picker.SearchFunc!("styles/ink", default)!)!)));
        Assert.Equal(requests + 1, catalog.Requests.Count);
        await page.InvokeAsync(() => picker.SelectOptionAsync(character));
        Assert.Equal(character.DefaultStrength.ToString(System.Globalization.CultureInfo.InvariantCulture), _dialogs.Find("input[aria-label='Strength for Mouse']").GetAttribute("value"));
        Assert.True(string.IsNullOrEmpty(_dialogs.Find("#lora-add").GetAttribute("value")));
        _dialogs.Find("input[aria-label='Strength for Mouse']").Input("999");
        await page.InvokeAsync(picker.OpenMenuAsync);
        Assert.Empty(await page.InvokeAsync(async () => (await picker.SearchFunc!("mouse", default)!)!));
        Assert.Equal(requests + 2, catalog.Requests.Count);
        Assert.Equal("999", _dialogs.Find("input[aria-label='Strength for Mouse']").GetAttribute("value"));
        Assert.True(_dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Apply changes").HasAttribute("disabled"));
        await page.InvokeAsync(() => picker.SelectOptionAsync(character));
        Assert.Single(_dialogs.FindAll(".lora-row"));
        await _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").ClickAsync(new());
        page.Find("#asset-image-workflow").Change("Flux2Klein9bKv");
        Assert.False(page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(b => b.TextContent == "Generate images").HasAttribute("disabled"));
    }

    [Fact]
    public void UnreadableSettingsKeepTheAssetLibraryUsable()
    {
        ((FakeAiSettingsStore)Services.GetRequiredService<IAiSettingsStore>()).LoadError = new WorkspaceStoreException("Settings unreadable");
        _assets.Library = _assets.Library with { Assets = [Asset("Character")] };
        var page = Page();
        Assert.Equal("Character", page.Find("#asset-name").GetAttribute("value"));
        Assert.Empty(page.FindAll(".lora-panel")); Assert.Contains("Settings unreadable", page.Markup);
        Assert.True(page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(b => b.TextContent == "Generate images").HasAttribute("disabled"));
    }
    [Fact]
    public async Task AssetLorasRememberWorkflowsAndMissingSelectionsBlockGenerationWithoutAlteringPrompt()
    {
        var settings = (FakeAiSettingsStore)Services.GetRequiredService<IAiSettingsStore>();
        var krea = LoraTests.Definition(); var klein = LoraTests.Definition(ImageWorkflow.Flux2Klein9bKv, "klein/style.safetensors", "Klein style");
        settings.Value = settings.Value with { LoraLibrary = [krea, klein] };
        _assets.Library = _assets.Library with { Assets = [Asset("Character"), Asset("Other")] };
        var page = Page(); var prompt = page.Find("#image-prompt").TextContent;
        await page.Find(".image-loras-button").ClickAsync(new());
        await _dialogs.InvokeAsync(() => _dialogs.FindComponent<MudBlazor.MudAutocomplete<LoraDefinition>>().Instance.SelectOptionAsync(krea));
        _dialogs.Find("input[aria-label='Strength for Character']").Input("0.45");
        await ApplyDialogChanges();
        Assert.Equal(prompt, page.Find("#image-prompt").TextContent);
        page.Find("#asset-image-workflow").Change("Flux2Klein9bKv"); Assert.Empty(page.FindAll(".lora-row"));
        await page.Find(".image-loras-button").ClickAsync(new());
        await _dialogs.InvokeAsync(() => _dialogs.FindComponent<MudBlazor.MudAutocomplete<LoraDefinition>>().Instance.SelectOptionAsync(klein));
        await ApplyDialogChanges();
        page.Find("#asset-image-workflow").Change("Krea2");
        await page.Find(".image-loras-button").ClickAsync(new());
        Assert.Equal("0.45", _dialogs.Find("input[aria-label='Strength for Character']").GetAttribute("value"));
        var catalog = (FakeLoraCatalog)Services.GetRequiredService<IComfyLoraCatalog>(); catalog.Value = new(true, "Missing", []);
        await _dialogs.InvokeAsync(() => _dialogs.FindComponent<MudBlazor.MudAutocomplete<LoraDefinition>>().Instance.OpenMenuAsync());
        Assert.True(page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(b => b.TextContent == "Generate images").HasAttribute("disabled"));
        Assert.Contains("exact LoRA file is missing", page.Markup);
        _dialogs.Find(".lora-row input[type=checkbox]").Change(false);
        await ApplyDialogChanges();
        _actions.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(b => b.TextContent.Trim() == "Save").Click();
        page.WaitForAssertion(() => Assert.True(_assets.Library.Assets[0].Loras.ContainsKey(ImageWorkflow.Krea2)));
        var preferences = _assets.Library.Assets[0].Loras;
        Assert.False(Assert.Single(preferences[ImageWorkflow.Krea2]).Enabled); Assert.Single(preferences[ImageWorkflow.Flux2Klein9bKv]);
        Assert.Empty(_assets.Library.Assets[1].Loras);
    }
}
