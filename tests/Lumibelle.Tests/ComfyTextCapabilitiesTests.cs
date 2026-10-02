using System.Net;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.Extensions.AI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed class ComfyTextCapabilitiesTests
{
    private const string File = "test-vl.safetensors";
    private static readonly TextModelReference Model = new(AiBackend.ComfyUI, File, "Vision", "http://127.0.0.1:8188");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ComfyTextModelVerification Verified(string version, ComfyTextModelCapabilities? capabilities, int minutesAgo = 0) =>
        new(Model.ComfyUrl!, version, File, DateTimeOffset.UtcNow.AddMinutes(-minutesAgo)) { Capabilities = capabilities };

    private static byte[] Png()
    {
        using var image = new Image<Rgba32>(20, 10, new Rgba32(255, 0, 0, 255));
        using var output = new MemoryStream(); image.SaveAsPng(output); return output.ToArray();
    }

    [Fact]
    public void NativeCaptureMovesLeadingSystemMessagesIntoTheSystemPrompt()
    {
        ChatMessage[] messages = [new(ChatRole.System, "Keep the format."), new(ChatRole.System, "JSON only."), new(ChatRole.User, "Write prose.")];
        var native = ComfyTextVision.Capture(messages, nativeSystemPrompt: true);
        Assert.Equal("Keep the format.\n\nJSON only.", native.SystemPrompt);
        Assert.Equal("Write prose.", native.Transcript);

        var legacy = ComfyTextVision.Capture(messages);
        Assert.Null(legacy.SystemPrompt);
        Assert.Equal("[system]\nKeep the format.\n\n[system]\nJSON only.\n\n[user]\nWrite prose.\n\n[assistant]\n", legacy.Transcript);
    }

    [Fact]
    public void NativeCaptureKeepsRoleMarkersForLaterHistory()
    {
        var input = ComfyTextVision.Capture([new(ChatRole.System, "Rules."), new(ChatRole.User, "First."), new(ChatRole.Assistant, "Reply."), new(ChatRole.User, "Second.")], true);
        Assert.Equal("Rules.", input.SystemPrompt);
        Assert.Equal("[user]\nFirst.\n\n[assistant]\nReply.\n\n[user]\nSecond.\n\n[assistant]\n", input.Transcript);
    }

    [Fact]
    public void NativeCapturePutsImageInstructionsInTheSystemPrompt()
    {
        var message = new ChatMessage(ChatRole.User, "Describe.");
        message.Contents.Add(new DataContent(Png(), "image/png"));
        var input = ComfyTextVision.Capture([new(ChatRole.System, "Rules."), message], true);
        Assert.StartsWith("The supplied images are inspection attachments", input.SystemPrompt);
        Assert.EndsWith("\n\nRules.", input.SystemPrompt);
        Assert.Equal("Describe.\n[Inspection attachment 1]\n", input.Transcript);
        Assert.Single(input.Images);
    }

    [Fact]
    public void NativeCaptureWithoutSystemTextKeepsTheLegacyTranscript()
    {
        ChatMessage[] messages = [new(ChatRole.User, "Write prose.")];
        var native = ComfyTextVision.Capture(messages, true);
        Assert.Equal(ComfyTextVision.Capture(messages).Transcript, native.Transcript);
        Assert.Null(native.SystemPrompt);
        Assert.Null(ComfyTextVision.Capture([new(ChatRole.System, "Only rules.")], true).SystemPrompt);
    }

    [Fact]
    public void WorkflowIncludesSystemPromptOnlyWhenProvided()
    {
        JsonElement Inputs(object workflow) => JsonSerializer.SerializeToElement(workflow).GetProperty("prompt").GetProperty("2").GetProperty("inputs");
        Assert.False(Inputs(ComfyChatClient.BuildWorkflow(File, "p", 10, .5f, 1, "c")).TryGetProperty("system_prompt", out _));
        Assert.False(Inputs(ComfyChatClient.BuildWorkflow(File, "p", 10, .5f, 1, "c", "")).TryGetProperty("system_prompt", out _));
        Assert.Equal("Rules.", Inputs(ComfyTextVision.BuildWorkflow(File, "p", 10, .5f, 1, "c", [], "Rules.")).GetProperty("system_prompt").GetString());
    }

    [Theory]
    [InlineData("482913", true)]
    [InlineData("The code is 482-913.", true)]
    [InlineData("48291", false)]
    [InlineData("482918", true)]
    [InlineData("482988", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RepeatsFindsTheCodeAmongDigits(string? response, bool expected) => Assert.Equal(expected, ComfyTextCapabilities.Repeats(response, "482913"));

    [Fact]
    public void RepeatsRequiresCodesInOrder()
    {
        Assert.True(ComfyTextCapabilities.Repeats("1. 4829 2. 1734", "4829", "1734"));
        Assert.False(ComfyTextCapabilities.Repeats("1734 4829", "4829", "1734"));
        Assert.False(ComfyTextCapabilities.Repeats("4829 4829", "4829", "1734"));
        // Qwen3.8 27B's real answer: both images seen, each zero misread as a nine.
        Assert.True(ComfyTextCapabilities.Repeats("7389 2999", "7380", "2990"));
    }

    [Fact]
    public void DigitImagesRenderEveryDigitLegibly() => Assert.Equal("0123456789", ReadDigits(ComfyTextCapabilities.DigitImage("0123456789")));

    [Fact]
    public void DetectedVisionReplacesTheLegacyChoicePerVersion()
    {
        var settings = new AiSettings
        {
            ComfyTextModels = new() { [TextModelPolicy.Key(Model)] = new(2048, .7f) { VisionInput = ComfyVisionInput.ImageBatch } },
            ComfyTextModelVerifications = [Verified("0.37.0", null, 10), Verified("0.38.0", new(true, ComfyVisionInput.SingleImage))]
        };
        Assert.Equal(ComfyVisionInput.SingleImage, ComfyTextVision.Mode(Model, settings));
        Assert.Equal(ComfyVisionInput.SingleImage, ComfyTextVision.Mode(Model, settings, "0.38.0"));
        Assert.Equal(ComfyVisionInput.ImageBatch, ComfyTextVision.Mode(Model, settings, "0.37.0"));
        Assert.Equal(ComfyVisionInput.ImageBatch, ComfyTextVision.Mode(Model, settings, "0.39.0"));
    }

    [Fact]
    public void SystemPromptVersionsFollowTheNewestTestPerVersion()
    {
        var settings = new AiSettings
        {
            ComfyTextModelVerifications =
            [
                Verified("0.37.0", new(true, ComfyVisionInput.Disabled), 30), Verified("0.37.0", new(false, ComfyVisionInput.Disabled), 20),
                Verified("0.38.0", new(false, ComfyVisionInput.Disabled), 10), Verified("0.38.0", new(true, ComfyVisionInput.Disabled)),
                Verified("0.39.0", null), Verified("0.40.0", new(true, ComfyVisionInput.Disabled)) with { Model = "other.safetensors" }
            ]
        };
        Assert.Equal(["0.38.0"], ComfyTextCapabilities.SystemPromptVersions(Model, settings));
        Assert.Empty(ComfyTextCapabilities.SystemPromptVersions(Model with { ComfyUrl = "http://127.0.0.1:9999" }, settings));
    }

    [Theory]
    [InlineData("0.38.0", true)]
    [InlineData("0.39.0", false)]
    public async Task SystemPromptIsUsedOnlyOnAConfirmedVersion(string running, bool expected)
    {
        using var server = new FakeComfy { Version = running }; using var http = server.Client();
        Assert.Equal(expected, await ComfyTextCapabilities.UseSystemPromptAsync(http, ["0.38.0"], Ct));
        Assert.False(await ComfyTextCapabilities.UseSystemPromptAsync(http, [], Ct));
        Assert.Single(server.Paths);
    }

    [Fact]
    public async Task DirectClientSendsTheSystemPromptNativelyOnAConfirmedVersion()
    {
        using var server = new FakeComfy(); using var http = server.Client(); var monitor = new AnsweringMonitor(server, _ => "Done.");
        using var client = new ComfyChatClient(http, File, monitor, systemPromptVersions: [server.Version]);
        await client.GetResponseAsync([new(ChatRole.System, "Rules."), new(ChatRole.User, "Write.")], cancellationToken: Ct);
        var inputs = monitor.Workflows.Single().GetProperty("prompt").GetProperty("2").GetProperty("inputs");
        Assert.Equal("Rules.", inputs.GetProperty("system_prompt").GetString());
        Assert.Equal("Write.", inputs.GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task ProbesDetectEveryHonoredChannel()
    {
        var capabilities = await Probe(new FakeModel(SystemPrompt: true, Images: 2));
        Assert.Equal(new ComfyTextModelCapabilities(true, ComfyVisionInput.ImageBatch), capabilities);
    }

    [Fact]
    public async Task AModelThatMisreadsTheDigitsIsCheckedWithColors()
    {
        var runs = new List<string>();
        using var server = new FakeComfy { Model = new(SystemPrompt: false, Images: 2, MisreadsDigits: true) }; using var http = server.Client();
        var capabilities = await ComfyTextCapabilities.ProbeAsync(http, File, false, new(true, true), (name, workflow, _) =>
        {
            runs.Add(name);
            return Task.FromResult(server.Model!.Answer(server, JsonSerializer.SerializeToElement(workflow("client"))));
        }, _ => Task.CompletedTask, Ct);
        Assert.Equal(new ComfyTextModelCapabilities(false, ComfyVisionInput.ImageBatch), capabilities);
        Assert.Equal(["image", "image-colors", "image-batch", "image-batch-colors"], runs);
        // A blind model is not rescued by the second chance.
        Assert.Equal(new ComfyTextModelCapabilities(false, ComfyVisionInput.Disabled), await Probe(new FakeModel(SystemPrompt: false, Images: 0)));
    }

    [Theory]
    [InlineData("Red, green, blue, yellow, black.", "01234")]
    [InlineData("1. **Yellow** 2. Black 3. Reddish 4. Blue", "342")]
    [InlineData(null, null)]
    public void ColorAnswersBecomeCodes(string? answer, string? code) => Assert.Equal(code, ComfyTextCapabilities.ColorDigits(answer));

    [Fact]
    public void ColorImagesShowEveryColorInOrder() => Assert.Equal("blue, black, red, yellow, green", ReadColors(ComfyTextCapabilities.ColorImage("24031")));

    [Fact]
    public async Task ProbesRejectChannelsTheTokenizerDropsSilently()
    {
        Assert.Equal(new ComfyTextModelCapabilities(false, ComfyVisionInput.SingleImage), await Probe(new FakeModel(SystemPrompt: false, Images: 1)));
        Assert.Equal(new ComfyTextModelCapabilities(false, ComfyVisionInput.Disabled), await Probe(new FakeModel(SystemPrompt: false, Images: 0)));
    }

    [Fact]
    public async Task FailedProbesAndMissingNodesDetectNothing()
    {
        Assert.Equal(new ComfyTextModelCapabilities(false, ComfyVisionInput.Disabled), await Probe(new FakeModel(true, 2, Fails: true)));
        var runs = new List<string>();
        using var server = new FakeComfy(); using var http = server.Client();
        var result = await ComfyTextCapabilities.ProbeAsync(http, File, false, new(false, false),
            (name, _, _) => { runs.Add(name); return Task.FromResult<string?>(null); }, _ => Task.CompletedTask, Ct);
        Assert.Equal(new ComfyTextModelCapabilities(false, ComfyVisionInput.Disabled), result);
        Assert.Empty(runs); Assert.Empty(server.Paths);
    }

    [Fact]
    public async Task ModelTestRecordsDetectedCapabilities()
    {
        using var server = new FakeComfy { SystemPromptInput = true, Model = new(SystemPrompt: true, Images: 2) };
        var registry = new AiProviderRegistry(new TestHttpFactory(server), new FakeAiSettingsStore(), new AnsweringMonitor(server, server.Model!.Answer));
        ComfyTextModelVerification? verification = null;
        await foreach (var update in registry.VerifyComfyTextModelAsync(File, new(), Ct)) verification ??= update.Verification;
        Assert.Equal(new ComfyTextModelCapabilities(true, ComfyVisionInput.ImageBatch), verification!.Capabilities);
        Assert.Equal(3, server.Uploads.Count);
    }

    [Fact]
    public async Task ModelTestWithoutProbeSupportStillVerifies()
    {
        using var server = new FakeComfy { ImageInput = false, Model = new(SystemPrompt: true, Images: 2) };
        var registry = new AiProviderRegistry(new TestHttpFactory(server), new FakeAiSettingsStore(), new AnsweringMonitor(server, server.Model!.Answer));
        ComfyTextModelVerification? verification = null;
        await foreach (var update in registry.VerifyComfyTextModelAsync(File, new(), Ct)) verification ??= update.Verification;
        Assert.Equal(new ComfyTextModelCapabilities(false, ComfyVisionInput.Disabled), verification!.Capabilities);
        Assert.Empty(server.Uploads);
    }

    private static async Task<ComfyTextModelCapabilities> Probe(FakeModel model)
    {
        using var server = new FakeComfy { Model = model }; using var http = server.Client();
        return await ComfyTextCapabilities.ProbeAsync(http, File, true, new(true, true),
            (_, workflow, _) => Task.FromResult(model.Fails ? null : model.Answer(server, JsonSerializer.SerializeToElement(workflow("client")))),
            _ => Task.CompletedTask, Ct);
    }

    /// <summary>Answers probes like a model whose tokenizer honors <paramref name="SystemPrompt"/> and the first <paramref name="Images"/> images.</summary>
    private sealed record FakeModel(bool SystemPrompt, int Images, bool Fails = false, bool MisreadsDigits = false)
    {
        public string? Answer(FakeComfy server, JsonElement workflow)
        {
            var graph = workflow.GetProperty("prompt");
            var inputs = graph.GetProperty("2").GetProperty("inputs");
            if (inputs.TryGetProperty("system_prompt", out var system))
                return SystemPrompt ? new string(system.GetString()!.Where(char.IsAsciiDigit).ToArray()) : "I don't know a code.";
            var images = graph.EnumerateObject().Where(node => node.Name.StartsWith("vision_image_", StringComparison.Ordinal))
                .OrderBy(node => node.Name, StringComparer.Ordinal).Take(Images)
                .Select(node => server.Uploads[node.Value.GetProperty("inputs").GetProperty("image").GetString()!]).ToArray();
            if (images.Length == 0) return "I cannot see an image.";
            if (inputs.GetProperty("prompt").GetString()!.Contains("colored squares", StringComparison.Ordinal))
                return string.Join(", ", images.Select(ReadColors));
            // Like Qwen3.5 27B, which read the dot-matrix 4713 as 4235.
            return string.Join(" ", images.Select(png => MisreadsDigits ? new string(ReadDigits(png).Select(d => (char)('0' + (d - '0' + 3) % 10)).ToArray()) : ReadDigits(png)));
        }
    }

    private static string ReadColors(byte[] png)
    {
        using var image = Image.Load<Rgba32>(png);
        return string.Join(", ", Enumerable.Range(0, (image.Width - 96 + 32) / 128).Select(i => ComfyTextCapabilities.ProbeColors
            .First(c => c.Color == image[48 + i * 128 + 48, 96]).Name));
    }

    private static string ReadDigits(byte[] png)
    {
        const int cell = 20, gap = 40, margin = 60;
        using var image = Image.Load<Rgba32>(png);
        var count = (image.Width - margin * 2 + gap) / (5 * cell + gap);
        var digits = new StringBuilder();
        for (var d = 0; d < count; d++)
        {
            var left = margin + d * (5 * cell + gap);
            var rows = Enumerable.Range(0, 7).Select(row => new string(Enumerable.Range(0, 5)
                .Select(column => image[left + column * cell + cell / 2, margin + row * cell + cell / 2].R < 128 ? '1' : '0').ToArray())).ToArray();
            digits.Append((char)('0' + Array.FindIndex(ComfyTextCapabilities.Glyphs, glyph => glyph.SequenceEqual(rows))));
        }
        return digits.ToString();
    }

    private sealed class AnsweringMonitor(FakeComfy server, Func<FakeComfy, JsonElement, string?> answer) : IComfyExecutionMonitor
    {
        public AnsweringMonitor(FakeComfy server, Func<JsonElement, string?> answer) : this(server, (_, workflow) => answer(workflow)) { }
        public List<JsonElement> Workflows { get; } = [];
        public async IAsyncEnumerable<ComfyExecutionUpdate> ExecuteAsync(HttpClient http, Func<string, object> workflowFactory,
            ComfyExecutionOptions options, CancellationToken operationToken,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken callerToken)
        {
            await Task.CompletedTask;
            var workflow = JsonSerializer.SerializeToElement(workflowFactory("test-client"));
            Workflows.Add(workflow);
            var job = JsonSerializer.SerializeToElement(new { outputs = new Dictionary<string, object> { ["3"] = new { text = new[] { answer(server, workflow) ?? "" } } } });
            yield return new(new(GenerationPhase.Completed, "Complete"), "test-prompt", job, true);
        }
    }

    private sealed class FakeComfy : HttpMessageHandler
    {
        public string Version { get; init; } = "0.38.0";
        public bool SystemPromptInput { get; init; }
        public bool ImageInput { get; init; } = true;
        public FakeModel? Model { get; init; }
        public Dictionary<string, byte[]> Uploads { get; } = [];
        public List<string> Paths { get; } = [];
        public HttpClient Client() => new(this, disposeHandler: false) { BaseAddress = new("http://127.0.0.1:8188/") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath; Paths.Add(path);
            switch (path)
            {
                case "/system_stats": return Json(new { system = new { comfyui_version = Version }, devices = Array.Empty<object>() });
                case "/queue": return Json(new { queue_running = Array.Empty<object>(), queue_pending = Array.Empty<object>() });
                case "/free": return Json(new { });
                case "/object_info": return Json(Catalog());
                case "/upload/image":
                    var body = Assert.IsType<MultipartFormDataContent>(request.Content);
                    var part = body.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "image");
                    var name = part.Headers.ContentDisposition!.FileName!.Trim('"');
                    Uploads[name] = await part.ReadAsByteArrayAsync(ct);
                    return Json(new { name, subfolder = "", type = "input" });
                default: return new(HttpStatusCode.NotFound);
            }
        }

        private Dictionary<string, object> Catalog()
        {
            var optional = new Dictionary<string, object>();
            if (ImageInput) optional["image"] = new[] { "IMAGE" };
            if (SystemPromptInput) optional["system_prompt"] = new object[] { "STRING", new { forceInput = true } };
            return new()
            {
                ["CLIPLoader"] = new { input = new { required = new { clip_name = new object[] { new[] { File } } } } },
                ["TextGenerate"] = new { input = new { optional } },
                ["LoadImage"] = new { output = new[] { "IMAGE", "MASK" } },
                ["ImageBatch"] = new { input = new { required = new { image1 = new[] { "IMAGE" }, image2 = new[] { "IMAGE" } } }, output = new[] { "IMAGE" } },
                ["PreviewAny"] = new { }
            };
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
