using Bunit;
using lumibelle.Components.Assets;
using lumibelle.Components.Pages;
using lumibelle.Components.Projects;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using TestContext = Xunit.TestContext;

namespace Lumibelle.Tests;

public sealed partial class LoraTests
{
    [Theory]
    [InlineData("sfw, character", "", "", true)]
    [InlineData("", "", "nsfw", true)]
    [InlineData("", "sfw", "", false)]
    [InlineData("character", "sfw, character", "", true)]
    [InlineData("nsfw, sfw", "sfw", "nsfw", false)]
    [InlineData("NSFW", "", "nsfw", false)]
    [InlineData("SFW", "sfw", "", true)]
    public void ProjectVisibilityUsesAnyAllowedTagAndHiddenTagsWin(string tags, string only, string hidden, bool visible)
    {
        var definition = Definition() with { Tags = tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) };
        Assert.Equal(visible, LoraPolicy.Visible(definition, new() { OnlyTags = LoraPolicy.ParseTags(only), HiddenTags = LoraPolicy.ParseTags(hidden) }));
        Assert.Equal(new[] { "sfw", "character" }, LoraPolicy.ParseTags(" SFW, sfw, , Character "));
    }

    [Theory]
    [InlineData(ImageWorkflow.Krea2, false)] [InlineData(ImageWorkflow.Krea2, true)]
    [InlineData(ImageWorkflow.Flux2Klein9bKv, false)] [InlineData(ImageWorkflow.Flux2Klein9bKv, true)]
    public async Task GenerationReadsProjectRulesBeforeSubmittingAndDoesNotSilentlyDropExcludedLoras(ImageWorkflow workflow, bool editing)
    {
        var projectId = Guid.NewGuid(); var definition = Definition(workflow) with { Tags = ["sfw", "nsfw"] };
        var preferences = new FakeProjectAiPreferencesStore();
        preferences.Values[projectId] = new() { ProjectId = projectId, LoraVisibility = new() { OnlyTags = ["sfw"], HiddenTags = ["nsfw"] } };
        var handler = Handler(workflow, editing);
        var service = Service(handler, new() { Value = new() { LoraLibrary = [definition] } }, preferences);
        async Task Run(Guid id, bool enabled, float strength)
        {
            using var bytes = new MemoryStream(AssetStoreTests.Png(20, 30));
            var loras = new LoraSelection[] { new(definition.Reference, strength, enabled) };
            var updates = editing ? service.EditAsync(new() { ProjectId = id, Workflow = workflow, Prompt = "Edit", AspectRatio = "1:1",
                SourceAssetId = Guid.NewGuid(), SourceImageId = Guid.NewGuid(), Loras = loras }, bytes, Ct)
                : service.GenerateAsync(new() { ProjectId = id, Workflow = workflow, Prompt = "Create", AspectRatio = "1:1", Loras = loras }, Ct);
            await foreach (var _ in updates) { }
        }
        var error = await Assert.ThrowsAsync<AiGenerationException>(() => Run(projectId, true, 1));
        Assert.Contains("Excluded by this project", error.Message); Assert.Empty(handler.Requests);
        await Run(projectId, false, 1); await Run(projectId, true, 0);
        await Run(Guid.NewGuid(), true, 1); // Another project defaults to all LoRAs.
        Assert.Equal(3, handler.Requests.Count(r => r.Path == "/prompt"));
        preferences.LoadError = new WorkspaceStoreException("Unreadable preferences");
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Run(projectId, true, 1));
        Assert.Equal(3, handler.Requests.Count(r => r.Path == "/prompt"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RulesAndLibraryTagsAreCapturedForEntireBatch(bool editing)
    {
        var projectId = Guid.NewGuid(); var tags = new List<string> { "sfw" };
        var definition = Definition() with { Tags = tags };
        var preferences = new FakeProjectAiPreferencesStore();
        preferences.Values[projectId] = new() { ProjectId = projectId, LoraVisibility = new() { OnlyTags = ["sfw"] } };
        var settings = new FakeAiSettingsStore { Value = new() { LoraLibrary = [definition] } };
        var handler = Handler(ImageWorkflow.Krea2, editing); var service = Service(handler, settings, preferences);
        using var bytes = new MemoryStream(AssetStoreTests.Png(20, 30));
        var updates = editing ? service.EditAsync(new() { ProjectId = projectId, Prompt = "Edit", AspectRatio = "1:1", Count = 2,
            SourceAssetId = Guid.NewGuid(), SourceImageId = Guid.NewGuid(), Loras = [new(definition.Reference)] }, bytes, Ct)
            : service.GenerateAsync(new() { ProjectId = projectId, Prompt = "Create", AspectRatio = "1:1", Count = 2, Loras = [new(definition.Reference)] }, Ct);
        var results = 0;
        await foreach (var update in updates)
        {
            tags.Clear(); tags.Add("nsfw");
            preferences.Values[projectId] = new() { ProjectId = projectId, LoraVisibility = new() { HiddenTags = ["nsfw"] } };
            if (update.Metadata is { } metadata) { results++; Assert.Single(metadata.Loras); }
        }
        Assert.Equal(2, results);
        await Assert.ThrowsAsync<AiGenerationException>(async () => { await foreach (var _ in service.GenerateAsync(new()
            { ProjectId = projectId, Prompt = "Next batch", AspectRatio = "1:1", Loras = [new(definition.Reference)] }, Ct)) { } });
    }
}

public sealed partial class AiSettingsStoreTests
{
    [Fact]
    public async Task TagsNormalizePersistAndCopyWithoutChangingProvenanceOrCredentials()
    {
        var ct = TestContext.Current.CancellationToken; var tags = new List<string> { " SFW ", "sfw", "Character" };
        var saved = await Store.SaveAsync(new() { LoraLibrary = [LoraTests.Definition() with { Tags = tags }] }, "protected-key", cancellationToken: ct);
        tags.Clear(); Assert.Equal(new[] { "sfw", "character" }, saved.LoraLibrary[0].Tags);
        var loaded = await Store.LoadAsync(ct); Assert.Equal(saved.LoraLibrary[0].Tags, loaded.LoraLibrary[0].Tags);
        Assert.Equal("protected-key", await Store.ReadOpenRouterKeyAsync(ct));
        foreach (var invalid in new IReadOnlyList<string>[] { null!, [null!], ["bad,tag"], [new string('a', 65)] })
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.SaveAsync(saved with { LoraLibrary = [LoraTests.Definition() with { Tags = invalid }] }, cancellationToken: ct));
        Assert.Equal(saved.Revision, (await Store.LoadAsync(ct)).Revision);
        Assert.Empty(JsonSerializer.Deserialize<LoraDefinition>("""{"Reference":{"ComfyUrl":"http://localhost:8188","FileName":"legacy.safetensors","Workflow":0,"Name":"Legacy"}}""")!.Tags);
    }
}

