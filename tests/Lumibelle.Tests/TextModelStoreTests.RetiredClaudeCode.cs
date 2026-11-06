using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

// Claude Code was removed as a provider. What an earlier build saved must keep loading and show as unavailable.
public sealed partial class TextModelStoreTests
{
    private const string ClaudeProfile = """{"backend":"ClaudeCode","model":"opus","name":"Careful","profileId":"5d0c1f0e-6a51-4c0b-9a55-0d6a7f1f2b11","reasoningEffort":"max"}""";
    private AiProviderRegistry Registry => new(new TestHttpFactory(new HttpClientHandler()), Settings, TestComfy.Monitor());

    private async Task<string> SaveClaudeSettingsAsync(bool profileDefault, CancellationToken ct)
    {
        await Settings.SaveAsync(new() { StarredTextModels = [Cloud] }, cancellationToken: ct);
        var path = Path.Combine(_root, "App_Data", "ai-settings.json");
        var file = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
        var settings = file["settings"]!.AsObject();
        settings["claudeCode"] = JsonNode.Parse("""{"enabled":true,"executablePath":"C:/tools/claude.exe","concurrency":2,"textModel":"opus","textEffort":"xhigh"}""");
        settings["defaultBackend"] = "ClaudeCode";
        settings["starredTextModels"]!.AsArray().Add(JsonNode.Parse("""{"backend":"ClaudeCode","model":"sonnet","name":"Sonnet"}"""));
        settings["textModelProfiles"] = new JsonArray(JsonNode.Parse(ClaudeProfile));
        if (profileDefault) settings["textDefault"] = JsonNode.Parse(ClaudeProfile);
        await File.WriteAllTextAsync(path, file.ToJsonString(), ct);
        return path;
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SettingsSavedWithClaudeCodeLoadAndReportItUnavailable(bool profileDefault)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await SaveClaudeSettingsAsync(profileDefault, ct);
        var loaded = await Settings.LoadAsync(ct);

        var chosen = TextModelPolicy.Default(loaded);
        Assert.Equal(AiBackend.ClaudeCode, chosen.Backend);
        Assert.Equal(profileDefault ? "opus" : "", chosen.Model);
        Assert.Equal(TextModelPolicy.RetiredIssue, TextModelPolicy.ConfigurationIssue(chosen, loaded));
        // Not even a successful check of that name makes it selectable again.
        Assert.Equal(TextModelPolicy.RetiredIssue, TextModelPolicy.Issue(chosen, loaded, new(true, "Connected.", [new("opus", "Opus")])));

        var profile = Assert.Single(loaded.TextModelProfiles);
        Assert.Equal((AiBackend.ClaudeCode, "max"), (profile.Backend, profile.ReasoningEffort));
        Assert.Equal(TextModelPolicy.RetiredIssue, TextModelPolicy.ConfigurationIssue(profile, loaded));
        // Its stars are dropped: no page is left to unstar them on.
        Assert.Equal([Cloud], loaded.StarredTextModels);

        // Saving still works, and the provider's own section is not written again.
        var saved = await Settings.SaveAsync(loaded with { DefaultBackend = AiBackend.OpenRouter, OpenRouterModel = Cloud.Model, TextDefault = null, TextModelProfiles = [] }, cancellationToken: ct);
        Assert.Equal(AiBackend.OpenRouter, TextModelPolicy.Default(saved).Backend);
        Assert.DoesNotContain("claudeCode", await File.ReadAllTextAsync(path, ct), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AProjectDefaultSavedForClaudeCodeLoadsAsUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await Projects.CreateAsync(new("First"), ct);
        var old = new TextModelReference(AiBackend.ClaudeCode, "opus", "Opus", ReasoningEffort: "xhigh");
        await Preferences.SetTextDefaultAsync(project.Id, old, 0, ct);

        var session = new TextModelSession(project.Id, Settings, Preferences, Registry);
        await session.RefreshAsync(allProviders: true, ct);
        Assert.Equal(TextModelSelectionSource.ProjectDefault, session.Source);
        Assert.Equal(old, session.Model);
        Assert.Equal(TextModelPolicy.RetiredIssue, session.Issue);
        Assert.Equal(TextModelPolicy.RetiredIssue, session.ChoiceIssueFor(old));
        Assert.False(session.State.Ready);
        Assert.Equal(TextModelPolicy.RetiredIssue, session.State.UnavailableReason);
    }

    [Fact]
    public async Task TheRemovedProviderCannotBeCheckedOrAsked()
    {
        var ct = TestContext.Current.CancellationToken;
        var check = await Registry.CheckAsync(AiBackend.ClaudeCode, new(), cancellationToken: ct);
        Assert.False(check.Success); Assert.Empty(check.Models);
        Assert.Equal(TextModelPolicy.RetiredIssue, check.Message);
        foreach (var model in new[] { "opus", "" })
            Assert.Equal(TextModelPolicy.RetiredIssue, (await Assert.ThrowsAsync<AiGenerationException>(() => Registry.CreateAsync(AiBackend.ClaudeCode, model, new(), ct))).Message);
    }
}
