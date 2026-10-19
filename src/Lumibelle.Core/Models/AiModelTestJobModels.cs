namespace lumibelle.Models;

public sealed record AiModelTestJobRequest(int Version, TextModelReference Model, AiSettings Settings,
    ComfyTextModelTestRequest Test, bool Advanced, long Seed);
public sealed record AiModelTestJobResult(ComfyTextModelVerification? Verification, string? Response,
    bool Recovered = false, bool Saved = false, TextModelBenchmark? OpenRouter = null, string? Refusal = null, string? Error = null, string? GuardrailReport = null,
    ComfyTextModelBenchmark? PartialBenchmark = null, VllmTextModelBenchmark? Vllm = null);

public sealed record VllmTextModelBenchmark(string Server, float? Temperature, TextModelBenchmark Measurement)
{
    public double? TokensPerSecond => Measurement is { Complete: true, HasReply: true, Refused: false, OutputTokens: > 0, ElapsedSeconds: > 0 }
        ? Measurement.OutputTokens / Measurement.ElapsedSeconds : null;
}

public sealed record TextModelBenchmark(Guid TestId, string Model, DateTimeOffset MeasuredUtc,
    int TokenLimit, bool CustomPrompt, double ElapsedSeconds, double? FirstTextSeconds,
    int? InputTokens, int? OutputTokens, int? ReasoningTokens, int? CachedTokens, decimal? Cost,
    string? ResponseModel, string? Provider, string? GenerationId, string? FinishReason, bool Complete,
    bool? HasResponseText = null, bool Refused = false, bool ReasoningObserved = false)
{
    public bool HasReply => HasResponseText ?? FirstTextSeconds is not null;
    public bool ReasoningOnly => !HasReply && !Refused && (ReasoningObserved || ReasoningTokens is > 0);
    public bool InconsistentTokenCounts => OutputTokens is { } total && ReasoningTokens > total;
    public int? ReplyTokens => !HasReply ? 0 : OutputTokens is { } total && ReasoningTokens is { } reasoning && reasoning <= total ? total - reasoning : null;
    public double? TokensPerSecond => Complete && !Refused && ReplyTokens is > 0 && ElapsedSeconds > 0 ? ReplyTokens / ElapsedSeconds : null;
}
