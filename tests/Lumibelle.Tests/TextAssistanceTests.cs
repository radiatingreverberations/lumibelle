using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class TextModelStoreTests
{
    [Fact]
    public async Task LegacyStudioModelsDoNotBecomeProjectDefaultsAndOtherPreferencesSurvive()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await Projects.CreateAsync(new("Legacy"), ct);
        var path = Path.Combine(_root, "App_Data", "Projects", project.Id.ToString("D"), "ai-preferences.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new ProjectAiPreferences { SchemaVersion = 1, ProjectId = project.Id,
            Story = Cloud, Shots = Local, LoraVisibility = new() { HiddenTags = ["private"] }, LoraVisibilityRevision = 3 }, AtomicJsonFile.Options), ct);
        var loaded = await Preferences.LoadAsync(project.Id, ct);
        Assert.Equal(2, loaded.SchemaVersion); Assert.Null(loaded.TextDefault); Assert.Equal(0, loaded.TextDefaultRevision);
        Assert.Equal(Cloud, loaded.Story); Assert.Equal(Local, loaded.Shots);
        var saved = await Preferences.SetTextDefaultAsync(project.Id, Cloud, 0, ct);
        Assert.Equal(3, saved.LoraVisibilityRevision); Assert.Equal("private", Assert.Single(saved.LoraVisibility.HiddenTags));
        Assert.Equal(Cloud, (await Preferences.LoadAsync(project.Id, ct)).TextDefault);
    }
    [Fact]
    public async Task ProjectDefaultsAreIndependentAndConcurrentWritesDoNotLoseLoraPreferences()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await Projects.CreateAsync(new("One"), ct); var other = await Projects.CreateAsync(new("Two"), ct);
        await Task.WhenAll(Preferences.SetTextDefaultAsync(project.Id, Cloud, 0, ct), Preferences.SetLoraVisibilityAsync(project.Id, new() { HiddenTags = ["hide"] }, 0, ct));
        var loaded = await Preferences.LoadAsync(project.Id, ct);
        Assert.Equal(Cloud, loaded.TextDefault); Assert.Single(loaded.LoraVisibility.HiddenTags);
        Assert.Null((await Preferences.LoadAsync(other.Id, ct)).TextDefault);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Preferences.SetTextDefaultAsync(project.Id, Local, 0, ct));
        Assert.Null((await Preferences.SetTextDefaultAsync(project.Id, null, 1, ct)).TextDefault);
    }
    [Fact]
    public async Task RequestOverridesAreTemporaryAndOnlyExplicitDefaultChangesPersist()
    {
        var ct = TestContext.Current.CancellationToken;
        var preferences = new FakeProjectAiPreferencesStore(); var id = Guid.NewGuid();
        preferences.Values[id] = new() { ProjectId = id, Story = Local, TextDefault = Cloud };
        var settings = new FakeAiSettingsStore { Value = new() { HasOpenRouterKey = true, DefaultBackend = AiBackend.OpenRouter, OpenRouterModel = "global", StarredTextModels = [Cloud] } };
        var providers = new FakeProviders { Models = [new("global", "Global"), new(Cloud.Model, Cloud.Name), new("alternate", "Alternate")] };
        var session = new TextModelSession(id, settings, preferences, providers);
        await session.RefreshAsync(ct: ct); Assert.Equal(Cloud, session.Model); Assert.Equal(TextModelSelectionSource.ProjectDefault, session.Source);
        session.Select(new(AiBackend.OpenRouter, "alternate", "Alternate")); await session.RefreshAsync(ct: ct);
        Assert.Equal("alternate", session.Model.Model); Assert.Equal(Cloud, preferences.Values[id].TextDefault);
        Assert.Equal(TextModelSelectionSource.RequestOverride, session.Source);
        await session.SubmittedAsync(ct); Assert.Equal(Cloud, session.Model);
        await session.SetDefaultAsync(true, ct); Assert.Equal("global", session.Model.Model); Assert.Equal(TextModelSelectionSource.GlobalDefault, session.Source);
    }
    [Fact]
    public async Task DefaultSaveFailureKeepsTheOverrideAndResolvedChoiceDetectsExternalChanges()
    {
        var ct = TestContext.Current.CancellationToken; var id = Guid.NewGuid();
        var preferences = new FakeProjectAiPreferencesStore(); var settings = new FakeAiSettingsStore(); var providers = new FakeProviders();
        var session = new TextModelSession(id, settings, preferences, providers); await session.RefreshAsync(ct: ct);
        var shown = session.State;
        preferences.Values[id] = new() { ProjectId = id, TextDefault = Cloud, TextDefaultRevision = 1 };
        await session.RefreshAsync(ct: ct); Assert.False(TextModelSession.SameChoice(shown, session.State));
        session.Select(Local); preferences.SaveError = new WorkspaceStoreException("disk unavailable");
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => session.SetDefaultAsync(false, ct));
        Assert.Equal(Local, session.Override); Assert.Equal(Cloud, preferences.Values[id].TextDefault);
    }
    [Theory]
    [InlineData(TextModelSelectionSource.GlobalDefault)]
    [InlineData(TextModelSelectionSource.ProjectDefault)]
    [InlineData(TextModelSelectionSource.RequestOverride)]
    public void CapturedSelectionAttributionPreservesTheExactRequest(TextModelSelectionSource source)
    {
        var payload = JsonSerializer.SerializeToElement(new { text = "Literal <Picture 1>\nÅngström" });
        var request = new AiTextJobRequest(2, AiJobKind.PromptEnhancement, Cloud, source == TextModelSelectionSource.GlobalDefault, new(), "fixture", .5f, 7, payload,
            [new("user", [new(Text: "Exact input")])]);
        var submission = AiJobSubmission.Create(Guid.NewGuid(), request.Kind, Cloud.Backend, new(Guid.NewGuid(), Guid.NewGuid()), "Fixture", "Enhance", Guid.NewGuid(), request);
        var attributed = TextModelSession.Attribute(submission, new(Cloud, true, Source: source));
        var captured = attributed.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(source, captured.SelectionSource); Assert.Equal(request.Messages[0].Parts[0].Text, captured.Messages[0].Parts[0].Text);
        Assert.True(JsonElement.DeepEquals(request.Task, captured.Task)); Assert.Equal(request.Seed, captured.Seed); Assert.Equal(request.Model, captured.Model);
        Assert.Null(request.SelectionSource);
    }
    [Fact]
    public async Task LoadingSavedModelChoicesDoesNotContactProviders()
    {
        var providers = new FakeProviders();
        var settings = new FakeAiSettingsStore { Value = new() { HasOpenRouterKey = true, StarredTextModels = [Cloud, Local] } };
        var session = new TextModelSession(Guid.NewGuid(), settings, new FakeProjectAiPreferencesStore(), providers);
        await session.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, providers.CheckCalls);
        Assert.Contains(Cloud, session.Choices);
        Assert.Null(session.ChoiceIssueFor(Cloud));
        Assert.False(session.State.Ready);
    }
    [Fact]
    public async Task AvailabilityChecksOnlyTheChosenOpenAiConnectionAndPreservesOtherResults()
    {
        var first = new OpenAiConnectionSettings { Name = "One", Url = "http://one:8000/v1" };
        var second = new OpenAiConnectionSettings { Name = "Two", Url = "http://two:8000/v1" };
        var model = new TextModelReference(AiBackend.Vllm, "served", "Served") { VllmUrl = first.Url };
        var other = model with { VllmUrl = second.Url };
        var settings = new FakeAiSettingsStore { Value = new() { TextDefault = model, OpenAiConnections = [first, second], StarredTextModels = [model, other] } };
        var providers = new FakeProviders { Models = [new("served", "Served")] };
        var session = new TextModelSession(Guid.NewGuid(), settings, new FakeProjectAiPreferencesStore(), providers);
        await session.RefreshAsync(ct: TestContext.Current.CancellationToken);
        Assert.Single(session.Checks[AiBackend.Vllm].Connections!);
        Assert.True(session.HasAvailability(model));
        Assert.False(session.HasAvailability(other));
        Assert.Null(session.ChoiceIssueFor(other));
        session.Select(other);
        await session.RefreshAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, providers.CheckCalls);
        Assert.True(session.State.Ready);
        session.Select(model);
        Assert.True(session.State.Ready);
        settings.Value = settings.Value with { OpenAiConnections = [first with { SupportsImages = true }, second] };
        await session.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Empty(session.Checks);
        Assert.Equal(2, providers.CheckCalls);
    }
    [Fact]
    public async Task MissingSelectedModelNeverFallsBackToAnAvailableModel()
    {
        var preferences = new FakeProjectAiPreferencesStore(); var id = Guid.NewGuid();
        preferences.Values[id] = new() { ProjectId = id, TextDefault = Cloud };
        var session = new TextModelSession(id, new FakeAiSettingsStore(), preferences, new FakeProviders { Models = [new("different", "Available")] });
        await session.RefreshAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(Cloud, session.Model); Assert.False(session.State.Ready); Assert.Contains("no longer available", session.Issue);
        Assert.Null(session.ChoiceIssueFor(Cloud)); // A saved choice can be picked again to recheck it.
    }
}
