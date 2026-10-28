using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed class Flux3ImageTests
{
    internal static AiSettings Settings(Flux3ImageVariant variant = Flux3ImageVariant.Distilled) => new()
    {
        DefaultImageWorkflow = ImageWorkflow.Flux3ImageKlein,
        Flux3Image = new() { Variant = variant, Model = variant == Flux3ImageVariant.Base
            ? "flux3-image-klein-base-int8-convrot.safetensors" : "flux3-image-klein-int8-convrot.safetensors" }
    };
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, AtomicJsonFile.Options);
    private static JsonElement Inputs(JsonElement graph, string node) => graph.GetProperty(node).GetProperty("inputs");
    private static byte[] Png(int width = 96, int height = 64)
    {
        using var image = new Image<Rgb24>(width, height); using var output = new MemoryStream(); image.SaveAsPng(output); return output.ToArray();
    }
    private static JsonElement Build(AiSettings settings, int references = 0, string prompt = "A brass key on a plain wooden table.", int size = 1024) =>
        Element(ComfyFlux3Images.BuildWorkflow(settings, prompt, 42, size, size,
            Enumerable.Range(0, references).Select(i => Convert.ToBase64String(Png(96 + i * 16))).ToArray())).GetProperty("prompt");
    private static AiImageJobRequest Request(int references = 0, int resolution = 1024, Flux3ImageVariant variant = Flux3ImageVariant.Distilled)
    {
        var project = Guid.NewGuid(); var asset = Guid.NewGuid(); var look = new AssetLookContext(asset, "Juniper", "", "", null, "", "", "");
        var inputs = Enumerable.Range(0, references).Select(_ => new AiImageInput(new(asset, Guid.NewGuid()), null, look, Png())).ToArray();
        ReferenceGenerationRequest? create = references == 0 ? new() { ProjectId = project, Workflow = ImageWorkflow.Flux3ImageKlein, Look = look,
            Prompt = "A brass key on a wooden table.", AspectRatio = "1:1", Seed = 42, Resolution = resolution } : null;
        ReferenceEditRequest? edit = references > 0 ? new() { ProjectId = project, Workflow = ImageWorkflow.Flux3ImageKlein, Look = look,
            SourceAssetId = asset, SourceImageId = inputs[0].Reference.ImageId, Prompt = "A silver key on the same wooden table.", AspectRatio = "1:1", Seed = 42,
            ReferenceLooks = inputs.Select(i => new AssetReferenceLook(i.Reference, i.Context)).ToArray(), Resolution = resolution } : null;
        return new(1, Guid.NewGuid(), project, asset, 0, Settings(variant), AiImageJobPolicy.Profile(ImageWorkflow.Flux3ImageKlein, references > 0), create, edit, inputs, []);
    }

    [Theory]
    [InlineData(Flux3ImageVariant.Distilled, 16, 1)] [InlineData(Flux3ImageVariant.Base, 50, 3.5)]
    public void NativeCreationUsesTheSelectedVariantAndExactCaption(Flux3ImageVariant variant, int steps, double cfg)
    {
        const string caption = "A small silver key <obj_1> on a wooden table. [{\"id\":\"obj_1\",\"bbox\":[200,200,600,600],\"desc\":\"silver key\"}]";
        var graph = Build(Settings(variant), prompt: caption);
        Assert.Equal("flux3", Inputs(graph, "2").GetProperty("type").GetString());
        Assert.Equal("EmptyFlux2LatentImage", graph.GetProperty("6").GetProperty("class_type").GetString());
        Assert.Equal(caption, Inputs(graph, "4").GetProperty("text").GetString());
        Assert.Equal(steps, Inputs(graph, "7").GetProperty("steps").GetInt32());
        Assert.Equal(cfg, Inputs(graph, "7").GetProperty("cfg").GetDouble());
        Assert.Equal("euler", Inputs(graph, "7").GetProperty("sampler_name").GetString());
        Assert.Equal("simple", Inputs(graph, "7").GetProperty("scheduler").GetString());
        Assert.Equal(variant == Flux3ImageVariant.Distilled ? "ConditioningZeroOut" : "CLIPTextEncode", graph.GetProperty("5").GetProperty("class_type").GetString());
        Assert.False(graph.TryGetProperty("references", out _));
        Assert.DoesNotContain("FluxKVCache", graph.GetRawText());
        Assert.DoesNotContain("TextGenerate", graph.GetRawText());
    }

    [Theory]
    [InlineData(1)] [InlineData(10)]
    public void EditingSuppliesAllReferencesToBothBranchesWithoutChangingTheCanvas(int count)
    {
        var settings = Settings() with { Flux3Image = new() { ReferenceMegapixels = .5 } };
        var graph = Build(settings, count, size: 2048);
        var refs = Inputs(graph, "references");
        Assert.Equal("ReferenceMultiLatent", graph.GetProperty("references").GetProperty("class_type").GetString());
        Assert.Equal("resize", refs.GetProperty("resolution").GetString());
        Assert.Equal(.5, refs.GetProperty("resolution.megapixels").GetDouble());
        Assert.Equal("3", refs.GetProperty("vae")[0].GetString());
        for (var i = 0; i < count; i++) Assert.Equal($"reference_{i}", refs.GetProperty($"images.image{i}")[0].GetString());
        Assert.Equal("references", Inputs(graph, "7").GetProperty("positive")[0].GetString());
        Assert.Equal(1, Inputs(graph, "7").GetProperty("negative")[1].GetInt32());
        Assert.Equal("6", Inputs(graph, "7").GetProperty("latent_image")[0].GetString());
        Assert.Equal(2048, Inputs(graph, "6").GetProperty("width").GetInt32());
        Assert.DoesNotContain("LoraLoaderModelOnly", graph.GetRawText());
    }

    [Fact]
    public void WrongVariantForeignLorasAndEleventhReferenceFailBeforeExecution()
    {
        Assert.Throws<WorkspaceStoreException>(() => Flux3ImagePolicy.ValidateSettings(new() { Model = Settings(Flux3ImageVariant.Base).Flux3Image!.Model }));
        Assert.Throws<WorkspaceStoreException>(() => Flux3ImagePolicy.ValidateSettings(new() { Guidance = 4 }));
        Assert.Throws<WorkspaceStoreException>(() => Flux3ImagePolicy.ValidateSettings(new() { ReferenceMegapixels = double.NaN }));
        Assert.Throws<AiGenerationException>(() => Build(Settings(), 11));
        var foreign = new AppliedLora(new("http://comfy.test", "style.safetensors", ImageWorkflow.Flux2Klein9bKv, "Style"), 1);
        Assert.Throws<AiGenerationException>(() => ComfyFlux3Images.BuildWorkflow(Settings(), "A key", 42, 1024, 1024, [], loras: [foreign]));
        Assert.Throws<AiGenerationException>(() => AiImageJobPolicy.Validate(Request(11)));
    }

    [Theory]
    [InlineData(0, 1024)] [InlineData(10, 2048)] [InlineData(1, 4096)]
    public void MetadataRetainsVariantReferencesAndActualCanvas(int references, int resolution)
    {
        var request = Request(references, resolution); AiImageJobPolicy.Validate(request);
        var metadata = AiImageJobPolicy.Metadata(request, Guid.NewGuid(), new(Guid.NewGuid(), 1, 42));
        Assert.Equal(request.Settings.Flux3Image, metadata.Flux3Image!.Settings);
        Assert.Equal((resolution, resolution), (metadata.Flux3Image.Width, metadata.Flux3Image.Height));
        Assert.False(Flux3ImagePolicy.InvalidMetadata(metadata, resolution, resolution));
        Assert.True(Flux3ImagePolicy.InvalidMetadata(metadata with { Flux3Image = null }, resolution, resolution));
        Assert.True(Flux3ImagePolicy.InvalidMetadata(metadata with { Steps = 5 }, resolution, resolution));
        if (references > 0) Assert.Equal(request.Inputs.Select(i => i.Reference), metadata.Edit!.References);
    }

    [Fact]
    public void FourKGeometryIsAvailableOnlyToFlux3RequestsAndAlignedToTheModel()
    {
        foreach (var workflow in Enum.GetValues<ImageWorkflow>())
            Assert.Equal(workflow == ImageWorkflow.Flux3ImageKlein, ImageWorkflowLimits.SupportsResolution(workflow, 4096));
        foreach (var aspect in new[] { "1:1", "16:9", "9:16", "From Image 1" })
        {
            var size = ImageAspectPolicy.Size(aspect, 800, 500, 4096);
            Assert.Equal(0, size.Width % 32); Assert.Equal(0, size.Height % 32);
            Assert.InRange((long)size.Width * size.Height, 15_000_000, Flux3ImagePolicy.MaximumPixels);
        }
        var other = Request() with { Settings = new() { DefaultImageWorkflow = ImageWorkflow.Krea2 } };
        other = other with { Profile = AiImageJobPolicy.Profile(ImageWorkflow.Krea2, false), Create = other.Create! with { Workflow = ImageWorkflow.Krea2, Resolution = 4096 } };
        Assert.Throws<AiGenerationException>(() => AiImageJobPolicy.Validate(other));
    }

    [Fact]
    public async Task ReferencePreparationPreservesDetailAndExplicitCrops()
    {
        using var source = new MemoryStream(Png(2048, 1536));
        var png = await NativeImageInputs.PrepareAsync(source, new() { X = .25, Y = 0, Width = .75, Height = 1 }, TestContext.Current.CancellationToken, "FLUX 3");
        var info = ImageInspector.Inspect(png);
        Assert.Equal((1536, 1536), (info.Width, info.Height));
        Assert.True((long)info.Width * info.Height > 2_000_000);
    }

    [Fact]
    public void EnhancementGuidesDistinguishLocalEditsFromNewCompositionsAndPreserveSourceLabels()
    {
        Assert.Contains("complete scene caption", PromptProfiles.Read(ImageWorkflow.Flux3ImageKlein, false));
        Assert.Contains("local modification", PromptProfiles.Read(ImageWorkflow.Flux3ImageKlein, true));
        Assert.Contains("newly composed scene", PromptProfiles.Read(ImageWorkflow.Flux3ImageKlein, true));
        Assert.Contains("<ref_image_0>", PromptProfiles.Read(ImageWorkflow.Flux3ImageKlein, true));
        Assert.False(Element(new AiSettings()).TryGetProperty("flux3Image", out _));
        Assert.Equal(LoraWorkflow.Flux3ImageKlein, ImageWorkflow.Flux3ImageKlein.LoraWorkflow());
    }

    private static JsonObject Catalog(bool flattened = false)
    {
        var result = new JsonObject();
        void Node(string type, string[] fields, params string[] outputs)
        {
            var inputs = new JsonObject(); foreach (var field in fields) inputs[field] = new JsonArray(JsonValue.Create("*"));
            result[type] = new JsonObject { ["input"] = new JsonObject { ["required"] = inputs }, ["output"] = JsonSerializer.SerializeToNode(outputs) };
        }
        Node("UNETLoader", ["unet_name", "weight_dtype"]); Node("CLIPLoader", ["clip_name", "type", "device"]);
        Node("VAELoader", ["vae_name"]); Node("CLIPTextEncode", ["clip", "text"]); Node("ConditioningZeroOut", ["conditioning"]);
        Node("EmptyFlux2LatentImage", ["width", "height", "batch_size"]); Node("VAEDecode", ["samples", "vae"]); Node("PreviewImage", ["images"]);
        Node("KSampler", ["model", "seed", "steps", "cfg", "sampler_name", "scheduler", "denoise", "positive", "negative", "latent_image"]);
        Node("ETN_LoadImageBase64", ["image"]); Node("ReferenceMultiLatent", ["positive", "negative", "vae", "resolution"], "CONDITIONING", "CONDITIONING", "LATENT");
        void Combo(string node, string field, params string[] choices) => result[node]!["input"]!["required"]![field] = new JsonArray(JsonSerializer.SerializeToNode(choices));
        var flux = Settings().Flux3Image!;
        Combo("UNETLoader", "unet_name", flux.Model); Combo("CLIPLoader", "clip_name", flux.TextEncoder); Combo("CLIPLoader", "type", "flux3");
        Combo("VAELoader", "vae_name", flux.Vae); Combo("KSampler", "sampler_name", "euler"); Combo("KSampler", "scheduler", "simple");
        var references = (JsonObject)result["ReferenceMultiLatent"]!["input"]!["required"]!;
        references["resolution"] = new JsonArray(JsonValue.Create("COMFY_DYNAMICCOMBO_V3"), new JsonObject
        { ["options"] = new JsonArray(new JsonObject { ["key"] = "resize", ["inputs"] = new JsonObject { ["required"] = new JsonObject { ["megapixels"] = new JsonArray(JsonValue.Create("FLOAT")) } } }) });
        if (flattened) for (var i = 0; i < 10; i++) references[$"images.image{i}"] = new JsonArray(JsonValue.Create("IMAGE"));
        else references["images"] = new JsonArray(JsonValue.Create("COMFY_AUTOGROW_V3"), new JsonObject { ["template"] = new JsonObject
        { ["prefix"] = "image", ["min"] = 1, ["max"] = 100, ["input"] = new JsonObject { ["optional"] = new JsonObject { ["image"] = new JsonArray(JsonValue.Create("IMAGE")) } } } });
        return result;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CapabilityChecksAcceptNativeOrFlattenedInputsAndKeepCreationIndependent(bool flattened)
    {
        var catalog = Catalog(flattened);
        Assert.True(ComfyFlux3Images.CheckCatalog(Element(catalog), Settings(), true).Success);
        catalog.Remove("ReferenceMultiLatent");
        Assert.True(ComfyFlux3Images.CheckCatalog(Element(catalog), Settings(), false).Success);
        Assert.Contains("ReferenceMultiLatent", ComfyFlux3Images.CheckCatalog(Element(catalog), Settings(), true).Message);
    }

    [Fact]
    public void MissingTenthReferenceAndWrongEncoderTypeProduceActionableFailures()
    {
        var catalog = Catalog(true); ((JsonObject)catalog["ReferenceMultiLatent"]!["input"]!["required"]!).Remove("images.image9");
        Assert.Contains("ten ordered", ComfyFlux3Images.CheckCatalog(Element(catalog), Settings(), true).Message);
        catalog = Catalog(); catalog["CLIPLoader"]!["input"]!["required"]!["type"] = new JsonArray(new JsonArray(JsonValue.Create("flux2")));
        Assert.Contains("flux3 type", ComfyFlux3Images.CheckCatalog(Element(catalog), Settings(), false).Message);
    }
}
