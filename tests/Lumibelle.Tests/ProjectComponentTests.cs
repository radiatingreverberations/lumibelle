using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Components.Projects;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

using lumibelle.Services.Shots;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class ProjectComponentTests : BunitContext
{
    private readonly FakeProjectStore _store = new();
    private readonly FakeProjectFolders _folders = new();
    private readonly FakeProjectCompaction _compaction = new();
    private readonly IRenderedComponent<SectionOutlet> _navigation;
    private readonly IRenderedComponent<SectionOutlet> _identity;

    public ProjectComponentTests()
    {
        Services.AddSingleton<IProjectStore>(_store);
        Services.AddSingleton<lumibelle.Services.Projects.IProjectFolders>(_folders);
        Services.AddSingleton<lumibelle.Services.Projects.IProjectCompaction>(_compaction);
        Services.AddSingleton<IAiSettingsStore>(new FakeAiSettingsStore());
        Services.AddSingleton<IAiProviderRegistry>(new FakeProviders());
        Services.AddSingleton<IProjectAiPreferencesStore>(new FakeProjectAiPreferencesStore());
        Services.AddMudServices();
        // These tests exercise app behavior; browser checks cover MudBlazor's DOM interop.
        JSInterop.Mode = JSRuntimeMode.Loose;
        Render<MudPopoverProvider>();
        _navigation = Render<SectionOutlet>(p => p.Add(x => x.SectionName, "project-navigation"));
        _identity = Render<SectionOutlet>(p => p.Add(x => x.SectionName, "project-identity"));
    }

    [Fact]
    public void EmptyLibraryOffersProjectCreation()
    {
        var page = Render<Home>();
        Assert.Contains("Your next story belongs here", page.Markup);
        Assert.Contains("New project", page.Markup);
        Assert.Empty(page.FindAll(".project-card"));
    }

    [Fact]
    public void PopulatedLibraryLinksToProjectsAndShowsWarnings()
    {
        var project = FakeProjectStore.Project("Garden", "A world beyond the gate.");
        _store.List = () => Task.FromResult(new ProjectLibrary([project], [new(Guid.NewGuid(), "Unreadable manifest")]));
        var page = Render<Home>();
        Assert.Equal($"/projects/{project.Id:D}", page.Find(".project-card").GetAttribute("href"));
        Assert.Contains(project.Description!, page.Markup);
        Assert.Contains("Some projects couldn’t be opened", page.Find("[role=alert]").TextContent);
    }

    [Fact]
    public async Task LibraryShowsProjectFoldersAndRemovesAnUnavailableOne()
    {
        var linked = FakeProjectStore.Project("On the road"); var missing = Guid.NewGuid();
        _folders.Locations.Add(new(linked.Id, @"D:\Films\On the road", DateTimeOffset.UtcNow));
        _folders.Locations.Add(new(missing, @"E:\Unplugged", DateTimeOffset.UtcNow));
        _store.List = () => Task.FromResult(new ProjectLibrary([linked],
            _folders.Locations.Any(l => l.ProjectId == missing) ? [new(missing, @"The project folder E:\Unplugged is missing.")] : []));
        var page = Render<Home>();
        Assert.Equal(@"D:\Films\On the road", page.Find(".project-location").TextContent.Trim());
        await page.InvokeAsync(() => page.FindAll(".project-issues button").Single(b => b.TextContent.Trim() == "Remove from library").ClickAsync(new()));
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".project-issues")));
        Assert.Equal(linked.Id, Assert.Single(_folders.Locations).ProjectId);
    }

    [Fact]
    public void LibraryShowsLoadingUntilReadCompletes()
    {
        var pending = new TaskCompletionSource<ProjectLibrary>();
        _store.List = () => pending.Task;
        var page = Render<Home>();
        Assert.Contains("Opening your library", page.Find("[role=status]").TextContent);
        pending.SetResult(new([], []));
        page.WaitForAssertion(() => Assert.Contains("Your next story belongs here", page.Markup));
    }

    [Fact]
    public void LibraryReadFailureCanBeRetried()
    {
        _store.List = () => throw new ProjectStoreException("Library unavailable.");
        var page = Render<Home>();
        Assert.Contains("Library unavailable.", page.Find("[role=alert]").TextContent);
        _store.List = () => Task.FromResult(new ProjectLibrary([], []));
        page.FindAll("button").Single(button => button.TextContent.Contains("Try again")).Click();
        page.WaitForAssertion(() => Assert.Contains("Your next story belongs here", page.Markup));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreationValidatesNameBeforeCallingStore(string name)
    {
        var form = Render<ProjectForm>();
        Assert.DoesNotContain("Give your project a name.", form.Markup);
        form.Find("input").Input(name);
        form.Find("form").Submit();
        Assert.Empty(_store.CreateCalls);
        Assert.Contains("Give your project a name.", form.Markup);
    }

    [Fact]
    public void CancelDoesNotCreateProject()
    {
        var cancelled = false;
        var form = Render<ProjectForm>(parameters => parameters.Add(component => component.OnCancel, () => cancelled = true));
        form.Find("input").Input("An unsaved idea");
        form.Find("button[type=button]").Click();
        Assert.True(cancelled);
        Assert.Empty(_store.CreateCalls);
    }

    [Fact]
    public void SuccessfulFormSubmissionReturnsSavedProject()
    {
        ProjectInfo? created = null;
        var form = Render<ProjectForm>(parameters => parameters.Add(component => component.OnSaved, project => created = project));
        form.Find("input").Input("Lumière 光");
        form.Find("textarea").Input("A quiet forest.");
        form.Find("form").Submit();
        form.WaitForAssertion(() => Assert.NotNull(created));
        Assert.Equal("Lumière 光", created!.Name);
        Assert.Equal("A quiet forest.", Assert.Single(_store.CreateCalls).Description);
    }

    [Fact]
    public async Task PendingSaveDisablesControlsAndPreventsDuplicateSubmission()
    {
        var pending = new TaskCompletionSource<ProjectInfo>();
        _store.Create = _ => pending.Task;
        var form = Render<ProjectForm>();
        form.Find("input").Input("One project");
        var submission = form.Find("form").SubmitAsync();
        form.WaitForAssertion(() => Assert.True(form.Find("button[type=submit]").HasAttribute("disabled")));
        Assert.True(form.Find("input").HasAttribute("disabled"));
        Assert.True(form.Find("button[type=button]").HasAttribute("disabled"));
        await form.Find("form").SubmitAsync();
        Assert.Single(_store.CreateCalls);
        pending.SetResult(FakeProjectStore.Project());
        await submission;
    }

    [Fact]
    public void FailedSaveRetainsInputAndAllowsRetry()
    {
        _store.Create = _ => throw new ProjectStoreException("Disk is full.");
        var form = Render<ProjectForm>();
        form.Find("input").Input("Keep my idea");
        form.Find("textarea").Input("Keep these notes too.");
        form.Find("form").Submit();
        Assert.Contains("Disk is full.", form.Find("[role=alert]").TextContent);
        Assert.Equal("Keep my idea", form.Find("input").GetAttribute("value"));
        Assert.Contains("Keep these notes too.", form.Find("textarea").TextContent);
        Assert.False(form.Find("button[type=submit]").HasAttribute("disabled"));
        _store.Create = request => Task.FromResult(FakeProjectStore.Project(request.Name));
        form.Find("form").Submit();
        Assert.Equal(2, _store.CreateCalls.Count);
    }

    [Fact]
    public void DialogCreationNavigatesToSavedProject()
    {
        var project = FakeProjectStore.Project("My film");
        _store.Create = _ => Task.FromResult(project);
        var dialogs = Render<MudDialogProvider>();
        var page = Render<Home>();
        page.Find("button").Click();
        dialogs.WaitForElement("input").Input("My film");
        dialogs.Find("form").Submit();
        page.WaitForAssertion(() => Assert.EndsWith($"/projects/{project.Id:D}", Services.GetRequiredService<NavigationManager>().Uri));
        Assert.Single(_store.CreateCalls);
    }

    [Fact]
    public void DialogCancellationStaysOnLibraryAndAllowsReopening()
    {
        var dialogs = Render<MudDialogProvider>();
        var page = Render<Home>();
        page.Find("button").Click();
        dialogs.WaitForElement(".mud-dialog-actions button[type=button]").Click();
        page.WaitForAssertion(() => Assert.False(page.Find("button").HasAttribute("disabled")));
        Assert.Empty(_store.CreateCalls);
        Assert.Equal("http://localhost/", Services.GetRequiredService<NavigationManager>().Uri);
        page.Find("button").Click();
        Assert.NotNull(dialogs.WaitForElement("input"));
    }

    [Fact]
    public void MissingProjectOffersReturnToLibrary()
    {
        var page = Render<ProjectHome>(parameters => parameters.Add(component => component.Id, Guid.NewGuid()));
        Assert.Contains("Project not found", page.Find("h1").TextContent);
        Assert.NotEmpty(page.FindAll("a[href='/']"));
    }

    [Fact]
    public void ProjectHomeShowsSavedMetadataAndFutureAreas()
    {
        var project = FakeProjectStore.Project("A forest", "First line\nSecond line");
        _store.Get = _ => Task.FromResult<ProjectInfo?>(project);
        var page = Render<ProjectHome>(parameters => parameters.Add(component => component.Id, project.Id));
        Assert.Equal(project.Name, page.Find("h1").TextContent);
        Assert.Contains("Second line", page.Find(".project-synopsis").TextContent);
        Assert.Equal(3, page.FindAll(".project-areas article").Count);
        Assert.Empty(page.FindAll($"a[href='/projects/{project.Id:D}/production']"));
        Assert.Equal(project.CreatedUtc.ToString("O"), page.Find("time").GetAttribute("datetime"));
        Assert.NotNull(page.Find($"a[href='/projects/{project.Id:D}/assets']"));
        Assert.NotNull(_navigation.Find($"a[href='/projects/{project.Id:D}/settings']"));
    }

    [Fact]
    public void ProjectSettingsShowsVisibilityAndEditsDetailsWithoutLosingTheFilterDraft()
    {
        var project = FakeProjectStore.Project("Garden", "A quiet world.");
        _store.Get = _ => Task.FromResult<ProjectInfo?>(project);
        var dialogs = Render<MudDialogProvider>();
        var page = Render<ProjectSettings>(p => p.Add(c => c.Id, project.Id));
        Assert.Equal("page", _navigation.Find(".project-tabs a[href$='/settings']").GetAttribute("aria-current"));
        Assert.Equal("Project settings", page.Find("h1").TextContent);
        page.Find("#lora-hidden-tags").Input("nsfw");
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Edit project").Click();
        dialogs.WaitForElement("input").Input("New project title"); dialogs.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("New project title", page.Find(".project-settings-name").TextContent));
        Assert.Equal("nsfw", page.Find("#lora-hidden-tags").GetAttribute("value"));
        Assert.Empty(((FakeProjectAiPreferencesStore)Services.GetRequiredService<IProjectAiPreferencesStore>()).Values);
    }

    [Fact]
    public void CompactProjectListsWhatItFindsAndRemovesOnlyTheChosenParts()
    {
        var project = FakeProjectStore.Project("Garden", "A quiet world.");
        _store.Get = _ => Task.FromResult<ProjectInfo?>(project);
        _compaction.Plan = new(project.Id, [], [], 0, [Guid.NewGuid()], 1200L * 1024 * 1024, ["production-before-global-setups.json"], 1024 * 1024, @"C:\episode\manifest.json", 1024);
        var dialogs = Render<MudDialogProvider>();
        var page = Render<ProjectSettings>(p => p.Add(c => c.Id, project.Id));
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Compact project…").Click();
        dialogs.WaitForAssertion(() => Assert.Contains($"Lossless reel archives (1 reel video) · {StorageSize.Format(1200L * 1024 * 1024)}", dialogs.Markup));
        Assert.DoesNotContain("take archives", dialogs.Markup); Assert.DoesNotContain("Trash (", dialogs.Markup);
        Assert.True(dialogs.Find("input[data-part=PackageManifest]").HasAttribute("checked"));
        dialogs.Find("input[data-part=Backups]").Change(false);
        Assert.Contains($"Frees about {StorageSize.Format(1200L * 1024 * 1024 + 1024)}", dialogs.Markup);
        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Remove permanently").Click();
        page.WaitForAssertion(() => Assert.Contains("Compacted. Freed", page.Markup));
        Assert.Equal(new[] { lumibelle.Services.Projects.CompactionPart.ReelArchives, lumibelle.Services.Projects.CompactionPart.PackageManifest }.ToHashSet(), _compaction.Compacted!.ToHashSet());
    }

    [Fact]
    public void ProjectReadErrorIsDistinctFromMissingProject()
    {
        _store.Get = _ => throw new ProjectStoreException("Manifest unreadable.");
        var page = Render<ProjectHome>(parameters => parameters.Add(component => component.Id, Guid.NewGuid()));
        Assert.Contains("Manifest unreadable.", page.Find("[role=alert]").TextContent);
        Assert.DoesNotContain("Project not found", page.Markup);
    }

    [Fact]
    public void EditingPrefillsDetailsAndUpdatesTheOverviewWithoutCreatingAProject()
    {
        var original = FakeProjectStore.Project("Juniper finds a key", "Original description");
        _store.Get = _ => Task.FromResult<ProjectInfo?>(original);
        var dialogs = Render<MudDialogProvider>();
        var page = Render<ProjectHome>(parameters => parameters.Add(component => component.Id, original.Id));
        page.Find("button").Click();
        Assert.Equal(original.Name, dialogs.WaitForElement("input").GetAttribute("value"));
        Assert.Contains(original.Description!, dialogs.Find("textarea").TextContent);
        dialogs.Find("input").Input("New title 光");
        dialogs.Find("textarea").Input("First line\nSecond line");
        dialogs.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Equal("New title 光", page.Find("h1").TextContent));
        Assert.Contains("New title 光", _identity.Find(".project-breadcrumb").TextContent);
        Assert.Equal("First line\nSecond line", page.Find(".project-synopsis").TextContent);
        Assert.Equal(original, Assert.Single(_store.UpdateCalls).Original);
        Assert.Empty(_store.CreateCalls);
        Assert.Equal("Juniper finds a key", original.Name);
        Assert.NotNull(page.Find($"a[href='/projects/{original.Id:D}/script']"));
    }

    [Fact]
    public void CancellingEditLeavesDetailsUntouchedAndReopeningUsesSavedValues()
    {
        var original = FakeProjectStore.Project("Original", "Saved description");
        _store.Get = _ => Task.FromResult<ProjectInfo?>(original);
        var dialogs = Render<MudDialogProvider>();
        var page = Render<ProjectHome>(parameters => parameters.Add(component => component.Id, original.Id));
        page.Find("button").Click();
        dialogs.WaitForElement("input").Input("Discard this");
        dialogs.Find("textarea").Input("Discard this too");
        dialogs.Find(".mud-dialog-actions button[type=button]").Click();
        Assert.Empty(_store.UpdateCalls);
        Assert.Equal(original.Name, page.Find("h1").TextContent);
        page.Find("button").Click();
        Assert.Equal(original.Name, dialogs.WaitForElement("input").GetAttribute("value"));
        Assert.Contains(original.Description!, dialogs.Find("textarea").TextContent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EditRequiresANameBeforeCallingStore(string name)
    {
        var form = Render<ProjectForm>(parameters => parameters.Add(component => component.Project, FakeProjectStore.Project()));
        form.Find("input").Input(name);
        form.Find("form").Submit();
        Assert.Contains("Give your project a name.", form.Markup);
        Assert.Empty(_store.UpdateCalls);
    }

    [Fact]
    public async Task EditSavePreventsDuplicateRequestsAndKeepsFailedDraftForRetry()
    {
        var pending = new TaskCompletionSource<ProjectInfo>();
        _store.Update = (_, _) => pending.Task;
        ProjectInfo? saved = null;
        var original = FakeProjectStore.Project("Original", "Original description");
        var form = Render<ProjectForm>(parameters => parameters
            .Add(component => component.Project, original)
            .Add(component => component.OnSaved, project => saved = project));
        form.Find("input").Input("Keep this title");
        form.Find("textarea").Input("");
        var saving = form.Find("form").SubmitAsync();
        form.WaitForAssertion(() => Assert.True(form.Find("button[type=submit]").HasAttribute("disabled")));
        Assert.True(form.Find("button[type=button]").HasAttribute("disabled"));
        Assert.True(form.Find("input").HasAttribute("disabled"));
        await form.Find("form").SubmitAsync();
        Assert.Single(_store.UpdateCalls);
        pending.SetException(new ProjectStoreException("Disk is full."));
        await saving;
        Assert.Contains("Disk is full.", form.Find("[role=alert]").TextContent);
        Assert.Equal("Keep this title", form.Find("input").GetAttribute("value"));
        Assert.Null(saved);
        _store.Update = (project, request) => Task.FromResult(project with { Name = request.Name, Description = null });
        form.Find("form").Submit();
        form.WaitForAssertion(() => Assert.NotNull(saved));
        Assert.Equal("Keep this title", saved!.Name);
        Assert.Null(saved.Description);
        Assert.Equal(2, _store.UpdateCalls.Count);
    }
}
