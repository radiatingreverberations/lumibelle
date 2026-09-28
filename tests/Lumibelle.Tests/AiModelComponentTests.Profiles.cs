using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiModelComponentTests
{
    [Fact]
    public void ProfileEditorCreatesAndDuplicatesConfigurationsWithoutChangingProviderSettings()
    {
        AvailableModels();
        var originalTemperature = _settings.Value.Temperature;
        var page = Render<AiSettingsPage>();
        page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-openrouter").Click();
        page.Find("#ai-text-provider-defaults").Click(); page.Find("#text-profiles-toggle").Click(); page.Find("#new-text-profile").Click();
        page.Find("#profile-model").Change(TextModelPolicy.Key(Cloud));
        page.Find("#profile-name").Change("Careful"); page.Find("#profile-temperature").Change("0");
        page.Find("#profile-output").Change("4096"); page.Find("#profile-reasoning").Change("effort");
        page.Find("#profile-effort").Change("high"); page.Find("#text-profile-form").Submit();
        var first = Assert.Single(_settings.Value.TextModelProfiles);
        Assert.Equal(Cloud.Model, first.Model); Assert.Equal(0f, first.Temperature); Assert.Equal("high", first.ReasoningEffort);
        Assert.Equal(originalTemperature, _settings.Value.Temperature); Assert.Null(_settings.Value.TextDefault);
        page.Find($"[data-profile-id='{first.ProfileId}'] [data-action='duplicate']").Click();
        Assert.Equal("Careful copy", page.Find("#profile-name").GetAttribute("value"));
        page.Find("#profile-name").Change("Fast"); page.Find("#profile-effort").Change("none");
        page.Find("#text-profile-form").Submit();
        Assert.Equal(2, _settings.Value.TextModelProfiles.Count);
        var second = _settings.Value.TextModelProfiles.Single(p => p.Name == "Fast");
        Assert.NotEqual(first.ProfileId, second.ProfileId); Assert.Equal(first.Model, second.Model); Assert.Equal("none", second.ReasoningEffort);
        page.Find("#global-text-default").Change(TextModelProfiles.ChoiceKey(second));
        Assert.Equal(second, TextModelPolicy.Default(_settings.Value));
    }

    [Fact]
    public void FailedProfileSavesKeepDraftsAndRejectIncompatibleBudgets()
    {
        AvailableModels();
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-openrouter").Click();
        page.Find("#ai-text-provider-defaults").Click(); page.Find("#text-profiles-toggle").Click(); page.Find("#new-text-profile").Click();
        page.Find("#profile-model").Change(TextModelPolicy.Key(Cloud)); page.Find("#profile-name").Change("Budget");
        page.Find("#profile-output").Change("4096"); page.Find("#profile-reasoning").Change("budget");
        page.Find("#profile-budget").Change("4096"); page.Find("#text-profile-form").Submit();
        Assert.Empty(_settings.Value.TextModelProfiles); Assert.Contains("exceed the thinking budget", page.Markup);
        page.Find("#profile-budget").Change("1024"); _settings.SaveError = new WorkspaceConflictException();
        page.Find("#text-profile-form").Submit();
        Assert.Empty(_settings.Value.TextModelProfiles); Assert.Contains("Another tab", page.Markup);
        Assert.Equal("Budget", page.Find("#profile-name").GetAttribute("value"));
        Assert.Equal("1024", page.Find("#profile-budget").GetAttribute("value"));
        _settings.SaveError = null; page.Find("#text-profile-form").Submit();
        var saved = Assert.Single(_settings.Value.TextModelProfiles);
        Assert.Equal(1024, saved.ReasoningMaxTokens); Assert.Null(saved.ReasoningEffort);
    }

    [Fact]
    public void RemovingAProfileKeepsTheGlobalDefaultSnapshot()
    {
        AvailableModels();
        var profile = Cloud with { ProfileId = Guid.NewGuid(), Name = "Kept snapshot", Temperature = .8f };
        _settings.Value = _settings.Value with { TextModelProfiles = [profile], TextDefault = profile };
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-defaults").Click(); page.Find("#text-profiles-toggle").Click();
        page.Find($"[data-profile-id='{profile.ProfileId}'] [data-action='remove']").Click();
        Assert.Single(_settings.Value.TextModelProfiles);
        page.Find("#confirm-remove-profile").Click();
        Assert.Empty(_settings.Value.TextModelProfiles); Assert.Equal(profile, TextModelPolicy.Default(_settings.Value));
        Assert.Contains("Kept snapshot", page.Find("#global-text-default").TextContent);
        page.Find("#global-text-default").Change(TextModelPolicy.Key(Cloud));
        Assert.Null(_settings.Value.TextDefault); Assert.Equal(Cloud.Model, TextModelPolicy.Default(_settings.Value).Model);
    }

    [Fact]
    public async Task PickersKeepProfilesSeparateAndProjectDefaultsRemainSnapshots()
    {
        AvailableModels(); var id = Guid.NewGuid(); var states = new List<TextModelSelectionState>();
        var first = Cloud with { ProfileId = Guid.NewGuid(), Name = "Careful", ReasoningEffort = "high" };
        var second = Cloud with { ProfileId = Guid.NewGuid(), Name = "Fast", ReasoningEffort = "none" };
        _settings.Value = _settings.Value with { TextModelProfiles = [first, second] };
        var picker = Picker(id, TextAssistantStudio.Story, states); var dialog = PickerDialog(picker);
        var select = dialog.Find("select[id^='text-model-']");
        select.Change(TextModelProfiles.ChoiceKey(first)); Assert.Equal(first, states.Last().Model);
        await picker.InvokeAsync(() => Button(dialog, "Set as project default").ClickAsync(new()));
        dialog.WaitForAssertion(() => Assert.Empty(dialog.FindAll("select[id^='text-model-']")));
        Assert.Equal(first, _preferences.Values[id].TextDefault);
        var edited = first with { ReasoningEffort = "low", Name = "Careful updated" };
        _settings.Value = _settings.Value with { TextModelProfiles = [edited, second] };
        dialog = PickerDialog(picker);
        Assert.Equal(first, states.Last().Model);
        dialog.WaitForAssertion(() =>
        {
            var options = dialog.FindAll("option").Select(o => o.GetAttribute("value")).ToArray();
            Assert.Contains(TextModelProfiles.ChoiceKey(first), options); Assert.Contains(TextModelProfiles.ChoiceKey(edited), options);
            Assert.Contains(TextModelProfiles.ChoiceKey(second), options);
        }, BunitDefaults.WaitTimeout(5));
        dialog.Find("select[id^='text-model-']").Change(TextModelProfiles.ChoiceKey(edited));
        Assert.Equal(edited, states.Last().Model); Assert.Equal(first, _preferences.Values[id].TextDefault);
        Assert.True(await picker.InvokeAsync(picker.Instance.PrepareSubmitAsync));
    }
}
