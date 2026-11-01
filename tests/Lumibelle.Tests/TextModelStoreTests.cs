using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumibelle.Tests;
public sealed partial class TextModelStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.Tests", Guid.NewGuid().ToString("D"));
    private StorageTestEnvironment Environment => new(_root);
    private FileProjectStore Projects => new(Options.Create(new ProjectStorageOptions()), Environment, TimeProvider.System, NullLogger<FileProjectStore>.Instance);
    private FileProjectAiPreferencesStore Preferences => new(new ProjectFiles(Options.Create(new ProjectStorageOptions()), Environment, Projects));
    private FileAiSettingsStore Settings => new(Environment, new lumibelle.WebSecretProtector(new EphemeralDataProtectionProvider()));
    private static readonly TextModelReference Local = new(AiBackend.ComfyUI, "qwen.safetensors", "Qwen", "http://localhost:8188");
    private static readonly TextModelReference Cloud = new(AiBackend.OpenRouter, "test/model", "Cloud model");

    [Fact]
    public async Task FavoritesReopenDeduplicatedByProviderModelAndServer()
    {
        var saved = await Settings.SaveAsync(new() { StarredTextModels = [Local, Local with { ComfyUrl = "HTTP://LOCALHOST:8188/" },
            Local with { ComfyUrl = "http://localhost:8189" }, Cloud, Cloud with { Name = "Renamed" }] }, cancellationToken: TestContext.Current.CancellationToken);
        var reopened = await Settings.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, reopened.StarredTextModels.Count);
        Assert.Equal(saved.StarredTextModels, reopened.StarredTextModels);
        Assert.Contains(Local, reopened.StarredTextModels);
    }
    [Fact]
    public async Task InvalidFavoriteDoesNotReplaceSavedSettings()
    {
        var saved = await Settings.SaveAsync(new(), cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Settings.SaveAsync(saved with { StarredTextModels = [Local with { ComfyUrl = "file:///secret" }] }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty((await Settings.LoadAsync(TestContext.Current.CancellationToken)).StarredTextModels);
    }
    [Fact]
    public async Task StudioChoicesAreIndependentPersistAndCanFollowDefaultAgain()
    {
        var project = await Projects.CreateAsync(new("First"), TestContext.Current.CancellationToken);
        var second = await Projects.CreateAsync(new("Second"), TestContext.Current.CancellationToken);
        Assert.Null((await Preferences.LoadAsync(project.Id, TestContext.Current.CancellationToken)).Story);
        var directory = Path.Combine(_root, "App_Data", "Projects", project.Id.ToString("D"));
        Assert.False(File.Exists(Path.Combine(directory, "ai-preferences.json")));
        await Task.WhenAll(Preferences.SetSelectionAsync(project.Id, TextAssistantStudio.Story, Local, TestContext.Current.CancellationToken),
            Preferences.SetSelectionAsync(project.Id, TextAssistantStudio.AssetExtraction, Cloud, TestContext.Current.CancellationToken));
        var reopened = await Preferences.LoadAsync(project.Id, TestContext.Current.CancellationToken);
        Assert.Equal(Local, reopened.Story); Assert.Equal(Cloud, reopened.AssetExtraction);
        Assert.Null((await Preferences.LoadAsync(second.Id, TestContext.Current.CancellationToken)).Story);
        await Preferences.SetSelectionAsync(project.Id, TextAssistantStudio.Story, null, TestContext.Current.CancellationToken);
        reopened = await Preferences.LoadAsync(project.Id, TestContext.Current.CancellationToken);
        Assert.Null(reopened.Story); Assert.Equal(Cloud, reopened.AssetExtraction);
        Assert.False(File.Exists(Path.Combine(directory, "script.json")));
        Assert.False(File.Exists(Path.Combine(directory, "assets.json")));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }
    [Fact]
    public async Task EnhancementChoicePersistsAlongsideConcurrentStudioAndVisibilityUpdates()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await Projects.CreateAsync(new("Enhancement"), ct);
        var second = await Projects.CreateAsync(new("Other"), ct);
        Assert.Null((await Preferences.LoadAsync(project.Id, ct)).PromptEnhancement);
        await Task.WhenAll(Preferences.SetSelectionAsync(project.Id, TextAssistantStudio.PromptEnhancement, Cloud, ct),
            Preferences.SetSelectionAsync(project.Id, TextAssistantStudio.Story, Local, ct),
            Preferences.SetSelectionAsync(project.Id, TextAssistantStudio.AssetExtraction, Local, ct),
            Preferences.SetLoraVisibilityAsync(project.Id, new() { HiddenTags = ["private"] }, 0, ct));
        var loaded = await Preferences.LoadAsync(project.Id, ct);
        Assert.Equal(Cloud, loaded.Selection(TextAssistantStudio.PromptEnhancement));
        Assert.Equal(Local, loaded.Story); Assert.Equal(Local, loaded.AssetExtraction);
        Assert.Equal("private", Assert.Single(loaded.LoraVisibility.HiddenTags));
        Assert.Null((await Preferences.LoadAsync(second.Id, ct)).PromptEnhancement);
        await Preferences.SetSelectionAsync(project.Id, TextAssistantStudio.PromptEnhancement, null, ct);
        loaded = await Preferences.LoadAsync(project.Id, ct);
        Assert.Null(loaded.PromptEnhancement); Assert.Equal(Local, loaded.Story); Assert.Equal(1, loaded.LoraVisibilityRevision);
    }
    [Fact]
    public async Task CorruptPreferencesAndCancelledChangesPreservePreviousFiles()
    {
        var project = await Projects.CreateAsync(new("First"), TestContext.Current.CancellationToken);
        await Preferences.SetSelectionAsync(project.Id, TextAssistantStudio.Story, Local, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Preferences.SetSelectionAsync(project.Id, TextAssistantStudio.Story, Cloud, cancellation.Token));
        Assert.Equal(Local, (await Preferences.LoadAsync(project.Id, TestContext.Current.CancellationToken)).Story);
        var directory = Path.Combine(_root, "App_Data", "Projects", project.Id.ToString("D"));
        TestProjectDatabase.Corrupt(directory, "ai_preferences");
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Preferences.SetSelectionAsync(project.Id, TextAssistantStudio.Story, Cloud, TestContext.Current.CancellationToken));
        Assert.Equal("invalid", TestProjectDatabase.Header(directory, "ai_preferences"));
    }
    [Fact]
    public void EligibilityRequiresExactVersionServerAndAvailableModel()
    {
        var settings = new AiSettings { ComfyUrl = Local.ComfyUrl!, ComfyTextModelVerifications = [new(Local.ComfyUrl!, "v1", Local.Model, DateTimeOffset.UtcNow)] };
        var check = new AiConnectionCheck(true, "Connected", [new(Local.Model, Local.Name)], "v1");
        Assert.Null(TextModelPolicy.Issue(Local, settings, check));
        Assert.Contains("Test", TextModelPolicy.Issue(Local, settings, check with { BackendVersion = "v2" }));
        Assert.Contains("different", TextModelPolicy.Issue(Local with { ComfyUrl = "http://localhost:8189" }, settings, check));
        Assert.Contains("no longer available", TextModelPolicy.Issue(Local, settings, check with { Models = [] }));
        Assert.Contains("Refresh", TextModelPolicy.Issue(Local, settings, null));
        Assert.Contains("key", TextModelPolicy.Issue(Cloud, settings, check));
        Assert.Throws<AiGenerationException>(() => TextModelPolicy.CheckRequestServer(Local, settings with { ComfyUrl = "http://localhost:8189" }));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