public sealed partial class TextModelStoreTests
{
    [Fact]
    public async Task ProjectVisibilityPersistsIndependentlyAndDetectsConflictsWithoutLosingStudioSelections()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await Projects.CreateAsync(new("First"), ct); var second = await Projects.CreateAsync(new("Second"), ct);
        var initial = await Preferences.LoadAsync(project.Id, ct);
        Assert.Empty(initial.LoraVisibility.OnlyTags); Assert.Empty(initial.LoraVisibility.HiddenTags);
        await Task.WhenAll(Preferences.SetLoraVisibilityAsync(project.Id, new() { OnlyTags = [" SFW ", "sfw"], HiddenTags = ["NSFW"] }, 0, ct),
            Preferences.SetSelectionAsync(project.Id, TextAssistantStudio.Story, Local, ct),
            Preferences.SetSelectionAsync(project.Id, TextAssistantStudio.AssetExtraction, Cloud, ct));
        var loaded = await Preferences.LoadAsync(project.Id, ct);
        Assert.Equal(new[] { "sfw" }, loaded.LoraVisibility.OnlyTags); Assert.Equal(new[] { "nsfw" }, loaded.LoraVisibility.HiddenTags);
        Assert.Equal(Local, loaded.Story); Assert.Equal(Cloud, loaded.AssetExtraction);
        Assert.Empty((await Preferences.LoadAsync(second.Id, ct)).LoraVisibility.HiddenTags);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Preferences.SetLoraVisibilityAsync(project.Id, new(), 0, ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Preferences.SetLoraVisibilityAsync(project.Id, new() { HiddenTags = [new string('x', 65)] }, 1, ct));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Preferences.SetLoraVisibilityAsync(project.Id, new(), 1, cancelled.Token));
        Assert.Equal(1, (await Preferences.LoadAsync(project.Id, ct)).LoraVisibilityRevision);
        var reset = await Preferences.SetLoraVisibilityAsync(project.Id, new(), 1, ct);
        Assert.Equal(2, reset.LoraVisibilityRevision); Assert.Empty(reset.LoraVisibility.OnlyTags); Assert.Equal(Local, reset.Story);
    }
}

