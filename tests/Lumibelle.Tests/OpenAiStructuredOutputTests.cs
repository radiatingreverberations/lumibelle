using System.Net;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed partial class VllmModelTestJobTests
{
    private static HttpResponseMessage AnswerSchemaProbe(JsonElement body)
    {
        var format = body.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        var code = format.GetProperty("json_schema").GetProperty("schema").GetProperty("properties").GetProperty("code").GetProperty("enum")[0].GetString();
        Assert.DoesNotContain(code!, body.GetProperty("messages")[0].GetProperty("content").GetString());
        return ImageResponse(JsonSerializer.Serialize(new { code, count = 2 }));
    }

    private static void Compatible(Fixture f) => f.Settings.Value = f.Settings.Value with { OpenAiConnections = [new() {
        Id = Guid.NewGuid(), Name = "Compatible", Url = Fixture.Model.VllmUrl!, Kind = OpenAiServerKind.Standard
    }] };

    [Fact]
    public async Task CompatibleModelTestsVerifySchemasAndRecoveryDoesNotReplayInference()
    {
        using var f = new Fixture(); Compatible(f);
        var submission = f.Capture(); var context = await f.Claim(submission);
        Assert.True(submission.Snapshot.Deserialize<AiModelTestJobRequest>(AtomicJsonFile.Options)!.TestStructuredOutput);
        await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        Assert.True(Assert.Single(f.Settings.Value.VllmTextModelBenchmarks).StructuredOutput!.Supported);
        var count = f.Http.Requests.Count; f.NoNetwork = true;
        await f.Worker.RecoverAsync(f.Recover(context), submission.Snapshot, Ct);
        Assert.Equal(count, f.Http.Requests.Count);
    }

    [Fact]
    public async Task AutoDetectedPromptValidationKeepsItsOriginalStreamingBehavior()
    {
        using var f = new Fixture { StrataDiscovery = true, SchemaReply = _ => throw new InvalidOperationException("Prompt-and-validate servers must not be schema-probed.") };
        f.Settings.Value = f.Settings.Value with { OpenAiConnections = [new() { Url = Fixture.Model.VllmUrl!, Kind = OpenAiServerKind.Auto }] };
        var submission = f.Capture(); var context = await f.Claim(submission);
        await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        Assert.Null(Assert.Single(f.Settings.Value.VllmTextModelBenchmarks).StructuredOutput);
        Assert.DoesNotContain(f.Http.Requests, r => r.Method == "POST" && r.Body.Contains("response_format"));
    }

    [Theory]
    [InlineData(400, "This model does not support json_schema", false)]
    [InlineData(400, "Invalid sampling parameter", null)]
    [InlineData(503, "Unavailable", null)]
    public async Task OnlyExplicitSchemaRejectionMarksSupportFalse(int status, string error, bool? expected)
    {
        using var f = new Fixture { SchemaReply = _ => new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(new { error = new { message = error } })) } };
        Compatible(f); var submission = f.Capture(); var context = await f.Claim(submission);
        Assert.Equal(AiJobState.Completed, (await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct)).State);
        Assert.Equal(expected, Assert.Single(f.Settings.Value.VllmTextModelBenchmarks).StructuredOutput!.Supported);
        Assert.True(Assert.Single(f.Settings.Value.VllmTextModelBenchmarks).ImageInput!.MultipleImages);
    }

    [Fact]
    public async Task IgnoringTheSchemaDoesNotConfirmSupportAndHistoricalTestsKeepTheirOriginalScope()
    {
        using var f = new Fixture { SchemaReply = _ => ImageResponse("ready") }; Compatible(f);
        var submission = f.Capture(); var context = await f.Claim(submission);
        await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        Assert.Null(Assert.Single(f.Settings.Value.VllmTextModelBenchmarks).StructuredOutput!.Supported);
        using var historical = new Fixture(); Compatible(historical);
        var next = historical.Capture();
        var old = next.Snapshot.Deserialize<AiModelTestJobRequest>(AtomicJsonFile.Options)! with { TestStructuredOutput = false };
        next = next with { Snapshot = JsonSerializer.SerializeToElement(old, AtomicJsonFile.Options) };
        var legacy = await historical.Claim(next);
        await historical.Worker.ExecuteAsync(legacy, next.Snapshot, Ct);
        Assert.DoesNotContain(historical.Http.Requests, r => r.Method == "POST" && r.Body.Contains("response_format"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task InterruptedCapabilityChecksPreserveCompletedWorkWithoutRepeatingInference(bool schemaFinished)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var f = new Fixture(); Compatible(f);
        f.SchemaReply = body => { if (!schemaFinished) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); } return AnswerSchemaProbe(body); };
        f.ImageReply = _ => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); };
        var submission = f.Capture(); var context = await f.Claim(submission);
        var running = new AiJobContext(context.Job, false, f.Jobs, TimeProvider.System, (_, _) => { }, _ => { }, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Worker.ExecuteAsync(running, submission.Snapshot, cancellation.Token));
        var count = f.Http.Requests.Count; f.NoNetwork = true;
        await f.Worker.RecoverAsync(f.Recover(context), submission.Snapshot, Ct);
        Assert.Equal(count, f.Http.Requests.Count);
        Assert.Equal(schemaFinished ? true : null, Assert.Single(f.Settings.Value.VllmTextModelBenchmarks).StructuredOutput?.Supported);
    }
}

