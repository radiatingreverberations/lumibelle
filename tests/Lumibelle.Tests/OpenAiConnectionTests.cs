using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed class OpenAiConnectionTests
{
    internal static OpenAiConnectionSettings Connection(string name, string url, int concurrency = 1) =>
        new() { Name = name, Url = url, Concurrency = concurrency, Kind = OpenAiServerKind.Standard };
    internal static readonly OpenAiConnectionSettings First = Connection("Studio", "http://llm-server:8000/v1");
    internal static readonly OpenAiConnectionSettings Second = Connection("Local", "http://localhost:8080/v1");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DiscoveryKeepsIdenticalModelIdsDistinctAndRefreshesRuntimeCapabilities()
    {
        var context = 32768;
        var handler = new ScriptedHttpHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.Host == "localhost"
                ? JsonSerializer.Serialize(new { data = new[] { new { id = "shared/model", meta = new { n_ctx = context }, architecture = new { input_modalities = new[] { "text", "image" } } } } })
                : """{"data":[{"id":"shared/model","max_model_len":131072}]}""")
        }));
        var settings = new AiSettings { OpenAiConnections = [First, Second] };
        var registry = new AiProviderRegistry(new TestHttpFactory(handler), new FakeAiSettingsStore(), TestComfy.Monitor());
        var firstCheck = await registry.CheckAsync(AiBackend.Vllm, settings, cancellationToken: Ct);
        Assert.True(firstCheck.Success); Assert.Equal(2, firstCheck.Models.Count);
        var selected = TextModelPolicy.Reference(AiBackend.Vllm, firstCheck.Models.Single(m => m.ServerUrl == Second.Url), settings);
        Assert.Equal(Second.Url, selected.VllmUrl);
        Assert.Equal(32768, TextModelPolicy.CatalogModel(selected, firstCheck)!.Catalog!.ContextLength);
        Assert.True(TextModelPolicy.CatalogModel(selected, firstCheck)!.SupportsImages);
        Assert.Contains("Local", TextModelPolicy.PickerLabel(selected, settings, [selected], _ => null));
        context = 65536;
        var refreshed = await registry.CheckAsync(AiBackend.Vllm, settings, cancellationToken: Ct);
        Assert.Equal(65536, TextModelPolicy.CatalogModel(selected, refreshed)!.Catalog!.ContextLength);
        Assert.Null(Second.ContextLength); // Discovery never persists a runtime limit as an override.
        Assert.All(handler.Requests, r => Assert.Equal("GET", r.Method));
        var captured = OpenAiConnections.Capture(selected, settings);
        Assert.Equal(Second.Url, captured.VllmUrl); Assert.Equal(Second.Id, Assert.Single(captured.OpenAiConnections!).Id);
    }

    [Fact]
    public async Task UnreachableConnectionDoesNotHideAnotherServersModels()
    {
        var handler = new ScriptedHttpHandler((request, _) => request.RequestUri!.Host == "llm-server"
            ? throw new HttpRequestException("Offline") : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"data":[{"id":"local/model"}]}""") }));
        var settings = new AiSettings { OpenAiConnections = [First, Second] };
        var check = await new AiProviderRegistry(new TestHttpFactory(handler), new FakeAiSettingsStore(), TestComfy.Monitor())
            .CheckAsync(AiBackend.Vllm, settings, cancellationToken: Ct);
        Assert.True(check.Success); Assert.Equal(Second.Url, Assert.Single(check.Models).ServerUrl);
        Assert.False(check.Connections!.Single(c => c.Server == First.Url).Success);
        Assert.NotNull(TextModelPolicy.Issue(new(AiBackend.Vllm, "local/model", "Model") { VllmUrl = First.Url }, settings, check));
    }

    [Theory]
    [InlineData(true, OpenAiServerKind.Strata)]
    [InlineData(false, OpenAiServerKind.Standard)]
    public async Task AutoDetectionUsesOptionalMetadataWithoutGeneratingText(bool strata, OpenAiServerKind expected)
    {
        var connection = Second with { Kind = OpenAiServerKind.Auto };
        var handler = new ScriptedHttpHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/v1/models" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":"local/model","meta":{"n_ctx":65536}}]}""") },
            "/v1/status" when strata => new(HttpStatusCode.OK) { Content = new StringContent("""{"structured_output":{"method":"prompt_and_validate","formats":["json_schema"]}}""") },
            _ => new(HttpStatusCode.NotFound)
        }));
        var settings = new AiSettings { OpenAiConnections = [connection] };
        var check = await new AiProviderRegistry(new TestHttpFactory(handler), new FakeAiSettingsStore(), TestComfy.Monitor())
            .CheckAsync(AiBackend.Vllm, settings, cancellationToken: Ct);
        Assert.True(check.Success); Assert.Equal(expected, Assert.Single(check.Connections!).Kind);
        Assert.Equal(expected, OpenAiConnections.Kind(Assert.Single(OpenAiConnections.WithDiscovery(settings, check).OpenAiConnections!)));
        Assert.All(handler.Requests, r => Assert.Equal("GET", r.Method));
    }

    [Fact]
    public void DisabledOrRemovedConnectionCannotSilentlyMoveASelection()
    {
        var model = new TextModelReference(AiBackend.Vllm, "shared/model", "Model") { VllmUrl = First.Url };
        Assert.Throws<AiGenerationException>(() => OpenAiConnections.Capture(model, new() { OpenAiConnections = [Second] }));
        Assert.Throws<AiGenerationException>(() => OpenAiConnections.Capture(model, new() { OpenAiConnections = [First with { Enabled = false }, Second] }));
        Assert.Throws<WorkspaceStoreException>(() => OpenAiConnections.Validate(new() { OpenAiConnections = [First, Second with { Url = First.Url + "/" }] }));
        Assert.Throws<WorkspaceStoreException>(() => OpenAiConnections.Validate(new() { OpenAiConnections = [First, Second with { Id = First.Id }] }));
    }

    [Fact]
    public async Task CapturedStrataSchemaRequestsKeepTheirBufferedStreamingBehavior()
    {
        var handler = new ScriptedHttpHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"answer\\\":\\\"ready\\\"}\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", System.Text.Encoding.UTF8, "text/event-stream") }));
        var connection = Second with { Kind = OpenAiServerKind.Strata };
        var settings = OpenAiConnections.Scope(new(), connection);
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}""");
        using var client = await new AiProviderRegistry(new TestHttpFactory(handler), new FakeAiSettingsStore(), TestComfy.Monitor())
            .CreateAsync(AiBackend.Vllm, "local/model", settings, Ct);
        var options = new ChatOptions { MaxOutputTokens = 512, ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement, "response") };
        var updates = new List<ProgressingChatUpdate>();
        await foreach (var update in ((IProgressReportingChatClient)client).GetStreamingResponseWithProgressAsync([new(ChatRole.User, "Say ready.")], options, Ct)) updates.Add(update);
        Assert.Contains(updates, u => u.Progress is { LiveUpdatesAvailable: false } && u.Progress.Label.Contains("validate"));
        Assert.Equal("{\"answer\":\"ready\"}", string.Concat(updates.Select(u => u.Response?.Text)));
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal("json_schema", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.False(body.RootElement.TryGetProperty("structured_outputs", out _));
    }

    [Fact]
    public async Task StrataStreamsNormalResponsesWithoutRequestingServerValidation()
    {
        var handler = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"answer\\\":\"},\"finish_reason\":null}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"\\\"ready\\\"}\"},\"finish_reason\":null}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", System.Text.Encoding.UTF8, "text/event-stream") }));
        var settings = OpenAiConnections.Scope(new(), Second with { Kind = OpenAiServerKind.Strata });
        using var client = await new AiProviderRegistry(new TestHttpFactory(handler), new FakeAiSettingsStore(), TestComfy.Monitor())
            .CreateAsync(AiBackend.Vllm, "local/model", settings, Ct);
        var updates = new List<ProgressingChatUpdate>();
        await foreach (var update in ((IProgressReportingChatClient)client).GetStreamingResponseWithProgressAsync([new(ChatRole.User, "Return a JSON answer.")], new() { MaxOutputTokens = 512 }, Ct)) updates.Add(update);
        Assert.Equal(2, updates.Count(u => u.Response?.Text is { Length: > 0 }));
        Assert.Equal("{\"answer\":\"ready\"}", string.Concat(updates.Select(u => u.Response?.Text)));
        Assert.DoesNotContain(updates, u => u.Progress?.LiveUpdatesAvailable == false);
        Assert.Null(updates.First(u => u.Response?.Text is { Length: > 0 }).Response!.FinishReason);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        Assert.False(body.RootElement.TryGetProperty("structured_outputs", out _));
    }
}

public sealed partial class AiSettingsStoreTests
{
    [Fact]
    public async Task LegacySettingsMigrateWithoutChangingKeysDefaultsOrModelIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        var initial = await Store.SaveVllmAsync(new() { VllmUrl = "http://llm-server:8000", VllmConcurrency = 2 }, "legacy-secret", cancellationToken: ct);
        var model = TextModelPolicy.Reference(AiBackend.Vllm, "local/model", "Model", initial);
        initial = await Store.SaveAsync(initial with { TextDefault = model, StarredTextModels = [model] }, cancellationToken: ct);
        var path = Path.Combine(_root, "App_Data", "ai-settings.json");
        var legacy = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
        legacy["settings"]!.AsObject().Remove("openAiConnections"); legacy.AsObject().Remove("protectedOpenAiKeys");
        await File.WriteAllTextAsync(path, legacy.ToJsonString(), ct);
        var loaded = await Store.LoadAsync(ct); var connection = Assert.Single(loaded.OpenAiConnections!);
        Assert.Equal(OpenAiConnections.LegacyId, connection.Id); Assert.Equal(OpenAiServerKind.Vllm, connection.Kind);
        Assert.Equal(2, connection.Concurrency); Assert.True(connection.HasApiKey);
        Assert.Equal(model, loaded.TextDefault); Assert.Equal(model, Assert.Single(loaded.StarredTextModels));
        Assert.Equal("legacy-secret", await Store.ReadVllmKeyAsync(connection.Url, ct));
        await Store.SaveAsync(loaded, cancellationToken: ct);
        Assert.DoesNotContain("legacy-secret", await File.ReadAllTextAsync(path, ct));
    }

    [Fact]
    public async Task ConnectionSecretsAreIndependentAndStayBoundToTheirEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = OpenAiConnectionTests.First; var b = OpenAiConnectionTests.Second;
        var saved = await Store.SaveAsync(new() { OpenAiConnections = [a, b] }, "hosted-secret", cancellationToken: ct);
        saved = await Store.SaveOpenAiConnectionAsync(saved, a.Id, "first-secret", cancellationToken: ct);
        saved = await Store.SaveOpenAiConnectionAsync(saved, b.Id, "second-secret", cancellationToken: ct);
        Assert.Equal("first-secret", await Store.ReadVllmKeyAsync(a.Url, ct));
        Assert.Equal("second-secret", await Store.ReadVllmKeyAsync(b.Url, ct));
        a = a with { Name = "Renamed" };
        saved = await Store.SaveOpenAiConnectionAsync(saved with { OpenAiConnections = [a, b] }, a.Id, cancellationToken: ct);
        Assert.Equal("first-secret", await Store.ReadVllmKeyAsync(a.Url, ct));
        var oldUrl = a.Url; a = a with { Url = "http://other-server:8000/v1" };
        saved = await Store.SaveOpenAiConnectionAsync(saved with { OpenAiConnections = [a, b] }, a.Id, cancellationToken: ct);
        Assert.Null(await Store.ReadVllmKeyAsync(oldUrl, ct)); Assert.Null(await Store.ReadVllmKeyAsync(a.Url, ct));
        Assert.False(saved.OpenAiConnections!.Single(c => c.Id == a.Id).HasApiKey);
        Assert.Equal("second-secret", await Store.ReadVllmKeyAsync(b.Url, ct));
        saved = await Store.SaveOpenAiConnectionAsync(saved, b.Id, removeKey: true, cancellationToken: ct);
        Assert.Null(await Store.ReadVllmKeyAsync(b.Url, ct)); Assert.Equal("hosted-secret", await Store.ReadOpenRouterKeyAsync(ct));
        var json = await File.ReadAllTextAsync(Path.Combine(_root, "App_Data", "ai-settings.json"), ct);
        Assert.DoesNotContain("first-secret", json); Assert.DoesNotContain("second-secret", json);
    }
}

public sealed partial class AiJobStoreTests
{
    private static AiJobSubmission ConnectionRequest(OpenAiConnectionSettings connection) => AiJobSubmission.Create(Guid.NewGuid(),
        AiJobKind.ScriptAssistant, AiBackend.Vllm, new(Guid.NewGuid()), "QA", "Synthetic request", Guid.NewGuid(),
        new { model = new { vllmUrl = connection.Url }, prompt = "Say hello." });

    [Fact]
    public async Task ConnectionQueuesHaveIndependentCapacity()
    {
        var a = await Store.EnqueueAsync(ConnectionRequest(OpenAiConnectionTests.First), _ct);
        var waiting = await Store.EnqueueAsync(ConnectionRequest(OpenAiConnectionTests.First), _ct);
        var b = await Store.EnqueueAsync(ConnectionRequest(OpenAiConnectionTests.Second), _ct);
        Assert.Equal(a.Id, (await Store.ClaimNextForConnectionAsync(OpenAiConnectionTests.First.Url, 1, _ct))!.Id);
        Assert.Null(await Store.ClaimNextForConnectionAsync(OpenAiConnectionTests.First.Url, 1, _ct));
        Assert.Equal(b.Id, (await Store.ClaimNextForConnectionAsync(OpenAiConnectionTests.Second.Url, 1, _ct))!.Id);
        Assert.Equal(AiJobState.Waiting, (await Store.ReadAsync(_ct)).Jobs.Single(j => j.Id == waiting.Id).State);
    }

    [Fact]
    public async Task LegacyQueueHeadersRecoverCapturedEndpointWithoutChangingInputs()
    {
        var request = ConnectionRequest(OpenAiConnectionTests.Second);
        var job = await Store.EnqueueAsync(request, _ct);
        var index = JsonNode.Parse(await File.ReadAllTextAsync(Index, _ct))!;
        index["jobs"]![0]!.AsObject().Remove("connectionUrl");
        await File.WriteAllTextAsync(Index, index.ToJsonString(), _ct);
        Assert.Null(await Store.ClaimNextForConnectionAsync(OpenAiConnectionTests.First.Url, 1, _ct));
        var claimed = await Store.ClaimNextForConnectionAsync(OpenAiConnectionTests.Second.Url, 1, _ct);
        Assert.Equal(job.Id, claimed!.Id); Assert.Equal(OpenAiConnectionTests.Second.Url, claimed.ConnectionUrl);
        Assert.Equal(job.RequestFingerprint, claimed.RequestFingerprint);
        Assert.True(JsonElement.DeepEquals(request.Snapshot, await Store.ReadSnapshotAsync(claimed, _ct)));
    }

    [Fact]
    public async Task UnreadableLegacyWaitingRequestDoesNotBlockAnotherConnection()
    {
        var broken = await Store.EnqueueAsync(ConnectionRequest(OpenAiConnectionTests.First) with {
            Snapshot = JsonSerializer.SerializeToElement(new { prompt = "Say hello." }) }, _ct);
        var usable = await Store.EnqueueAsync(ConnectionRequest(OpenAiConnectionTests.Second), _ct);
        var claimed = await Store.ClaimNextForConnectionAsync(OpenAiConnectionTests.Second.Url, 1, _ct);
        Assert.Equal(usable.Id, claimed!.Id);
        var saved = (await Store.ReadAsync(_ct)).Jobs.Single(j => j.Id == broken.Id);
        Assert.Equal(AiJobState.NeedsAttention, saved.State); Assert.Equal(AiJobRecovery.GenerateAgain, saved.Recovery);
        Assert.Contains("connection", saved.Error);
    }
}

public sealed partial class AiJobCoordinatorTests
{
    [Fact]
    public async Task TwoOpenAiServersDispatchSimultaneouslyAndPauseTogether()
    {
        var a = OpenAiConnectionTests.First; var b = OpenAiConnectionTests.Second;
        _settings.Value = _settings.Value with { OpenAiConnections = [a, b] };
        AiJobSubmission Submit(OpenAiConnectionSettings connection) => AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.ScriptAssistant,
            AiBackend.Vllm, new(Guid.NewGuid()), "QA", "Synthetic request", Guid.NewGuid(), new { model = new { vllmUrl = connection.Url } });
        var first = await Store.EnqueueAsync(Submit(a), _ct);
        var second = await Store.EnqueueAsync(Submit(b), _ct);
        var waiting = await Store.EnqueueAsync(Submit(a), _ct);
        var handler = new FakeHandler(); var queue = await Start(handler);
        var running = new[] { await handler.Next(_ct), await handler.Next(_ct) };
        Assert.Equal(new[] { first.Id, second.Id }.Order(), running.Select(r => r.Context.Job.Id).Order());
        await queue.SetPausedAsync(AiBackend.Vllm, true, _ct);
        foreach (var call in running) { await call.Complete(); await State(call.Context.Job.Id, j => j.State == AiJobState.Completed); }
        Assert.Equal(AiJobState.Waiting, (await Store.ReadAsync(_ct)).Jobs.Single(j => j.Id == waiting.Id).State);
        await queue.SetPausedAsync(AiBackend.Vllm, false, _ct);
        var next = await handler.Next(_ct); Assert.Equal(waiting.Id, next.Context.Job.Id); await next.Complete();
    }
}

public sealed partial class AiTextJobTests
{
    [Theory]
    [InlineData("planning", 1)]
    [InlineData("draft", 3)]
    [InlineData("review", 4)]
    public async Task StrataStreamingStillRejectsInvalidBriefsDraftsAndReviewsLocally(string stage, int calls)
    {
        var connection = OpenAiConnectionTests.Second with { Kind = OpenAiServerKind.Strata };
        _settings.Value = _settings.Value with { OpenAiConnections = [connection] };
        _model = new(AiBackend.Vllm, "test/model", "Model") { VllmUrl = connection.Url };
        var writing = ScriptFromOutline(await AcceptedScriptSource());
        var submission = await SourceScriptCapture.ScriptAsync(writing.Run.Id, Guid.NewGuid(), writing, true, _ct);
        const string invalid = "{\"incomplete\":";
        if (stage != "planning") _providers.Chat.Outputs.Enqueue(PlannedScene);
        if (stage == "review") _providers.Chat.Outputs.Enqueue(ScriptSequenceDraftingTests.Candidate);
        _providers.Chat.Outputs.Enqueue(invalid);
        _providers.Chat.Outputs.Enqueue(invalid); // One bounded repair/review retry, when applicable.
        var context = await Claim(submission);
        if (stage == "planning") await Assert.ThrowsAsync<AiGenerationException>(() => Handler().ExecuteAsync(context, submission.Snapshot, _ct));
        else Assert.Equal(AiJobState.NeedsAttention, (await Handler().ExecuteAsync(context, submission.Snapshot, _ct)).State);
        var result = (await context.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct))!;
        Assert.NotNull(result.Error); Assert.Null(result.Value);
        Assert.Equal(stage == "review" ? ScriptSequenceDraftingTests.Candidate : invalid, result.Raw);
        Assert.Equal(calls, _providers.Created);
        Assert.Empty(_script.SourceSceneLinks);
    }

    [Theory]
    [InlineData(OpenAiServerKind.Strata, false)]
    [InlineData(OpenAiServerKind.Standard, false)]
    public async Task OpenAiSequenceDraftingKeepsPlanningWritingAndReviewOnTheSelectedConnection(OpenAiServerKind kind, bool schema)
    {
        var connection = OpenAiConnectionTests.Second with { Kind = kind };
        _settings.Value = _settings.Value with { OpenAiConnections = [OpenAiConnectionTests.First, connection] };
        _model = new(AiBackend.Vllm, "test/model", "Model") { VllmUrl = connection.Url };
        var writing = ScriptFromOutline(await AcceptedScriptSource());
        var submission = await SourceScriptCapture.ScriptAsync(writing.Run.Id, Guid.NewGuid(), writing, true, _ct);
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(connection.Url, captured.Settings.VllmUrl);
        Assert.Equal(connection.Id, Assert.Single(captured.Settings.OpenAiConnections!).Id);
        Assert.Equal(schema, captured.ResponseSchema is not null); Assert.Null(captured.ResponseGrammar);
        foreach (var output in new[] { PlannedScene, ScriptSequenceDraftingTests.Candidate, ScriptSequenceDraftingTests.Review(severity: "none") })
            _providers.Chat.Outputs.Enqueue(output);
        var context = await Claim(submission);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, submission.Snapshot, _ct)).State);
        Assert.Equal(3, _providers.Created);
        foreach (var stage in new[] { "scene-plan", "scene-draft", "sequence-review-0-0" }) {
            var request = await context.ReadOperationAsync<AiTextJobRequest>(stage, AiOperationArtifact.Request, _ct);
            Assert.Equal(connection.Url, request!.Settings.VllmUrl);
            Assert.Equal(schema, request.ResponseSchema is not null); Assert.Null(request.ResponseGrammar);
            if (schema) Assert.Equal("object", request.ResponseSchema!.Value.GetProperty("type").GetString());
        }
    }
}
