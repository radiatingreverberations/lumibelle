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
    private const long Vram = 21469069312;
    private static readonly TextModelReference Model = new(AiBackend.ComfyUI, File, "Qwen", "http://127.0.0.1:8188");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Shaped like Qwen3.8 27B on a 20 GB GPU: reading the prompt costs far more per token than reserving reply tokens.
    private static ComfyTextModelBenchmark Measured() =>
        new(DateTimeOffset.UtcNow, "GPU", 0, Vram, 1291321344, 18_000_000_000, 0, 3555186032, 2048, 359, 12.9, true, false)
        {
            ContextTokens = ComfyTextBenchmark.ContextTokens, BytesPerReplyToken = 80 * 1024, BytesPerPromptToken = 360 * 1024,
            CapacityContextTokens = ComfyTextBenchmark.LargeContextTokens, CapacityPeakVramUsedBytes = 20_200_000_000
        };

    private static AiSettings Settings(ComfyTextModelBenchmark? benchmark, int? imageSide = null) => new()
    {
        ComfyTextModelVerifications = benchmark is null ? [] : [new(Model.ComfyUrl!, "0.38.0", File, DateTimeOffset.UtcNow, [benchmark])],
        ComfyTextModels = new() { [TextModelPolicy.Key(Model)] = new(2048, .7f) { BatchImageSide = imageSide } }
    };

    private static byte[] Png(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(10, 20, 30, 255));
        using var output = new MemoryStream(); image.SaveAsPng(output); return output.ToArray();
    }

    private static ChatMessage[] Messages(int systemCharacters, int userCharacters, params byte[][] images)
    {
        var user = new ChatMessage(ChatRole.User, new string('u', userCharacters));
        foreach (var image in images) user.Contents.Add(new DataContent(image, "image/png"));
        return [new(ChatRole.System, new string('s', systemCharacters)), user];
    }

    private static AiTextJobRequest Request(AiSettings settings, params ChatMessage[] messages) =>
        new(2, AiJobKind.Guidance, Model, false, settings, "test", .7f, 1, JsonSerializer.SerializeToElement(new { }),
            messages.Select(AiTextMessage.Capture).ToArray(), null);

    [Fact]
    public void PerTokenCostIsTheMemoryDifferenceBetweenTwoRuns()
    {
        Assert.Equal(1000, ComfyTextCapacity.PerToken(3_016_000, 2_000_000, 1016));
        Assert.Null(ComfyTextCapacity.PerToken(3_000_000, null, 2048));
        Assert.Null(ComfyTextCapacity.PerToken(3_000_000, 3_000_000, 2048));
        // Too close to separate per-token cost from noise.
        Assert.Null(ComfyTextCapacity.PerToken(3_000_000, 1_000_000, 200));
    }

    [Fact]
    public void PromptCapacityAddsTheFreeMemoryLeftAtTheLargePromptPeak()
    {
        var benchmark = Measured();
        var expected = 8192 + (Vram - 20_200_000_000 - ComfyTextCapacity.MarginBytes - (2048L - ComfyTextCapacity.FootprintTokens) * 80 * 1024) / (360 * 1024);
        Assert.Equal((int)expected, ComfyTextCapacity.PromptCapacity(benchmark, 2048));
        // A longer reply limit leaves less room for the prompt, but only at the reply-token cost.
        Assert.InRange(ComfyTextCapacity.PromptCapacity(benchmark, 8192)!.Value, (int)expected - 6144 * 80 / 360 - 1, (int)expected - 6144 * 80 / 360 + 1);
        Assert.Null(ComfyTextCapacity.PromptCapacity(benchmark with { BytesPerPromptToken = null }, 2048));
        Assert.Null(ComfyTextCapacity.PromptCapacity(benchmark with { CustomPrompt = true }, 2048));
        // A prompt size that ran out of memory caps the estimate; nothing is ever negative.
        Assert.Equal(8192, ComfyTextCapacity.PromptCapacity(benchmark with
            { CapacityContextTokens = 4096, OutOfMemoryContextTokens = 8192, CapacityPeakVramUsedBytes = 10_000_000_000 }, 2048));
        Assert.Equal(0, ComfyTextCapacity.PromptCapacity(benchmark with { CapacityPeakVramUsedBytes = Vram, CapacityContextTokens = 1 }, 2048));
    }

    [Fact]
    public void EstimateSplitsInstructionsTaskContentAndImagesAtTheirCanvas()
    {
        var input = ComfyTextVision.Capture(Messages(4000, 8000, Png(2048, 2048), Png(2048, 1024)));
        var batch = ComfyTextCapacity.Estimate(File, input, 2048, 1024)!;
        Assert.Equal(2, batch.Images);
        Assert.Equal(2 * 1024, batch.ImageTokens);
        // The image-inspection instructions are system rows too.
        Assert.InRange(batch.InstructionTokens, 1000, 1200);
        Assert.InRange(batch.ContentTokens, 2000, 2100);
        Assert.Equal(batch.InstructionTokens + batch.ContentTokens + batch.ImageTokens, batch.PromptTokens);
        Assert.Equal(2 * 256, ComfyTextCapacity.Estimate(File, input, 1, 512)!.ImageTokens);
        var native = ComfyTextCapacity.Estimate(File, ComfyTextVision.Capture(Messages(4000, 8000), nativeSystemPrompt: true), 1, 1024)!;
        Assert.Equal((1000, 2000), (native.InstructionTokens, native.ContentTokens));
        // A single image keeps its captured size.
        Assert.Equal(40 * 20, ComfyTextCapacity.Estimate(File, ComfyTextVision.Capture(Messages(10, 10, Png(1280, 640))), 1, 512)!.ImageTokens);
        Assert.Equal(280, ComfyTextCapacity.ImageTokens("gemma4_e4b.safetensors", 1024, 1024));
    }

    [Fact]
    public void NoticeWarnsOnlyWhenThePromptExceedsTheMeasuredCapacity()
    {
        var nine = Enumerable.Range(0, 9).Select(_ => Png(1024, 1024)).ToArray();
        var large = Messages(8600, 10800, nine);
        var notice = ComfyTextCapacity.Notice(Request(Settings(Measured()), large));
        Assert.NotNull(notice);
        Assert.Contains("9 images", notice);
        Assert.Contains(ComfyTextCapacity.PromptCapacity(Measured(), 2048)!.Value.ToString("N0"), notice);
        // The same request fits at 512 pixels; nothing is claimed before a capacity test.
        Assert.Null(ComfyTextCapacity.Notice(Request(Settings(Measured(), 512), large)));
        Assert.Null(ComfyTextCapacity.Notice(Request(Settings(null), large)));
        var stage = Assert.Single(ComfyTextCapacity.Assess(Request(Settings(Measured(), 512), large))!);
        Assert.Null(stage.Step);
        Assert.Equal(9 * 256, stage.Size.ImageTokens);
        Assert.False(stage.TooLarge);
    }

    [Fact]
    public void AutomaticImageSizeIsTheLargestThatFits()
    {
        int Side(AiSettings settings, ChatMessage[] messages) =>
            ComfyTextSettings.BatchImageSide(Model, ComfyTextCapacity.FitImageSide(Request(settings, messages)).Settings);
        var nine = Enumerable.Range(0, 9).Select(_ => Png(1024, 1024)).ToArray();
        // Nine references overflow at 1,024 pixels but fit at 512, as in the notice above; two fit at full size.
        Assert.Equal(512, Side(Settings(Measured()), Messages(8600, 10800, nine)));
        Assert.Equal(1024, Side(Settings(Measured()), Messages(8600, 10800, nine[..2])));
        // With a short prompt, nine references fit at 768 pixels (576 tokens each) but not at 1,024.
        Assert.Equal(768, Side(Settings(Measured()), Messages(2000, 2000, nine)));
        // Nothing measured: the full size. A chosen size, a single image or a prompt that never fits: as stated.
        Assert.Equal(1024, Side(Settings(null), Messages(8600, 10800, nine)));
        Assert.Equal(1024, Side(Settings(Measured(), 1024), Messages(8600, 10800, nine)));
        Assert.Equal(1024, Side(Settings(Measured()), Messages(8600, 10800, nine[..1])));
        Assert.Equal(512, Side(Settings(Measured()), Messages(8600, 400_000, nine)));
        // The size is recorded, so the request keeps it whatever the settings say later.
        Assert.Equal(512, ComfyTextCapacity.FitImageSide(Request(Settings(Measured()), Messages(8600, 10800, nine))).Settings.ComfyTextModels[TextModelPolicy.Key(Model)].BatchImageSide);
    }

    [Fact]
    public void ReplyCapacityShrinksAsThePromptGrows()
    {
        var small = ComfyTextCapacity.ReplyCapacity(Measured(), 2000)!.Value;
        var large = ComfyTextCapacity.ReplyCapacity(Measured(), 8900)!.Value;
        Assert.True(small > large);
        Assert.Equal(ComfyTextCapacity.FootprintTokens + (Vram - 20_200_000_000 - ComfyTextCapacity.MarginBytes - 708L * 360 * 1024) / (80 * 1024), large);
        Assert.Equal(0, ComfyTextCapacity.ReplyCapacity(Measured() with { OutOfMemoryContextTokens = 6000 }, 6000));
        Assert.Null(ComfyTextCapacity.ReplyCapacity(Measured() with { BytesPerPromptToken = null }, 2000));
    }

    [Fact]
    public void ReplyLimitIsRaisedToWhatFitsButNeverLowered()
    {
        // A short prompt leaves room: the limit rises to the cap.
        var drafting = ComfyTextCapacity.FitReplyLimit(Request(Settings(Measured()), Messages(1300, 6800)));
        Assert.Equal(ComfyTextCapacity.RaisedReplyCap, drafting.Settings.MaxOutputTokens);
        // A prompt that leaves less room than the configured limit keeps the configured limit.
        var crowded = ComfyTextCapacity.FitReplyLimit(Request(Settings(Measured()), Messages(8600, 30000)));
        Assert.Equal(2048, crowded.Settings.MaxOutputTokens);
        // Nothing measured, a fixed limit, a profile's explicit limit or another backend: unchanged.
        Assert.Equal(2048, ComfyTextCapacity.FitReplyLimit(Request(Settings(null), Messages(1300, 6800))).Settings.MaxOutputTokens);
        var fixedLimit = Settings(Measured()) with { ComfyTextModels = new() { [TextModelPolicy.Key(Model)] = new(2048, .7f) { FixedReplyLimit = true } } };
        Assert.Equal(2048, ComfyTextCapacity.FitReplyLimit(Request(fixedLimit, Messages(1300, 6800))).Settings.MaxOutputTokens);
        var profile = Request(Settings(Measured()), Messages(1300, 6800)) with { Version = 3, Model = Model with { MaxOutputTokens = 3000 } };
        Assert.Equal(2048, ComfyTextCapacity.FitReplyLimit(profile).Settings.MaxOutputTokens);
        var hosted = Request(Settings(Measured()), Messages(1300, 6800)) with { Model = new(AiBackend.OpenRouter, "hosted", "Hosted") };
        Assert.Equal(2048, ComfyTextCapacity.FitReplyLimit(hosted).Settings.MaxOutputTokens);
    }

    [Theory]
    [InlineData(1000, ComfyTextFit.Fits)]
    [InlineData(1100, ComfyTextFit.AtLimit)]
    [InlineData(1101, ComfyTextFit.TooLarge)]
    public void StagesUpToTenPercentOverAreOnlyAtTheLimit(int promptTokens, ComfyTextFit fit)
    {
        var stage = new ComfyTextStageSize(null, new(promptTokens, 0, 0, 0, 2048), 1000);
        Assert.Equal(fit, stage.Fit);
        Assert.Equal(fit == ComfyTextFit.TooLarge, stage.TooLarge);
        Assert.Equal(ComfyTextFit.Unknown, stage with { Capacity = null } is { } unknown ? unknown.Fit : default);
    }

    [Fact]
    public void SummaryExplainsCapacityOrAnOutOfMemoryResult()
    {
        var summary = ComfyTextCapacity.Summary(Model, Settings(Measured()), 2048, 512)!;
        Assert.Contains(ComfyTextCapacity.PromptCapacity(Measured(), 2048)!.Value.ToString("N0"), summary);
        Assert.Contains("256", summary);
        Assert.Null(ComfyTextCapacity.Summary(Model, Settings(null), 2048, 512));
        var failed = Measured() with { BytesPerPromptToken = null, CapacityContextTokens = null, OutOfMemoryContextTokens = 4096 };
        Assert.Contains(4096.ToString("N0") + "-token test prompt did not fit in GPU memory", ComfyTextCapacity.Summary(Model, Settings(failed), 2048, 512));
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MeasurementFallsBackToASmallerPromptWhenTheLargeOneRunsOutOfMemory(bool largeFails, bool fallbackFails)
    {
        var runs = new List<string>();
        Task<ComfyMeasuredRun> Run(string name, Func<string, object> workflow, CancellationToken _)
        {
            runs.Add(name);
            var oom = name == "context-8192" && largeFails || name == "context-4096" && fallbackFails;
            var context = name.StartsWith("context-", StringComparison.Ordinal) ? int.Parse(name[8..]) : ComfyTextBenchmark.ContextTokens;
            return Task.FromResult(new ComfyMeasuredRun(!oom, oom, oom ? null : 1_000_000_000L + context * 300_000L, 15_000_000_000));
        }
        var start = Measured() with { BytesPerReplyToken = null, BytesPerPromptToken = null, CapacityContextTokens = null, CapacityPeakVramUsedBytes = null,
            PeakTorchAllocatedBytes = 1_000_000_000L + 2048 * 300_000L + 2016 * 50_000L };
        var measured = await ComfyTextCapacity.MeasureAsync(start, File, "Describe.", 7, Run, Ct);

        Assert.Equal(50_000, measured.BytesPerReplyToken);
        Assert.Equal(largeFails ? ["footprint", "context-8192", "context-4096"] : ["footprint", "context-8192"], runs);
        Assert.Equal(largeFails ? fallbackFails ? 4096 : 8192 : null, measured.OutOfMemoryContextTokens);
        Assert.Equal(fallbackFails ? null : 300_000, measured.BytesPerPromptToken);
        Assert.Equal(fallbackFails ? null : largeFails ? 4096 : 8192, measured.CapacityContextTokens);
    }

    [Fact]
    public async Task ACapacityRunThatCrawlsDidNotFit()
    {
        // With dynamic VRAM loading an oversized prompt is streamed from system RAM instead of failing.
        Task<ComfyMeasuredRun> Run(string name, Func<string, object> workflow, CancellationToken _) =>
            Task.FromResult(new ComfyMeasuredRun(true, false, 1_000_000_000L + (name == "context-8192" ? 8192 : name == "context-4096" ? 4096 : 2048) * 300_000L,
                15_000_000_000, name == "context-8192" ? 0.6 : 12));
        var start = Measured() with { BytesPerReplyToken = null, BytesPerPromptToken = null, CapacityContextTokens = null, CapacityPeakVramUsedBytes = null,
            PeakTorchAllocatedBytes = 1_000_000_000L + 2048 * 300_000L + 2016 * 50_000L };
        var measured = await ComfyTextCapacity.MeasureAsync(start, File, "Describe.", 7, Run, Ct);
        Assert.Equal(8192, measured.OutOfMemoryContextTokens);
        Assert.Equal(4096, measured.CapacityContextTokens);
    }

    private static GenerationProgress Tokens(double current, double seconds, string stage = "2") =>
        new(GenerationPhase.Generating, "Generating text", current, 2048, "tokens", TimeSpan.FromSeconds(seconds)) { ExecutionStageId = stage };

    [Fact]
    public void GenerationWatchNoticesStreamingButNotNormalSpeed()
    {
        var normal = new ComfyGenerationWatch(12.6);
        Assert.All(Enumerable.Range(1, 30).Select(i => normal.Observe(Tokens(i * 12, i))), Assert.Null);

        var slow = new ComfyGenerationWatch(12.6);
        Assert.Null(slow.Observe(Tokens(1, 1)));
        Assert.Null(slow.Observe(Tokens(10, 15)));  // too short a window to judge
        Assert.Contains("far below the " + 12.6.ToString("N1"), slow.Observe(Tokens(15, 25)));
        // A new generation (the second step of a composition) starts over.
        Assert.Null(slow.Observe(Tokens(1, 30)));

        var reading = new ComfyGenerationWatch(12.6);
        Assert.Null(reading.Observe(new(GenerationPhase.Preparing, "Preparing the text model…", Elapsed: TimeSpan.FromSeconds(5)) { ExecutionStageId = "2" }));
        Assert.Contains("Still reading the prompt after 2 minutes",
            reading.Observe(new(GenerationPhase.Preparing, "Preparing the text model…", Elapsed: TimeSpan.FromSeconds(126)) { ExecutionStageId = "2" }));
        // Without a tested speed there is nothing to compare with.
        Assert.Null(new ComfyGenerationWatch(null).Observe(Tokens(15, 25)));
        Assert.Equal(12.9, ComfyGenerationWatch.ExpectedTokensPerSecond(Model, Settings(Measured())));
    }

    [Fact]
    public async Task ModelTestMeasuresReplyAndPromptCostsFromSampledMemory()
    {
        using var server = new MemoryComfy();
        var registry = new AiProviderRegistry(new TestHttpFactory(server), new FakeAiSettingsStore(), server);
        ComfyTextModelVerification? verification = null;
        await foreach (var update in registry.VerifyComfyTextModelAsync(File, new(), Ct))
            verification ??= update.Verification;

        var benchmark = Assert.Single(verification!.Benchmarks!);
        Assert.Equal([2048, ComfyTextCapacity.FootprintTokens, ComfyTextCapacity.FootprintTokens], server.Limits);
        Assert.Equal(MemoryComfy.PerReplyToken, benchmark.BytesPerReplyToken);
        var prompts = server.PromptCharacters;
        Assert.Equal((prompts[2] - prompts[1]) * MemoryComfy.PerPromptCharacter / (ComfyTextBenchmark.LargeContextTokens - ComfyTextBenchmark.ContextTokens),
            benchmark.BytesPerPromptToken);
        Assert.Equal(ComfyTextBenchmark.LargeContextTokens, benchmark.CapacityContextTokens);
        Assert.NotNull(ComfyTextCapacity.PromptCapacity(benchmark, 2048));
    }

    /// <summary>Reports reserved memory that grows with the running workflow's prompt length and reply limit.</summary>
    private sealed class MemoryComfy : HttpMessageHandler, IComfyExecutionMonitor
    {
        public const long PerReplyToken = 40_000, PerPromptCharacter = 50_000;
        private long _allocated;
        public List<int> Limits { get; } = [];
        public List<long> PromptCharacters { get; } = [];

        public async IAsyncEnumerable<ComfyExecutionUpdate> ExecuteAsync(HttpClient http, Func<string, object> workflowFactory,
            ComfyExecutionOptions options, CancellationToken operationToken,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken callerToken)
        {
            var inputs = JsonSerializer.SerializeToElement(workflowFactory("client")).GetProperty("prompt").GetProperty("2").GetProperty("inputs");
            var limit = inputs.GetProperty("max_length").GetInt32();
            long characters = inputs.GetProperty("prompt").GetString()!.Length;
            Limits.Add(limit); PromptCharacters.Add(characters);
            Interlocked.Exchange(ref _allocated, (1L << 30) + characters * PerPromptCharacter + limit * PerReplyToken);
            // Long enough for several memory samples.
            await Task.Delay(900, callerToken);
            Interlocked.Exchange(ref _allocated, 0);
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
                "/system_stats" => Stats(Interlocked.Read(ref _allocated)),
                "/queue" => new { queue_running = Array.Empty<object>(), queue_pending = Array.Empty<object>() },
                _ => new { }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") });
        }

        private static object Stats(long allocated)
        {
            var reserved = allocated == 0 ? 0 : allocated + (256L << 20);
            return new
            {
                system = new { comfyui_version = "0.38.0" },
                devices = new[] { new { name = "GPU", index = 0, vram_total = 24L << 30, vram_free = (22L << 30) - reserved,
                    torch_vram_total = reserved, torch_vram_free = reserved - allocated } }
            };
        }
    }
}
