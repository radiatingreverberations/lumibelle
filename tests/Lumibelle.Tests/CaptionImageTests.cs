using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class CaptionImageTests
{
    internal static AiSettings Settings(ImageWorkflow workflow, CaptionImageVariant variant)
    {
        var value = CaptionImagePolicy.Defaults(workflow) with { Variant = variant };
        value = value with { Model = workflow == ImageWorkflow.Fibo2
            ? variant == CaptionImageVariant.Turbo ? "fibo2_edit_turbo_int8_convrot.safetensors" : "fibo2_dev_int8_convrot.safetensors"
            : variant == CaptionImageVariant.Turbo ? "z_image_turbo_bf16.safetensors" : "z_image_bf16.safetensors" };
        var settings = new AiSettings { DefaultImageWorkflow = workflow };
        return workflow == ImageWorkflow.Fibo2 ? settings with { Fibo2 = value } : settings with { ZImage = value };
    }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Inputs(JsonElement graph, string id) => graph.GetProperty(id).GetProperty("inputs");

    [Theory]
    [InlineData(ImageWorkflow.Fibo2, CaptionImageVariant.Base, 0, 50, 5)]
    [InlineData(ImageWorkflow.Fibo2, CaptionImageVariant.Base, 4, 50, 5)]
    [InlineData(ImageWorkflow.Fibo2, CaptionImageVariant.Turbo, 1, 4, 1)]
    [InlineData(ImageWorkflow.Fibo2, CaptionImageVariant.Turbo, 4, 4, 1)]
    [InlineData(ImageWorkflow.ZImage, CaptionImageVariant.Base, 0, 25, 4)]
    [InlineData(ImageWorkflow.ZImage, CaptionImageVariant.Turbo, 0, 8, 1)]
    public void NativeGraphsUseThePublishedEncodersLatentsSamplingAndReferencePorts(ImageWorkflow workflow, CaptionImageVariant variant, int count, int steps, double cfg)
    {
        const string caption = "{\"short_description\":\"A brass key on a wooden table.\",\"edit_instruction\":\"Make the key silver.\"}";
        var graph = Json(ComfyCaptionImages.BuildWorkflow(Settings(workflow, variant), workflow, caption, 42, 1024, 1024,
            Enumerable.Range(0, count).Select(i => "reference-" + i).ToArray())).GetProperty("prompt");
        Assert.Equal(steps, Inputs(graph, "7").GetProperty("steps").GetInt32()); Assert.Equal(cfg, Inputs(graph, "7").GetProperty("cfg").GetDouble());
        Assert.Equal("simple", Inputs(graph, "7").GetProperty("scheduler").GetString());
        var fibo = workflow == ImageWorkflow.Fibo2;
        Assert.Equal(fibo ? "fibo2" : "lumina2", Inputs(graph, "2").GetProperty("type").GetString());
        Assert.Equal(fibo ? "euler" : "res_multistep", Inputs(graph, "7").GetProperty("sampler_name").GetString());
        Assert.Equal(caption, Inputs(graph, "4").GetProperty(fibo ? "prompt" : "text").GetString());
        if (fibo)
        {
            Assert.Equal("TextEncodeFibo2", graph.GetProperty("4").GetProperty("class_type").GetString());
            Assert.Equal("[\"4\",2]", Inputs(graph, "7").GetProperty("latent_image").GetRawText());
            Assert.Equal("[\"4\",1]", Inputs(graph, "7").GetProperty("negative").GetRawText());
            for (var i = 0; i < count; i++) Assert.Equal($"[\"reference_{i}\",0]", Inputs(graph, "4").GetProperty($"images.image_{i + 1}").GetRawText());
        }
        else
        {
            Assert.Equal("EmptySD3LatentImage", graph.GetProperty("6").GetProperty("class_type").GetString());
            Assert.Equal(3, Inputs(graph, "sampling").GetProperty("shift").GetDouble());
        }
        Assert.DoesNotContain("LoraLoader", graph.GetRawText()); Assert.DoesNotContain("TextGenerate", graph.GetRawText());
    }

    [Fact]
    public void UnsupportedModesAndVariantMismatchesFailBeforeSubmission()
    {
        Assert.Throws<AiGenerationException>(() => ComfyCaptionImages.BuildWorkflow(Settings(ImageWorkflow.ZImage, CaptionImageVariant.Turbo), ImageWorkflow.ZImage, "A key", 1, 1024, 1024, ["image"]));
        Assert.Throws<AiGenerationException>(() => ComfyCaptionImages.BuildWorkflow(Settings(ImageWorkflow.Fibo2, CaptionImageVariant.Turbo), ImageWorkflow.Fibo2, "A key", 1, 1024, 1024, []));
        Assert.Throws<AiGenerationException>(() => ComfyCaptionImages.BuildWorkflow(Settings(ImageWorkflow.Fibo2, CaptionImageVariant.Base), ImageWorkflow.Fibo2, "A key", 1, 1024, 1024, ["1", "2", "3", "4", "5"]));
        Assert.Throws<WorkspaceStoreException>(() => CaptionImagePolicy.ValidateSettings(CaptionImagePolicy.Defaults(ImageWorkflow.ZImage) with { Variant = CaptionImageVariant.Base }, ImageWorkflow.ZImage));
        Assert.Throws<WorkspaceStoreException>(() => CaptionImagePolicy.ValidateSettings(CaptionImagePolicy.Defaults(ImageWorkflow.ZImage) with { Guidance = double.NaN }, ImageWorkflow.ZImage));
    }

    private static JsonObject Catalog(ImageWorkflow workflow)
    {
        var root = new JsonObject();
        void Node(string name, string[] fields, string[]? output = null) => root[name] = new JsonObject
        { ["input"] = new JsonObject { ["required"] = new JsonObject(fields.Select(f => KeyValuePair.Create<string, JsonNode?>(f, new JsonArray(JsonValue.Create("ANY"))))) }, ["output"] = JsonSerializer.SerializeToNode(output ?? []) };
        void Choice(string node, string field, params string[] values) => root[node]!["input"]!["required"]![field] = new JsonArray(JsonSerializer.SerializeToNode(values));
        var settings = CaptionImagePolicy.Defaults(workflow);
        Node("UNETLoader", ["unet_name", "weight_dtype"]); Node("CLIPLoader", ["clip_name", "type", "device"]); Node("VAELoader", ["vae_name"]);
        Node("KSampler", ["model", "seed", "steps", "cfg", "sampler_name", "scheduler", "denoise", "positive", "negative", "latent_image"]);
        Node("VAEDecode", ["samples", "vae"]); Node("PreviewImage", ["images"]); Node("ETN_LoadImageBase64", ["image"]);
        Node("TextEncodeFibo2", ["clip", "prompt", "negative_prompt", "vae", "width", "height", "batch_size"], ["CONDITIONING", "CONDITIONING", "LATENT"]);
        root["TextEncodeFibo2"]!["input"]!["required"]!["images"] = new JsonArray(JsonValue.Create("COMFY_AUTOGROW_V3"), new JsonObject
        { ["template"] = new JsonObject { ["min"] = 0, ["names"] = new JsonArray("image_1", "image_2", "image_3", "image_4"),
            ["input"] = new JsonObject { ["required"] = new JsonObject { ["image"] = new JsonArray("IMAGE") } } } });
        Node("EmptySD3LatentImage", ["width", "height", "batch_size"]); Node("CLIPTextEncode", ["clip", "text"]);
        Node("ModelSamplingAuraFlow", ["model", "shift"]); Node("ConditioningZeroOut", ["conditioning"]);
        Choice("UNETLoader", "unet_name", settings.Model); Choice("CLIPLoader", "clip_name", settings.TextEncoder); Choice("VAELoader", "vae_name", settings.Vae);
        Choice("CLIPLoader", "type", "fibo2", "lumina2"); Choice("KSampler", "sampler_name", "euler", "res_multistep"); Choice("KSampler", "scheduler", "simple");
        return root;
    }

    [Fact]
    public void CapabilityChecksDistinguishCreationEditingAndMissingNativeNodes()
    {
        var fibo = Settings(ImageWorkflow.Fibo2, CaptionImageVariant.Base); var catalog = Catalog(ImageWorkflow.Fibo2);
        Assert.True(ComfyCaptionImages.CheckCatalog(Json(catalog), fibo, ImageWorkflow.Fibo2, true).Success);
        catalog["TextEncodeFibo2"]!["input"]!["required"]!["images"]![1]!["template"]!["names"] = new JsonArray("image_1", "image_2", "image_3");
        Assert.Contains("four ordered", ComfyCaptionImages.CheckCatalog(Json(catalog), fibo, ImageWorkflow.Fibo2, true).Message);
        Assert.True(ComfyCaptionImages.CheckCatalog(Json(catalog), fibo, ImageWorkflow.Fibo2, false).Success);
        var z = Settings(ImageWorkflow.ZImage, CaptionImageVariant.Turbo); catalog = Catalog(ImageWorkflow.ZImage);
        Assert.True(ComfyCaptionImages.CheckCatalog(Json(catalog), z, ImageWorkflow.ZImage, false).Success);
        Assert.Contains("creation", ComfyCaptionImages.CheckCatalog(Json(catalog), z, ImageWorkflow.ZImage, true).Message);
        catalog.Remove("ModelSamplingAuraFlow");
        Assert.Contains("ModelSamplingAuraFlow", ComfyCaptionImages.CheckCatalog(Json(catalog), z, ImageWorkflow.ZImage, false).Message);
    }

    [Fact]
    public void FiboEnhancementRequiresACompleteVglCaptionInsideTheApplicationEnvelope()
    {
        var context = new PromptEnhancementContext { ProjectId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Prompt = "Make the key silver.", Workflow = ImageWorkflow.Fibo2, IsEdit = true };
        string Envelope(string text) => JsonSerializer.Serialize(new { kind = "Prompt", text });
        Assert.Null(PromptEnhancer.ParseForContext(Envelope("{\"short_description\":\"A key\""), context));
        Assert.Null(PromptEnhancer.ParseForContext(Envelope("{\"short_description\":\"A key\"}"), context));
        var caption = "{\"short_description\":\"A silver key.\",\"edit_instruction\":\"Make the key silver.\"}";
        Assert.Equal(caption, PromptEnhancer.ParseForContext(Envelope(caption), context)!.Text);
        var guide = PromptProfiles.Read(ImageWorkflow.Fibo2, true); Assert.Contains("edit_instruction", guide);
        Assert.Contains("Z-Image", PromptProfiles.Read(ImageWorkflow.ZImage, false));
    }
}