public sealed partial class AssetComponentTests
{
    [Fact]
    public async Task ProjectFiltersHideChoicesAndBlockExistingSelectionsInCreateAndEdit()
    {
        var settings = (FakeAiSettingsStore)Services.GetRequiredService<IAiSettingsStore>();
        var safe = LoraTests.Definition(name: "Safe") with { Tags = ["sfw"] };
        var excluded = LoraTests.Definition(file: "styles/ink.safetensors", name: "Ink") with { Tags = ["sfw", "nsfw"] };
        settings.Value = settings.Value with { LoraLibrary = [safe, excluded] };
        var source = new AssetImage { Id = Guid.NewGuid(), FileName = "source.png", Width = 4, Height = 6, ContentType = "image/png" };
        _assets.Library = _assets.Library with { Assets = [Asset("Character") with { Images = [source],
            Loras = new Dictionary<ImageWorkflow, IReadOnlyList<LoraSelection>> { [ImageWorkflow.Krea2] = [new(excluded.Reference)] } }] };
        var page = Page(); page.Find("#image-prompt").Input("A character portrait");
        Assert.Empty(page.FindAll(".project-lora-visibility"));
        Assert.NotEmpty(_navigation.FindAll($"a[href='/projects/{_projectId:D}/settings']"));
        var settingsPage = Render<ProjectSettings>(p => p.Add(c => c.Id, _projectId));
        settingsPage.Find("#lora-only-tags").Input("sfw"); settingsPage.Find("#lora-hidden-tags").Input("nsfw");
        settingsPage.FindAll("button").Single(b => b.TextContent == "Save project visibility").Click();
        await page.Find(".image-loras-button").ClickAsync(new());
        await _dialogs.InvokeAsync(async () => { var picker = _dialogs.FindComponent<MudBlazor.MudAutocomplete<LoraDefinition>>().Instance; await picker.CloseMenuAsync(); await picker.OpenMenuAsync(); });
        Assert.Contains("Excluded by this project", page.Markup);
        Assert.True(page.FindAll("button").Single(b => b.TextContent == "Generate images").HasAttribute("disabled"));
        Assert.Single(await _dialogs.InvokeAsync(async () => (await _dialogs.FindComponent<MudBlazor.MudAutocomplete<LoraDefinition>>().Instance.SearchFunc!("", default)!)!));
        _dialogs.Find(".lora-row input[type=checkbox]").Change(false);
        await ApplyDialogChanges();
        Assert.False(page.FindAll("button").Single(b => b.TextContent == "Generate images").HasAttribute("disabled"));
        page.Find(".media-select").Click();
        page.Find("#image-prompt").Input("Change the background");
        await page.Find(".image-loras-button").ClickAsync(new());
        _dialogs.Find(".lora-row input[type=checkbox]").Change(true);
        await ApplyDialogChanges();
        Assert.True(page.FindAll("button").Single(b => b.TextContent == "Generate edited images").HasAttribute("disabled"));
        settingsPage.Find("#lora-hidden-tags").Input("");
        settingsPage.FindAll("button").Single(b => b.TextContent == "Save project visibility").Click();
        await page.Find(".image-loras-button").ClickAsync(new());
        await _dialogs.InvokeAsync(async () => { var picker = _dialogs.FindComponent<MudBlazor.MudAutocomplete<LoraDefinition>>().Instance; await picker.CloseMenuAsync(); await picker.OpenMenuAsync(); });
        Assert.False(page.FindAll("button").Single(b => b.TextContent == "Generate edited images").HasAttribute("disabled"));
    }

