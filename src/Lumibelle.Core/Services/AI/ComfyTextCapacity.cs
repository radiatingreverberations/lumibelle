using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

/// <summary>Approximate size of one ComfyUI text request, in the tokens TextGenerate reserves memory for.</summary>
public sealed record ComfyTextRequestSize(int TextTokens, int Images, int ImageTokens, int ReplyTokens)
{
    public int Total => TextTokens + ImageTokens + ReplyTokens;
}

/// <summary>
/// Estimates how many tokens a model can hold in GPU memory before ComfyUI streams its weights from system RAM,
/// which makes generation many times slower. TextGenerate reserves memory for prompt plus reply limit up front;
/// the model test measures that cost per token and the free memory left at its peak.
/// </summary>
public static class ComfyTextCapacity
{
    /// <summary>Reply limit of the second measurement, which shares the benchmark prompt.</summary>
    public const int FootprintTokens = 32;
    private const int MinimumSpread = 256;
    // Room kept free for the desktop, allocator fragmentation and the vision encoder's working memory.
    public const long MarginBytes = 768L << 20;
    private const int CharactersPerToken = 4;
    // Qwen vision: 16-pixel patches merged 2×2, without downscaling below 12.8 megapixels.
    private const int PixelsPerImageToken = 32;
    // Gemma 4 pools each image to at most 280 soft tokens.
    private const int GemmaImageTokens = 280;

    public static long? BytesPerToken(long fullPeak, long? footprintPeak, int tokenLimit) =>
        footprintPeak is { } small && tokenLimit - FootprintTokens >= MinimumSpread && fullPeak > small
            ? (fullPeak - small) / (tokenLimit - FootprintTokens) : null;

    /// <summary>Prompt plus reply tokens that fit in GPU memory under the benchmark's conditions.</summary>
    public static int? FastTokens(ComfyTextModelBenchmark? benchmark) => benchmark is
        { CustomPrompt: false, ContextTokens: { } context, BytesPerToken: > 0, VramTotalBytes: { } total, PeakVramUsedBytes: { } peak }
        ? (int)Math.Clamp(context + benchmark.TokenLimit + (total - peak - MarginBytes) / benchmark.BytesPerToken!.Value, 0, int.MaxValue)
        : null;

    /// <summary>The newest standard benchmark with a capacity estimate for this model and server.</summary>
    public static ComfyTextModelBenchmark? Benchmark(TextModelReference model, AiSettings settings) =>
        TextModelPolicy.Verification(model, settings)?.Benchmarks?.Where(b => FastTokens(b) is not null).MaxBy(b => b.MeasuredUtc);

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
        var text = (input.Transcript.Length + (input.SystemPrompt?.Length ?? 0)) / CharactersPerToken;
        return new(text, sizes.Count, sizes.Sum(size => ImageTokens(model, size.Width, size.Height)), replyTokens);
    }

    public static int ImageTokens(string model, int width, int height)
    {
        var tokens = (int)(Math.Ceiling(width / (double)PixelsPerImageToken) * Math.Ceiling(height / (double)PixelsPerImageToken));
        // Only an estimate, so the file name is an acceptable family hint here.
        return model.Contains("gemma", StringComparison.OrdinalIgnoreCase) ? Math.Min(GemmaImageTokens, tokens) : tokens;
    }

    /// <summary>A warning when the request is larger than the model keeps in GPU memory, or null.</summary>
    public static string? Notice(TextModelReference model, AiSettings settings, ComfyTextInput input, int replyTokens)
    {
        if (model.Backend != AiBackend.ComfyUI || FastTokens(Benchmark(model, settings)) is not { } fast ||
            Estimate(model.Model, input, replyTokens, ComfyTextSettings.BatchImageSide(model, settings)) is not { } size || size.Total <= fast) return null;
        var parts = $"{size.TextTokens:N0} text" + (size.Images > 0 ? $", {size.Images} {(size.Images == 1 ? "image" : "images")} ≈ {size.ImageTokens:N0}" : "") +
            $", up to {size.ReplyTokens:N0} reply";
        return $"This request needs about {size.Total:N0} tokens ({parts}), but {TextModelPolicy.DisplayName(model, settings)} stays in GPU memory only up to " +
            $"about {fast:N0} on this machine. Generation may be very slow. Use fewer references, a smaller image size or reply limit for this model, or a smaller model.";
    }
}
