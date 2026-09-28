using Bunit;
using lumibelle.Components.AI;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class TextModelProfileReasoningOffComponentTests : BunitContext
{
    private static readonly TextModelReference Model = new(AiBackend.OpenRouter, "deepseek/deepseek-v4-flash", "DeepSeek V4 Flash");
    private static AiModelCatalogInfo Info(bool mandatory) => new(SupportsReasoning: true)
    {
        SupportedParameters = ["reasoning", "reasoning_effort", "temperature", "max_tokens"],
        SupportedReasoningEfforts = ["xhigh", "high"], ReasoningMandatory = mandatory
    };

    private IRenderedComponent<TextModelProfilesPanel> Editor(AiModelCatalogInfo info,
        Action<IReadOnlyList<TextModelReference>> saved, TextModelReference? existing = null)
    {
        var settings = new AiSettings { TextModelProfiles = existing is null ? [] : [existing] };
        Func<TextModelReference, AiModel?> catalog = model => new(model.Model, model.Name, Catalog: info);
        Func<IReadOnlyList<TextModelReference>, Task<bool>> save = profiles =>
        {
            // Exercise the same validation the production settings store uses.
            TextModelProfiles.ValidateSettings(settings with { TextModelProfiles = profiles.ToList() });
            saved(profiles);
            return Task.FromResult(true);
        };
        var component = Render<TextModelProfilesPanel>(p => p
            .Add(c => c.SettingsSnapshot, settings)
            .Add(c => c.Models, new[] { Model })
            .Add(c => c.CatalogModel, catalog)
            .Add(c => c.SaveProfiles, save));
        component.Find("#text-profiles-toggle").Click();
        if (existing is null) component.Find("#new-text-profile").Click();
        else component.Find("[data-action='edit']").Click();
        return component;
    }

    [Fact]
    public void ProfileEditorOffersAndSavesOffAlongsideHighAndXhigh()
    {
        IReadOnlyList<TextModelReference>? saved = null;
        var editor = Editor(Info(false), profiles => saved = profiles);
        editor.Find("#profile-name").Change("DeepSeek Flash (no thinking)");
        editor.Find("#profile-reasoning").Change("effort");
        Assert.Equal(new[] { "", "none", "xhigh", "high" },
            editor.FindAll("#profile-effort option").Select(o => o.GetAttribute("value")));
        Assert.Contains("reasoning off", editor.Find("#profile-effort option[value='none']").TextContent);
        editor.Find("#profile-effort").Change("none");
        editor.Find("#text-profile-form").Submit();
        Assert.NotNull(saved);
        var profile = Assert.Single(saved!);
        Assert.Equal("none", profile.ReasoningEffort);
        Assert.Equal(Model.Model, profile.Model);
        Assert.NotNull(profile.ProfileId);
        Assert.Null(profile.ReasoningMaxTokens);
        Assert.Null(profile.Temperature);
        Assert.Null(profile.MaxOutputTokens);
        Assert.Empty(editor.FindAll("[role='alert']"));
    }

    [Fact]
    public void MandatoryReasoningProfileHidesOffAndRejectsAForcedOffSubmission()
    {
        var saves = 0;
        var editor = Editor(Info(true), _ => saves++);
        editor.Find("#profile-name").Change("Invalid off");
        editor.Find("#profile-reasoning").Change("effort");
        Assert.Empty(editor.FindAll("#profile-effort option[value='none']"));
        editor.Find("#profile-effort").Change("none"); // Simulate a stale or tampered form.
        editor.Find("#text-profile-form").Submit();
        Assert.Equal(0, saves);
        Assert.Contains("requires reasoning", editor.Find("[role='alert']").TextContent);
        Assert.Equal("Invalid off", editor.Find("#profile-name").GetAttribute("value"));
    }

    [Fact]
    public void SwitchingProfileFromBudgetToOffClearsTheBudget()
    {
        IReadOnlyList<TextModelReference>? saved = null;
        var editor = Editor(Info(false) with { SupportsReasoningBudget = true }, profiles => saved = profiles);
        editor.Find("#profile-name").Change("No thinking");
        editor.Find("#profile-output").Change("4096");
        editor.Find("#profile-reasoning").Change("budget");
        editor.Find("#profile-budget").Change("1024");
        editor.Find("#profile-reasoning").Change("effort");
        editor.Find("#profile-effort").Change("none");
        editor.Find("#text-profile-form").Submit();
        Assert.NotNull(saved);
        var profile = Assert.Single(saved!);
        Assert.Equal("none", profile.ReasoningEffort);
        Assert.Null(profile.ReasoningMaxTokens);
        Assert.Equal(4096, profile.MaxOutputTokens);
    }

    [Fact]
    public void ExistingOffProfileCanBeEditedBackToProviderDefault()
    {
        var original = Model with { ProfileId = Guid.NewGuid(), Name = "No thinking", ReasoningEffort = "none" };
        IReadOnlyList<TextModelReference>? saved = null;
        var editor = Editor(Info(false), profiles => saved = profiles, original);
        Assert.Single(editor.FindAll("#profile-effort option[value='none']"));
        editor.Find("#profile-reasoning").Change("default");
        editor.Find("#text-profile-form").Submit();
        Assert.NotNull(saved);
        var profile = Assert.Single(saved!);
        Assert.Equal(original.ProfileId, profile.ProfileId);
        Assert.Null(profile.ReasoningEffort);
        Assert.Null(profile.ReasoningMaxTokens);
    }
}
