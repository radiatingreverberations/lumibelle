using lumibelle.Models;

namespace lumibelle.Services.AI;

public sealed record OpenRouterTestStatus(string Label, string Detail, bool WarnBeforeStarring)
{
    public static OpenRouterTestStatus For(TextModelReference model,
        IEnumerable<TextModelBenchmark> benchmarks, IEnumerable<AiJobHeader> jobs)
    {
        // Custom prompts can deliberately provoke refusals. Use the neutral benchmark
        // to indicate whether a routine request completed for this exact model.
        var benchmark = benchmarks.Where(b => b.Model == model.Model && !b.CustomPrompt)
            .OrderByDescending(b => b.MeasuredUtc).FirstOrDefault();
        var key = TextModelPolicy.Key(model);
        var job = jobs.Where(j => j.Backend == AiBackend.OpenRouter && j.Kind == AiJobKind.TextBenchmark && j.Target.ModelKey == key)
            .OrderByDescending(j => j.CreatedUtc).FirstOrDefault();
        if (job is not null && (benchmark is null || job.Id == benchmark.TestId || job.CreatedUtc >= benchmark.MeasuredUtc))
        {
            if (job.State == AiJobState.NeedsAttention)
            {
                if (job.Recovery != AiJobRecovery.RetryOutput && benchmark?.TestId == job.Id && benchmark.ReasoningOnly)
                    return new("Reasoning only · no answer", NoAnswerDetail(benchmark), true);
                return new(job.Recovery == AiJobRecovery.RetryOutput ? "Benchmark result not saved" : "Last benchmark failed",
                    job.Error ?? "The benchmark needs attention. Open AI activity to review it.", true);
            }
            if (job.State is AiJobState.Waiting or AiJobState.Running)
                return new(job.State == AiJobState.Waiting ? "Benchmark queued" : "Benchmark running",
                    "Wait for the benchmark result before deciding whether to star this model.", true);
        }
        if (benchmark is null)
            return new("Not tested", "This model has no saved standard benchmark. Catalog discovery does not test generation.", true);
        if (benchmark.ReasoningOnly) return new("Reasoning only · no answer", NoAnswerDetail(benchmark), true);
        if (benchmark.Complete && benchmark.HasReply && !benchmark.Refused && benchmark.FinishReason is "stop" or "length")
            return new("Benchmark completed", $"Completed {benchmark.MeasuredUtc.ToLocalTime():g}. Availability can change between requests.", false);
        return new(benchmark.FinishReason == "content_filter" ? "Benchmark blocked" : "Benchmark incomplete",
            "The last standard benchmark did not complete with a text reply. Run another test to check this model.", true);
    }

    public static string NoAnswerDetail(TextModelBenchmark benchmark) => benchmark.ReasoningOnly
        ? benchmark.FinishReason == "length"
            ? "The token limit was reached during reasoning before any answer text was returned. Try a larger total token limit in Advanced model test. Reasoning uses the same output budget."
            : "The model returned reasoning but no answer text. This does not confirm that it can complete a writing request."
        : "The model returned no answer text. This does not confirm that it can complete a writing request.";
}
