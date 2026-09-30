using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

/// <summary>Approximate size of one ComfyUI text request, in the tokens TextGenerate needs memory for.</summary>
public sealed record ComfyTextRequestSize(int InstructionTokens, int ContentTokens, int Images, int ImageTokens, int ReplyTokens)
{
    public int PromptTokens => InstructionTokens + ContentTokens + ImageTokens;
}

/// <summary>One request, or one step of a two-step composition, with the model's prompt capacity at that step's reply limit.</summary>
public sealed record ComfyTextStageSize(string? Step, ComfyTextRequestSize Size, int? Capacity)
{
    public bool TooLarge => Capacity is { } capacity && Size.PromptTokens > capacity;
}

/// <summary>One capacity run: whether it finished or ran out of GPU memory, and the peaks observed while it ran.</summary>
public sealed record ComfyMeasuredRun(bool Completed, bool OutOfMemory, long? PeakTorchAllocatedBytes, long? PeakVramUsedBytes,
    double? TokensPerSecond = null);

public delegate Task<ComfyMeasuredRun> ComfyMeasuredRunner(string name, Func<string, object> workflowFactory, CancellationToken ct);

/// <summary>
/// Estimates how large a prompt a model can read without running out of GPU memory, or, with dynamic VRAM
/// loading, without streaming its weights from system RAM (many times slower). Reading the prompt needs working
/// memory for every prompt token at once, while each reply token only reserves KV cache, so the model test prices
/// the two separately: the same prompt with a short reply limit isolates the reply cost, and a larger prompt with
/// that short limit isolates the prompt cost.
/// </summary>
public static class ComfyTextCapacity
{
    /// <summary>Reply limit of the capacity runs.</summary>
    public const int FootprintTokens = 32;
    private const int MinimumSpread = 256;
    // Room kept free for the desktop, allocator fragmentation and the vision encoder's working memory.
    public const long MarginBytes = 768L << 20;
    private const int CharactersPerToken = 4;
    // Qwen vision: 16-pixel patches merged 2×2, without downscaling below 12.8 megapixels.
    private const int PixelsPerImageToken = 32;
    // Gemma 4 pools each image to at most 280 soft tokens.
    private const int GemmaImageTokens = 280;

    internal static long? PerToken(long larger, long? smaller, int tokens) =>
        smaller is { } small && tokens >= MinimumSpread && larger > small ? (larger - small) / tokens : null;

    /// <summary>Runs the capacity measurements after a standard benchmark and records them on it.</summary>
    public static async Task<ComfyTextModelBenchmark> MeasureAsync(ComfyTextModelBenchmark benchmark, string model, string instruction,
        long seed, ComfyMeasuredRunner run, CancellationToken ct)
    {
        if (benchmark.CustomPrompt || benchmark.PeakTorchAllocatedBytes is not { } full || benchmark.ContextTokens is not { } context) return benchmark;
        object Workflow(string client, int tokens) =>
            ComfyChatClient.BuildWorkflow(model, ComfyTextBenchmark.Prompt(instruction, tokens), FootprintTokens, 0.7f, seed, client);
        var footprint = await run("footprint", client => Workflow(client, context), ct);
        if (!footprint.Completed || footprint.PeakTorchAllocatedBytes is not { } shortPeak) return benchmark;
        benchmark = benchmark with { BytesPerReplyToken = PerToken(full, shortPeak, benchmark.TokenLimit - FootprintTokens) };
        foreach (var larger in new[] { ComfyTextBenchmark.LargeContextTokens, ComfyTextBenchmark.FallbackContextTokens })
        {
            var measured = await run("context-" + larger, client => Workflow(client, larger), ct);
            // With dynamic VRAM loading a prompt that does not fit is streamed from system RAM instead of failing.
            if (measured.OutOfMemory || ComfyGenerationWatch.IsSlow(measured.TokensPerSecond, benchmark.TokensPerSecond))
            { benchmark = benchmark with { OutOfMemoryContextTokens = larger }; continue; }
            if (measured is { Completed: true, PeakTorchAllocatedBytes: { } largePeak, PeakVramUsedBytes: { } device })
                benchmark = benchmark with { BytesPerPromptToken = PerToken(largePeak, shortPeak, larger - context),
                    CapacityContextTokens = larger, CapacityPeakVramUsedBytes = device };
            break;
        }
        return benchmark;
    }

