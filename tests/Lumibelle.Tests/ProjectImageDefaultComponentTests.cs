using Bunit;
using lumibelle.Components;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class ProjectImageDefaultComponentTests : BunitContext
{
    private readonly FakeProjectAiPreferencesStore _preferences = new();
    private readonly Guid _project = Guid.NewGuid();
    private readonly IRenderedComponent<MudDialogProvider> _dialogs;
    public ProjectImageDefaultComponentTests()
    {
        Services.AddMudServices(); JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IProjectAiPreferencesStore>(_preferences);
        Services.AddSingleton<IAiSettingsStore>(new FakeAiSettingsStore());
        _dialogs = Render<MudDialogProvider>();
    }
    private void Save() => _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Save default").Click();

    [Fact]
    public void ImageDefaultCancelDoesNotSaveAndFailedSaveRetainsChoiceForRetry()
    {
        ImageWorkflow? changed = null;
        var picker = Render<ProjectImageDefaultPicker>(p => p.Add(c => c.ProjectId, _project).Add(c => c.Changed, w => changed = w));
        picker.Find("button").Click();
        _dialogs.WaitForElement("select").Change("Flux2Klein9bKv");
        _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Close").Click();
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll("select")));
        Assert.Empty(_preferences.Values); Assert.Null(changed);
        picker.Find("button").Click();
        _dialogs.WaitForElement("select").Change("Flux2Klein9bKv");
        _preferences.SaveError = new WorkspaceStoreException("Disk full"); Save();
        Assert.Contains("Disk full", _dialogs.Find("[role=alert]").TextContent);
        Assert.Equal("Flux2Klein9bKv", _dialogs.Find("select").GetAttribute("value"));
        _preferences.SaveError = null; Save();
        picker.WaitForAssertion(() => Assert.Equal(ImageWorkflow.Flux2Klein9bKv, changed));
        Assert.Equal(ImageWorkflow.Flux2Klein9bKv, _preferences.Values[_project].ImageDefault);
        picker.Find("button").Click(); _dialogs.WaitForElement("select").Change("global"); Save();
        picker.WaitForAssertion(() => Assert.Null(_preferences.Values[_project].ImageDefault));
        Assert.Contains("Global default", picker.Markup);
    }

    [Fact]
    public void ImageDefaultConflictKeepsDraftAndRequiresAnotherExplicitSave()
    {
        var picker = Render<ProjectImageDefaultPicker>(p => p.Add(c => c.ProjectId, _project));
        picker.Find("button").Click(); _dialogs.WaitForElement("select").Change("Flux2Klein9bKv");
        _preferences.Values[_project] = new() { ProjectId = _project, ImageDefault = ImageWorkflow.CodexImages, ImageDefaultRevision = 1 };
        Save(); Assert.Contains("changed in another tab", _dialogs.Find("[role=alert]").TextContent);
        Assert.Equal(ImageWorkflow.CodexImages, _preferences.Values[_project].ImageDefault);
        Assert.Equal("Flux2Klein9bKv", _dialogs.Find("select").GetAttribute("value"));
        Save(); picker.WaitForAssertion(() => Assert.Equal(ImageWorkflow.Flux2Klein9bKv, _preferences.Values[_project].ImageDefault));
    }
}
