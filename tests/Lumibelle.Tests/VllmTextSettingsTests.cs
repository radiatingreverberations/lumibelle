using System.Text.Json;
using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class VllmTextSettingsTests
{
    private static readonly TextModelReference Model = new(AiBackend.Vllm, "test/model", "Local model") { VllmUrl = "http://llm-server:8000/v1" };

    [Fact]
    public void ModelDefaultsStayWithTheirServerAndProfilesOverrideEachField()
    {
        var settings = JsonSerializer.Deserialize<AiSettings>("{}", AtomicJsonFile.Options)!;
        Assert.Equal(new(), VllmTextSettings.Resolve(Model, settings));
        settings.VllmTextModels[TextModelPolicy.Key(Model)] = new(.4f, 1024);
        FileAiSettingsStore.Validate(settings);
        Assert.Equal(new(.4f, 1024), VllmTextSettings.Resolve(Model with { Name = "Alias", VllmUrl = "http://LLM-SERVER:8000/" }, settings));
        Assert.Equal(new(), VllmTextSettings.Resolve(Model with { VllmUrl = "http://other:8000/v1" }, settings));
        Assert.Equal(new(), VllmTextSettings.Resolve(Model with { Model = "different/model" }, settings));
        var options = TextGenerationOptions.Create(AiBackend.Vllm, settings, selection: Model);
        Assert.Equal(.4f, options.Temperature); Assert.Equal(1024, options.MaxOutputTokens);
        var profile = Model with { ProfileId = Guid.NewGuid(), Temperature = 0 };
        options = TextGenerationOptions.Create(AiBackend.Vllm, settings, selection: profile);
        Assert.Equal(0f, options.Temperature); Assert.Equal(1024, options.MaxOutputTokens);
        options = TextGenerationOptions.Create(AiBackend.Vllm, settings, selection: profile with { Temperature = null, MaxOutputTokens = 2048 });
        Assert.Equal(.4f, options.Temperature); Assert.Equal(2048, options.MaxOutputTokens);
        settings.VllmTextModels.Clear();
        options = TextGenerationOptions.Create(AiBackend.Vllm, settings, selection: Model);
        Assert.Null(options.Temperature); Assert.Null(options.MaxOutputTokens);
    }

    [Theory]
    [InlineData(-.1f, 100)]
    [InlineData(2.1f, 100)]
    [InlineData(float.NaN, 100)]
    [InlineData(.5f, 0)]
    [InlineData(.5f, 1000001)]
    public void InvalidSavedControlsAreRejected(float temperature, int tokens) =>
        Assert.Throws<WorkspaceStoreException>(() => FileAiSettingsStore.Validate(new()
            { VllmTextModels = new() { [TextModelPolicy.Key(Model)] = new(temperature, tokens) } }));

    [Fact]
    public void InvalidAndUnnormalizedKeysAreRejected()
    {
        foreach (var key in new[] { "model", "ComfyUI\ntest/model\nhttp://server:8188", "Vllm\ntest/model\nhttp://LLM-SERVER:8000", "Vllm\ntest/model\nfile:///tmp" })
            Assert.Throws<WorkspaceStoreException>(() => FileAiSettingsStore.Validate(new() { VllmTextModels = new() { [key] = new(.4f, 512) } }));
    }

    [Fact]
    public async Task WritingTestsCaptureResolvedSettingsAndSeparateComparisonsAfterEdits()
    {
        using var f = new ContentProbeFixture();
        f.Settings.Value = f.Settings.Value with { VllmUrl = Model.VllmUrl!, VllmTextModels = new() { [TextModelPolicy.Key(Model)] = new(.4f, 1024) } };
        var test = ContentProbeBuiltIns.All(await f.Probes.LoadLibraryAsync(f.Ct)).First();
        async Task<ContentProbeRequest> Capture() => Assert.Single(await f.Capture.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), [test.Id], [Model], 1, 0, 0, f.Ct))
            .Snapshot.Deserialize<ContentProbeRequest>(AtomicJsonFile.Options)!;
        var before = await Capture();
        f.Settings.Value.VllmTextModels[TextModelPolicy.Key(Model)] = new(.9f, 2048);
        var after = await Capture();
        Assert.Equal(.4f, before.Model.Temperature); Assert.Equal(1024, before.Model.MaxOutputTokens);
        Assert.Equal(.9f, after.Model.Temperature);
        Assert.NotEqual(ContentProbePolicy.ComparisonKey(before), ContentProbePolicy.ComparisonKey(after));
        Assert.Equal(0, f.Providers.Creates);
    }
}