    [Fact]
    public void VisibilityDraftSurvivesFailuresAndConflictsWhileReloadUpdatesTheEffectiveRules()
    {
        var store = (FakeProjectAiPreferencesStore)Services.GetRequiredService<IProjectAiPreferencesStore>();
        ProjectAiPreferences? effective = null;
        var component = Render<ProjectLoraVisibility>(p => p.Add(c => c.ProjectId, _projectId).Add(c => c.Changed, value => effective = value));
        component.Find("#lora-hidden-tags").Input("nsfw");
        store.SaveError = new WorkspaceStoreException("Disk unavailable");
        component.FindAll("button").Single(b => b.TextContent == "Save project visibility").Click();
        Assert.Contains("Disk unavailable", component.Markup); Assert.Equal("nsfw", component.Find("#lora-hidden-tags").GetAttribute("value"));
        Assert.Empty(effective!.LoraVisibility.HiddenTags);
        store.SaveError = null;
        store.Values[_projectId] = new() { ProjectId = _projectId, LoraVisibilityRevision = 1, LoraVisibility = new() { OnlyTags = ["character"] } };
        component.FindAll("button").Single(b => b.TextContent == "Save project visibility").Click();
        Assert.Null(effective); Assert.Contains("Another tab", component.Markup);
        component.FindAll("button").Single(b => b.TextContent == "Reload project visibility").Click();
        Assert.Equal(new[] { "character" }, effective!.LoraVisibility.OnlyTags);
        Assert.Equal("nsfw", component.Find("#lora-hidden-tags").GetAttribute("value"));
        component.FindAll("button").Single(b => b.TextContent == "Save project visibility").Click();
        Assert.Equal(new[] { "nsfw" }, effective!.LoraVisibility.HiddenTags); Assert.Equal(2, effective.LoraVisibilityRevision);
        component.Find("#lora-hidden-tags").Input("unsaved");
        component.FindAll("button").Single(b => b.TextContent == "Cancel visibility changes").Click();
        Assert.Equal("nsfw", component.Find("#lora-hidden-tags").GetAttribute("value"));
        store.LoadError = new WorkspaceStoreException("Cannot load preferences");
        component.FindAll("button").Single(b => b.TextContent == "Reload project visibility").Click();
        Assert.Null(effective); Assert.Contains("Cannot load preferences", component.Markup);
    }

    [Fact]
    public async Task AssetsCanRetryFailedVisibilityLoadsWithoutRestoringTheSettingsForm()
    {
        var store = (FakeProjectAiPreferencesStore)Services.GetRequiredService<IProjectAiPreferencesStore>();
        store.LoadError = new WorkspaceStoreException("Project preferences unavailable");
        _assets.Library = _assets.Library with { Assets = [Asset("Character")] };
        // The AI queue re-renders the page from other threads; see BunitClicks.ClickCurrent.
        var page = Page(); page.WaitForElement("#image-prompt");
        await page.InvokeAsync(() => page.Find("#image-prompt").Input("A portrait"));
        page.WaitForAssertion(() => Assert.Contains("Project preferences unavailable", page.Markup));
        await page.InvokeAsync(() =>
        {
            Assert.True(page.FindAll("button").Single(b => b.TextContent == "Generate images").HasAttribute("disabled"));
            Assert.Empty(page.FindAll(".project-lora-visibility"));
        });
        store.LoadError = null;
        await page.ClickCurrent(() => page.FindAll("button").Single(b => b.TextContent == "Retry project visibility"));
        page.WaitForAssertion(() => Assert.False(page.FindAll("button").Single(b => b.TextContent == "Generate images").HasAttribute("disabled")));
    }
}
