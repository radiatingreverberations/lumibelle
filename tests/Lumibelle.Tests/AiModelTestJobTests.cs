using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task QueuedModelTestsCaptureTheirContractAndClearCacheOnlyWhenDispatched(bool advanced)
    {
        using var f = new ModelJobFixture(); var submission = f.Capture(advanced); var ct = TestContext.Current.CancellationToken;
        await f.Jobs.EnqueueAsync(submission, ct); Assert.Empty(f.Http.Requests);
        var context = await f.Claim(); var request = submission.Snapshot.Deserialize<AiModelTestJobRequest>(AtomicJsonFile.Options)!;
        f.Settings.Value = f.Settings.Value with { ComfyUrl = "http://another.test", Temperature = .1f };
        await f.Worker.ExecuteAsync(context, submission.Snapshot, ct);
        var result = await context.ReadAsync<AiModelTestJobResult>(AiJobArtifact.Result, ct);
        Assert.True(result!.Saved); Assert.False(result.Recovered);
        Assert.Equal(advanced ? "An exact model reply." : null, result.Response);
        Assert.Single(f.Settings.Value.ComfyTextModelVerifications); Assert.Equal("http://another.test", f.Settings.Value.ComfyUrl);
        var free = Assert.Single(f.Http.Requests, r => r.Path == "/free"); var prompt = Assert.Single(f.Http.Requests, r => r.Path == "/prompt");
        Assert.True(f.Http.Requests.IndexOf(free) < f.Http.Requests.IndexOf(prompt));
        using var body = JsonDocument.Parse(prompt.Body); var nodes = body.RootElement.GetProperty("prompt");
        Assert.Equal(ModelJobFixture.Model.Model, nodes.GetProperty("1").GetProperty("inputs").GetProperty("clip_name").GetString());
        Assert.Equal(request.Test.Prompt, nodes.GetProperty("2").GetProperty("inputs").GetProperty("prompt").GetString());
        Assert.Equal(advanced ? 80 : 256, nodes.GetProperty("2").GetProperty("inputs").GetProperty("max_length").GetInt32());
        var benchmark = Assert.Single(Assert.IsType<ComfyTextModelVerification>(result.Verification).Benchmarks!); Assert.Equal(advanced, benchmark.CustomPrompt); Assert.True(benchmark.CacheClearConfirmed);
        Assert.DoesNotContain(f.Http.Requests, r => r.Path.Contains("interrupt"));
    }

    [Fact]
    public async Task ModelVerificationSaveFailureRetriesOnlyPublicationAndPreservesNewSettings()
    {
        using var f = new ModelJobFixture(); var ct = TestContext.Current.CancellationToken; var submission = f.Capture(true);
        await f.Jobs.EnqueueAsync(submission, ct); var context = await f.Claim(); f.Settings.SaveError = new WorkspaceStoreException("Disk full");
        var error = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, submission.Snapshot, ct));
        Assert.Equal(AiJobRecovery.RetryOutput, error.Recovery);
        Assert.False((await context.ReadAsync<AiModelTestJobResult>(AiJobArtifact.Result, ct))!.Saved);
        f.Settings.SaveError = null; f.NoNetwork = true;
        f.Settings.Value = f.Settings.Value with { Temperature = .25f, OpenRouterConcurrency = 3 };
        await f.Worker.RecoverAsync(f.Context(context.Job, true), submission.Snapshot, ct);
        Assert.True((await context.ReadAsync<AiModelTestJobResult>(AiJobArtifact.Result, ct))!.Saved);
        Assert.Equal(.25f, f.Settings.Value.Temperature); Assert.Equal(3, f.Settings.Value.OpenRouterConcurrency);
        var saves = f.Settings.SaveCalls;
        await f.Worker.RecoverAsync(f.Context(context.Job, true), submission.Snapshot, ct);
        Assert.Equal(saves, f.Settings.SaveCalls); Assert.Single(f.Settings.Value.ComfyTextModelVerifications);
        Assert.Single(f.Http.Requests, r => r.Path == "/prompt"); Assert.Single(f.Http.Requests, r => r.Path == "/free");
    }

    [Fact]
    public async Task RecoveredModelOutputNeverClearsCacheOrClaimsUnobservedMeasurements()
    {
        using var f = new ModelJobFixture(); var ct = TestContext.Current.CancellationToken; var submission = f.Capture(true);
        await f.Jobs.EnqueueAsync(submission, ct); var context = await f.Claim();
        var request = submission.Snapshot.Deserialize<AiModelTestJobRequest>(AtomicJsonFile.Options)!;
        await f.Registry.RunQueuedTestAsync(context, request, f.Execution, ct); // Crash before result/verification publication.
        f.NoNetwork = true;
        await f.Worker.RecoverAsync(f.Context(context.Job, true), submission.Snapshot, ct);
        var result = (await context.ReadAsync<AiModelTestJobResult>(AiJobArtifact.Result, ct))!;
        Assert.True(result.Recovered); Assert.True(result.Saved); Assert.Equal("An exact model reply.", result.Response);
        var benchmark = Assert.Single(Assert.IsType<ComfyTextModelVerification>(result.Verification).Benchmarks!);
        Assert.Null(benchmark.PeakVramUsedBytes); Assert.Null(benchmark.TokensPerSecond);
        Assert.Null(result.Verification!.Capabilities);
        Assert.Single(f.Http.Requests, r => r.Path == "/prompt"); Assert.Single(f.Http.Requests, r => r.Path == "/free");
    }

    [Fact]
    public async Task QueuedModelTestProbesAndRecordsSystemPromptSupport()
    {
        using var f = new ModelJobFixture { SystemPromptInput = true }; var ct = TestContext.Current.CancellationToken; var submission = f.Capture();
        await f.Jobs.EnqueueAsync(submission, ct); var context = await f.Claim();
        await f.Worker.ExecuteAsync(context, submission.Snapshot, ct);
        var result = (await context.ReadAsync<AiModelTestJobResult>(AiJobArtifact.Result, ct))!;
        Assert.Equal(new ComfyTextModelCapabilities(true, ComfyVisionInput.Disabled), result.Verification!.Capabilities);
        Assert.Equal(new ComfyTextModelCapabilities(true, ComfyVisionInput.Disabled), Assert.Single(f.Settings.Value.ComfyTextModelVerifications).Capabilities);
        var prompts = f.Http.Requests.Where(r => r.Path == "/prompt").ToArray();
        Assert.Equal(2, prompts.Length);
        Assert.DoesNotContain("system_prompt", prompts[0].Body);
        Assert.Contains("system_prompt", prompts[1].Body);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task QueuedModelTestRejectsMissingModelsAndExternalComfyWorkBeforeClearing(bool busy)
    {
        using var f = new ModelJobFixture { Busy = busy, MissingModel = !busy }; var ct = TestContext.Current.CancellationToken;
        var submission = f.Capture(); await f.Jobs.EnqueueAsync(submission, ct); var context = await f.Claim();
        await Assert.ThrowsAsync<AiGenerationException>(() => f.Worker.ExecuteAsync(context, submission.Snapshot, ct));
        Assert.DoesNotContain(f.Http.Requests, r => r.Path is "/free" or "/prompt"); Assert.Empty(f.Settings.Value.ComfyTextModelVerifications);
        await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.RecoverAsync(f.Context(context.Job, true), submission.Snapshot, ct));
        Assert.DoesNotContain(f.Http.Requests, r => r.Path is "/free" or "/prompt");
    }

    [Fact]
    public async Task ComfyAdvancedTestAccepts32kAndSavesResultsWithoutChangingModelDefaults()
    {
        using var f = new ModelJobFixture(); var ct = TestContext.Current.CancellationToken;
        var original = f.Settings.Value;
        var submission = AiModelTestJobHandler.Capture(Guid.NewGuid(), Guid.NewGuid(), ModelJobFixture.Model,
            original, new("Manual long reply test", 32768));
        await f.Jobs.EnqueueAsync(submission, ct); var context = await f.Claim();
        await f.Worker.ExecuteAsync(context, submission.Snapshot, ct);
        var result = (await context.ReadAsync<AiModelTestJobResult>(AiJobArtifact.Result, ct))!;
        Assert.True(result.Saved);
        Assert.Equal(32768, Assert.Single(result.Verification!.Benchmarks!).TokenLimit);
        using var graph = JsonDocument.Parse(Assert.Single(f.Http.Requests, r => r.Path == "/prompt").Body);
        Assert.Equal(32768, graph.RootElement.GetProperty("prompt").GetProperty("2").GetProperty("inputs").GetProperty("max_length").GetInt32());
        Assert.Equal(original.MaxOutputTokens, f.Settings.Value.MaxOutputTokens);
        Assert.Equal(original.Temperature, f.Settings.Value.Temperature);
        Assert.Equal(original.ComfyTextModels, f.Settings.Value.ComfyTextModels);
        FileAiSettingsStore.Validate(f.Settings.Value);
        f.NoNetwork = true;
        await f.Worker.RecoverAsync(f.Context(context.Job, true), submission.Snapshot, ct);
        Assert.Single(f.Http.Requests, r => r.Path == "/prompt");
    }

    [Theory] [InlineData(0)] [InlineData(32769)]
    public async Task ComfyAdvancedTestRejectsOutOfRangeLimitsBeforeDispatch(int limit)
    {
        using var f = new ModelJobFixture(); var ct = TestContext.Current.CancellationToken;
        Assert.Throws<WorkspaceStoreException>(() => AiModelTestJobHandler.Capture(Guid.NewGuid(), Guid.NewGuid(),
            ModelJobFixture.Model, f.Settings.Value, new("Manual test", limit)));
        await Assert.ThrowsAsync<AiGenerationException>(async () =>
        {
            await foreach (var update in f.Registry.TestComfyTextModelAsync(ModelJobFixture.Model.Model, f.Settings.Value, new("Manual test", limit), ct)) { }
        });
        Assert.Empty(f.Http.Requests);
    }

    private sealed class ModelJobFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.ModelJobs", Guid.NewGuid().ToString("N"));
        private readonly string _promptId = Guid.NewGuid().ToString("D");
        public static TextModelReference Model { get; } = new(AiBackend.ComfyUI, "qwen_3_4b.safetensors", "QA model", "http://model.test:8188");
        public FakeAiSettingsStore Settings { get; } = new();
        public FileAiJobStore Jobs { get; }
        public ScriptedHttpHandler Http { get; }
        public AiProviderRegistry Registry { get; }
        public ComfyJobExecution Execution { get; } = new(TestComfy.Monitor());
        public AiModelTestJobHandler Worker { get; }
        public bool NoNetwork, Busy, MissingModel, SystemPromptInput;
        private readonly Dictionary<string, string> _probeAnswers = [];
        public ModelJobFixture()
        {
            Jobs = new(_root, TimeProvider.System);
            Http = new((request, _) =>
            {
                if (NoNetwork) throw new InvalidOperationException("Recovered model output must not contact ComfyUI");
                Assert.Equal("model.test", request.RequestUri!.Host);
                var path = request.RequestUri.AbsolutePath;
                if (path == "/prompt" && Http!.Requests[^1].Body is var body && body.Contains("system_prompt", StringComparison.Ordinal))
                {
                    // A model that honors the system prompt repeats its code.
                    var id = Guid.NewGuid().ToString("D");
                    using var workflow = JsonDocument.Parse(body);
                    _probeAnswers[id] = new(workflow.RootElement.GetProperty("prompt").GetProperty("2").GetProperty("inputs").GetProperty("system_prompt").GetString()!.Where(char.IsAsciiDigit).ToArray());
                    return Task.FromResult(JsonResponse($"{{\"prompt_id\":\"{id}\"}}"));
                }
                if (_probeAnswers.Keys.FirstOrDefault(id => path.Contains(id, StringComparison.Ordinal)) is { } probe)
                    return Task.FromResult(JsonResponse($"{{\"{probe}\":{{\"status\":{{\"completed\":true,\"status_str\":\"success\"}},\"outputs\":{{\"3\":{{\"text\":[\"{_probeAnswers[probe]}\"]}}}}}}}}"));
                return Task.FromResult(JsonResponse(path switch
                {
                    "/object_info" when SystemPromptInput => JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["CLIPLoader"] = new { input = new { required = new { clip_name = new object[] { new[] { Model.Model }, new { } } } } },
                        ["TextGenerate"] = new { input = new { optional = new { system_prompt = new object[] { "STRING", new { forceInput = true } } } } },
                        ["PreviewAny"] = new { }
                    }),
                    "/object_info" => ComfyCatalog(MissingModel ? "different.safetensors" : Model.Model),
                    "/system_stats" => ComfyStats("test-version"),
                    "/queue" => Busy ? "{\"queue_running\":[[1,\"unrelated\"]],\"queue_pending\":[]}" : "{\"queue_running\":[],\"queue_pending\":[]}",
                    "/free" => "{}", "/prompt" => $"{{\"prompt_id\":\"{_promptId}\"}}",
                    _ => $"{{\"{_promptId}\":{{\"status\":{{\"completed\":true,\"status_str\":\"success\"}},\"outputs\":{{\"3\":{{\"text\":[\"An exact model reply.\"]}}}}}}}}"
                }));
            });
            var factory = new TestHttpFactory(Http); Registry = new(factory, Settings, TestComfy.Monitor());
            Worker = new(Registry, Settings, factory, Execution, TimeProvider.System);
        }
        public AiJobSubmission Capture(bool advanced = false) => AiModelTestJobHandler.Capture(Guid.NewGuid(), Guid.NewGuid(), Model,
            Settings.Value, advanced ? new("Keep this exact test message", 80) : null);
        public async Task<AiJobContext> Claim() => Context((await Jobs.ClaimNextAsync(AiBackend.ComfyUI, 1, TestContext.Current.CancellationToken))!, false);
        public AiJobContext Context(AiJobHeader job, bool recovering) => new(job, recovering, Jobs, TimeProvider.System, (_, _) => { }, _ => { }, TestContext.Current.CancellationToken);
        public void Dispose() { Http.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