public sealed partial class AiSettingsStoreTests
{
    [Fact]
    public async Task VllmBenchmarksPersistSeparatelyForEachServerAndRejectInvalidMeasurements()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var measured = new TextModelBenchmark(Guid.NewGuid(), "test/model", DateTimeOffset.UtcNow, 2048, false, 2, .2,
            12, 42, null, null, null, "test/model", null, "test-generation", "stop", true);
        var sample = new VllmTextModelBenchmark("http://llm-server:8000/v1", .7f, measured) { ImageInput = new(DateTimeOffset.UtcNow, true, true, "Both images were identified.") };
        var list = Enumerable.Range(0, 7).Select(i => sample with { Measurement = measured with { TestId = Guid.NewGuid(), MeasuredUtc = measured.MeasuredUtc.AddMinutes(i) } }).ToList();
        list.Add(sample with { Server = "http://other:8000/v1" });
        var saved = await Store.SaveAsync(new() { VllmTextModelBenchmarks = list }, cancellationToken: ct);
        var loaded = await Store.LoadAsync(ct);
        Assert.Equal(6, loaded.VllmTextModelBenchmarks.Count); Assert.Empty(loaded.OpenRouterTextModelBenchmarks);
        Assert.Single(loaded.VllmTextModelBenchmarks, b => b.Server == "http://other:8000/v1");
        Assert.All(loaded.VllmTextModelBenchmarks, b => Assert.Equal(sample.ImageInput, b.ImageInput));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.SaveAsync(saved with { VllmTextModelBenchmarks = [sample with { Server = "file:///private" }] }, cancellationToken: ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.SaveAsync(saved with { VllmTextModelBenchmarks = [sample with { Temperature = float.NaN }] }, cancellationToken: ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.SaveAsync(saved with { VllmTextModelBenchmarks = [sample with { ImageInput = sample.ImageInput with { Supported = null } }] }, cancellationToken: ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.SaveAsync(saved with { VllmTextModelBenchmarks = [sample with { Measurement = measured with { OutputTokens = -1 } }] }, cancellationToken: ct));
    }

    [Fact]
    public async Task VllmModelSettingsPersistAndResetIndependentlyOfConnections()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var settings = await Store.LoadAsync(ct);
        var model = TextModelPolicy.Reference(AiBackend.Vllm, "local/model", "Local model", settings);
        var key = TextModelPolicy.Key(model);
        settings = await Store.SaveAsync(settings with { VllmTextModels = new() { [key] = new(0, 1024) } }, cancellationToken: ct);
        Assert.Equal(new(0, 1024), (await Store.LoadAsync(ct)).VllmTextModels[key]);
        var changed = await Store.SaveVllmAsync(settings with { VllmUrl = "http://other:8000" }, cancellationToken: ct);
        Assert.Equal(new(0, 1024), changed.VllmTextModels[key]);
        Assert.Equal(new(), VllmTextSettings.Resolve(model with { VllmUrl = changed.VllmUrl }, changed));
        await Store.SaveAsync(changed with { VllmTextModels = [] }, cancellationToken: ct);
        Assert.Empty((await Store.LoadAsync(ct)).VllmTextModels);
    }
}

public sealed partial class AiSettingsComponentTests
{
    [Fact]
    public async Task VllmModelControlsPreserveDraftsOnConflictAndSaveOnlyTheirModel()
    {
        _providers.Models = [new("local/model", "Local model")];
        var page = Render<AiSettingsPage>();
        await page.Find("#ai-tab-text").ClickAsync(new());
        await page.Find("#ai-text-provider-vllm").ClickAsync(new());
        await page.Find(".text-model-expand").ClickAsync(new());
        page.Find(".model-generation-settings input[id$='-tokens']").Change("1024");
        page.Find(".model-generation-settings input[id$='-temperature']").Change("0");
        page.Find(".model-alias-form input").Input("Unsaved alias");
        await page.Find("#ai-text-provider-defaults").ClickAsync(new()); page.Find("#timeout").Change("555");
        await page.Find("#ai-text-provider-vllm").ClickAsync(new());
        _settings.SaveError = new WorkspaceConflictException();
        await page.Find(".model-generation-settings").SubmitAsync();
        Assert.Empty(_settings.Value.VllmTextModels);
        Assert.Equal("1024", page.Find(".model-generation-settings input[id$='-tokens']").GetAttribute("value"));
        _settings.SaveError = null;
        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Reload saved settings").ClickAsync(new());
        await page.Find(".model-generation-settings").SubmitAsync();
        Assert.Equal(new(0, 1024), Assert.Single(_settings.Value.VllmTextModels).Value);
        Assert.Empty(_settings.Value.TextModelAliases); Assert.Equal(300, _settings.Value.TimeoutSeconds);
        page.Find(".model-generation-settings input[id$='-temperature']").Change("1.2");
        await page.Find(".model-generation-settings").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Cancel").ClickAsync(new());
        Assert.Equal("0", page.Find(".model-generation-settings input[id$='-temperature']").GetAttribute("value"));
        await page.Find(".model-generation-settings").QuerySelectorAll("button").Single(b => b.TextContent.Contains("Use server defaults")).ClickAsync(new());
        Assert.Empty(_settings.Value.VllmTextModels);
        Assert.True(string.IsNullOrEmpty(page.Find(".model-generation-settings input[id$='-tokens']").GetAttribute("value")));
    }
}
