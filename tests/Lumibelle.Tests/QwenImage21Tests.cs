using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed partial class QwenImage21Tests
{
    private static readonly AiSettings Settings = new() { DefaultImageWorkflow = ImageWorkflow.QwenImage21, QwenImage21 = new() };
    private static JsonElement Graph(object workflow) => JsonSerializer.SerializeToElement(workflow, AtomicJsonFile.Options).GetProperty("prompt");
    private static JsonElement Inputs(JsonElement graph, string node) => graph.GetProperty(node).GetProperty("inputs");
    private static byte[] Png(int width = 96, int height = 64, byte alpha = 255)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(50, 100, 150, alpha));
        using var output = new MemoryStream(); image.SaveAsPng(output); return output.ToArray();
    }
    private static AiImageJobRequest Request(int references = 0, int resolution = 1024, int steps = 25)
    {
        var project = Guid.NewGuid(); var asset = Guid.NewGuid();
        var look = new AssetLookContext(asset, "Mira", "", "", null, "", "", "");
        var options = new QwenImage21Options(resolution, steps);
        var inputs = Enumerable.Range(0, references).Select(index => new AiImageInput(new(asset, Guid.NewGuid()), null, look, Png(96 + index * 32, 64))).ToArray();
        ReferenceGenerationRequest? create = references == 0 ? new() { ProjectId = project, Workflow = ImageWorkflow.QwenImage21,
            Look = look, Prompt = "A quiet landscape", AspectRatio = "1:1", QwenImage21 = options, Seed = 42 } : null;
        ReferenceEditRequest? edit = references > 0 ? new() { ProjectId = project, Workflow = ImageWorkflow.QwenImage21,
            Look = look, SourceAssetId = asset, SourceImageId = inputs[0].Reference.ImageId, Prompt = references > 1 ? $"Edit <image1> using <image{references}> only as a clothing reference." : "Edit <image1>.",
            AspectRatio = ImageAspectPolicy.FromImage1, QwenImage21 = options, Seed = 42, ReferenceLooks = inputs.Select(i => new AssetReferenceLook(i.Reference, i.Context)).ToArray() } : null;
        return new(1, Guid.NewGuid(), project, asset, 0, Settings, AiImageJobPolicy.Profile(ImageWorkflow.QwenImage21, references > 0), create, edit, inputs, []);
    }
    private static JsonElement Build(AiImageJobRequest request)
    {
        var size = QwenImage21Policy.OutputSize(request);
        return Graph(ComfyQwenImage21.BuildWorkflow(request.Settings, request.Prompt, 42, size.Width, size.Height,
            request.Inputs.Select(i => Convert.ToBase64String(i.Png)).ToArray(), QwenImage21Policy.Options(request), "contract-test", request.AppliedLoras));
    }
    private static JsonElement Supplied(string filename) => JsonSerializer.Deserialize<JsonElement>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "QwenImage21", filename)));

    [Fact]
    public void CreateMatchesTheSuppliedModelsAndSamplerWithoutAnEditCache()
    {
        var graph = Build(Request()); var supplied = Supplied("image_qwen_image_2_1_t2i.json");
        Assert.Equal(Inputs(supplied, "459:451").GetProperty("unet_name").GetString(), Inputs(graph, "1").GetProperty("unet_name").GetString());
        Assert.Equal(Inputs(supplied, "459:453").GetProperty("clip_name").GetString(), Inputs(graph, "2").GetProperty("clip_name").GetString());
        Assert.Equal("qwen_image", Inputs(graph, "2").GetProperty("type").GetString());
        Assert.Equal(Inputs(supplied, "459:454").GetProperty("vae_name").GetString(), Inputs(graph, "3").GetProperty("vae_name").GetString());
        var sampler = Inputs(graph, "7"); var expected = Inputs(supplied, "459:458");
        foreach (var field in new[] { "steps", "cfg", "denoise" }) Assert.Equal(expected.GetProperty(field).GetDouble(), sampler.GetProperty(field).GetDouble());
        foreach (var field in new[] { "sampler_name", "scheduler" }) Assert.Equal(expected.GetProperty(field).GetString(), sampler.GetProperty(field).GetString());
        Assert.Equal("EmptyLatentImage", graph.GetProperty("5").GetProperty("class_type").GetString());
        Assert.Equal(1024, Inputs(graph, "5").GetProperty("width").GetInt32());
        Assert.Equal(1024, Inputs(graph, "5").GetProperty("height").GetInt32());
        Assert.Equal("5", sampler.GetProperty("latent_image")[0].GetString());
        Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == "QwenImage21Cache");
        Assert.Equal("PreviewImage", graph.GetProperty(ComfyQwenImage21.OutputNode).GetProperty("class_type").GetString());
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void EditUsesEveryOrderedSlotAndTheEncoderLatent(int count)
    {
        var request = Request(count); AiImageJobPolicy.Validate(request); var graph = Build(request); var encoder = Inputs(graph, "4");
        Assert.Equal(count, encoder.EnumerateObject().Count(p => p.Name.StartsWith("images.image_", StringComparison.Ordinal)));
        for (var i = 0; i < count; i++)
        {
            var node = encoder.GetProperty("images.image_" + (i + 1))[0].GetString()!;
            Assert.Equal(Convert.ToBase64String(request.Inputs[i].Png), Inputs(graph, node).GetProperty("image").GetString());
        }
        Assert.Equal(request.Prompt, encoder.GetProperty("prompt").GetString());
        Assert.Equal("4", Inputs(graph, "7").GetProperty("latent_image")[0].GetString());
        Assert.Equal(2, Inputs(graph, "7").GetProperty("latent_image")[1].GetInt32());
        Assert.False(graph.TryGetProperty("5", out _));
        Assert.Equal("QwenImage21Cache", graph.GetProperty("6").GetProperty("class_type").GetString());
        Assert.Equal("auto", Inputs(graph, "6").GetProperty("device").GetString());
        Assert.Equal("default", Inputs(graph, "6").GetProperty("dtype").GetString());
        Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() is "ImageBatch" or "LoraLoaderModelOnly" or "Krea2EditModelPatch");
    }

    [Fact]
    public void OriginalEditModeMatchesTheSuppliedZeroResolutionAndSourceLatent()
    {
        var graph = Build(Request(2, 0)); var supplied = Supplied("image_qwen_image_2_1_image_edit.json");
        Assert.Equal(Inputs(supplied, "459:474").GetProperty("resolution").GetInt32(), Inputs(graph, "4").GetProperty("resolution").GetInt32());
        Assert.Equal(0, Inputs(graph, "4").GetProperty("resolution").GetInt32());
        Assert.Equal(2, Inputs(graph, "7").GetProperty("latent_image")[1].GetInt32());
    }

    [Theory]
    [InlineData(1024, 1024, 1024, 1024, 1024)]
    [InlineData(1024, 1024, 1440, 1440, 1440)]
    [InlineData(1024, 1024, 2048, 2048, 2048)]
    [InlineData(1920, 1080, 1024, 1376, 768)]
    [InlineData(1920, 1080, 1440, 1920, 1088)]
    [InlineData(1920, 1080, 2048, 2720, 1536)]
    [InlineData(1080, 1920, 2048, 1536, 2720)]
    [InlineData(1500, 1000, 0, 1504, 992)]
    [InlineData(48, 80, 0, 64, 64)]
    [InlineData(1, 1, 0, 32, 32)]
    public void GeometryMatchesTheNativeEncoderRounding(int w, int h, int resolution, int expectedW, int expectedH) =>
        Assert.Equal((expectedW, expectedH), QwenImage21Policy.Size(w, h, resolution));

    [Theory]
    [InlineData("1:1")] [InlineData("4:3")] [InlineData("3:4")] [InlineData("3:2")]
    [InlineData("2:3")] [InlineData("16:9")] [InlineData("9:16")]
    public void AllExistingAspectsHaveANative2KCreateBudget(string aspect)
    {
        var size = QwenImage21Policy.CreateSize(aspect, 2048);
        Assert.Equal(0, size.Width % 32); Assert.Equal(0, size.Height % 32);
        Assert.InRange((long)size.Width * size.Height, 4_000_000, QwenImage21Policy.MaximumNativePixels);
    }

    [Theory]
    [InlineData(-1, 25, true)] [InlineData(4096, 25, true)] [InlineData(0, 25, false)]
    [InlineData(1024, 0, true)] [InlineData(1024, 101, false)]
    public void UnsupportedBudgetsOrStepsAreRejected(int resolution, int steps, bool editing) =>
        Assert.Throws<AiGenerationException>(() => QwenImage21Policy.ValidateOptions(new(resolution, steps), editing));

    [Fact]
    public void NativeOversizeFailsInsteadOfSilentlyDownsizing()
    {
        Assert.Throws<AiGenerationException>(() => QwenImage21Policy.Size(4096, 4096, 0));
        Assert.Equal((2048, 2048), QwenImage21Policy.Size(4096, 4096, 2048));
    }

    [Fact]
    public void ExplicitEditCanvasKeepsEveryReferenceAndUsesIndependentOutputDimensions()
    {
        var request = Request(2);
        request = request with { Edit = request.Edit! with { AspectRatio = "16:9" } };
        AiImageJobPolicy.Validate(request);
        var copy = JsonSerializer.Deserialize<AiImageJobRequest>(JsonSerializer.Serialize(request, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
        var graph = Build(copy);
        Assert.Equal((1376, 768), QwenImage21Policy.OutputSize(copy));
        Assert.Equal("5", Inputs(graph, "7").GetProperty("latent_image")[0].GetString());
        Assert.Equal(1376, Inputs(graph, "5").GetProperty("width").GetInt32());
        Assert.Equal(768, Inputs(graph, "5").GetProperty("height").GetInt32());
        for (var i = 0; i < copy.Inputs.Count; i++)
        {
            var node = Inputs(graph, "4").GetProperty("images.image_" + (i + 1))[0].GetString()!;
            Assert.Equal(Convert.ToBase64String(request.Inputs[i].Png), Inputs(graph, node).GetProperty("image").GetString());
        }
        Assert.Equal(request.Prompt, Inputs(graph, "4").GetProperty("prompt").GetString());
        var metadata = QwenImage21Policy.Metadata(copy, Guid.NewGuid(), new(Guid.NewGuid(), 1, 42));
        Assert.Equal("16:9", metadata.AspectRatio);
        Assert.Equal("qwen-image21-output-latent", metadata.Edit!.FitMode);
        Assert.False(QwenImage21Policy.InvalidMetadata(metadata, 1376, 768));
        Assert.False(QwenImage21Policy.InvalidEdit(metadata.Edit));
    }

    [Fact]
    public void ExplicitAspectWithOriginalReferencesUsesTheFirstImagesAreaForItsCanvas()
    {
        var request = Request(2, 0);
        request = request with { Edit = request.Edit! with { AspectRatio = "9:16" } };
        Assert.Equal((64, 96), QwenImage21Policy.OutputSize(request));
        Assert.Equal(0, Inputs(Build(request), "4").GetProperty("resolution").GetInt32());
    }

    [Theory]
    [InlineData(ImageWorkflow.Krea2)]
    [InlineData(ImageWorkflow.Flux2Klein9bKv)]
    [InlineData(ImageWorkflow.QwenImage21)]
    public async Task SourceAspectUsesThePreparedCropAndSurvivesRequestSerialization(ImageWorkflow workflow)
    {
        var request = Request(1);
        using var stream = new MemoryStream(Png(192, 128));
        var crop = new ImageCropRegion { Width = .5, Height = 1 };
        var png = await QwenImage21Inputs.PrepareAsync(stream, crop, Xunit.TestContext.Current.CancellationToken);
        request = request with
        {
            Settings = request.Settings with { DefaultImageWorkflow = workflow },
            Profile = AiImageJobPolicy.Profile(workflow, true),
            Edit = request.Edit! with { Workflow = workflow, SourceCrop = crop, QwenImage21 = workflow == ImageWorkflow.QwenImage21 ? new() : null },
            Inputs = [request.Inputs[0] with { Png = png, Crop = crop }]
        };
        var copy = JsonSerializer.Deserialize<AiImageJobRequest>(JsonSerializer.Serialize(request, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
        AiImageJobPolicy.Validate(copy);
        Assert.Equal((896, 1184), QwenImage21Policy.OutputSize(copy));
        Assert.Equal(ImageAspectPolicy.FromImage1, copy.AspectRatio);
    }

    [Fact]
    public void SourceAspectRequiresAReferenceWhileExplicitCreationStillWorks()
    {
        var request = Request();
        Assert.Throws<AiGenerationException>(() => AiImageJobPolicy.Validate(request with { Create = request.Create! with { AspectRatio = ImageAspectPolicy.FromImage1 } }));
        Assert.Throws<AiGenerationException>(() => ImageAspectPolicy.Size(ImageAspectPolicy.FromImage1));
        Assert.Equal((1344, 768), ImageAspectPolicy.Size("16:9"));
    }

    [Theory]
    [InlineData(ImageWorkflow.Krea2, false)]
    [InlineData(ImageWorkflow.Krea2, true)]
    [InlineData(ImageWorkflow.Flux2Klein9bKv, false)]
    [InlineData(ImageWorkflow.Flux2Klein9bKv, true)]
    public void ComfyResolutionSurvivesCaptureAndControlsTheWorkflowCanvas(ImageWorkflow workflow, bool edit)
    {
        var request = Request(edit ? 1 : 0);
        request = request with {
            Settings = request.Settings with { DefaultImageWorkflow = workflow },
            Profile = AiImageJobPolicy.Profile(workflow, edit),
            Create = request.Create is { } create ? create with { Workflow = workflow, QwenImage21 = null, Resolution = 2048 } : null,
            Edit = request.Edit is { } change ? change with { Workflow = workflow, QwenImage21 = null, Resolution = 2048 } : null
        };
        request = JsonSerializer.Deserialize<AiImageJobRequest>(JsonSerializer.Serialize(request, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
        AiImageJobPolicy.Validate(request);
        var candidate = new AiBatchCandidate(Guid.NewGuid(), 1, 42);
        var graph = Graph(new ComfyImageJobAdapter(null!, null!, null!, null!, null!, null!).Build(request, candidate, "resolution-test"));
        var size = QwenImage21Policy.OutputSize(request);
        Assert.True((long)size.Width * size.Height > 4_000_000);
        var canvas = graph.EnumerateObject().Select(p => p.Value).Single(n => n.GetProperty("class_type").GetString() == (workflow == ImageWorkflow.Flux2Klein9bKv ? "EmptyFlux2LatentImage" : edit ? "EmptySD3LatentImage" : "EmptyLatentImage")).GetProperty("inputs");
        Assert.Equal(size.Width, canvas.GetProperty("width").GetInt32());
        Assert.Equal(size.Height, canvas.GetProperty("height").GetInt32());
        Assert.Equal(2048, AiImageJobPolicy.Metadata(request, Guid.NewGuid(), candidate).Resolution);
        var invalid = request with { Create = request.Create is { } c ? c with { Resolution = 17 } : null, Edit = request.Edit is { } e ? e with { Resolution = 17 } : null };
        Assert.Throws<AiGenerationException>(() => AiImageJobPolicy.Validate(invalid));
    }

    [Fact]
    public void CodexSourceAspectRefersToTheSubmittedImageWithoutAddingAReference()
    {
        var request = Request(2);
        var messages = AiImageJobHandler.CodexMessages(request);
        var serialized = JsonSerializer.Serialize(messages);
        Assert.Contains("match Image 1's proportions, including its submitted crop", serialized.Replace("\\u0027", "'"));
        Assert.Equal(2, messages.SelectMany(m => m.Parts).Count(p => p.Image is not null));
    }

    [Fact]
    public void ElevenOrDuplicateInputsAreRejectedNotDropped()
    {
        Assert.Throws<AiGenerationException>(() => AiImageJobPolicy.Validate(Request(11)));
        var request = Request(2);
        request = request with { Inputs = [request.Inputs[0], request.Inputs[0]] };
        Assert.Throws<AiGenerationException>(() => AiImageJobPolicy.Validate(request));
    }

    [Fact]
    public void OtherWorkflowsKeepTheirOwnReferenceAndByteLimits()
    {
        Assert.Equal(2, ImageWorkflowLimits.MaximumReferences(ImageWorkflow.Krea2));
        Assert.Equal(8, ImageWorkflowLimits.MaximumReferences(ImageWorkflow.Flux2Klein9bKv));
        Assert.Equal(8, ImageWorkflowLimits.MaximumReferences(ImageWorkflow.CodexImages));
        Assert.Equal(10, ImageWorkflowLimits.MaximumReferences(ImageWorkflow.QwenImage21));
        Assert.Equal(16 * 1024 * 1024, ImageWorkflowLimits.MaximumPreparedImageBytes(ImageWorkflow.Krea2));
        Assert.Equal(25 * 1024 * 1024, ImageWorkflowLimits.MaximumPreparedImageBytes(ImageWorkflow.QwenImage21));
        var old = Request() with { Settings = Settings with { DefaultImageWorkflow = ImageWorkflow.Krea2 } };
        Assert.Throws<AiGenerationException>(() => QwenImage21Policy.ValidateRequest(old));
    }

    [Fact]
    public void MissingReferenceOrInvalidPngFails()
    {
        var request = Request(1);
        Assert.Throws<AiGenerationException>(() => QwenImage21Policy.ValidateRequest(request with { Inputs = [] }));
        Assert.Throws<AiGenerationException>(() => QwenImage21Policy.ValidateRequest(request with { Inputs = [request.Inputs[0] with { Png = [1, 2, 3] }] }));
    }

    [Fact]
    public void SavedQwenRequestsCannotSubstituteNewDefaultsDuringRecovery()
    {
        var request = Request();
        Assert.Throws<AiGenerationException>(() => AiImageJobPolicy.Validate(request with { Settings = Settings with { QwenImage21 = null } }));
        Assert.Throws<AiGenerationException>(() => AiImageJobPolicy.Validate(request with { Create = request.Create! with { QwenImage21 = null } }));
    }

    [Fact]
    public void SavedJobRetainsResolutionStepsCacheAndOrderedInputs()
    {
        var request = Request(10, 2048, 40) with { Settings = Settings with { QwenImage21 = new() { CacheDevice = "cpu", CacheDtype = "int8" } } };
        var json = JsonSerializer.SerializeToUtf8Bytes(request, AtomicJsonFile.Options);
        var copy = JsonSerializer.Deserialize<AiImageJobRequest>(json, AtomicJsonFile.Options)!;
        AiImageJobPolicy.Validate(copy);
        Assert.Equal(new QwenImage21Options(2048, 40), QwenImage21Policy.Options(copy));
        Assert.Equal("cpu", QwenImage21Policy.Settings(copy.Settings).CacheDevice);
        Assert.Equal(request.Inputs.Select(i => i.Reference), copy.Inputs.Select(i => i.Reference));
        var graph = Build(copy);
        Assert.Equal(2048, Inputs(graph, "4").GetProperty("resolution").GetInt32());
        Assert.Equal(40, Inputs(graph, "7").GetProperty("steps").GetInt32());
        Assert.Equal("int8", Inputs(graph, "6").GetProperty("dtype").GetString());
    }

    [Fact]
    public void GenerationMetadataUsesQwenModelsActualDimensionsAndNoKreaSettings()
    {
        var request = Request(10, 2048, 40); var candidate = new AiBatchCandidate(Guid.NewGuid(), 3, 987);
        var job = Guid.NewGuid(); var metadata = AiImageJobPolicy.Metadata(request, job, candidate);
        Assert.Equal(job, metadata.AiJobId); Assert.Equal(request.BatchId, metadata.BatchId);
        Assert.Equal(3, metadata.CandidateNumber); Assert.Equal(987, metadata.Seed);
        Assert.Equal(QwenImage21Policy.Settings(request.Settings).Vae, metadata.Vae);
        var size = QwenImage21Policy.OutputSize(request);
        Assert.Equal(size.Width, metadata.QwenImage21!.Width); Assert.Equal(size.Height, metadata.QwenImage21.Height);
        Assert.False(QwenImage21Policy.InvalidMetadata(metadata, size.Width, size.Height));
        Assert.True(QwenImage21Policy.InvalidMetadata(metadata, 1024, 1024));
        Assert.False(QwenImage21Policy.InvalidEdit(metadata.Edit!));
        Assert.Equal(10, metadata.Edit!.References.Count); Assert.Equal("", metadata.Edit.Lora); Assert.Equal(0, metadata.Edit.ReferenceBoost);
        Assert.Equal("qwen-image21-source-latent", metadata.Edit.FitMode);
    }

    [Fact]
    public void QwenGeneratedImageCanBeValidatedByTheExistingAssetStore()
    {
        var request = Request(2); var size = QwenImage21Policy.OutputSize(request);
        var metadata = AiImageJobPolicy.Metadata(request, Guid.NewGuid(), new(Guid.NewGuid(), 1, 42));
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "output.png", ContentType = "image/png", Width = size.Width,
            Height = size.Height, Origin = AssetImageOrigin.Edited, CreatedUtc = DateTimeOffset.UtcNow, Generation = metadata };
        var library = new AssetLibrary { ProjectId = request.ProjectId, Assets = [new ReferenceAsset { Id = request.AssetId, Name = "Mira", Images = [image] }] };
        FileAssetStore.Validate(library, library.ProjectId);
        library.Assets[0] = library.Assets[0] with { Images = [image with { Generation = metadata with { QwenImage21 = metadata.QwenImage21! with { Width = 1 } } }] };
        Assert.Throws<WorkspaceStoreException>(() => FileAssetStore.Validate(library, library.ProjectId));
    }

    [Fact]
    public void LegacyJsonDoesNotAcquireQwenFieldsAndEnumsKeepTheirValues()
    {
        Assert.Equal(0, (int)ImageWorkflow.Krea2); Assert.Equal(1, (int)ImageWorkflow.Flux2Klein9bKv); Assert.Equal(2, (int)ImageWorkflow.CodexImages);
        Assert.Equal(2, (int)LoraWorkflow.MiniMaxH3Ref2VA); Assert.Equal(8, (int)ImageWorkflow.MiniMaxH3);
        Assert.DoesNotContain("h3Image", JsonSerializer.Serialize(new AssetGenerationMetadata(), AtomicJsonFile.Options));
        Assert.DoesNotContain("qwenImage21", JsonSerializer.Serialize(new AiSettings(), AtomicJsonFile.Options));
        Assert.DoesNotContain("qwenImage21", JsonSerializer.Serialize(new AssetGenerationMetadata(), AtomicJsonFile.Options));
        Assert.DoesNotContain("qwenImage21", JsonSerializer.Serialize(new ReferenceGenerationRequest { Prompt = "old", AspectRatio = "1:1" }, AtomicJsonFile.Options));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EnhancementHasASeparateQwenProfile(bool edit)
    {
        Assert.Equal(edit ? "qwen-image21-edit-v1" : "qwen-image21-create-v1", PromptProfiles.Id(ImageWorkflow.QwenImage21, edit));
        var guide = PromptProfiles.Read(ImageWorkflow.QwenImage21, edit);
        Assert.Contains("Qwen Image 2.1", guide);
        if (edit) { Assert.Contains("<image1>", guide); Assert.Contains("<image10>", guide); }
    }
}
