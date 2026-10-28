using System.Net;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed class OpenAiImageInputTests
{
    [Fact]
    public async Task MissingMetadataIsUnknownAndSavedEvidenceIsScopedToTheExactServerAndModel()
    {
        var server = OpenAiConnectionTests.First;
        var measured = new TextModelBenchmark(Guid.NewGuid(), "test/model", DateTimeOffset.UtcNow, 2048, false, 1, .1,
            12, 40, null, null, null, "test/model", null, null, "stop", true);
        var benchmark = new VllmTextModelBenchmark(server.Url, null, measured) { ImageInput = new(DateTimeOffset.UtcNow, true, true, "Confirmed") };
        using var http = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("""{"data":[{"id":"test/model"},{"id":"other/model"}]}""") }));
        var registry = new AiProviderRegistry(new TestHttpFactory(http), new FakeAiSettingsStore(), TestComfy.Monitor());
        var settings = new AiSettings { OpenAiConnections = [server, OpenAiConnectionTests.Second] };
        var before = await registry.CheckAsync(AiBackend.Vllm, settings, cancellationToken: TestContext.Current.CancellationToken);
        Assert.All(before.Models, m => { Assert.False(m.SupportsImages); Assert.False(m.ImageInputKnown); });
        var after = await registry.CheckAsync(AiBackend.Vllm, settings with { VllmTextModelBenchmarks = [benchmark] }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(after.Models, m => m.SupportsImages && m.ImageInputKnown && m.ServerUrl == server.Url && m.Id == "test/model");
        Assert.All(after.Models.Where(m => m.Id != "test/model" || m.ServerUrl != server.Url), m => Assert.False(m.SupportsImages));
        var overridden = await registry.CheckAsync(AiBackend.Vllm, settings with { OpenAiConnections = [server with { SupportsImages = false }], VllmTextModelBenchmarks = [benchmark] }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.All(overridden.Models, m => { Assert.False(m.SupportsImages); Assert.True(m.ImageInputKnown); });
        var rejected = await registry.CheckAsync(AiBackend.Vllm, settings with { VllmTextModelBenchmarks = [benchmark with { ImageInput = benchmark.ImageInput with { Supported = false, MultipleImages = false } }] }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(rejected.Models.Single(m => m.Id == "test/model" && m.ServerUrl == server.Url).ImageInputKnown);
        Assert.False(rejected.Models.Single(m => m.Id == "test/model" && m.ServerUrl == server.Url).SupportsImages);
        Assert.All(http.Requests, r => Assert.Equal("GET", r.Method));
    }
}
