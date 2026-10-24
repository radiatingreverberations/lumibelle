using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;
using TestContext = Xunit.TestContext;

namespace Lumibelle.Tests;

public sealed class VllmTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("http://llm-server:8000", "http://llm-server:8000/v1")]
    [InlineData(" http://LLM-SERVER:8000/v1/ ", "http://llm-server:8000/v1")]
    [InlineData("https://llm-server.example/inference/v1/", "https://llm-server.example/inference/v1")]
    public void ServerAddressesNormalizeWithoutLosingProxyPaths(string input, string expected) =>
        Assert.Equal(expected, VllmConnection.NormalizeUrl(input));

    [Theory]
    [InlineData("file:///tmp/server")]
    [InlineData("http://user:secret@llm-server:8000")]
    [InlineData("http://llm-server:8000/v1?key=secret")]
    [InlineData("http://llm-server:8000/v1#fragment")]
    [InlineData("")]
    public void InvalidServerAddressesAreRejected(string url) =>
        Assert.Throws<WorkspaceStoreException>(() => FileAiSettingsStore.Validate(new() { VllmUrl = url }));

    [Theory]
    [InlineData(null)]
    [InlineData("llm-server-test-key")]
    public async Task DiscoveryReadsOnlyModelsAndDoesNotRequireOpenRouter(string? key)
    {
        var handler = new ScriptedHttpHandler((request, _) =>
        {
            Assert.Equal("http://llm-server:8000/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(key, request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                """{"data":[{"id":"local/qwen","max_model_len":32768}]}""") });
        });
        var store = new FakeAiSettingsStore { Key = null };
        var check = await new AiProviderRegistry(new TestHttpFactory(handler), store, TestComfy.Monitor())
            .CheckAsync(AiBackend.Vllm, new() { VllmUrl = "http://llm-server:8000" }, key, _ct);
        Assert.True(check.Success);
        var model = Assert.Single(check.Models);
        Assert.Equal("local/qwen", model.Id); Assert.Equal(model.Id, model.Name);
        Assert.Equal(32768, model.Catalog!.ContextLength); Assert.False(model.SupportsImages);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"id\":\"\"}]}")]
    public async Task EmptyOrMalformedCatalogIsNotAUsableConnection(string json)
    {
        var http = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));
        var check = await new AiProviderRegistry(new TestHttpFactory(http), new FakeAiSettingsStore(), TestComfy.Monitor()).CheckAsync(AiBackend.Vllm, new(), cancellationToken: _ct);
        Assert.False(check.Success); Assert.Empty(check.Models);
    }

    [Theory]
    [InlineData("{\"id\":\"local/model\"}")]
    [InlineData("{\"id\":\"local/model\",\"max_model_len\":null}")]
    public async Task DiscoveryAcceptsModelsWithoutAContextLimit(string model)
    {
        var http = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"data\":[" + model + "]}") }));
        var check = await new AiProviderRegistry(new TestHttpFactory(http), new FakeAiSettingsStore(), TestComfy.Monitor())
            .CheckAsync(AiBackend.Vllm, new(), cancellationToken: _ct);
        Assert.True(check.Success); Assert.Null(Assert.Single(check.Models).Catalog!.ContextLength);
    }

    [Fact]
    public async Task StreamsFromConfiguredServerWithProfileAndKeepsReasoningOutOfAnswerAndBilling()
    {
        var stream = """
            data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"local/qwen","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"Thinking"},"finish_reason":null}]}

            data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"local/qwen","choices":[{"index":0,"delta":{"content":"Hello world"},"finish_reason":"stop"}]}

            data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"local/qwen","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}

            data: [DONE]


            """;
        var handler = new ScriptedHttpHandler((request, _) =>
        {
            Assert.Equal("http://llm-server:8000/proxy/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.NotEqual("test-only-key", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") });
        });
        var settings = new AiSettings { VllmUrl = "http://llm-server:8000/proxy/v1" };
        var model = TextModelPolicy.Reference(AiBackend.Vllm, "local/qwen", "Qwen", settings) with { Temperature = .3f, MaxOutputTokens = 512 };
        using var client = await new AiProviderRegistry(new TestHttpFactory(handler), new FakeAiSettingsStore(), TestComfy.Monitor()).CreateAsync(AiBackend.Vllm, model.Model, settings, _ct);
        var updates = new List<ProgressingChatUpdate>();
        await foreach (var update in ((IProgressReportingChatClient)client).GetStreamingResponseWithProgressAsync(
            [new ChatMessage(ChatRole.User, "Say hello")], TextGenerationOptions.Create(AiBackend.Vllm, settings, selection: model), _ct)) updates.Add(update);
        Assert.Equal("Hello world", string.Concat(updates.Select(u => u.Response?.Text)));
        Assert.Contains(updates, u => u.Activity); Assert.All(updates, u => Assert.Null(u.OpenRouterUsage));
        Assert.Contains(updates, u => u.Response?.FinishReason == ChatFinishReason.Stop);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("local/qwen", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(.3f, body.RootElement.GetProperty("temperature").GetSingle());
        Assert.Equal(512, body.RootElement.TryGetProperty("max_completion_tokens", out var tokens) ? tokens.GetInt32() : body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public async Task FailedGenerationIsNeverAutomaticallyRetried()
    {
        var handler = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":{\"message\":\"unavailable\"}}") }));
        using var client = await new AiProviderRegistry(new TestHttpFactory(handler), new FakeAiSettingsStore(), TestComfy.Monitor()).CreateAsync(AiBackend.Vllm, "local/qwen", new(), _ct);
        await Assert.ThrowsAsync<System.ClientModel.ClientResultException>(async () =>
        { await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Hello")], cancellationToken: _ct)) { } });
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void SavedChoicesKeepTheirServerAndUseServerDefaults()
    {
        var settings = new AiSettings { VllmUrl = "http://llm-server:8000" };
        var model = TextModelPolicy.Reference(AiBackend.Vllm, "local/qwen", "Qwen", settings);
        var other = model with { VllmUrl = "http://other:8000/v1" };
        Assert.False(TextModelPolicy.Same(model, other));
        Assert.Equal("http://llm-server:8000/v1", TextModelPolicy.Normalize(TextModelProfiles.ModelOnly(model)).VllmUrl);
        Assert.Throws<AiGenerationException>(() => TextModelPolicy.CheckRequestServer(other, settings));
        Assert.Contains("different OpenAI-compatible connection", TextModelPolicy.Issue(other, settings, null));
        var options = TextGenerationOptions.Create(AiBackend.Vllm, settings, selection: model);
        Assert.Null(options.Temperature); Assert.Null(options.MaxOutputTokens); Assert.Null(options.RawRepresentationFactory);
        var captured = settings with { DefaultBackend = AiBackend.Vllm, TextDefault = model };
        Assert.Equal(model, TextModelPolicy.Default(captured));
    }
}

public sealed partial class AiSettingsStoreTests
{
    [Fact]
    public async Task VllmKeysAreProtectedIndependentAndBoundToTheirServer()
    {
        var ct = TestContext.Current.CancellationToken;
        var initial = await Store.SaveAsync(new(), "openrouter-secret", cancellationToken: ct);
        var saved = await Store.SaveVllmAsync(initial with { VllmUrl = "http://llm-server:8000", VllmConcurrency = 2 }, "llm-server-secret", cancellationToken: ct);
        Assert.True(saved.HasVllmKey); Assert.Equal("http://llm-server:8000/v1", saved.VllmUrl);
        Assert.Equal("llm-server-secret", await Store.ReadVllmKeyAsync(saved.VllmUrl, ct));
        Assert.Null(await Store.ReadVllmKeyAsync("http://other:8000", ct));
        Assert.Equal("openrouter-secret", await Store.ReadOpenRouterKeyAsync(ct));
        Assert.DoesNotContain("llm-server-secret", await File.ReadAllTextAsync(Path.Combine(_root, "App_Data", "ai-settings.json"), ct));
        Assert.DoesNotContain("llm-server-secret", JsonSerializer.Serialize(saved));
        saved = await Store.SaveAsync(saved with { TimeoutSeconds = 600 }, cancellationToken: ct);
        Assert.True((await Store.LoadAsync(ct)).HasVllmKey);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.SaveVllmAsync(initial, "stale-secret", cancellationToken: ct));
        saved = await Store.SaveVllmAsync(saved, "replacement-secret", cancellationToken: ct);
        Assert.Equal("replacement-secret", await Store.ReadVllmKeyAsync(saved.VllmUrl, ct));
        saved = await Store.SaveVllmAsync(saved with { VllmUrl = "http://other:8000" }, cancellationToken: ct);
        Assert.False(saved.HasVllmKey); Assert.Null(await Store.ReadVllmKeyAsync(saved.VllmUrl, ct));
        saved = await Store.SaveVllmAsync(saved, "new-server-key", cancellationToken: ct);
        saved = await Store.SaveVllmAsync(saved, removeKey: true, cancellationToken: ct);
        Assert.False(saved.HasVllmKey); Assert.Equal("openrouter-secret", await Store.ReadOpenRouterKeyAsync(ct));
    }
}

public sealed partial class AiTextJobTests
{
    [Fact]
    public async Task InterruptedVllmRequestRequiresExplicitRetryWithoutReconnecting()
    {
        _model = TextModelPolicy.Reference(AiBackend.Vllm, "test/model", "Local model", _settings.Value);
        var request = await Request(AiJobKind.ScriptAssistant);
        var context = await Claim(request);
        _providers.OnCheck = () => throw new InvalidOperationException("Recovery must not reconnect");
        var outcome = await Handler().RecoverAsync(await Recover(context), request.Snapshot, _ct);
        Assert.Equal(AiJobState.NeedsAttention, outcome.State);
        Assert.Equal(AiJobRecovery.GenerateAgain, outcome.Recovery);
        Assert.Equal(0, _providers.Created);
    }

    [Theory]
    [InlineData(AiJobKind.ScriptAssistant)]
    [InlineData(AiJobKind.AssetExtraction)]
    [InlineData(AiJobKind.ShotPlanning)]
    [InlineData(AiJobKind.PromptEnhancement)]
    [InlineData(AiJobKind.Guidance)]
    public async Task VllmTextJobsCaptureServerAndRecoverWithoutGeneratingAgain(AiJobKind kind)
    {
        _settings.Value = _settings.Value with { VllmUrl = "http://llm-server:8000/v1" };
        _model = TextModelPolicy.Reference(AiBackend.Vllm, "test/model", "Local model", _settings.Value);
        _settings.Value.VllmTextModels[TextModelPolicy.Key(_model)] = new(.3f, 512);
        var request = await Request(kind);
        var captured = request.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        _settings.Value.VllmTextModels[TextModelPolicy.Key(_model)] = new(.9f, 2048);
        _settings.Value = _settings.Value with { VllmUrl = "http://other:8000/v1" };
        Assert.Equal(3, captured.Version);
        Assert.Equal(.3f, captured.Model.Temperature); Assert.Equal(512, captured.Model.MaxOutputTokens);
        Assert.Equal("http://llm-server:8000/v1", captured.Settings.VllmUrl);
        Assert.Equal(captured.Model.VllmUrl, captured.Settings.VllmUrl);
        _providers.Chat.Output = Response(kind); var context = await Claim(request);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, request.Snapshot, _ct)).State);
        Assert.Equal(.3f, _providers.Chat.Options!.Temperature); Assert.Equal(512, _providers.Chat.Options.MaxOutputTokens);
        Assert.Null((await Store.ReadArtifactAsync<AiTextJobResult>(request.Id, AiJobArtifact.Result, _ct))!.Error);
        var saved = await context.ReadOperationAsync<AiTextJobResult>("text", AiOperationArtifact.Output, _ct);
        Assert.True(saved!.Complete);
        _providers.Created = 0;
        Assert.Equal(AiJobState.Completed, (await Handler().RecoverAsync(await Recover(context), request.Snapshot, _ct)).State);
        Assert.Equal(0, _providers.Created);
    }
}

public sealed partial class AiJobCoordinatorTests
{
    [Fact]
    public async Task VllmQueueDispatchesAndPausesIndependently()
    {
        var local = await Store.EnqueueAsync(Request(AiBackend.Vllm), _ct);
        var remote = await Store.EnqueueAsync(Request(AiBackend.OpenRouter), _ct);
        await Store.SetPausedAsync(AiBackend.Vllm, true, _ct);
        var handler = new FakeHandler(); var queue = await Start(handler);
        var first = await handler.Next(_ct); Assert.Equal(remote.Id, first.Context.Job.Id);
        Assert.Equal(AiJobState.Waiting, (await Store.ReadAsync(_ct)).Jobs.Single(j => j.Id == local.Id).State);
        await queue.SetPausedAsync(AiBackend.Vllm, false, _ct);
        var second = await handler.Next(_ct); Assert.Equal(local.Id, second.Context.Job.Id);
        await first.Complete(); await second.Complete();
        await State(local.Id, j => j.State == AiJobState.Completed);
    }
}

public sealed partial class AiSettingsComponentTests
{
    [Fact]
    public async Task VllmConnectionAndModelSelectionSaveWithoutOtherDrafts()
    {
        var page = Render<AiSettingsPage>();
        page.Find("#comfy-url").Change("http://unsaved:8188");
        await page.Find("#ai-provider-vllm").ClickAsync(new());
        page.Find("#openai-url").Change("http://llm-server:8000");
        page.Find("#openai-key").Change("local-secret");
        page.Find("#openai-concurrency").Change("2");
        await page.Find("#openai-connection-form").SubmitAsync();
        page.WaitForAssertion(() => Assert.Equal("http://llm-server:8000/v1", Assert.Single(_settings.Value.OpenAiConnections!).Url));
        Assert.Equal("http://127.0.0.1:8188", _settings.Value.ComfyUrl);
        Assert.Equal(2, Assert.Single(_settings.Value.OpenAiConnections!).Concurrency); Assert.True(Assert.Single(_settings.Value.OpenAiConnections!).HasApiKey);
        Assert.Equal("", page.Find("#openai-key").GetAttribute("value"));
        _providers.Models = [new("local/qwen", "Local Qwen")];
        await page.Find("#ai-tab-text").ClickAsync(new());
        await page.Find("button[aria-label='Refresh OpenAI-compatible models']").ClickAsync(new());
        await page.Find("button[aria-label='Star Local Qwen']").ClickAsync(new());
        var model = Assert.Single(_settings.Value.StarredTextModels);
        Assert.Equal(AiBackend.Vllm, model.Backend); Assert.Equal(Assert.Single(_settings.Value.OpenAiConnections!).Url, model.VllmUrl);
        await page.Find("#ai-text-provider-defaults").ClickAsync(new());
        await page.Find("#global-text-default").ChangeAsync(new() { Value = TextModelProfiles.ChoiceKey(model) });
        page.WaitForAssertion(() => Assert.Equal(model, _settings.Value.TextDefault));
        Assert.Equal(AiBackend.Vllm, _settings.Value.DefaultBackend);
    }
}
