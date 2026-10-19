using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiSettingsStoreTests
{
    [Fact]
    public async Task OpenRouterBenchmarksPersistPerExactModelAndPreserveAliasesCredentialsAndDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var sample = new TextModelBenchmark(Guid.NewGuid(), "provider/model", now, 256, false, 2, .2, 12, 42, null, null,
            .000000001m, "provider/model", "Test provider", "test-generation", "stop", true);
        var aliases = new Dictionary<string, string> { [lumibelle.Services.AI.TextModelPolicy.Key(new(AiBackend.OpenRouter, sample.Model, "Model"))] = "Model alias" };
        var settings = new AiSettings { OpenRouterTextModelBenchmarks = Enumerable.Range(0, 7)
            .Select(i => sample with { TestId = Guid.NewGuid(), MeasuredUtc = now.AddMinutes(i) }).Append(sample with { TestId = Guid.NewGuid(), Model = "Provider/Model" }).ToList(), TextModelAliases = aliases };
        var saved = await Store.SaveAsync(settings, "protected-key", cancellationToken: ct);
        var loaded = await Store.LoadAsync(ct);
        Assert.Equal(6, loaded.OpenRouterTextModelBenchmarks.Count); Assert.Equal(5, loaded.OpenRouterTextModelBenchmarks.Count(b => b.Model == sample.Model));
        Assert.All(loaded.OpenRouterTextModelBenchmarks, b => Assert.Equal(.000000001m, b.Cost));
        Assert.Equal(aliases, loaded.TextModelAliases); Assert.Equal(settings.DefaultBackend, loaded.DefaultBackend);
        Assert.Equal("protected-key", await Store.ReadOpenRouterKeyAsync(ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.SaveAsync(saved with { OpenRouterTextModelBenchmarks = [sample with { Cost = -1 }] }, cancellationToken: ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.SaveAsync(saved with { OpenRouterTextModelBenchmarks = [sample with { ElapsedSeconds = double.NaN }] }, cancellationToken: ct));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.SaveAsync(settings, cancellationToken: ct));
        var path = Path.Combine(_root, "App_Data", "ai-settings.json");
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
        legacy["settings"]!.AsObject().Remove("openRouterTextModelBenchmarks");
        await File.WriteAllTextAsync(path, legacy.ToJsonString(), ct);
        Assert.Empty((await Store.LoadAsync(ct)).OpenRouterTextModelBenchmarks);
    }
}
