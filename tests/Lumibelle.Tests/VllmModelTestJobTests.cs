using System.Net;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class VllmModelTestJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Reply = "data: {\"model\":\"served/model\",\"choices\":[{\"delta\":{\"content\":\"Visible reply.\"},\"finish_reason\":\"stop\"}]}\n\n";
    private const string Usage = "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":42,\"cost\":9}}\n\ndata: [DONE]\n\n";

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CapturesSettingsAndServerAndRetainsResponseWithoutHostedBilling(bool advanced)
    {
        using var f = new Fixture();
        f.Settings.Value.VllmTextModels[TextModelPolicy.Key(Fixture.Model)] = new(0, 4096);
        var submission = f.Capture(advanced); var context = await f.Claim(submission);
        f.Settings.Value = f.Settings.Value with { VllmUrl = "http://other:8000/v1", VllmTextModels = [] };
        Assert.Equal(AiJobState.Completed, (await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct)).State);
        var result = (await context.ReadAsync<AiModelTestJobResult>(AiJobArtifact.Result, Ct))!;
        var saved = Assert.Single(f.Settings.Value.VllmTextModelBenchmarks);
        Assert.True(result.Saved); Assert.Equal("Visible reply.", result.Response);
        Assert.Null(result.OpenRouter); Assert.Null(result.Verification); Assert.Null(saved.Measurement.Cost);
        Assert.Equal(Fixture.Model.VllmUrl, saved.Server); Assert.Equal(0f, saved.Temperature);
        Assert.Equal(42, saved.Measurement.OutputTokens); Assert.Equal(11, saved.Measurement.InputTokens);
        Assert.Equal(42 / saved.Measurement.ElapsedSeconds, saved.TokensPerSecond);
        Assert.NotNull(saved.Measurement.FirstTextSeconds); Assert.Equal(advanced, saved.Measurement.CustomPrompt);
        Assert.Equal("http://other:8000/v1", f.Settings.Value.VllmUrl); Assert.Empty(f.Settings.Value.OpenRouterTextModelBenchmarks);
        using var body = JsonDocument.Parse(Assert.Single(f.Http.Requests, IsTextPost).Body);
        Assert.Equal(0f, body.RootElement.GetProperty("temperature").GetSingle());
        Assert.Equal(advanced ? 96 : 4096, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("provider", out _));
        Assert.All(f.Addresses, uri => Assert.StartsWith(Fixture.Model.VllmUrl!, uri));
        f.NoNetwork = true;
        await f.Worker.RecoverAsync(f.Recover(context), submission.Snapshot, Ct);
        Assert.Single(f.Settings.Value.VllmTextModelBenchmarks);
        Assert.Single(f.Http.Requests, IsTextPost);
    }

    [Fact]
    public async Task UnsetTemperatureUsesServerDefaultAndTestsHaveABoundedReplyLimit()
    {
        using var f = new Fixture(); var submission = f.Capture(); var context = await f.Claim(submission);
        await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        using var body = JsonDocument.Parse(Assert.Single(f.Http.Requests, IsTextPost).Body);
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.Equal(2048, body.RootElement.GetProperty("max_tokens").GetInt32());
        var captured = submission.Snapshot.Deserialize<AiModelTestJobRequest>(AtomicJsonFile.Options)!;
        var wrongServer = captured with { Settings = captured.Settings with { VllmUrl = "http://other:8000/v1" } };
        Assert.Throws<WorkspaceStoreException>(() => AiModelTestJobHandler.Read(context.Job, JsonSerializer.SerializeToElement(wrongServer, AtomicJsonFile.Options)));
        Assert.Throws<WorkspaceStoreException>(() => AiModelTestJobHandler.Read(context.Job, JsonSerializer.SerializeToElement(captured with { Version = 1 }, AtomicJsonFile.Options)));
    }

    [Fact]
    public async Task SaveFailureCanRecoverWithoutAnotherGeneration()
    {
        using var f = new Fixture(); var submission = f.Capture(); var context = await f.Claim(submission);
        f.Settings.SaveError = new WorkspaceStoreException("Disk unavailable");
        var error = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, submission.Snapshot, Ct));
        Assert.Equal(AiJobRecovery.RetryOutput, error.Recovery);
        f.Settings.SaveError = null; f.NoNetwork = true;
        Assert.Equal(AiJobState.Completed, (await f.Worker.RecoverAsync(f.Recover(context), submission.Snapshot, Ct)).State);
        Assert.Single(f.Http.Requests, IsTextPost);
    }

    [Theory] [InlineData(401)] [InlineData(503)]
    public async Task RejectedRequestsRemainInspectableWithoutAutomaticRetries(int status)
    {
        using var f = new Fixture { Status = (HttpStatusCode)status }; var submission = f.Capture(); var context = await f.Claim(submission);
        var outcome = await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        Assert.Equal(AiJobState.NeedsAttention, outcome.State); Assert.Equal(AiJobRecovery.GenerateAgain, outcome.Recovery);
        Assert.False(Assert.Single(f.Settings.Value.VllmTextModelBenchmarks).Measurement.Complete);
        f.NoNetwork = true; await f.Worker.RecoverAsync(f.Recover(context), submission.Snapshot, Ct);
        Assert.Single(f.Http.Requests, IsTextPost);
    }

    [Fact]
    public async Task PartialReplyAndMissingUsageAreNotPresentedAsMeasuredSpeed()
    {
        using var f = new Fixture { Stream = Reply }; var submission = f.Capture(true); var context = await f.Claim(submission);
        Assert.Equal(AiJobState.NeedsAttention, (await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct)).State);
        var result = (await context.ReadAsync<AiModelTestJobResult>(AiJobArtifact.Result, Ct))!;
        Assert.Equal("Visible reply.", result.Response); Assert.Null(result.Vllm!.TokensPerSecond);
        Assert.Null(result.Vllm.Measurement.OutputTokens);
    }

    [Fact]
    public async Task ReasoningOnlyResponseIsNotACompletedWritingTest()
    {
        using var f = new Fixture { Stream = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"private reasoning\"},\"finish_reason\":\"length\"}],\"usage\":{\"completion_tokens\":2048}}\n\ndata: [DONE]\n\n" };
        var submission = f.Capture(); var context = await f.Claim(submission);
        Assert.Equal(AiJobState.NeedsAttention, (await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct)).State);
        var measured = Assert.Single(f.Settings.Value.VllmTextModelBenchmarks);
        Assert.True(measured.Measurement.ReasoningOnly); Assert.Null(measured.TokensPerSecond);
        Assert.DoesNotContain("private reasoning", JsonSerializer.Serialize(measured));
    }

    [Fact]
    public async Task CancelledTestsNeverContactComfyOrReplayInference()
    {
        using var f = new Fixture { WaitForCancellation = true }; var submission = f.Capture(); var context = await f.Claim(submission);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = new AiJobContext(context.Job, false, f.Jobs, TimeProvider.System, (_, _) => { }, _ => { }, cancel.Token);
        var task = f.Worker.ExecuteAsync(running, submission.Snapshot, cancel.Token);
        await f.PostStarted.Task.WaitAsync(Ct); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(await f.Worker.CancelRemoteAsync(context, submission.Snapshot, Ct));
        f.NoNetwork = true;
        await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.RecoverAsync(f.Recover(context), submission.Snapshot, Ct));
        Assert.Single(f.Http.Requests, IsTextPost);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.VllmTests", Guid.NewGuid().ToString("N"));
        public static readonly TextModelReference Model = new(AiBackend.Vllm, "test/model", "Test model") { VllmUrl = "http://llm-server:8000/v1" };
        public FakeAiSettingsStore Settings { get; } = new() { Value = new() { VllmUrl = Model.VllmUrl! } };
        public FileAiJobStore Jobs { get; }
        public ScriptedHttpHandler Http { get; }
        public AiModelTestJobHandler Worker { get; }
        public List<string> Addresses { get; } = [];
        public string Stream = Reply + Usage;
        public bool NoNetwork, WaitForCancellation;
        public bool StrataDiscovery;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public Func<JsonElement, HttpResponseMessage>? ImageReply;
        public Func<JsonElement, HttpResponseMessage>? SchemaReply;
        public TaskCompletionSource PostStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Fixture()
        {
            Jobs = new(_root, TimeProvider.System);
            Http = new(async (request, ct) =>
            {
                if (NoNetwork) throw new InvalidOperationException("Recovery must not contact the server.");
                Addresses.Add(request.RequestUri!.AbsoluteUri);
                Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("X-OpenRouter-Metadata"));
                if (request.Method == HttpMethod.Post) PostStarted.TrySetResult();
                if (request.Method == HttpMethod.Post && WaitForCancellation) await Task.Delay(Timeout.Infinite, ct);
                if (request.Method == HttpMethod.Post)
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    if (body.RootElement.TryGetProperty("response_format", out _)) return SchemaReply?.Invoke(body.RootElement) ?? AnswerSchemaProbe(body.RootElement);
                    if (!body.RootElement.GetProperty("stream").GetBoolean()) return ImageReply?.Invoke(body.RootElement) ?? AnswerImageProbe(body.RootElement);
                }
                if (StrataDiscovery && request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = new StringContent("{\"structured_output\":{\"method\":\"prompt_and_validate\"}}", Encoding.UTF8, "application/json") };
                return request.Method == HttpMethod.Get
                    ? new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"test/model\"}]}", Encoding.UTF8, "application/json") }
                    : new(Status) { Content = new StringContent(Stream, Encoding.UTF8, "text/event-stream") };
            });
            var factory = new TestHttpFactory(Http);
            Worker = new(new AiProviderRegistry(factory, Settings, TestComfy.Monitor()), Settings, factory, new(TestComfy.Monitor()), TimeProvider.System);
        }
        public AiJobSubmission Capture(bool advanced = false) => AiModelTestJobHandler.Capture(Guid.NewGuid(), Guid.NewGuid(), Model, Settings.Value, advanced ? new("Custom test prompt", 96) : null);
        public async Task<AiJobContext> Claim(AiJobSubmission submission)
        {
            await Jobs.EnqueueAsync(submission, Ct);
            return new((await Jobs.ClaimNextAsync(AiBackend.Vllm, 1, Ct))!, false, Jobs, TimeProvider.System, (_, _) => { }, _ => { }, Ct);
        }
        public AiJobContext Recover(AiJobContext context) => new(context.Job, true, Jobs, TimeProvider.System, (_, _) => { }, _ => { }, Ct);
        public void Dispose() { Http.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
