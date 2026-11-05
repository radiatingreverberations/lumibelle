using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed class H3ImageTests
{
    private static readonly AiSettings Settings = new() { DefaultImageWorkflow = ImageWorkflow.MiniMaxH3 };
    private static JsonElement Graph(object value) => JsonSerializer.SerializeToElement(value, AtomicJsonFile.Options).GetProperty("prompt");
    private static JsonElement Inputs(JsonElement graph, string node) => graph.GetProperty(node).GetProperty("inputs");
    private static string Type(JsonElement graph, string node) => graph.GetProperty(node).GetProperty("class_type").GetString()!;
    private static byte[] Png(int width = 96, int height = 64)
    {
        using var image = new Image<Rgb24>(width, height); using var output = new MemoryStream(); image.SaveAsPng(output); return output.ToArray();
    }
    private static AiImageJobRequest Request(int references = 1, RegionalImageCapture? regional = null, IReadOnlyList<RegionalImageSelection>? regions = null, string aspect = "1:1", byte[]? source = null, string? preset = null)
    {
        var project = Guid.NewGuid(); var asset = Guid.NewGuid(); var look = new AssetLookContext(asset, "Juniper", "", "", null, "", "", "");
        var inputs = Enumerable.Range(0, references).Select(i => new AiImageInput(new(asset, Guid.NewGuid()), null, look, i == 0 && source is not null ? source : Png())).ToArray();
        ReferenceEditRequest edit = new() { ProjectId = project, Workflow = ImageWorkflow.MiniMaxH3, Look = look,
            SourceAssetId = asset, SourceImageId = inputs[0].Reference.ImageId, Prompt = "The coat of Image 1 becomes red", AspectRatio = aspect, Seed = 42, Regions = regions, H3Preset = preset,
            ReferenceLooks = inputs.Select(i => new AssetReferenceLook(i.Reference, i.Context)).ToArray() };
        return new(1, Guid.NewGuid(), project, asset, 0, Settings, AiImageJobPolicy.Profile(ImageWorkflow.MiniMaxH3, true), null, edit, inputs, []) { Regional = regional };
    }
    private static JsonElement Catalog(Action<JsonNode>? change = null)
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/h3-contract.json")))!;
        root["LatentCut"] = JsonNode.Parse("""{"input":{"required":{"samples":["LATENT"],"dim":[["x","y","t"]],"index":["INT"],"amount":["INT"]}},"output":["LATENT"]}""");
        root["LTXVSeparateAVLatent"] = JsonNode.Parse("""{"input":{"required":{"av_latent":["LATENT"]}},"output":["LATENT","LATENT"]}""");
        root["LTXVConcatAVLatent"] = JsonNode.Parse("""{"input":{"required":{"video_latent":["LATENT"],"audio_latent":["LATENT"]}},"output":["LATENT"]}""");
        root["PreviewImage"] = JsonNode.Parse("""{"input":{"required":{"images":["IMAGE"]}},"output":[]}""");
        root["ETN_LoadImageBase64"] = JsonNode.Parse("""{"input":{"required":{"image":["STRING"]}},"output":["IMAGE","MASK"]}""");
        change?.Invoke(root);
        return JsonSerializer.SerializeToElement(root);
    }

    [Fact]
    public void GraphSamplesOneLatentFrameWithOrderedReferences()
    {
        var images = new[] { "c291cmNl", "cmVmZXJlbmNl" };
        var graph = Graph(ComfyH3Images.BuildWorkflow(Settings, "The coat of Image 1 takes the color of Image 2", 42, 1024, 1024, images));
        var encoder = Inputs(graph, "5");
        Assert.Equal("MiniMaxH3ReferenceToVideo", Type(graph, "5"));
        Assert.Equal(H3ImagePolicy.Length, encoder.GetProperty("length").GetInt32());
        Assert.Equal(1024, encoder.GetProperty("width").GetInt32());
        Assert.Equal("match", encoder.GetProperty("ref_image_size").GetString());
        Assert.Equal("The coat of <Picture 1> takes the color of <Picture 2>", encoder.GetProperty("prompt").GetString());
        Assert.Equal("3", encoder.GetProperty("vae")[0].GetString());
        Assert.Equal("4", encoder.GetProperty("audio_vae")[0].GetString());
        for (var i = 0; i < images.Length; i++)
        {
            Assert.Equal($"reference_{i + 1}", encoder.GetProperty($"ref_images.ref_image_{i}")[0].GetString());
            Assert.Equal(images[i], Inputs(graph, $"reference_{i + 1}").GetProperty("image").GetString());
            Assert.Equal("ETN_LoadImageBase64", Type(graph, $"reference_{i + 1}"));
        }
        Assert.Equal("5", Inputs(graph, "separate").GetProperty("av_latent")[0].GetString());
        Assert.Equal(1, Inputs(graph, "separate").GetProperty("av_latent")[1].GetInt32());
        Assert.Equal("LatentCut", Type(graph, "cut"));
        Assert.Equal("t", Inputs(graph, "cut").GetProperty("dim").GetString());
        Assert.Equal(0, Inputs(graph, "cut").GetProperty("index").GetInt32());
        Assert.Equal(1, Inputs(graph, "cut").GetProperty("amount").GetInt32());
        Assert.Equal("cut", Inputs(graph, "join").GetProperty("video_latent")[0].GetString());
        Assert.Equal("separate", Inputs(graph, "join").GetProperty("audio_latent")[0].GetString());
        Assert.Equal(1, Inputs(graph, "join").GetProperty("audio_latent")[1].GetInt32());
        Assert.Equal("join", Inputs(graph, "10").GetProperty("latent_image")[0].GetString());
        Assert.Equal("11", Inputs(graph, ComfyH3Images.OutputNode).GetProperty("images")[0].GetString());
        Assert.Equal("PreviewImage", Type(graph, ComfyH3Images.OutputNode));
        Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() is "VAEDecodeAudio" or "CreateVideo" or "SaveVideo");
    }

    [Fact]
    public void SamplingMatchesTheStandardVideoGraph()
    {
        var graph = Graph(ComfyH3Images.BuildWorkflow(Settings, "A change", 42, 1024, 1024, ["c291cmNl"]));
        var standard = H3ImagePolicy.Sampling(Settings.H3, H3ImagePolicy.StandardPreset);
        Assert.Equal("h3-standard-v1", standard.Profile);
        Assert.Equal(20, Inputs(graph, "9").GetProperty("steps").GetInt32());
        Assert.Equal("simple", Inputs(graph, "9").GetProperty("scheduler").GetString());
        Assert.Equal("res_multistep", Inputs(graph, "8").GetProperty("sampler_name").GetString());
        Assert.Equal("1", Inputs(graph, "9").GetProperty("model")[0].GetString());
        Assert.Equal("1", Inputs(graph, "6").GetProperty("model")[0].GetString());
        Assert.Equal(42, Inputs(graph, "7").GetProperty("noise_seed").GetInt64());
    }

    [Theory]
    [InlineData("Image 1 changes", 1, "<Picture 1> changes")]
    [InlineData("image 2 and IMAGE 1", 2, "<Picture 2> and <Picture 1>")]
    [InlineData("<image1> and <image2>", 2, "<Picture 1> and <Picture 2>")]
    [InlineData("<Picture 1> stays", 1, "<Picture 1> stays")]
    [InlineData("Image 3 is not supplied", 2, "Image 3 is not supplied")]
    [InlineData("Image 0 is not an input", 2, "Image 0 is not an input")]
    [InlineData("a sign reading \"Image 1\" hangs in Image 1", 1, "a sign reading \"Image 1\" hangs in <Picture 1>")]
    [InlineData("a “Image 1” poster", 1, "a “Image 1” poster")]
    [InlineData("Image 1's coat", 1, "<Picture 1>'s coat")]
    [InlineData("The image 12 and subimage 1", 2, "The image 12 and subimage 1")]
    public void AuthorImageNumbersBecomePictureTagsOutsideQuotes(string prompt, int references, string expected) =>
        Assert.Equal(expected, H3ImagePrompts.Compile(prompt, references));

    [Fact]
    public void MetadataKeepsTheAuthorsWordingAndValidates()
    {
        var request = Request(2);
        var candidate = new AiBatchCandidate(Guid.NewGuid(), 1, 7);
        var metadata = AiImageJobPolicy.Metadata(request, Guid.NewGuid(), candidate);
        Assert.Equal(ImageWorkflow.MiniMaxH3, metadata.Workflow);
        Assert.Equal("The coat of Image 1 becomes red", metadata.Prompt);
        Assert.Equal(Settings.H3.Model, metadata.DiffusionModel);
        Assert.Equal(Settings.H3.VideoVae, metadata.Vae);
        Assert.Equal(20, metadata.Steps);
        Assert.Equal(new H3ImageOutput(Settings.H3.AudioVae, "standard", "h3-standard-v1", "res_multistep", "simple", null, H3ImagePolicy.Length, H3ImagePolicy.LatentFrames, "match", 1024, 1024), metadata.H3Image);
        Assert.Equal(H3ImagePolicy.FitMode, metadata.Edit!.FitMode);
        Assert.False(H3ImagePolicy.InvalidMetadata(metadata, 1024, 1024));
        Assert.True(H3ImagePolicy.InvalidMetadata(metadata, 1024, 992));
        Assert.True(H3ImagePolicy.InvalidMetadata(metadata with { H3Image = null }, 1024, 1024));
        Assert.True(H3ImagePolicy.InvalidMetadata(metadata with { H3Image = metadata.H3Image! with { Length = 6 } }, 1024, 1024));
        Assert.True(H3ImagePolicy.InvalidMetadata(metadata with { H3Image = metadata.H3Image! with { Preset = "nope" } }, 1024, 1024));
        Assert.True(H3ImagePolicy.InvalidMetadata(metadata with { Edit = null }, 1024, 1024));
        Assert.True(H3ImagePolicy.InvalidMetadata(metadata with { Workflow = ImageWorkflow.MossImage }, 1024, 1024));
        Assert.False(H3ImagePolicy.InvalidMetadata(new AssetGenerationMetadata { Workflow = ImageWorkflow.MossImage }, 1024, 1024));
    }

    [Fact]
    public void AnEarlierRecipeStillLoads()
    {
        var metadata = AiImageJobPolicy.Metadata(Request(), Guid.NewGuid(), new(Guid.NewGuid(), 1, 7));
        Assert.False(H3ImagePolicy.InvalidMetadata(metadata with { H3Image = metadata.H3Image! with { Length = 22, LatentFrames = 5, RefImageSize = "max" } }, 1024, 1024));
    }

    [Fact]
    public void EditingIsTheOnlyOperation()
    {
        Assert.Throws<AiGenerationException>(() => AiImageJobPolicy.Profile(ImageWorkflow.MiniMaxH3, false));
        Assert.Throws<AiGenerationException>(() => PromptProfiles.Id(ImageWorkflow.MiniMaxH3, false));
        Assert.Equal("h3-image-edit-v1", AiImageJobPolicy.Profile(ImageWorkflow.MiniMaxH3, true));
        Assert.Throws<AiGenerationException>(() => ComfyH3Images.BuildWorkflow(Settings, "A change", 42, 1024, 1024, []));
        Assert.False(ComfyH3Images.CheckCatalog(Catalog(), Settings, false).Success);
    }

    [Theory]
    [InlineData(1000, 1024)] [InlineData(1024, 1000)] [InlineData(4096, 4096)]
    public void UnsupportedCanvasesAreRefused(int width, int height) =>
        Assert.Throws<AiGenerationException>(() => ComfyH3Images.BuildWorkflow(Settings, "A change", 42, width, height, ["c291cmNl"]));

    [Fact]
    public void TooManyReferencesAreRefused()
    {
        Assert.Equal(9, ImageWorkflowLimits.MaximumReferences(ImageWorkflow.MiniMaxH3));
        Assert.Throws<AiGenerationException>(() => ComfyH3Images.BuildWorkflow(Settings, "A change", 42, 1024, 1024, Enumerable.Repeat("c291cmNl", 10).ToArray()));
    }

    [Theory]
    [InlineData("10Eros_Max_h3_8stepTURBO_beta7_int8HQ.safetensors")]
    [InlineData("minimax_h3_fl2va_pruned_int8_convrot.safetensors")]
    public void ModelsThatCannotRunStandardAreRefused(string model)
    {
        var settings = Settings with { H3 = Settings.H3 with { Model = model } };
        Assert.Throws<AiGenerationException>(() => ComfyH3Images.BuildWorkflow(settings, "A change", 42, 1024, 1024, ["c291cmNl"]));
    }

    [Fact]
    public void AccelerationLorasAreRefusedAndOptionalOnesApply()
    {
        var optional = new AppliedLora(new(Settings.ComfyUrl, "styles/ink.safetensors", ImageWorkflow.MiniMaxH3, "Ink"), .5f);
        var graph = Graph(ComfyH3Images.BuildWorkflow(Settings, "A change", 42, 1024, 1024, ["c291cmNl"], null, [optional]));
        Assert.Equal("lora_1", Inputs(graph, "6").GetProperty("model")[0].GetString());
        Assert.Equal("1", Inputs(graph, "9").GetProperty("model")[0].GetString());
        var turbo = new AppliedLora(new(Settings.ComfyUrl, "minimax_h3_turbo_v4_step600_ema.safetensors", ImageWorkflow.MiniMaxH3, "Turbo"), .38f);
        Assert.Throws<AiGenerationException>(() => ComfyH3Images.BuildWorkflow(Settings, "A change", 42, 1024, 1024, ["c291cmNl"], null, [turbo]));
    }

    [Fact]
    public void CatalogCheckReportsReadinessMissingNodesAndFiles()
    {
        var ready = ComfyH3Images.CheckCatalog(Catalog(root => AddFiles(root, Settings.H3)), Settings, true);
        Assert.True(ready.Success, ready.Message);
        Assert.Contains("nine", ready.Message);
        Assert.Contains("has not been tested", ready.Message);

        var noNode = ComfyH3Images.CheckCatalog(Catalog(root => { AddFiles(root, Settings.H3); root.AsObject().Remove("LatentCut"); }), Settings, true);
        Assert.False(noNode.Success); Assert.Contains("LatentCut.samples", noNode.Message);

        var noTooling = ComfyH3Images.CheckCatalog(Catalog(root => { AddFiles(root, Settings.H3); root.AsObject().Remove("ETN_LoadImageBase64"); }), Settings, true);
        Assert.False(noTooling.Success); Assert.Contains("comfyui-tooling-nodes", noTooling.Message);

        var noFile = ComfyH3Images.CheckCatalog(Catalog(), Settings with { H3 = Settings.H3 with { AudioVae = "absent.safetensors" } }, true);
        Assert.False(noFile.Success); Assert.Contains("absent.safetensors", noFile.Message);

        var baked = ComfyH3Images.CheckCatalog(Catalog(), Settings with { H3 = Settings.H3 with { Model = "10Eros_Max_h3_8stepTURBO_beta7_int8HQ.safetensors" } }, true);
        Assert.False(baked.Success); Assert.Contains("Video models", baked.Message);
    }

    public static TheoryData<string> OfferedPresets => [.. H3Presets.Offered];

    [Theory]
    [MemberData(nameof(OfferedPresets))]
    public void EveryOfferedPresetBuildsAClosedOneFrameGraph(string preset)
    {
        var graph = Graph(ComfyH3Images.BuildWorkflow(Settings, "A change", 42, 1024, 1024, ["c291cmNl"], preset: preset));
        Assert.Equal(H3Presets.Model(preset, Settings.H3), Inputs(graph, "1").GetProperty("unet_name").GetString());
        Assert.Equal("join", Inputs(graph, "10").GetProperty("latent_image")[0].GetString());
        foreach (var node in graph.EnumerateObject())
            foreach (var input in node.Value.GetProperty("inputs").EnumerateObject())
                if (input.Value is { ValueKind: JsonValueKind.Array } link && link.GetArrayLength() == 2 && link[0].ValueKind == JsonValueKind.String && link[1].ValueKind == JsonValueKind.Number)
                    Assert.True(graph.TryGetProperty(link[0].GetString()!, out _), $"{node.Name}.{input.Name} points at a missing node");
    }

    [Fact]
    public void AnAcceleratedCheckpointRunsItsOwnRecipeLikeVideo()
    {
        var preset = H3AcceleratedCheckpoints.Compact;
        var graph = Graph(ComfyH3Images.BuildWorkflow(Settings, "A change", 42, 1024, 1024, ["c291cmNl"], preset: preset));
        Assert.Equal(H3AcceleratedCheckpoints.CompactFile, Inputs(graph, "1").GetProperty("unet_name").GetString());
        Assert.Equal("MiniMaxH3SigmaShift", Type(graph, "18"));
        Assert.Equal("compact-attention", Inputs(graph, "6").GetProperty("model")[0].GetString());
        Assert.Equal("18", Inputs(graph, "9").GetProperty("model")[0].GetString());
        Assert.Equal(8, Inputs(graph, "9").GetProperty("steps").GetInt32());
        Assert.Equal("euler", Inputs(graph, "8").GetProperty("sampler_name").GetString());
    }

    [Fact]
    public void APresetsOwnModelIgnoresTheVideoModelSelection()
    {
        var baked = Settings with { H3 = Settings.H3 with { Model = H3AcceleratedCheckpoints.CompactFile } };
        Assert.Throws<AiGenerationException>(() => ComfyH3Images.BuildWorkflow(baked, "A change", 42, 1024, 1024, ["c291cmNl"]));
        var graph = Graph(ComfyH3Images.BuildWorkflow(baked, "A change", 42, 1024, 1024, ["c291cmNl"], preset: H3AcceleratedCheckpoints.Compact));
        Assert.Equal(H3AcceleratedCheckpoints.CompactFile, Inputs(graph, "1").GetProperty("unet_name").GetString());
    }

    [Fact]
    public void ThePresetIsRecordedPerImage()
    {
        var metadata = AiImageJobPolicy.Metadata(Request(preset: H3AcceleratedCheckpoints.Compact), Guid.NewGuid(), new(Guid.NewGuid(), 1, 7));
        Assert.Equal(H3AcceleratedCheckpoints.CompactFile, metadata.DiffusionModel);
        Assert.Equal(8, metadata.Steps);
        Assert.Equal(H3AcceleratedCheckpoints.Compact, metadata.H3Image!.Preset);
        Assert.Equal("euler", metadata.H3Image.Sampler);
        Assert.False(H3ImagePolicy.InvalidMetadata(metadata, 1024, 1024));
        var pika = AiImageJobPolicy.Metadata(Request(preset: H3PikaFlexRecipe.Key), Guid.NewGuid(), new(Guid.NewGuid(), 1, 7));
        Assert.Equal(H3PikaFlexRecipe.Adapter(Settings.H3), pika.H3Image!.Checkpoint);
    }

    [Theory]
    [InlineData("nope", "supported")]
    [InlineData("turbo8", "removed")]
    public void UnofferedPresetsAreRefused(string preset, string message)
    {
        Assert.Contains(message, Assert.Throws<AiGenerationException>(() => ComfyH3Images.BuildWorkflow(Settings, "A change", 42, 1024, 1024, ["c291cmNl"], preset: preset)).Message);
        Assert.Contains(message, Assert.Throws<AiGenerationException>(() => AiImageJobPolicy.Validate(Request(preset: preset))).Message);
    }

    [Fact]
    public void PresetsAreCheckedSeparatelyFromTheSharedFiles()
    {
        var catalog = Catalog(root => AddFiles(root, Settings.H3));
        var edit = ComfyH3Images.CheckEditCatalog(catalog, Settings);
        Assert.True(edit.Success, edit.Message);
        Assert.Equal(H3Presets.Offered, edit.H3Presets.Select(p => p.Key));
        Assert.Null(edit.H3Presets.Single(p => p.Key == H3ImagePolicy.StandardPreset).Issue);
        var compact = edit.H3Presets.Single(p => p.Key == H3AcceleratedCheckpoints.Compact).Issue;
        Assert.NotNull(compact);
        // The pointer to Video models belongs only to a problem with the model selected there.
        Assert.DoesNotContain("use the model selected", edit.H3Presets.Single(p => p.Key == "pdd").Issue);
        var missing = ComfyH3Images.CheckEditCatalog(Catalog(), Settings with { H3 = Settings.H3 with { Model = "absent.safetensors" } });
        Assert.Contains("use the model selected", missing.H3Presets.Single(p => p.Key == H3ImagePolicy.StandardPreset).Issue);
        var check = ComfyH3Images.CheckCatalog(catalog, Settings, true, H3AcceleratedCheckpoints.Compact);
        Assert.False(check.Success); Assert.Equal(compact, check.Message);
        Assert.True(ComfyH3Images.CheckCatalog(catalog, Settings, true).Success);
        Assert.False(ComfyH3Images.CheckCatalog(catalog, Settings, true, "nope").Success);
    }

    private static void AddFiles(JsonNode root, H3Settings h3)
    {
        static void Add(JsonNode root, string node, string input, string file)
        {
            var choices = root[node]!["input"]!["required"]![input]![0]!.AsArray();
            if (choices.All(c => c!.GetValue<string>() != file)) choices.Add(file);
        }
        Add(root, "UNETLoader", "unet_name", h3.Model); Add(root, "CLIPLoader", "clip_name", h3.Encoder);
        Add(root, "VAELoader", "vae_name", h3.VideoVae); Add(root, "VAELoader", "vae_name", h3.AudioVae);
    }

    [Fact]
    public void RegionalOutputSizeIsTheCapturedCanvasEvenWhereRecomputingWouldDrift()
    {
        // At a 1024 budget this canvas recomputes to 1632x640, which compositing would later reject.
        var png = Png(1600, 640);
        Assert.Equal((1632, 640), ImageAspectPolicy.Size(ImageAspectPolicy.FromImage1, png, 1024));
        var source = new AssetImageReference(Guid.NewGuid(), Guid.NewGuid());
        var selection = new RegionalImageSelection { Source = source, SourceHash = "hash", Width = 1600, Height = 640 };
        var request = Request(1, new RegionalImageCapture(png, selection, new(0, 0, 1600, 640, 1600, 640)), aspect: ImageAspectPolicy.FromImage1, source: png);
        Assert.Equal((1600, 640), QwenImage21Policy.OutputSize(request));
    }

    [Fact]
    public void GuideIsBundled() => Assert.Contains("one still frame", PromptProfiles.Read(ImageWorkflow.MiniMaxH3, true));
}