    /// <summary>Prompt tokens (text and images) that fit with the given reply limit, under the benchmark's conditions.</summary>
    public static int? PromptCapacity(ComfyTextModelBenchmark? benchmark, int replyTokens)
    {
        if (benchmark is not { CustomPrompt: false, BytesPerPromptToken: { } perPrompt and > 0, CapacityContextTokens: { } measured,
            CapacityPeakVramUsedBytes: { } peak, VramTotalBytes: { } total }) return null;
        // The capacity run reserved FootprintTokens of reply; a longer reply limit reserves more KV cache.
        var reply = (long)(replyTokens - FootprintTokens) * (benchmark.BytesPerReplyToken ?? perPrompt);
        var tokens = measured + (total - peak - MarginBytes - reply) / perPrompt;
        if (benchmark.OutOfMemoryContextTokens is { } failed) tokens = Math.Min(tokens, failed);
        return (int)Math.Clamp(tokens, 0, int.MaxValue);
    }

    /// <summary>Upper bound for a raised reply limit: a model that rambles would otherwise run for many minutes.</summary>
    public const int RaisedReplyCap = 8192;

    /// <summary>The largest reply limit that fits beside a prompt of the given size, or null without a capacity measurement.</summary>
    public static int? ReplyCapacity(ComfyTextModelBenchmark? benchmark, int promptTokens)
    {
        if (benchmark is not { CustomPrompt: false, BytesPerPromptToken: { } perPrompt and > 0, CapacityContextTokens: { } measured,
            CapacityPeakVramUsedBytes: { } peak, VramTotalBytes: { } total }) return null;
        if (benchmark.OutOfMemoryContextTokens is { } failed && promptTokens >= failed) return 0;
        var room = total - peak - MarginBytes - (long)(promptTokens - measured) * perPrompt;
        return (int)Math.Clamp(FootprintTokens + room / (benchmark.BytesPerReplyToken ?? perPrompt), 0, 32768);
    }

    /// <summary>
    /// Raises a ComfyUI request's reply limit to what fits beside its prompt, up to <see cref="RaisedReplyCap"/>. A larger limit
    /// costs no time, since the model stops at its natural end, only memory. It never lowers the model's configured limit, and
    /// leaves explicit LLM-profile limits and models set to a fixed limit alone.
    /// </summary>
    public static AiTextJobRequest FitReplyLimit(AiTextJobRequest request)
    {
        if (request.Model.Backend != AiBackend.ComfyUI || request.Version >= 3 && request.Model.MaxOutputTokens is not null ||
            ComfyTextSettings.Resolve(request.Model, request.Settings).FixedReplyLimit) return request;
        // The last step is the one that uses the reply limit; a visual-brief step keeps its own short limit.
        if (Assess(request)?.LastOrDefault() is not { } stage || ReplyCapacity(Benchmark(request.Model, request.Settings), stage.Size.PromptTokens) is not { } fits)
            return request;
        var raised = Math.Min(RaisedReplyCap, fits) / 256 * 256;
        return raised > request.Settings.MaxOutputTokens ? request with { Settings = request.Settings with { MaxOutputTokens = raised } } : request;
    }

    /// <summary>The newest standard benchmark with capacity measurements for this model and server.</summary>
    public static ComfyTextModelBenchmark? Benchmark(TextModelReference model, AiSettings settings) =>
        TextModelPolicy.Verification(model, settings)?.Benchmarks?
            .Where(b => !b.CustomPrompt && (b.BytesPerPromptToken is not null || b.OutOfMemoryContextTokens is not null)).MaxBy(b => b.MeasuredUtc);

    public static ComfyTextRequestSize? Estimate(string model, ComfyTextInput input, int replyTokens, int batchImageSide)
    {
        IReadOnlyList<(int Width, int Height)> sizes;
        try
        {
            sizes = input.Images.Count == 0 ? [] : ComfyTextVision.InspectSizes(input.Images);
            // Batches are letterboxed to one shared canvas; a single image keeps its captured size.
            if (sizes.Count > 1) sizes = Enumerable.Repeat(ComfyTextVision.BatchCanvas(sizes, batchImageSide), sizes.Count).ToArray();
        }
        catch (WorkspaceStoreException) { return null; }
        // Leading instructions (the system prompt, or its marked transcript rows) are reported apart from task content.
        var instructions = input.SystemPrompt?.Length ?? SystemRows(input.Transcript);
        return new(instructions / CharactersPerToken, (input.Transcript.Length - (input.SystemPrompt is null ? instructions : 0)) / CharactersPerToken,
            sizes.Count, sizes.Sum(size => ImageTokens(model, size.Width, size.Height)), replyTokens);
    }

