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

public sealed class ComfyTextVisionTests
{
    private const string File = "test-vl.safetensors";
    private static readonly TextModelReference Model = new(AiBackend.ComfyUI, File, "Vision", "http://127.0.0.1:8188");
    private static AiSettings Settings(ComfyVisionInput mode) => new()
    {
        ComfyTextModels = new() { [TextModelPolicy.Key(Model)] = new(2048, .7f) { VisionInput = mode } }
    };
    private static byte[] Png(int width = 20, int height = 10, Rgba32? color = null)
    {
        using var image = new Image<Rgba32>(width, height, color ?? new Rgba32(255, 0, 0, 255));
        using var output = new MemoryStream(); image.SaveAsPng(output); return output.ToArray();
    }
    private static ChatMessage ImageMessage(params byte[][] images)
    {
        var message = new ChatMessage(ChatRole.User, "Describe the attached references.");
        foreach (var image in images) message.Contents.Add(new DataContent(image, "image/png"));
        return message;
    }

    [Fact]
    public void LegacyModelSettingsRemainTextOnlyAndSerializeWithoutNewFields()
    {
        const string original = "{\"maxOutputTokens\":1024,\"temperature\":0.7}";
        var value = JsonSerializer.Deserialize<ComfyTextModelSettings>(original, AtomicJsonFile.Options)!;
        Assert.Equal(ComfyVisionInput.Disabled, value.VisionInput);
        var json = JsonSerializer.SerializeToElement(value, AtomicJsonFile.Options);
        Assert.False(json.TryGetProperty("visionInput", out _));
        Assert.Equal(ComfyVisionInput.Disabled, ComfyTextVision.Mode(Model, new()));
    }

