using System.Net;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed class ComfyTextCapacityTests
{
    private const string File = "Qwen3.8-27B-test.safetensors";
    private static readonly TextModelReference Model = new(AiBackend.ComfyUI, File, "Qwen", "http://127.0.0.1:8188");

    // Measured with Qwen3.8 27B on a 20 GB RTX 4000 Ada: 8k-token prompt, 2,048-token reply limit.
    private static ComfyTextModelBenchmark Measured(long? bytesPerToken = 80 * 1024) =>
        new(DateTimeOffset.UtcNow, "GPU", 0, 21469069312, 1291321344, 20494230600, 0, 3555186032, 2048, 359, 12.9, true, false)
        { ContextTokens = 8192, BytesPerToken = bytesPerToken };

    private static AiSettings Settings(ComfyTextModelBenchmark? benchmark, int? imageSide = null) => new()
    {
        ComfyTextModelVerifications = benchmark is null ? [] : [new(Model.ComfyUrl!, "0.38.0", File, DateTimeOffset.UtcNow, [benchmark])],
        ComfyTextModels = imageSide is null ? new() : new() { [TextModelPolicy.Key(Model)] = new(2048, .7f) { BatchImageSide = imageSide } }
    };

    private static byte[] Png(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(10, 20, 30, 255));
        using var output = new MemoryStream(); image.SaveAsPng(output); return output.ToArray();
    }

    private static ComfyTextInput Input(int characters, params byte[][] images)
    {
        var message = new ChatMessage(ChatRole.User, new string('a', characters));
        foreach (var image in images) message.Contents.Add(new DataContent(image, "image/png"));
        return ComfyTextVision.Capture([message]);
    }

    [Fact]
    public void BytesPerTokenIsTheReservedMemoryDifferenceBetweenTwoReplyLimits()
    {
        Assert.Equal(1000, ComfyTextCapacity.BytesPerToken(3_016_000, 1_000_000, 2048));
        Assert.Null(ComfyTextCapacity.BytesPerToken(3_000_000, null, 2048));
        Assert.Null(ComfyTextCapacity.BytesPerToken(3_000_000, 3_000_000, 2048));
        // Too close to the short run to separate per-token cost from noise.
        Assert.Null(ComfyTextCapacity.BytesPerToken(3_000_000, 1_000_000, 200));
    }

    [Fact]
    public void FastTokensAddTheFreeMemoryLeftAtThePeakKeepingAMargin()
    {
        var fast = ComfyTextCapacity.FastTokens(Measured());
        Assert.Equal((int)(8192 + 2048 + (21469069312 - 20494230600 - ComfyTextCapacity.MarginBytes) / (80 * 1024)), fast);
        Assert.InRange(fast!.Value, 12_000, 12_500);
        Assert.Null(ComfyTextCapacity.FastTokens(Measured(null)));
        Assert.Null(ComfyTextCapacity.FastTokens(Measured() with { CustomPrompt = true }));
        Assert.Null(ComfyTextCapacity.FastTokens(Measured() with { ContextTokens = null }));
        // A run that already exceeded GPU memory reports less than it measured, never a negative size.
        Assert.Equal(0, ComfyTextCapacity.FastTokens(Measured(1) with { PeakVramUsedBytes = 21469069312 }));
    }

    [Fact]
    public void EstimateCountsTextImagesAtTheirCanvasAndTheReplyLimit()
    {
        var batch = ComfyTextCapacity.Estimate(File, Input(4000, Png(2048, 2048), Png(2048, 1024)), 2048, 1024)!;
        Assert.Equal(2, batch.Images);
        Assert.Equal(2 * 1024, batch.ImageTokens);
        // The transcript adds role markers and the image-inspection instructions to the 4,000 characters.
        Assert.InRange(batch.TextTokens, 1000, 1300);
        Assert.Equal(batch.TextTokens + batch.ImageTokens + 2048, batch.Total);
        Assert.Equal(2 * 256, ComfyTextCapacity.Estimate(File, Input(10, Png(2048, 2048), Png(2048, 1024)), 1, 512)!.ImageTokens);
        // A single image keeps its captured size.
        Assert.Equal(40 * 20, ComfyTextCapacity.Estimate(File, Input(10, Png(1280, 640)), 1, 512)!.ImageTokens);
        Assert.Equal(280, ComfyTextCapacity.ImageTokens("gemma4_e4b.safetensors", 1024, 1024));
        Assert.Equal(1024, ComfyTextCapacity.ImageTokens(File, 1024, 1024));
    }

    [Fact]
    public void NoticeWarnsOnlyWhenARequestExceedsTheMeasuredCapacity()
    {
        var nine = Enumerable.Range(0, 9).Select(_ => Png(1024, 1024)).ToArray();
        var notice = ComfyTextCapacity.Notice(Model, Settings(Measured()), Input(19_400, nine), 2048);
        Assert.NotNull(notice);
        Assert.Contains("9 images", notice);
        Assert.Contains(ComfyTextCapacity.FastTokens(Measured())!.Value.ToString("N0"), notice);
        // The same request fits at 512 pixels, or when nothing has been measured yet.
        Assert.Null(ComfyTextCapacity.Notice(Model, Settings(Measured(), 512), Input(19_400, nine), 2048));
        Assert.Null(ComfyTextCapacity.Notice(Model, Settings(null), Input(19_400, nine), 2048));
        Assert.Null(ComfyTextCapacity.Notice(Model, Settings(Measured()), Input(4000), 2048));
    }

    [Fact]
    public void ImageSizeSettingIsValidatedAndBoundsTheBatchCanvas()
    {
        ComfyTextSettings.Validate(Settings(null, 768));
        Assert.Throws<WorkspaceStoreException>(() => ComfyTextSettings.Validate(Settings(null, 600)));
        Assert.Equal(768, ComfyTextSettings.BatchImageSide(Model, Settings(null, 768)));
        Assert.Equal(ComfyTextVision.BatchMaximumSide, ComfyTextSettings.BatchImageSide(Model, Settings(null)));
        Assert.Equal((512, 256), ComfyTextVision.BatchCanvas([(2048, 1024), (400, 200)], 512));
    }

    [Fact]
    public async Task ModelTestMeasuresMemoryPerTokenWithASecondShortRun()
    {
        using var server = new MemoryComfy();
        var registry = new AiProviderRegistry(new TestHttpFactory(server), new FakeAiSettingsStore(), server);
        ComfyTextModelVerification? verification = null;
        await foreach (var update in registry.VerifyComfyTextModelAsync(File, new(), TestContext.Current.CancellationToken))
            verification ??= update.Verification;

        var benchmark = Assert.Single(verification!.Benchmarks!);
        Assert.Equal([2048, ComfyTextCapacity.FootprintTokens], server.Limits);
        Assert.Equal((MemoryComfy.FullAllocated - MemoryComfy.ShortAllocated) / (2048 - ComfyTextCapacity.FootprintTokens), benchmark.BytesPerToken);
        Assert.Equal(MemoryComfy.FullAllocated, benchmark.PeakTorchAllocatedBytes);
        Assert.NotNull(ComfyTextCapacity.FastTokens(benchmark));
    }

    /// <summary>Reports more reserved memory while a longer reply limit is running.</summary>
    private sealed class MemoryComfy : HttpMessageHandler, IComfyExecutionMonitor
    {
        public const long FullAllocated = 3_500L << 20, ShortAllocated = 3_000L << 20;
        private volatile int _running;
        public List<int> Limits { get; } = [];

        public async IAsyncEnumerable<ComfyExecutionUpdate> ExecuteAsync(HttpClient http, Func<string, object> workflowFactory,
            ComfyExecutionOptions options, CancellationToken operationToken,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken callerToken)
        {
            var workflow = JsonSerializer.SerializeToElement(workflowFactory("client"));
            var limit = workflow.GetProperty("prompt").GetProperty("2").GetProperty("inputs").GetProperty("max_length").GetInt32();
            Limits.Add(limit);
            _running = limit;
            // Long enough for several memory samples.
            await Task.Delay(900, callerToken);
            _running = 0;
            var job = JsonSerializer.SerializeToElement(new { outputs = new Dictionary<string, object> { ["3"] = new { text = new[] { "Mist over the forest." } } } });
            yield return new(new(GenerationPhase.Completed, "Complete"), "prompt", job, true);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object body = request.RequestUri!.AbsolutePath switch
            {
                "/object_info" => new Dictionary<string, object>
                {
                    ["CLIPLoader"] = new { input = new { required = new { clip_name = new object[] { new[] { File } } } } },
                    ["TextGenerate"] = new { input = new { required = new { max_length = new object[] { "INT" } } } },
                    ["PreviewAny"] = new { }
                },
                "/system_stats" => Stats(),
                "/queue" => new { queue_running = Array.Empty<object>(), queue_pending = Array.Empty<object>() },
                _ => new { }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") });
        }

        private object Stats()
        {
            var allocated = _running switch { 0 => 0, ComfyTextCapacity.FootprintTokens => ShortAllocated, _ => FullAllocated };
            const long reserved = 4_000L << 20;
            return new
            {
                system = new { comfyui_version = "0.38.0" },
                devices = new[] { new { name = "GPU", index = 0, vram_total = 20L << 30, vram_free = (18L << 30) - allocated,
                    torch_vram_total = _running == 0 ? 0 : reserved, torch_vram_free = _running == 0 ? 0 : reserved - allocated } }
            };
        }
    }
}
