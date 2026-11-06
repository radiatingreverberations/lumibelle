using Bunit;
using lumibelle.Components.AI;
using lumibelle.Models;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class RetiredClaudeCodeComponentTests : BunitContext
{
    // Claude Code was removed as a provider. A profile saved for it stays listed so it can be removed, and nothing else.
    [Fact]
    public void AClaudeCodeProfileIsListedAsUnavailableAndCanOnlyBeRemoved()
    {
        var old = new TextModelReference(AiBackend.ClaudeCode, "opus", "Careful", ReasoningEffort: "max") { ProfileId = Guid.NewGuid() };
        var cloud = new TextModelReference(AiBackend.OpenRouter, "test/model", "Cloud model");
        IReadOnlyList<TextModelReference>? saved = null;
        Func<IReadOnlyList<TextModelReference>, Task<bool>> save = profiles => { saved = profiles; return Task.FromResult(true); };
        var panel = Render<TextModelProfilesPanel>(p => p
            .Add(c => c.SettingsSnapshot, new AiSettings { TextModelProfiles = [old] })
            .Add(c => c.Models, new[] { cloud })
            .Add(c => c.SaveProfiles, save));
        panel.Find("#text-profiles-toggle").Click();

        var row = panel.Find($"[data-profile-id='{old.ProfileId}']");
        Assert.Contains("Claude Code is no longer available in Lumibelle. Remove this profile.", row.TextContent);
        Assert.True(row.QuerySelector("[data-action='edit']")!.HasAttribute("disabled"));
        Assert.True(row.QuerySelector("[data-action='duplicate']")!.HasAttribute("disabled"));
        Assert.False(row.QuerySelector("[data-action='remove']")!.HasAttribute("disabled"));

        // A new profile cannot be based on its model.
        panel.Find("#new-text-profile").Click();
        Assert.Equal(["OpenRouter · Cloud model · test/model"], panel.FindAll("#profile-model option").Select(option => option.TextContent));
        panel.FindAll("#text-profile-form button").Single(button => button.TextContent == "Cancel profile edit").Click();

        panel.Find($"[data-profile-id='{old.ProfileId}'] [data-action='remove']").Click();
        panel.Find("#confirm-remove-profile").Click();
        Assert.NotNull(saved); Assert.Empty(saved);
    }
}