    private static int SystemRows(string transcript)
    {
        var user = transcript.IndexOf("\n\n[user]\n", StringComparison.Ordinal);
        return transcript.StartsWith("[system]\n", StringComparison.Ordinal) && user > 0 ? user : 0;
    }

    public static int ImageTokens(string model, int width, int height)
    {
        var tokens = (int)(Math.Ceiling(width / (double)PixelsPerImageToken) * Math.Ceiling(height / (double)PixelsPerImageToken));
        // Only an estimate, so the file name is an acceptable family hint here.
        return model.Contains("gemma", StringComparison.OrdinalIgnoreCase) ? Math.Min(GemmaImageTokens, tokens) : tokens;
    }

    /// <summary>
    /// The captured request's size per step with the model's prompt capacity; null for other backends. A two-step composition
    /// has a visual-brief step with the images and a composition step with the brief; a brief not yet written counts at its full limit.
    /// </summary>
    public static IReadOnlyList<ComfyTextStageSize>? Assess(AiTextJobRequest request)
    {
        if (request.Model.Backend != AiBackend.ComfyUI) return null;
        var reply = TextGenerationOptions.Captured(request).MaxOutputTokens ?? request.Settings.MaxOutputTokens;
        var side = ComfyTextSettings.BatchImageSide(request.Model, request.Settings);
        var benchmark = Benchmark(request.Model, request.Settings);
        ComfyTextStageSize? Stage(string? step, IReadOnlyList<AiTextMessage> messages, int replyTokens) =>
            Estimate(request.Model.Model, ComfyTextVision.Capture(messages.Select(m => m.ToMessage())), replyTokens, side) is { } size
                ? new(step, size, PromptCapacity(benchmark, replyTokens)) : null;
        if (!request.TwoStep) return Stage(null, request.Messages, reply) is { } single ? [single] : null;
        var briefTokens = Math.Min(Production.PromptComposer.BriefTokens, reply);
        var stages = new List<ComfyTextStageSize>();
        if (request.BriefMessages is { } briefMessages && Stage("Step 1 (visual brief)", briefMessages, briefTokens) is { } first) stages.Add(first);
        var brief = request.VisualBrief ?? new string('.', briefTokens * CharactersPerToken);
        if (Stage("Step 2 (composition)", Production.PromptComposer.WithBrief(request.Messages, brief), reply) is { } second) stages.Add(second);
        return stages;
    }

    /// <summary>A warning when a step's prompt is larger than the model reads within GPU memory, or null.</summary>
    public static string? Notice(AiTextJobRequest request) => Assess(request)?.FirstOrDefault(stage => stage.TooLarge) is { } over
        ? (over.Step is { } step ? step + ": t" : "T") + $"his prompt is about {over.Size.PromptTokens:N0} tokens ({Describe(over.Size)}), but " +
          $"{TextModelPolicy.DisplayName(request.Model, request.Settings)} reads only about {over.Capacity:N0} within GPU memory at a " +
          $"{over.Size.ReplyTokens:N0}-token reply limit. It may run out of memory or be very slow. " +
          "Use fewer references, a smaller image size or reduced script context, or a smaller model."
        : null;

    /// <summary>What the measured capacity means at a reply limit and image size, or null before a capacity test.</summary>
    public static string? Summary(TextModelReference model, AiSettings settings, int replyTokens, int imageSide)
    {
        var benchmark = Benchmark(model, settings);
        var failed = benchmark?.OutOfMemoryContextTokens is { } oom ? $" A {oom:N0}-token test prompt did not fit in GPU memory." : "";
        if (PromptCapacity(benchmark, replyTokens) is not { } capacity)
            return failed.Length == 0 ? null : failed.TrimStart() + " Keep prompts well below that, or test again after freeing GPU memory.";
        return $"Prompts up to about {capacity:N0} tokens fit in GPU memory at a {replyTokens:N0}-token reply limit (measured {benchmark!.MeasuredUtc.ToLocalTime():d}): " +
            $"roughly {capacity * CharactersPerToken:N0} characters of text, with each reference image using up to {ImageTokens(model.Model, imageSide, imageSide):N0} of them. " +
            "Larger prompts run out of memory or, with dynamic VRAM loading, become very slow." + failed;
    }

    public static string Describe(ComfyTextRequestSize size) =>
        $"{size.InstructionTokens:N0} instructions, {size.ContentTokens:N0} task and context" +
        (size.Images > 0 ? $", {size.Images} {(size.Images == 1 ? "image" : "images")} {size.ImageTokens:N0}" : "");
}
