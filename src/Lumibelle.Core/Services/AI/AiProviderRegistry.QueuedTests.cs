using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiProviderRegistry
{
    // Probe flags default to false so preparations saved by older versions still deserialize.
    private sealed record QueuedTestPreparation(string Version, CacheBaseline Cache, bool SystemPromptInput = false,
        ComfyVisionCapabilities? Vision = null);

    public async Task<AiModelTestJobResult> RunQueuedTestAsync(AiJobContext context, AiModelTestJobRequest request,
        ComfyJobExecution execution, CancellationToken ct)
    {
        if (request.Model.Backend == AiBackend.OpenRouter) return await RunOpenRouterTestAsync(context, request, ct);
        var caller = ct;
        using var inactivity = new TextInactivityWatchdog(request.Settings.TimeoutSeconds, ct, context.Clock);
        ct = inactivity.Token;
        const string operation = "model-test";
        using var http = clients.CreateClient("ComfyUI");
        http.BaseAddress = new(NormalizeComfyUrl(request.Settings.ComfyUrl) + "/"); http.Timeout = Timeout.InfiniteTimeSpan;
        var submitted = (await context.ExecutionAsync(ct)).Submissions.Any(s => s.Operation == operation);
        var retainedPreparation = await context.ReadOperationAsync<QueuedTestPreparation>(operation, AiOperationArtifact.TestPreparation, ct);
        QueuedTestPreparation preparation;
        if (!submitted && retainedPreparation is null)
        {
            if (context.Recovering) throw new AiJobRecoveryException("The app stopped before this test was submitted. Run a new test explicitly.", AiJobRecovery.GenerateAgain);
            await context.ReportAsync(new(new(GenerationPhase.Preparing, "Checking the captured model and ComfyUI queue…")), true);
            var catalog = await PrepareVerificationAsync(http, request.Model.Model, ct, ct);
            await context.ReportAsync(new(new(GenerationPhase.Preparing, "Clearing ComfyUI model cache…")), true);
            preparation = new(catalog.Version, await ClearCachesAndReadBaselineAsync(http, catalog.Memory, ct, ct), catalog.SystemPromptInput, catalog.Vision);
            await context.SaveOperationAsync(operation, AiOperationArtifact.TestPreparation, preparation, ct);
        }
        else preparation = retainedPreparation ?? throw new WorkspaceStoreException("The original benchmark environment could not be recovered.");
        // A resumed observer cannot claim to have measured peaks or timing during
        // the period when Lumibelle was offline. Preserve the verified response.
        var recovered = submitted || context.Recovering || retainedPreparation is not null;
        var memory = !recovered && preparation.Cache.Baseline is { } baseline ? new ComfyMemoryTracker(baseline, preparation.Cache.ClearConfirmed) : null;
        await using var sampler = memory is null ? null : new ComfyMemorySampler(http, memory, ct);
        var tokens = new TokenRateTracker(); var complete = false; string? response = null;
        var lastCheckpoint = DateTimeOffset.MinValue;
        var updates = submitted ? execution.ObserveAsync(context, operation, http, ct: ct, onProviderCompleted: inactivity.Stop)
            : execution.ExecuteAsync(context, operation, http, client => ComfyChatClient.BuildWorkflow(request.Model.Model,
                request.Advanced ? request.Test.Prompt : ComfyTextBenchmark.Prompt(request.Test.Prompt), request.Test.MaxOutputTokens, request.Advanced ? request.Settings.Temperature : .7f, request.Seed, client), ComfyChatClient.ExecutionOptions, ct, onProviderCompleted: inactivity.Stop);
        await foreach (var update in updates.WithCancellation(ct))
        {
            inactivity.Observe(update.Progress); if (update.Complete) inactivity.Stop();
            tokens.Observe(update.Progress); await context.ReportAsync(new(update.Progress)); complete |= update.Complete;
            if (request.Advanced && update.Complete && update.Job is { } output) response = ComfyChatClient.TryReadText(output, out var text) ? text : "";
            if (!complete && context.Clock.GetUtcNow() - lastCheckpoint >= TimeSpan.FromSeconds(1))
            {
                lastCheckpoint = context.Clock.GetUtcNow();
                try { await context.SaveResultAsync(new AiModelTestJobResult(null, response, PartialBenchmark: memory?.Build(request.Test.MaxOutputTokens, tokens.GeneratedTokens, tokens.TokensPerSecond, request.Advanced))); }
                catch (WorkspaceStoreException) { /* Keep observing the submitted workflow if a checkpoint fails. */ }
            }
        }
        if (!complete) throw new AiJobRecoveryException("The test did not return a completed response. Check its saved remote job.", AiJobRecovery.CheckStatus);
        if (sampler is not null) await sampler.StopAsync();
        var benchmark = memory?.Build(request.Test.MaxOutputTokens, tokens.GeneratedTokens, tokens.TokensPerSecond, request.Advanced) ??
            new(DateTimeOffset.UtcNow, null, null, null, null, null, null, null, request.Test.MaxOutputTokens,
                recovered ? null : tokens.GeneratedTokens, recovered ? null : tokens.TokensPerSecond, preparation.Cache.ClearConfirmed, request.Advanced);
        if (!request.Advanced) benchmark = benchmark with { ContextTokens = ComfyTextBenchmark.ContextTokens };
        // Probes are new submissions, which recovery never makes; a recovered test records no capabilities.
        ComfyTextModelCapabilities? capabilities = null;
        if (!recovered)
        {
            capabilities = await ComfyTextCapabilities.ProbeAsync(http, request.Model.Model, preparation.SystemPromptInput,
                preparation.Vision ?? new(false, false), (name, workflow, token) => RunQueuedProbeAsync(context, execution, http, operation + "/" + name, workflow,
                    request.Settings.TimeoutSeconds, token), message => context.ReportAsync(new(new(GenerationPhase.Finalizing, message)), true), caller);
        }
        return new(new(NormalizeComfyUrl(request.Model.ComfyUrl!), preparation.Version, request.Model.Model, DateTimeOffset.UtcNow, [benchmark])
            { Capabilities = capabilities }, request.Advanced ? response ?? "" : null, recovered);
    }

    private async Task<string?> RunQueuedProbeAsync(AiJobContext context, ComfyJobExecution execution, HttpClient http, string operation,
        Func<string, object> workflow, int timeoutSeconds, CancellationToken caller)
    {
        using var inactivity = new TextInactivityWatchdog(timeoutSeconds, caller, context.Clock);
        try
        {
            await foreach (var update in execution.ExecuteAsync(context, operation, http, workflow, ComfyChatClient.ExecutionOptions,
                inactivity.Token, onProviderCompleted: inactivity.Stop).WithCancellation(inactivity.Token))
            {
                inactivity.Observe(update.Progress);
                if (update.Complete && update.Job is { } job) return ComfyChatClient.TryReadText(job, out var text) ? text : null;
            }
            return null;
        }
        // A rejected, failed or stalled probe means the capability was not observed; it does not fail the test.
        catch (Exception e) when (e is AiGenerationException or AiJobRecoveryException or OperationCanceledException && !caller.IsCancellationRequested)
        {
            if (inactivity.Expired)
            {
                // Stop the stalled probe so it neither holds the ComfyUI queue nor remains an uncertain submission.
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller);
                cancellation.CancelAfter(TimeSpan.FromSeconds(10));
                try { await execution.CancelAsync(context, ProbeClient, cancellation.Token); }
                catch (Exception cancel) when (cancel is HttpRequestException or OperationCanceledException or AiGenerationException or WorkspaceStoreException) { }
            }
            return null;
        }
    }

    private HttpClient ProbeClient(string server)
    {
        var http = clients.CreateClient("ComfyUI");
        http.BaseAddress = new(NormalizeComfyUrl(server) + "/"); http.Timeout = Timeout.InfiniteTimeSpan;
        return http;
    }
}
