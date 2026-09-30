using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public interface IModelTestRunner
{
    Task<AiModelTestJobResult> RunQueuedTestAsync(AiJobContext context, AiModelTestJobRequest request, ComfyJobExecution execution, CancellationToken ct);
}

public sealed class AiModelTestJobHandler(IModelTestRunner runner, IAiSettingsStore settings, IHttpClientFactory clients,
    ComfyJobExecution execution, TimeProvider clock) : IAiJobHandler
{
    public const int OpenRouterBenchmarkTokens = 2048;
    public const int OpenRouterAdvancedMaxTokens = 8192;
    public const int ComfyAdvancedMaxTokens = 32768;
    public const string OpenRouterBenchmarkPrompt = "Write one short paragraph of about 80 words describing a quiet forest at dawn. Return only the description, without headings or commentary.";
    public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.TextBenchmark, AiJobKind.TextAdvancedTest];
    public Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => RunAsync(context, Read(context.Job, snapshot), ct);
    public Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => RunAsync(context, Read(context.Job, snapshot), ct);
    public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => context.Job.Backend == AiBackend.ComfyUI ? execution.CancelAsync(context, Client, ct) : Task.FromResult(true);
    public static AiJobSubmission Capture(Guid id, Guid tab, TextModelReference model, AiSettings settings, ComfyTextModelTestRequest? advanced = null)
    {
        model = TextModelPolicy.Normalize(model); settings = ShotCopy.Of(settings);
        if (model.Backend == AiBackend.ComfyUI) settings = ComfyTextSettings.Capture(model, settings with { ComfyUrl = model.ComfyUrl! });
        var hosted = model.Backend == AiBackend.OpenRouter;
        var request = new AiModelTestJobRequest(hosted ? 2 : 1, model, settings, advanced is null
            ? new(hosted ? OpenRouterBenchmarkPrompt : AiProviderRegistry.StandardBenchmarkPrompt,
                hosted ? OpenRouterBenchmarkTokens : settings.MaxOutputTokens) : advanced with { }, advanced is not null, Random.Shared.NextInt64(1, long.MaxValue));
        Validate(request);
        return AiJobSubmission.Create(id, advanced is null ? AiJobKind.TextBenchmark : AiJobKind.TextAdvancedTest, model.Backend,
            new(ModelKey: TextModelPolicy.Key(model)), "AI settings", model.Name + (advanced is null ? " · Benchmark" : " · Advanced test"), tab, request);
    }
    public static AiModelTestJobRequest Read(AiJobHeader job, JsonElement snapshot)
    {
        var request = snapshot.Deserialize<AiModelTestJobRequest>(AtomicJsonFile.Options) ?? throw new WorkspaceStoreException("The saved model test is missing.");
        Validate(request);
        if (job.Backend != request.Model.Backend || job.Target != new AiJobTarget(ModelKey: TextModelPolicy.Key(request.Model)) ||
            job.Kind != (request.Advanced ? AiJobKind.TextAdvancedTest : AiJobKind.TextBenchmark) || job.Batch is not null)
            throw new WorkspaceStoreException("This test belongs to another model or operation.");
        return request;
    }
    private static void Validate(AiModelTestJobRequest request)
    {
        var hostedV2 = request.Version == 2 && request.Model?.Backend == AiBackend.OpenRouter;
        var maxTokens = request.Model?.Backend == AiBackend.ComfyUI ? ComfyAdvancedMaxTokens : hostedV2 ? OpenRouterAdvancedMaxTokens : 2048;
        if (request.Version != 1 && !hostedV2 || request.Model is null || request.Settings is null || request.Test is null || request.Model.Backend is not (AiBackend.ComfyUI or AiBackend.OpenRouter) ||
            string.IsNullOrWhiteSpace(request.Test.Prompt) || request.Test.Prompt.Length > 12000 || request.Test.MaxOutputTokens < 1 || request.Test.MaxOutputTokens > maxTokens || request.Seed < 0)
            throw new WorkspaceStoreException($"Choose a ComfyUI or OpenRouter text model, a nonempty test message, and a token limit between 1 and {maxTokens:N0}.");
        TextModelPolicy.Validate(request.Model); FileAiSettingsStore.Validate(request.Settings);
        if (request.Model.Backend == AiBackend.ComfyUI && !TextModelPolicy.SameServer(request.Settings.ComfyUrl, request.Model.ComfyUrl) || !request.Advanced &&
            (request.Test.Prompt != (hostedV2 ? OpenRouterBenchmarkPrompt : AiProviderRegistry.StandardBenchmarkPrompt) ||
             (hostedV2 ? request.Test.MaxOutputTokens != OpenRouterBenchmarkTokens
                 : request.Test.MaxOutputTokens != request.Settings.MaxOutputTokens && request.Test.MaxOutputTokens != AiProviderRegistry.LegacyBenchmarkTokens)))
            throw new WorkspaceStoreException("The model test no longer matches its captured server or standard benchmark.");
    }
    private async Task<AiJobOutcome> RunAsync(AiJobContext context, AiModelTestJobRequest request, CancellationToken ct)
    {
        var result = await context.ReadOperationAsync<AiModelTestJobResult>("model-test", AiOperationArtifact.TestResult, ct);
        if (result is null && request.Model.Backend == AiBackend.OpenRouter)
            result = await context.ReadOperationAsync<AiModelTestJobResult>("model-test", AiOperationArtifact.Output, ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            if (result is null)
            {
                result = await runner.RunQueuedTestAsync(context, request, execution, linked.Token);
                await context.SaveOperationAsync("model-test", AiOperationArtifact.TestResult, result, ct);
            }
            if (request.Model.Backend == AiBackend.OpenRouter)
            {
                if (result.OpenRouter is not { } measured || measured.Model != request.Model.Model || measured.TestId != context.Job.Id || result.Verification is not null)
                    throw new WorkspaceStoreException("The test result belongs to another model.");
                if (result.Error is null && measured.Complete && !measured.HasReply && !measured.Refused &&
                    string.IsNullOrWhiteSpace(result.Refusal) && measured.FinishReason != "content_filter")
                    result = result with { Error = OpenRouterTestStatus.NoAnswerDetail(measured) };
                await context.SaveResultAsync(result); await context.MarkReviewableAsync(ct);
                await SaveOpenRouterBenchmarkAsync(measured, ct);
                await context.SaveResultAsync(result with { Saved = true });
                return result.Error is null ? AiJobOutcome.Complete() : AiJobOutcome.Attention(result.Error, AiJobRecovery.GenerateAgain);
            }
            if (result.Verification is not { } verification || !TextModelPolicy.SameServer(verification.ComfyUrl, request.Model.ComfyUrl) || verification.Model != request.Model.Model)
                throw new WorkspaceStoreException("The test result belongs to another model.");
            await context.SaveResultAsync(result); await context.MarkReviewableAsync(ct);
            await context.ReportAsync(new(new(GenerationPhase.Saving, "Saving model verification and measurements…")), true);
            await SaveVerificationAsync(verification, linked.Token);
            await context.SaveResultAsync(result with { Saved = true });
            return AiJobOutcome.Complete();
        }
        catch (Exception e) when (e is WorkspaceStoreException or IOException)
        {
            var remote = await context.ExecutionAsync(ct);
            var recovery = result is not null || remote.Submissions.Any(s => s.State == AiRemoteState.Finished) ? AiJobRecovery.RetryOutput
                : remote.MayBeRunning ? AiJobRecovery.CheckStatus : AiJobRecovery.GenerateAgain;
            throw new AiJobRecoveryException((recovery == AiJobRecovery.RetryOutput
                ? "The completed model test could not be saved. Retry download/save without generating again. "
                : "The model test could not finish. Inspect the saved request before retrying. ") + e.Message, recovery, e);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
            try { if (request.Model.Backend == AiBackend.ComfyUI) await execution.CancelAsync(context, Client, cancellation.Token); }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or AiGenerationException or WorkspaceStoreException) { }
            var remote = await context.ExecutionAsync(ct);
            throw new AiJobRecoveryException($"No new text or generation progress arrived for {request.Settings.TimeoutSeconds:N0} seconds. Its captured request and any completed response are retained.",
                remote.MayBeRunning ? AiJobRecovery.CheckStatus : remote.Submissions.Any(s => s.State == AiRemoteState.Finished) ? AiJobRecovery.RetryOutput : AiJobRecovery.GenerateAgain);
        }
    }
    private async Task SaveOpenRouterBenchmarkAsync(OpenRouterTextModelBenchmark benchmark, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var latest = await settings.LoadAsync(ct);
            if (latest.OpenRouterTextModelBenchmarks.Any(b => b.TestId == benchmark.TestId)) return;
            var benchmarks = latest.OpenRouterTextModelBenchmarks.Append(benchmark).GroupBy(b => b.Model, StringComparer.Ordinal)
                .SelectMany(g => g.OrderByDescending(b => b.MeasuredUtc).Take(5)).ToList();
            try { await settings.SaveAsync(latest with { OpenRouterTextModelBenchmarks = benchmarks }, cancellationToken: ct); return; }
            catch (WorkspaceConflictException) when (attempt < 4) { }
        }
    }
    private async Task SaveVerificationAsync(ComfyTextModelVerification verification, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var latest = await settings.LoadAsync(ct);
            if (latest.ComfyTextModelVerifications.Any(v => TextModelPolicy.SameServer(v.ComfyUrl, verification.ComfyUrl) && v.Model == verification.Model &&
                v.ComfyVersion == verification.ComfyVersion && (verification.Benchmarks ?? []).All(b => (v.Benchmarks ?? []).Contains(b)))) return;
            try { await settings.SaveAsync(latest with { ComfyTextModelVerifications = [.. latest.ComfyTextModelVerifications, verification] }, cancellationToken: ct); return; }
            catch (WorkspaceConflictException) when (attempt < 4) { }
        }
    }
    private HttpClient Client(string server)
    {
        var http = clients.CreateClient("ComfyUI"); http.BaseAddress = new(AiProviderRegistry.NormalizeComfyUrl(server) + "/");
        http.Timeout = Timeout.InfiniteTimeSpan; return http;
    }
}
