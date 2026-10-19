using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed class OpenRouterTestStatusTests
{
    private static readonly TextModelReference Model = new(AiBackend.OpenRouter, "provider/model:free", "Model");
    private static TextModelBenchmark Benchmark() => new(Guid.NewGuid(), Model.Model, DateTimeOffset.UtcNow,
        256, false, 2, .25, 10, 50, null, null, 0, Model.Model, "Provider", null, "stop", true);
    private static AiJobHeader Job(DateTimeOffset created, AiJobKind kind = AiJobKind.TextBenchmark) => new()
    {
        Id = Guid.NewGuid(), Kind = kind, Backend = Model.Backend, Target = new(ModelKey: TextModelPolicy.Key(Model)),
        ProjectName = "AI settings", TargetName = Model.Name, OriginTabId = Guid.NewGuid(), RequestFingerprint = "test",
        CreatedUtc = created, State = AiJobState.NeedsAttention, Recovery = AiJobRecovery.GenerateAgain, Error = "Rate limit reached."
    };

    [Fact]
    public void OnlyACompletedStandardTextBenchmarkSkipsTheWarning()
    {
        var benchmark = Benchmark();
        Assert.False(OpenRouterTestStatus.For(Model, [benchmark], []).WarnBeforeStarring);
        Assert.True(OpenRouterTestStatus.For(Model, [], []).WarnBeforeStarring);
        foreach (var unsuitable in new[] { benchmark with { CustomPrompt = true }, benchmark with { Complete = false },
                     benchmark with { FinishReason = "content_filter" }, benchmark with { FirstTextSeconds = null },
                     benchmark with { Model = Model.Model.ToUpperInvariant() } })
            Assert.True(OpenRouterTestStatus.For(Model, [unsuitable], []).WarnBeforeStarring);
        // Usage counts are optional and are not proof that a reply was received.
        Assert.False(OpenRouterTestStatus.For(Model, [benchmark with { OutputTokens = null, Cost = null, FinishReason = "length" }], []).WarnBeforeStarring);
    }

    [Fact]
    public void ANewFailureOverridesAnOldSuccessAndKeepsItsReason()
    {
        var benchmark = Benchmark(); var job = Job(benchmark.MeasuredUtc.AddMinutes(1));
        var status = OpenRouterTestStatus.For(Model, [benchmark], [job]);
        Assert.True(status.WarnBeforeStarring); Assert.Equal("Last benchmark failed", status.Label); Assert.Equal(job.Error, status.Detail);
        Assert.False(OpenRouterTestStatus.For(Model, [benchmark], [job with { CreatedUtc = benchmark.MeasuredUtc.AddMinutes(-1) }]).WarnBeforeStarring);
        Assert.False(OpenRouterTestStatus.For(Model, [benchmark], [job with { Kind = AiJobKind.TextAdvancedTest }]).WarnBeforeStarring);
        Assert.False(OpenRouterTestStatus.For(Model, [benchmark], [job with { Target = new(ModelKey: "other") }]).WarnBeforeStarring);
        var recovered = benchmark with { TestId = job.Id, MeasuredUtc = job.CreatedUtc.AddSeconds(1) };
        Assert.False(OpenRouterTestStatus.For(Model, [recovered], [job with { State = AiJobState.Completed }]).WarnBeforeStarring);
    }

    [Theory]
    [InlineData(AiJobState.Running, "Benchmark running")]
    [InlineData(AiJobState.Waiting, "Benchmark queued")]
    public void ActiveBenchmarksAreNotSuccesses(AiJobState state, string label)
    {
        var status = OpenRouterTestStatus.For(Model, [], [Job(DateTimeOffset.UtcNow) with { State = state }]);
        Assert.True(status.WarnBeforeStarring); Assert.Equal(label, status.Label);
    }

    [Fact]
    public void SaveFailuresAreDistinguishedFromRequestFailures()
    {
        var benchmark = Benchmark();
        var job = Job(benchmark.MeasuredUtc.AddSeconds(-1)) with { Id = benchmark.TestId, Recovery = AiJobRecovery.RetryOutput };
        var status = OpenRouterTestStatus.For(Model, [benchmark], [job]);
        Assert.True(status.WarnBeforeStarring); Assert.Equal("Benchmark result not saved", status.Label);
    }

    [Fact]
    public void LegacyReasoningOnlyMeasurementsRemainUnsuccessfulWithoutAJobRecord()
    {
        var benchmark = Benchmark() with { FirstTextSeconds = null, OutputTokens = 256, ReasoningTokens = 256, FinishReason = "length" };
        var status = OpenRouterTestStatus.For(Model, [benchmark], []);
        Assert.True(status.WarnBeforeStarring); Assert.Equal("Reasoning only · no answer", status.Label);
        Assert.Null(benchmark.TokensPerSecond); Assert.Equal(0, benchmark.ReplyTokens);
        Assert.Contains("token limit", status.Detail);
    }

    [Fact]
    public void MissingOrInconsistentTokenBreakdownsNeverInventReplySpeed()
    {
        var benchmark = Benchmark();
        Assert.Null((benchmark with { ReasoningTokens = null }).TokensPerSecond);
        var inconsistent = benchmark with { OutputTokens = 50, ReasoningTokens = 60 };
        Assert.True(inconsistent.HasReply); Assert.True(inconsistent.InconsistentTokenCounts);
        Assert.Null(inconsistent.ReplyTokens); Assert.Null(inconsistent.TokensPerSecond);
        Assert.Null((benchmark with { Refused = true }).TokensPerSecond);
        Assert.True(OpenRouterTestStatus.For(Model, [benchmark with { Refused = true }], []).WarnBeforeStarring);
    }
}