public sealed class OpenAiStructuredOutputTests
{
    internal static VllmTextModelBenchmark Verified(string server, string model) => new(server, null,
        new(Guid.NewGuid(), model, DateTimeOffset.UtcNow, 2048, false, 1, .1, 1, 1, null, null, null, null, null, null, "stop", true)) {
            StructuredOutput = new(DateTimeOffset.UtcNow, true, "Confirmed by a synthetic schema test.")
        };
    [Theory]
    [InlineData("{\"supported_parameters\":[\"structured_outputs\",\"response_format\"]}", true)]
    [InlineData("{\"supported_parameters\":[\"response_format\"]}", null)]
    [InlineData("{}", null)]
    public void JsonObjectModeIsNotAssumedToMeanJsonSchema(string raw, bool? expected)
    {
        using var json = JsonDocument.Parse(raw);
        Assert.Equal(expected, OpenAiStructuredOutput.Advertised(json.RootElement));
    }

    [Fact]
    public async Task DiscoveryUsesMetadataOrTestsForTheExactEndpointAndModel()
    {
        using var http = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("{\"data\":[{\"id\":\"with-schema\",\"supported_parameters\":[\"structured_outputs\"]},{\"id\":\"unknown\"}]}")
        }));
        var endpoint = OpenAiConnectionTests.First;
        var settings = new AiSettings { OpenAiConnections = [endpoint] };
        var measured = new TextModelBenchmark(Guid.NewGuid(), "unknown", DateTimeOffset.UtcNow, 2048, false, 1, .1, 1, 1, null, null, null, null, null, null, "stop", true);
        var verified = new VllmTextModelBenchmark(endpoint.Url, null, measured) { StructuredOutput = new(DateTimeOffset.UtcNow, true, "Confirmed") };
        var registry = new AiProviderRegistry(new TestHttpFactory(http), new FakeAiSettingsStore(), TestComfy.Monitor());
        var first = await registry.CheckAsync(AiBackend.Vllm, settings, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(first.Models.Single(m => m.Id == "with-schema").SupportsJsonSchema);
        Assert.Null(first.Models.Single(m => m.Id == "unknown").SupportsJsonSchema);
        var tested = await registry.CheckAsync(AiBackend.Vllm, settings with { VllmTextModelBenchmarks = [verified] }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(tested.Models.Single(m => m.Id == "unknown").SupportsJsonSchema);
        var other = await registry.CheckAsync(AiBackend.Vllm, settings with { VllmTextModelBenchmarks = [verified with { Server = OpenAiConnectionTests.Second.Url }] }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(other.Models.Single(m => m.Id == "unknown").SupportsJsonSchema);
    }

    [Fact]
    public async Task StandardSchemaTransportPreservesAllCapturedConstraintsAndStreamsNormally()
    {
        using var http = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"{}\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", System.Text.Encoding.UTF8, "text/event-stream")
        }));
        var settings = OpenAiConnections.Scope(new(), OpenAiConnectionTests.First);
        var model = new TextModelReference(AiBackend.Vllm, "test", "Test") { VllmUrl = settings.VllmUrl };
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { items = new { type = "array", minItems = 2, maxItems = 2, items = new { type = "string", minLength = 1 } } }, required = new[] { "items" }, additionalProperties = false });
        var request = new AiTextJobRequest(3, AiJobKind.ShotPlanning, model, false, settings, "test", .2f, 1, JsonSerializer.SerializeToElement(new { }), []) { ResponseSchema = schema };
        using var client = await new AiProviderRegistry(new TestHttpFactory(http), new FakeAiSettingsStore(), TestComfy.Monitor()).CreateAsync(AiBackend.Vllm, "test", settings, TestContext.Current.CancellationToken);
        await foreach (var _ in client.GetStreamingResponseAsync([new(ChatRole.User, "Return two items.")], TextGenerationOptions.Captured(request), TestContext.Current.CancellationToken)) { }
        using var body = JsonDocument.Parse(Assert.Single(http.Requests).Body);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("structured_outputs", out _));
        Assert.Equal("json_schema", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.True(JsonElement.DeepEquals(schema, body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema")));
    }
}

public sealed partial class AiTextJobTests
{
    [Theory]
    [InlineData(OpenAiServerKind.Standard, false)] [InlineData(OpenAiServerKind.Standard, true)] [InlineData(OpenAiServerKind.Strata, true)]
    public async Task VerifiedCompatibleModelsCaptureShotSchemasWithoutChangingHistoricalRequests(OpenAiServerKind kind, bool single)
    {
        _settings.Value = _settings.Value with { OpenAiConnections = [new() { Url = _settings.Value.VllmUrl, Kind = kind }],
            VllmTextModelBenchmarks = [OpenAiStructuredOutputTests.Verified(_settings.Value.VllmUrl, "test/model")] };
        _model = TextModelPolicy.Reference(AiBackend.Vllm, "test/model", "Compatible model", _settings.Value);
        var request = new ShotPlanningRequest(Approved, _assets.Library, [_script.Blocks[0].Id], 8, "Keep it calm.", _model) {
            CoverageOnly = true, SingleShot = single ? new(Guid.NewGuid(), 0, [], null) : null
        };
        var submission = await Capture.PlanShotsAsync(Guid.NewGuid(), Guid.NewGuid(), request, false, _ct);
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(kind == OpenAiServerKind.Standard, captured.ResponseSchema is not null);
        Assert.Null(captured.ResponseGrammar);
        _providers.Chat.Output = Response(AiJobKind.ShotPlanning); var context = await Claim(submission);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, submission.Snapshot, _ct)).State);
        Assert.Equal(kind == OpenAiServerKind.Standard, _providers.Chat.Options!.RawRepresentationFactory is not null);
        _providers.Created = 0; _providers.OnCheck = () => throw new InvalidOperationException("Recovery must not connect.");
        await Handler().RecoverAsync(await Recover(context), submission.Snapshot, _ct);
        Assert.Equal(0, _providers.Created);
    }

    [Fact]
    public async Task VerifiedCompatibleSequencesCapturePlanningAndDraftSchemasWithoutNativeGrammar()
    {
        _settings.Value = _settings.Value with { OpenAiConnections = [new() { Url = "http://llm-server:8000/v1", Kind = OpenAiServerKind.Standard }],
            VllmTextModelBenchmarks = [OpenAiStructuredOutputTests.Verified("http://llm-server:8000/v1", "test/model")] };
        var submission = await LocalSceneRequest(reviewed: true);
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.NotNull(captured.ResponseSchema); Assert.NotNull(captured.ScenePlanResponseSchema);
        Assert.Null(captured.ResponseGrammar); Assert.Null(captured.ScenePlanResponseGrammar);
        _providers.Chat.Outputs.Enqueue(PlannedScene);
        _providers.Chat.Outputs.Enqueue(ScriptSequenceDraftingTests.Candidate);
        _providers.Chat.Outputs.Enqueue(ScriptSequenceDraftingTests.Review());
        var context = await Claim(submission);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, submission.Snapshot, _ct)).State);
        var planning = await context.ReadOperationAsync<AiTextJobRequest>("scene-plan", AiOperationArtifact.Request, _ct);
        Assert.True(JsonElement.DeepEquals(captured.ScenePlanResponseSchema!.Value, planning!.ResponseSchema!.Value));
        Assert.Null(planning.ResponseGrammar);
        var review = await context.ReadOperationAsync<AiTextJobRequest>("sequence-review-0-0", AiOperationArtifact.Request, _ct);
        Assert.NotNull(review!.ResponseSchema); Assert.Null(review.ResponseGrammar);
        Assert.NotNull(_providers.Chat.Options!.RawRepresentationFactory);
    }
}
