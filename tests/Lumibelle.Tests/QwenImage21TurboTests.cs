using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class QwenImage21Tests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TurboUsesThePublishedSigmasAndCapturedNoiseWithoutChangingReferenceEncoding(bool editing)
    {
        var settings = Settings with { QwenImage21 = Settings.QwenImage21! with { Model = "qwen_image_2.1_turbo_int8_convrot.safetensors", Variant = QwenImage21Variant.Turbo } };
        var images = editing ? new[] { Convert.ToBase64String(Png(96, 64)), Convert.ToBase64String(Png(64, 96)) } : [];
        var graph = JsonSerializer.SerializeToElement(ComfyQwenImage21.BuildWorkflow(settings, "A brass key on a table.", 42, 1024, 1024, images, new(Steps: 8)), AtomicJsonFile.Options).GetProperty("prompt");
        Assert.Equal("SamplerCustomAdvanced", graph.GetProperty("7").GetProperty("class_type").GetString());
        Assert.Equal("1.0, 0.978453, 0.95418, 0.926626, 0.89508, 0.845148, 0.704534, 0.414568, 0", graph.GetProperty("turbo_sigmas").GetProperty("inputs").GetProperty("sigmas").GetString());
        Assert.Equal(42, graph.GetProperty("turbo_noise").GetProperty("inputs").GetProperty("noise_seed").GetInt64());
        Assert.Equal("euler", graph.GetProperty("turbo_sampler").GetProperty("inputs").GetProperty("sampler_name").GetString());
        if (editing) Assert.True(graph.GetProperty("4").GetProperty("inputs").TryGetProperty("images.image_2", out _));
        Assert.Throws<AiGenerationException>(() => ComfyQwenImage21.BuildWorkflow(settings, "A key", 42, 1024, 1024, images, new(Steps: 25)));
    }

    [Fact]
    public void TurboCatalogRequiresItsSamplerNodesAndDoesNotRequireTheBaseSampler()
    {
        var settings = Settings with { QwenImage21 = Settings.QwenImage21! with { Model = "qwen_image_2.1_turbo_int8_convrot.safetensors", Variant = QwenImage21Variant.Turbo } };
        var catalog = Catalog(); catalog.Remove("KSampler");
        void Node(string name, params string[] fields) => catalog[name] = new JsonObject { ["input"] = new JsonObject
        { ["required"] = new JsonObject(fields.Select(f => KeyValuePair.Create<string, JsonNode?>(f, new JsonArray("ANY")))) } };
        Node("ManualSigmas", "sigmas"); Node("KSamplerSelect", "sampler_name"); Node("RandomNoise", "noise_seed");
        Node("CFGGuider", "model", "positive", "negative", "cfg"); Node("SamplerCustomAdvanced", "noise", "guider", "sampler", "sigmas", "latent_image");
        catalog["KSamplerSelect"]!["input"]!["required"]!["sampler_name"] = new JsonArray(new JsonArray("euler"));
        catalog["UNETLoader"]!["input"]!["required"]!["unet_name"] = new JsonArray(new JsonArray(settings.QwenImage21!.Model));
        var root = JsonSerializer.SerializeToElement(catalog, AtomicJsonFile.Options);
        Assert.True(ComfyQwenImage21.CheckCatalog(root, settings, true).Success);
        catalog.Remove("ManualSigmas");
        Assert.Contains("ManualSigmas", ComfyQwenImage21.CheckCatalog(JsonSerializer.SerializeToElement(catalog), settings, false).Message);
    }
}