    [Theory]
    [InlineData(ComfyVisionInput.SingleImage)]
    [InlineData(ComfyVisionInput.ImageBatch)]
    public void ModeIsCapturedPerModelAndServerAndSharedByProfiles(ComfyVisionInput mode)
    {
        var settings = Settings(mode);
        ComfyTextSettings.Validate(settings);
        var saved = JsonSerializer.Deserialize<AiSettings>(JsonSerializer.Serialize(settings, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
        Assert.Equal(mode, ComfyTextVision.Mode(Model, saved));
        Assert.Equal(mode, ComfyTextVision.Mode(Model with { ProfileId = Guid.NewGuid(), Name = "Profile", Temperature = .4f }, saved));
        Assert.Equal(ComfyVisionInput.Disabled, ComfyTextVision.Mode(Model with { ComfyUrl = "http://127.0.0.1:9999" }, saved));
        Assert.Equal(ComfyVisionInput.Disabled, ComfyTextVision.Mode(Model with { Model = "other.safetensors" }, saved));
        Assert.Equal(mode, ComfyTextVision.Mode(Model, ComfyTextSettings.Capture(Model, saved)));
    }

    [Fact]
    public void InvalidImageModeCannotBeSaved() => Assert.Throws<WorkspaceStoreException>(() => ComfyTextSettings.Validate(Settings((ComfyVisionInput)99)));

    [Theory]
    [InlineData(ComfyVisionInput.Disabled, 0)]
    [InlineData(ComfyVisionInput.SingleImage, 0)]
    [InlineData(ComfyVisionInput.SingleImage, 1)]
    [InlineData(ComfyVisionInput.ImageBatch, 0)]
    [InlineData(ComfyVisionInput.ImageBatch, 1)]
    [InlineData(ComfyVisionInput.ImageBatch, 36)]
    public void SupportedCountsAreAccepted(ComfyVisionInput mode, int count) => ComfyTextVision.ValidateCount(mode, count);

    [Theory]
    [InlineData(ComfyVisionInput.Disabled, 1)]
    [InlineData(ComfyVisionInput.SingleImage, 2)]
    [InlineData(ComfyVisionInput.ImageBatch, 37)]
    [InlineData(ComfyVisionInput.ImageBatch, -1)]
    public void UnsupportedCountsNeverSilentlyLoseAttachments(ComfyVisionInput mode, int count) =>
        Assert.Throws<WorkspaceStoreException>(() => ComfyTextVision.ValidateCount(mode, count));

    [Fact]
    public void CapabilitiesRequireActualNodeInputAndOutputTypes()
    {
        Assert.True(ComfyTextVision.Capabilities(Catalog()).Supports(ComfyVisionInput.ImageBatch));
        Assert.False(ComfyTextVision.Capabilities(Catalog()).Supports(ComfyVisionInput.Disabled));
        Assert.True(ComfyTextVision.Capabilities(Catalog(batch: false)).SingleImage);
        Assert.False(ComfyTextVision.Capabilities(Catalog(batch: false)).ImageBatch);
        Assert.False(ComfyTextVision.Capabilities(Catalog(image: false)).SingleImage);
        using var bad = JsonDocument.Parse("{\"TextGenerate\":{\"input\":{\"optional\":{\"image\":[\"STRING\"]}}},\"LoadImage\":{\"output\":[\"IMAGE\"]}}");
        Assert.False(ComfyTextVision.Capabilities(bad.RootElement).SingleImage);
        using var malformed = JsonDocument.Parse("[null]");
        Assert.False(ComfyTextVision.Capabilities(malformed.RootElement).SingleImage);
    }

    [Fact]
    public void TextOnlyTranscriptAndWorkflowRemainUnchanged()
    {
        ChatMessage[] messages = [new(ChatRole.System, "Keep the format."), new(ChatRole.User, "Write prose."), new(ChatRole.Assistant, "Earlier text.")];
        messages[1].Contents.Add(new TextContent("Second text part."));
        var input = ComfyTextVision.Capture(messages);
        Assert.Empty(input.Images);
        Assert.Equal(string.Join("\n\n", messages.Select(m => $"[{m.Role}]\n{m.Text}")) + "\n\n[assistant]\n", input.Transcript);
        var old = ComfyChatClient.BuildWorkflow(File, input.Transcript, 1234, .4f, 456, "same-client");
        var next = ComfyTextVision.BuildWorkflow(File, input.Transcript, 1234, .4f, 456, "same-client", []);
        Assert.Equal(JsonSerializer.Serialize(old), JsonSerializer.Serialize(next));
    }

    [Fact]
    public void CaptureRetainsAttachmentOrderAcrossMessagesAndCopiesBytes()
    {
        var first = Png(); var second = Png(10, 20);
        var input = ComfyTextVision.Capture([new(ChatRole.System, "JSON only"), ImageMessage(first), ImageMessage(second)]);
        Assert.Equal(first, input.Images[0]); Assert.Equal(second, input.Images[1]);
        Assert.NotSame(first, input.Images[0]);
        Assert.Contains("[Inspection attachment 1]", input.Transcript);
        Assert.Contains("[Inspection attachment 2]", input.Transcript);
        Assert.Contains("separate image, not a collage", input.Transcript);
        Assert.Contains("Picture/Video labels", input.Transcript);
        Assert.Contains("Ignore uniform padding", input.Transcript);
        Assert.True(input.Transcript.IndexOf("\n[Inspection attachment 1]\n", StringComparison.Ordinal) < input.Transcript.IndexOf("\n[Inspection attachment 2]\n", StringComparison.Ordinal));
    }

    [Fact]
    public void NonImageMediaCannotFallThroughAsText()
    {
        var message = new ChatMessage(ChatRole.User, "Listen");
        message.Contents.Add(new DataContent(new byte[] { 1, 2 }, "audio/wav"));
        Assert.Throws<WorkspaceStoreException>(() => ComfyTextVision.Capture([message]));
    }

    [Fact]
    public void UnsupportedRolesAreRejected()
    {
        Assert.Throws<WorkspaceStoreException>(() => ComfyTextVision.Capture([new(new ChatRole("tool"), "result")]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(36)]
    public void GraphConnectsEveryImageExactlyOnceInOriginalOrder(int count)
    {
        var names = Enumerable.Range(1, count).Select(i => new ComfyTextImage($"lumibelle-vision-test-{i}.png")).ToArray();
        var workflow = JsonSerializer.SerializeToElement(ComfyTextVision.BuildWorkflow(File, "prompt", 2500, .6f, 88, "client", names));
        var graph = workflow.GetProperty("prompt");
        var inputs = graph.GetProperty("2").GetProperty("inputs");
        Assert.Equal("TextGenerate", graph.GetProperty("2").GetProperty("class_type").GetString());
        Assert.Equal(2500, inputs.GetProperty("max_length").GetInt32());
        Assert.Equal(88L, inputs.GetProperty("sampling_mode.seed").GetInt64());
        Assert.Equal(.6f, inputs.GetProperty("sampling_mode.temperature").GetSingle());
        Assert.True(inputs.GetProperty("use_default_template").GetBoolean());
        Assert.Equal("client", workflow.GetProperty("client_id").GetString());
        Assert.Equal(names.Select(n => n.Name), Expand(inputs.GetProperty("image")[0].GetString()!));
        Assert.False(inputs.TryGetProperty("video", out _));
        Assert.Equal(count - 1, graph.EnumerateObject().Count(n => n.Value.GetProperty("class_type").GetString() == "ImageBatch"));
        IEnumerable<string> Expand(string id)
        {
            var node = graph.GetProperty(id); var fields = node.GetProperty("inputs");
            return node.GetProperty("class_type").GetString() == "LoadImage"
                ? new[] { fields.GetProperty("image").GetString()! }
                : Expand(fields.GetProperty("image1")[0].GetString()!).Concat(Expand(fields.GetProperty("image2")[0].GetString()!));
        }
    }

    [Theory]
    [InlineData("../secret.png")]
    [InlineData("https://example.com/image.png")]
    [InlineData("lumibelle-vision-ok.png [output]")]
    [InlineData("subfolder/lumibelle-vision-ok.png")]
    public void GraphRejectsUntrustedUploadPaths(string name) => Assert.Throws<WorkspaceStoreException>(() =>
        ComfyTextVision.BuildWorkflow(File, "prompt", 512, .7f, 1, "client", [new(name)]));

    [Fact]
    public void LetterboxingPreservesPixelsAndDoesNotUpscaleSmallSources()
    {
        var red = new Rgba32(255, 0, 0, 255);
        using var wide = Image.Load<Rgba32>(ComfyTextVision.Letterbox(Png(20, 10, red), 20, 20));
        Assert.Equal(red, wide[10, 10]);
        Assert.Equal(new Rgba32(128, 128, 128, 255), wide[10, 0]);
        using var small = Image.Load<Rgba32>(ComfyTextVision.Letterbox(Png(4, 2, red), 20, 20));
        Assert.Equal(red, small[10, 10]);
        Assert.Equal(new Rgba32(128, 128, 128, 255), small[7, 10]);
        Assert.Equal((1024, 1024), ComfyTextVision.BatchCanvas([(4000, 2000), (1000, 3000)]));
        Assert.Equal((20, 10), ComfyTextVision.BatchCanvas([(20, 10)]));
    }

    [Fact]
    public void InvalidPngIsRejectedBeforeUploads() => Assert.Throws<WorkspaceStoreException>(() => ComfyTextVision.InspectSizes([new byte[] { 1, 2, 3 }]));

    [Fact]
    public void ImageByteLimitsAreChecked()
    {
        Assert.Throws<WorkspaceStoreException>(() => ComfyTextVision.ValidateByteLimits([Array.Empty<byte>()]));
        Assert.Throws<WorkspaceStoreException>(() => ComfyTextVision.ValidateByteLimits(Enumerable.Repeat(new byte[] { 1 }, 37).ToArray()));
        var large = new byte[ComfyTextVision.MaximumImageBytes];
        Assert.Throws<WorkspaceStoreException>(() => ComfyTextVision.ValidateByteLimits(Enumerable.Repeat(large, 6).ToArray()));
    }

    [Fact]
    public async Task TextOnlyExecutionNeedsNoImageNetworkCalls()
    {
        using var server = new Server(); using var http = Client(server);
        Assert.Empty(await ComfyTextVision.UploadAsync(http, File, ComfyVisionInput.Disabled, [], TestContext.Current.CancellationToken));
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task DisabledAndSingleModesRejectBeforeNetworkCalls()
    {
        using var server = new Server(); using var http = Client(server);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => ComfyTextVision.UploadAsync(http, File, ComfyVisionInput.Disabled, [Png()], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => ComfyTextVision.UploadAsync(http, File, ComfyVisionInput.SingleImage, [Png(), Png()], TestContext.Current.CancellationToken));
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task SingleImageUploadsExactCapturedPngWithNoOverwrite()
    {
        var original = Png(); using var server = new Server(); using var http = Client(server);
        var images = await ComfyTextVision.UploadAsync(http, File, ComfyVisionInput.SingleImage, [original], TestContext.Current.CancellationToken);
        Assert.Single(images); Assert.Single(server.Uploads);
        Assert.Equal(original, server.Uploads[0].Bytes);
        Assert.Equal(images[0].Name, server.Uploads[0].Name);
        Assert.Equal("false", server.Uploads[0].Overwrite);
        Assert.Equal("input", server.Uploads[0].Type);
    }

    [Fact]
    public async Task DifferentAspectImagesAreUploadedAsSeparateEqualCanvasesInOrder()
    {
        var red = new Rgba32(255, 0, 0, 255); var blue = new Rgba32(0, 0, 255, 255);
        using var server = new Server(); using var http = Client(server);
        var result = await ComfyTextVision.UploadAsync(http, File, ComfyVisionInput.ImageBatch, [Png(20, 10, red), Png(10, 20, blue)], TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        using var first = Image.Load<Rgba32>(server.Uploads[0].Bytes); using var second = Image.Load<Rgba32>(server.Uploads[1].Bytes);
        Assert.Equal((20, 20), (first.Width, first.Height)); Assert.Equal((20, 20), (second.Width, second.Height));
        Assert.Equal(red, first[10, 10]); Assert.Equal(blue, second[10, 10]);
        Assert.NotEqual(result[0].Name, result[1].Name);
    }

    [Theory]
    [InlineData(false, true, File)]
    [InlineData(true, false, File)]
    [InlineData(true, true, "removed.safetensors")]
    public async Task MissingCapabilitiesOrModelStopBeforeAnyMediaIsSent(bool image, bool batch, string model)
    {
        using var server = new Server { Schema = Catalog(image, batch) }; using var http = Client(server);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => ComfyTextVision.UploadAsync(http, model, ComfyVisionInput.ImageBatch, [Png(), Png()], TestContext.Current.CancellationToken));
        Assert.Empty(server.Uploads);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"input\":null}")]
    [InlineData("{\"input\":{\"required\":[]}}")]
    public async Task MalformedModelCatalogFailsWithoutUploading(string malformedLoader)
    {
        var nodes = System.Text.Json.Nodes.JsonNode.Parse(Catalog().GetRawText())!;
        nodes["CLIPLoader"] = System.Text.Json.Nodes.JsonNode.Parse(malformedLoader);
        using var server = new Server { Schema = JsonSerializer.SerializeToElement(nodes) }; using var http = Client(server);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => ComfyTextVision.UploadAsync(http, File, ComfyVisionInput.SingleImage, [Png()], TestContext.Current.CancellationToken));
        Assert.Empty(server.Uploads);
    }

    [Fact]
    public async Task AllImagesAreValidatedBeforeTheFirstUpload()
    {
        using var server = new Server(); using var http = Client(server);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => ComfyTextVision.UploadAsync(http, File, ComfyVisionInput.ImageBatch, [Png(), new byte[] { 1 }], TestContext.Current.CancellationToken));
        Assert.Empty(server.Uploads);
    }

    [Fact]
    public async Task AFailedUploadDoesNotReturnAPartialSelection()
    {
        using var server = new Server { FailUpload = 2 }; using var http = Client(server);
        await Assert.ThrowsAsync<HttpRequestException>(() => ComfyTextVision.UploadAsync(http, File, ComfyVisionInput.ImageBatch, [Png(), Png(), Png()], TestContext.Current.CancellationToken));
        Assert.Equal(2, server.Uploads.Count);
        Assert.DoesNotContain(server.Paths, p => p.EndsWith("prompt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnexpectedUploadReceiptsAreRejected()
    {
        using var server = new Server { BadReceipt = true }; using var http = Client(server);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => ComfyTextVision.UploadAsync(http, File, ComfyVisionInput.SingleImage, [Png()], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationStopsBeforeSendingMedia()
    {
        using var server = new Server(); using var http = Client(server);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ComfyTextVision.UploadAsync(http, File, ComfyVisionInput.SingleImage, [Png()], cancellation.Token));
        Assert.Empty(server.Uploads);
    }

    [Fact]
    public void QueuedDescriptionKeepsItsPngAndVisionSettingWhenLiveSettingsChange()
    {
        var settings = Settings(ComfyVisionInput.SingleImage); var png = Png();
        var (header, request) = DescriptionRequest(settings, png);
        var snapshot = JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options);
        settings.ComfyTextModels[TextModelPolicy.Key(Model)] = new(512, .4f);
        var restored = AiTextJobHandler.Read(header, snapshot);
        Assert.True(restored.InspectsImages);
        Assert.Equal(ComfyVisionInput.SingleImage, ComfyTextVision.Mode(restored.Model, restored.Settings));
        Assert.Equal(png, restored.Messages.SelectMany(m => m.Parts).Single(p => p.Image is not null).Image);
    }

    [Fact]
    public void ATextOnlyCapturedModelCannotAcceptAnImageByChangingTheBackendGate()
    {
        var (header, request) = DescriptionRequest(Settings(ComfyVisionInput.Disabled), Png());
        Assert.Throws<WorkspaceStoreException>(() => AiTextJobHandler.Read(header, JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options)));
    }

    [Fact]
    public void CapturedImageCountStillHasToMatchTheTask()
    {
        var (header, request) = DescriptionRequest(Settings(ComfyVisionInput.ImageBatch), Png());
        request = request with { Messages = [new("user", [new(Image: Png(), MediaType: "image/png"), new(Image: Png(), MediaType: "image/png")])] };
        Assert.Throws<WorkspaceStoreException>(() => AiTextJobHandler.Read(header, JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options)));
    }

    [Fact]
    public async Task DirectClientPassesCapturedImagesIntoTheExecutingWorkflow()
    {
        using var server = new Server(); using var http = Client(server); var monitor = new RecordingMonitor();
        using var client = new ComfyChatClient(http, File, monitor, ComfyVisionInput.ImageBatch);
        var response = await client.GetResponseAsync([ImageMessage(Png(20, 10), Png(10, 20))], new() { MaxOutputTokens = 900, Temperature = .4f }, TestContext.Current.CancellationToken);
        Assert.Equal("The two references differ.", response.Text);
        Assert.Equal(1, monitor.Executions);
        Assert.Equal(2, server.Uploads.Count);
        var graph = monitor.Workflow!.Value.GetProperty("prompt");
        Assert.Equal("vision_batch_2", graph.GetProperty("2").GetProperty("inputs").GetProperty("image")[0].GetString());
        Assert.Equal(server.Uploads[0].Name, graph.GetProperty("vision_image_1").GetProperty("inputs").GetProperty("image").GetString());
        Assert.Equal(server.Uploads[1].Name, graph.GetProperty("vision_image_2").GetProperty("inputs").GetProperty("image").GetString());
    }

    [Fact]
    public async Task DirectClientNeverSubmitsAfterAnImageUploadFails()
    {
        using var server = new Server { FailUpload = 2 }; using var http = Client(server); var monitor = new RecordingMonitor();
        using var client = new ComfyChatClient(http, File, monitor, ComfyVisionInput.ImageBatch);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync([ImageMessage(Png(), Png())], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, monitor.Executions);
    }

    [Fact]
    public async Task DirectClientWithLegacyConstructorStillRunsTextWithoutImageEndpoints()
    {
        using var server = new Server(); using var http = Client(server); var monitor = new RecordingMonitor();
        using var client = new ComfyChatClient(http, File, monitor);
        await client.GetResponseAsync([new(ChatRole.User, "Write text.")], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, monitor.Executions);
        Assert.Equal(0, server.Requests);
        Assert.False(monitor.Workflow!.Value.GetProperty("prompt").GetProperty("2").GetProperty("inputs").TryGetProperty("image", out _));
    }

    private sealed class RecordingMonitor : IComfyExecutionMonitor
    {
        public int Executions { get; private set; }
        public JsonElement? Workflow { get; private set; }
        public async IAsyncEnumerable<ComfyExecutionUpdate> ExecuteAsync(HttpClient http, Func<string, object> workflowFactory,
            ComfyExecutionOptions options, CancellationToken operationToken,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken callerToken)
        {
            operationToken.ThrowIfCancellationRequested(); callerToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            Executions++;
            Workflow = JsonSerializer.SerializeToElement(workflowFactory("test-client"));
            var job = JsonSerializer.SerializeToElement(new { outputs = new Dictionary<string, object> {
                ["3"] = new { text = new[] { "The two references differ." } }
            } });
            yield return new(new(GenerationPhase.Completed, "Complete"), "test-prompt", job, true);
        }
    }

    private static (AiJobHeader Header, AiTextJobRequest Request) DescriptionRequest(AiSettings settings, byte[] png)
    {
        var project = Guid.NewGuid(); var asset = Guid.NewGuid(); var image = Guid.NewGuid();
        var target = new GuidanceTarget(project, asset, GuidanceScope.Image, ImageId: image);
        var context = new GuidanceContext(target, "Prop", AssetCategory.Prop, "", "Image", "", "", []);
        var payload = new GuidanceRequest(context, Model, new(asset, image));
        var header = new AiJobHeader { Id = Guid.NewGuid(), Kind = AiJobKind.Guidance, Backend = AiBackend.ComfyUI,
            Target = new(project, asset, GuidanceScope: GuidanceScope.Image, ImageId: image), ProjectName = "Project", TargetName = "Image", OriginTabId = Guid.NewGuid(), RequestFingerprint = "test" };
        var request = new AiTextJobRequest(2, AiJobKind.Guidance, Model, false, settings, GuidanceAssistant.Profile, .7f, 123,
            JsonSerializer.SerializeToElement(payload, AtomicJsonFile.Options), GuidanceAssistant.BuildMessages(payload, png).Select(AiTextMessage.Capture).ToArray());
        return (header, request);
    }

    private static JsonElement Catalog(bool image = true, bool batch = true)
    {
        var nodes = new Dictionary<string, object>
        {
            ["CLIPLoader"] = new { input = new { required = new { clip_name = new object[] { new[] { File } } } } },
            ["TextGenerate"] = new { input = new { optional = image ? new Dictionary<string, object> { ["image"] = new[] { "IMAGE" } } : new Dictionary<string, object>() } },
            ["LoadImage"] = new { output = new[] { "IMAGE", "MASK" } },
            ["PreviewAny"] = new { }
        };
        if (batch) nodes["ImageBatch"] = new { input = new { required = new { image1 = new[] { "IMAGE" }, image2 = new[] { "IMAGE" } } }, output = new[] { "IMAGE" } };
        return JsonSerializer.SerializeToElement(nodes);
    }
    private static HttpClient Client(Server server) => new(server) { BaseAddress = new("http://127.0.0.1:8188/") };
    private sealed record Upload(string Name, byte[] Bytes, string Type, string Overwrite);
    private sealed class Server : HttpMessageHandler
    {
        public JsonElement Schema { get; set; } = Catalog();
        public int Requests { get; private set; }
        public int? FailUpload { get; init; }
        public bool BadReceipt { get; init; }
        public List<Upload> Uploads { get; } = [];
        public List<string> Paths { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Requests++; Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/object_info") return Json(Schema);
            Assert.Equal("/upload/image", request.RequestUri.AbsolutePath);
            var body = Assert.IsType<MultipartFormDataContent>(request.Content);
            var part = body.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "image");
            var name = part.Headers.ContentDisposition!.FileName!.Trim('"');
            var bytes = await part.ReadAsByteArrayAsync(ct);
            async Task<string> Field(string key) => await body.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == key).ReadAsStringAsync(ct);
            Uploads.Add(new(name, bytes, await Field("type"), await Field("overwrite")));
            if (Uploads.Count == FailUpload) return new(HttpStatusCode.BadRequest);
            return Json(new { name = BadReceipt ? "../unexpected.png" : name, subfolder = "", type = "input" });
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
