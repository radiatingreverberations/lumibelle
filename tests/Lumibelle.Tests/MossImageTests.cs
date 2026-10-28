using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed class MossImageTests
{
    internal static readonly AiSettings Settings = new() { DefaultImageWorkflow = ImageWorkflow.MossImage,
        MossImage = new() { EditLora = "moss-edit/adapter_model.safetensors" } };
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, AtomicJsonFile.Options);
    private static JsonElement Inputs(JsonElement graph, string node) => graph.GetProperty(node).GetProperty("inputs");
    private static byte[] Png(int width = 96, int height = 64)
    {
        using var image = new Image<Rgb24>(width, height); using var output = new MemoryStream(); image.SaveAsPng(output); return output.ToArray();
    }
    internal static AiImageJobRequest Request(int references = 0)
    {
        var project = Guid.NewGuid(); var asset = Guid.NewGuid(); var look = new AssetLookContext(asset, "Juniper", "", "", null, "", "", "");
        var inputs = Enumerable.Range(0, references).Select(_ => new AiImageInput(new(asset, Guid.NewGuid()), null, look, Png())).ToArray();
        ReferenceGenerationRequest? create = references == 0 ? new() { ProjectId = project, Workflow = ImageWorkflow.MossImage, Look = look, Prompt = "A brass key on a wooden table", AspectRatio = "1:1", Seed = 42 } : null;
        ReferenceEditRequest? edit = references > 0 ? new() { ProjectId = project, Workflow = ImageWorkflow.MossImage, Look = look,
            SourceAssetId = asset, SourceImageId = inputs[0].Reference.ImageId, Prompt = "Make the key silver", AspectRatio = "1:1", Seed = 42,
            ReferenceLooks = inputs.Select(i => new AssetReferenceLook(i.Reference, i.Context)).ToArray() } : null;
        return new(1, Guid.NewGuid(), project, asset, 0, Settings, AiImageJobPolicy.Profile(ImageWorkflow.MossImage, references > 0), create, edit, inputs, []);
    }

    [Fact]
    public void CreationUsesNativeMossEncoderFlux2LatentAndMultiplierOneShift()
    {
        var graph = Element(ComfyMossImages.BuildWorkflow(Settings with { MossImage = Settings.MossImage! with { EditLora = "" } },
            "A brass key", 42, 1024, 1024, [])).GetProperty("prompt");
        Assert.Equal("moss_image", Inputs(graph, "2").GetProperty("type").GetString());
        Assert.Equal("EmptyFlux2LatentImage", graph.GetProperty("6").GetProperty("class_type").GetString());
        Assert.Equal("ModelSamplingAuraFlow", graph.GetProperty("shift").GetProperty("class_type").GetString());
        Assert.Equal(5, Inputs(graph, "shift").GetProperty("shift").GetDouble());
        Assert.Equal(50, Inputs(graph, "7").GetProperty("steps").GetInt32());
        Assert.Equal(3, Inputs(graph, "7").GetProperty("cfg").GetDouble());
        Assert.Equal("22", Inputs(graph, "slg").GetProperty("single_layers").GetString());
        Assert.False(graph.TryGetProperty("edit_lora", out _));
        Assert.DoesNotContain("ModelSamplingSD3", graph.GetRawText());
    }

    [Theory]
    [InlineData("Moss-Image-Edit", 1)] [InlineData("Moss-Image-Edit", 4)]
    [InlineData("ai-toolkit", 2)] [InlineData("ai-toolkit (kv_cache off)", 3)]
    public void EditingRetainsReferenceOrderOnBothBranchesAndUsesRequiredAdapter(string format, int references)
    {
        var settings = Settings with { MossImage = Settings.MossImage! with { EditFormat = format } };
        var images = Enumerable.Range(1, references).Select(i => Convert.ToBase64String(Png(800 + i * 16, 400))).ToArray();
        var graph = Element(ComfyMossImages.BuildWorkflow(settings, "Make Image 1 silver", 42, 1024, 1024, images)).GetProperty("prompt");
        Assert.Equal(settings.MossImage.EditLora, Inputs(graph, "edit_lora").GetProperty("lora_name").GetString());
        Assert.Equal(1, Inputs(graph, "edit_lora").GetProperty("strength_model").GetDouble());
        Assert.Equal(30, Inputs(graph, "7").GetProperty("steps").GetInt32());
        Assert.Equal(4, Inputs(graph, "7").GetProperty("cfg").GetDouble());
        foreach (var node in new[] { "4", "5" })
        {
            Assert.Equal("TextEncodeMOSSImageEdit", graph.GetProperty(node).GetProperty("class_type").GetString());
            Assert.Equal(format, Inputs(graph, node).GetProperty("edit_format").GetString());
            Assert.Equal("3", Inputs(graph, node).GetProperty("vae")[0].GetString());
            for (var i = 1; i <= references; i++)
            {
                Assert.Equal($"reference_{i}_scale", Inputs(graph, node).GetProperty($"images.image_{i - 1}")[0].GetString());
                Assert.Equal(images[i - 1], Inputs(graph, $"reference_{i}").GetProperty("image").GetString());
                var sized = Inputs(graph, $"reference_{i}_scale");
                var width = sized.GetProperty("width").GetInt32(); var height = sized.GetProperty("height").GetInt32();
                Assert.InRange(width * height, (int)(1024 * 1024 * .97), (int)(1024 * 1024 * 1.03));
                Assert.InRange(Math.Abs((double)width / height / ((800 + i * 16) / 400.0) - 1), 0, .03);
            }
        }
        Assert.False(graph.TryGetProperty("slg", out _));
    }

    [Theory]
    [InlineData("moss-image-edit-v1", 512)] [InlineData("moss-image-edit-v2", 1024)]
    public void SquarePortraitKeepsDetailOnTallCanvasAndHistoricalJobsKeepTheirSizing(string profile, int expectedEdge)
    {
        var request = Request(1);
        request = request with { Profile = profile, Edit = request.Edit! with { AspectRatio = "9:16" },
            Inputs = request.Inputs.Select(i => i with { Png = Png(1024, 1024) }).ToArray() };
        AiImageJobPolicy.Validate(request);
        // Building the captured graph is pure; validation/catalog services are unused.
        var adapter = new ComfyImageJobAdapter(null!, null!, null!, null!, null!, null!);
        var graph = Element(adapter.Build(request, new(Guid.NewGuid(), 1, 42), "test-client")).GetProperty("prompt");
        Assert.Equal(expectedEdge, Inputs(graph, "reference_1_scale").GetProperty("width").GetInt32());
        Assert.Equal(expectedEdge, Inputs(graph, "reference_1_scale").GetProperty("height").GetInt32());
        Assert.Equal(768, Inputs(graph, "6").GetProperty("width").GetInt32());
        Assert.Equal(1344, Inputs(graph, "6").GetProperty("height").GetInt32());
        Assert.Equal("reference_1_scale", Inputs(graph, "4").GetProperty("images.image_0")[0].GetString());
        Assert.Equal("reference_1_scale", Inputs(graph, "5").GetProperty("images.image_0")[0].GetString());
        Assert.Equal("moss-image-edit-v2", AiImageJobPolicy.Profile(ImageWorkflow.MossImage, true));
    }

    [Fact]
    public void HigherResolutionMossEditsScaleReferencesWithTheCanvasWithoutStretchingThem()
    {
        var input = Convert.ToBase64String(Png(1024, 512));
        foreach (var resolution in new[] { 1024, 2048 })
        {
            var graph = Element(ComfyMossImages.BuildWorkflow(Settings, "Make the key silver", 42, resolution, resolution, [input])).GetProperty("prompt");
            var reference = Inputs(graph, "reference_1_scale");
            var width = reference.GetProperty("width").GetInt32(); var height = reference.GetProperty("height").GetInt32();
            Assert.InRange(Math.Abs((double)width / height - 2), 0, .03);
            Assert.InRange((double)width * height / (resolution * resolution), .97, 1.03);
            Assert.Equal(0, width % 16); Assert.Equal(0, height % 16);
        }
    }

    [Fact]
    public void InvalidEditCapturesAndMismatchedOptionalLorasFailBeforeSubmission()
    {
        Assert.Throws<AiGenerationException>(() => ComfyMossImages.BuildWorkflow(Settings, "Edit", 42, 1024, 1024,
            Enumerable.Repeat(Convert.ToBase64String(Png()), 5).ToArray()));
        Assert.Throws<AiGenerationException>(() => ComfyMossImages.BuildWorkflow(Settings with { MossImage = new() }, "Edit", 42, 1024, 1024, [Convert.ToBase64String(Png())]));
        var wrong = new AppliedLora(new("http://comfy.test", "style.safetensors", ImageWorkflow.Krea2, "Style"), 1);
        Assert.Throws<AiGenerationException>(() => ComfyMossImages.BuildWorkflow(Settings, "Create", 42, 1024, 1024, [], loras: [wrong]));
        Assert.Throws<AiGenerationException>(() => AiImageJobPolicy.Validate(Request(5)));
        Assert.Throws<AiGenerationException>(() => AiImageJobPolicy.Validate(Request() with { Settings = Settings with { MossImage = null } }));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(4)]
    public void SavedMetadataCapturesTheActualModelAndEditParameters(int references)
    {
        var request = Request(references); AiImageJobPolicy.Validate(request);
        var saved = AiImageJobPolicy.Metadata(request, Guid.NewGuid(), new(Guid.NewGuid(), 1, 42));
        Assert.Equal(Settings.MossImage, saved.MossImage!.Settings);
        Assert.False(MossImagePolicy.InvalidMetadata(saved, 1024, 1024));
        Assert.True(MossImagePolicy.InvalidMetadata(saved with { MossImage = null }, 1024, 1024));
        Assert.True(MossImagePolicy.InvalidMetadata(saved with { Steps = 9 }, 1024, 1024));
        Assert.True(MossImagePolicy.InvalidMetadata(saved, 800, 1024));
        if (references > 0) Assert.Equal(request.Inputs.Select(i => i.Reference), saved.Edit!.References);
    }

    [Fact]
    public void HistoricalSettingsRemainUnchangedAndMossNumbersMustBeFinite()
    {
        var legacy = Element(new AiSettings()); Assert.False(legacy.TryGetProperty("mossImage", out _));
        Assert.Throws<WorkspaceStoreException>(() => FileAiSettingsStore.Validate(Settings with { MossImage = new() { Guidance = double.NaN } }));
        Assert.Throws<WorkspaceStoreException>(() => FileAiSettingsStore.Validate(Settings with { MossImage = new() { Shift = 0 } }));
        Assert.Equal(4, ImageWorkflowLimits.MaximumReferences(ImageWorkflow.MossImage));
        Assert.Equal(LoraWorkflow.MossImage, ImageWorkflow.MossImage.LoraWorkflow());
        Assert.Contains("MOSS", PromptProfiles.Read(ImageWorkflow.MossImage, false));
        Assert.Contains("Image 1", PromptProfiles.Read(ImageWorkflow.MossImage, true));
    }

    internal static JsonObject Catalog(bool flattened = false)
    {
        var result = new JsonObject();
        // Schema fixture transcribed from the native node APIs, independent of our generated graph.
        void Node(string type, params string[] fields)
        {
            var inputs = new JsonObject();
            foreach (var field in fields) inputs[field] = new JsonArray(JsonValue.Create("*"));
            result[type] = new JsonObject { ["input"] = new JsonObject { ["required"] = inputs }, ["output"] = new JsonArray(JsonValue.Create("CONDITIONING")) };
        }
        Node("UNETLoader", "unet_name", "weight_dtype"); Node("CLIPLoader", "clip_name", "type", "device");
        Node("VAELoader", "vae_name"); Node("CLIPTextEncode", "clip", "text");
        Node("EmptyFlux2LatentImage", "width", "height", "batch_size"); Node("ModelSamplingAuraFlow", "model", "shift");
        Node("KSampler", "model", "seed", "steps", "cfg", "sampler_name", "scheduler", "denoise", "positive", "negative", "latent_image");
        Node("VAEDecode", "samples", "vae"); Node("PreviewImage", "images");
        Node("SkipLayerGuidanceDiTSimple", "model", "double_layers", "single_layers", "start_percent", "end_percent");
        Node("LoraLoaderModelOnly", "model", "lora_name", "strength_model"); Node("ETN_LoadImageBase64", "image");
        Node("ImageScale", "image", "width", "height", "upscale_method", "crop");
        Node("TextEncodeMOSSImageEdit", "clip", "prompt", "vae", "edit_format", "image_strength");
        void Combo(string node, string field, params string[] options) => result[node]!["input"]!["required"]![field] = new JsonArray(JsonSerializer.SerializeToNode(options));
        Combo("UNETLoader", "unet_name", Settings.MossImage!.Model); Combo("CLIPLoader", "clip_name", Settings.MossImage.TextEncoder);
        Combo("VAELoader", "vae_name", Settings.MossImage.Vae); Combo("CLIPLoader", "type", "moss_image");
        Combo("LoraLoaderModelOnly", "lora_name", Settings.MossImage.EditLora);
        Combo("KSampler", "sampler_name", "euler"); Combo("KSampler", "scheduler", "simple");
        Combo("TextEncodeMOSSImageEdit", "edit_format", MossImagePolicy.EditFormats.ToArray());
        var encoding = (JsonObject)result["TextEncodeMOSSImageEdit"]!["input"]!["required"]!;
        encoding.Remove("images.image_1");
        if (flattened) for (var i = 0; i < 4; i++) encoding[$"images.image_{i}"] = new JsonArray(JsonValue.Create("IMAGE"));
        else encoding["images"] = new JsonArray(JsonValue.Create("COMFY_AUTOGROW_V3"), new JsonObject
        {
            ["template"] = new JsonObject { ["prefix"] = "image_", ["min"] = 1, ["max"] = 10,
                ["input"] = new JsonObject { ["required"] = new JsonObject { ["image"] = new JsonArray(JsonValue.Create("IMAGE")) } } }
        });
        return result;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InstalledSchemaSupportsBothNativeAndFlattenedOrderedInputs(bool flattened)
    {
        var catalog = Catalog(flattened);
        Assert.True(ComfyMossImages.CheckCatalog(Element(catalog), Settings, false).Success);
        Assert.True(ComfyMossImages.CheckCatalog(Element(catalog), Settings, true).Success);
        if (flattened) ((JsonObject)catalog["TextEncodeMOSSImageEdit"]!["input"]!["required"]!).Remove("images.image_3");
        else catalog.Remove("TextEncodeMOSSImageEdit");
        var edit = ComfyMossImages.CheckCatalog(Element(catalog), Settings, true);
        Assert.False(edit.Success); Assert.Contains("TextEncodeMOSSImageEdit", edit.Message);
        Assert.True(ComfyMossImages.CheckCatalog(Element(catalog), Settings, false).Success);
    }

    [Fact]
    public void MissingAdapterDoesNotBlockCreationAndUnsupportedFormatsAreActionable()
    {
        var catalog = Element(Catalog());
        var settings = Settings with { MossImage = Settings.MossImage! with { EditLora = "missing.safetensors" } };
        Assert.True(ComfyMossImages.CheckCatalog(catalog, settings, false).Success);
        Assert.Contains("edit LoRA", ComfyMossImages.CheckCatalog(catalog, settings, true).Message);
        var unsupported = Catalog(); unsupported["TextEncodeMOSSImageEdit"]!["input"]!["required"]!["edit_format"] = new JsonArray(new JsonArray(JsonValue.Create("ai-toolkit")));
        Assert.Contains("format Moss-Image-Edit", ComfyMossImages.CheckCatalog(Element(unsupported), Settings, true).Message);
        var limited = Catalog(); limited["TextEncodeMOSSImageEdit"]!["input"]!["required"]!["images"]![1]!["template"]!["max"] = 2;
        Assert.False(ComfyMossImages.CheckCatalog(Element(limited), Settings, true).Success);
    }
}
