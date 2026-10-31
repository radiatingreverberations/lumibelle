using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public void PikaFlexFollowsComfyTemplateOnItsOwnBaseWithOptionalLorasAfterTheAdapter()
    {
        var ordinary = PresetSnapshot(H3PikaFlexRecipe.Key);
        ordinary.Settings.Model = H3AcceleratedCheckpoints.CompactFile;
        // Like SMD, the base is selected separately from the ordinary H3 model.
        H3Presets.Capture(ordinary.Shot, ordinary.Settings);
        var snapshot = WithLoras(H3PikaFlexRecipe.Effective(ordinary));
        Assert.Equal(H3SmdRecipe.BaseFile, snapshot.Settings.Model);
        Assert.Equal(6, H3Policy.Quality(snapshot.Shot));
        Assert.Equal("Pika H3 Flex · 6 steps", H3Policy.QualityLabel(snapshot.Shot));
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 42, "test", [])).GetProperty("prompt");
        JsonElement Inputs(string id) => graph.GetProperty(id).GetProperty("inputs");
        // The values of ComfyUI's video_pika_h3_flex_ref2va template.
        Assert.Equal("ManualSigmas", graph.GetProperty("9").GetProperty("class_type").GetString());
        Assert.Equal("1.0, 0.972973, 0.923077, 0.8, 0.571429, 0.333333, 0.0", Inputs("9").GetProperty("sigmas").GetString());
        Assert.Equal("lcm", Inputs("8").GetProperty("sampler_name").GetString());
        Assert.Equal("BasicGuider", graph.GetProperty("6").GetProperty("class_type").GetString());
        Assert.Equal(H3PikaFlexRecipe.AdapterFile, Inputs("pika-adapter").GetProperty("lora_name").GetString());
        Assert.Equal(1, Inputs("pika-adapter").GetProperty("strength_model").GetDouble());
        Assert.Equal("pika-adapter", Inputs("lora_1").GetProperty("model")[0].GetString());
        // The template has no shift node and no Turbo LoRA.
        Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() is "MiniMaxH3SigmaShift" or "BasicScheduler");
        Assert.False(graph.TryGetProperty("16", out _));
        var identities = H3GenerationRecipes.Models(new(2, Guid.NewGuid(), snapshot, []));
        Assert.Equal(H3SmdRecipe.BaseFile, identities[0].FileName);
        Assert.Equal(H3PikaFlexRecipe.AdapterFile, identities.First(m => m.Category == "loras").FileName);
        Assert.Equal([H3Requirements.PikaBase, H3Requirements.PikaAdapter], H3Requirements.ForPreset(H3PikaFlexRecipe.Key));
    }

    [Fact]
    public void PikaFlexChecksNativeContractsAndPinsBothBaseAndAdapterContents()
    {
        var root = PresetCatalog();
        var native = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/h3-native-acceleration-contract.json")))!.AsObject();
        foreach (var pair in native) root[pair.Key] = pair.Value!.DeepClone();
        root["LoraLoaderModelOnly"]!["input"]!["required"]!["lora_name"]![0]!.AsArray().Add(H3PikaFlexRecipe.AdapterFile);
        var settings = new H3Settings();
        H3PresetSetup Check() => H3PikaFlexRecipe.Inspect(JsonSerializer.SerializeToElement(root), settings, null);
        Assert.Null(Check().Issue);
        settings.PikaBaseModel = "incompatible.safetensors"; Assert.NotNull(Check().Issue); settings.PikaBaseModel = null;
        settings.PikaLora = "pika_h3_flex_fl2va_v1_bf16.safetensors"; Assert.NotNull(Check().Issue); settings.PikaLora = null;
        root["ManualSigmas"]!["output"] = new JsonArray("MODEL"); Assert.NotNull(Check().Issue);
        var snapshot = H3PikaFlexRecipe.Effective(PresetSnapshot(H3PikaFlexRecipe.Key));
        var request = new AiVideoJobRequest(2, Guid.NewGuid(), snapshot, []);
        var evidence = new H3RecipeEvidence(H3GenerationRecipes.Models(request).Select(m => m with { Bytes = 10,
            Sha256 = m.Category == "diffusion_models" ? H3SmdRecipe.BaseHash : m.Category == "loras" ? H3PikaFlexRecipe.AdapterHash : new('A', 64) }).ToArray(),
            new(new Dictionary<string, string?>(), new Dictionary<string, string?>()));
        H3GenerationRecipes.Validate(request with { Recipe = H3GenerationRecipes.Capture(request, evidence) });
        evidence = evidence with { Models = evidence.Models.Select(m => m.Category == "loras" ? m with { Sha256 = new('B', 64) } : m).ToArray() };
        Assert.Throws<WorkspaceStoreException>(() => H3GenerationRecipes.Validate(request with { Recipe = H3GenerationRecipes.Capture(request, evidence) }));
        // The Pika weights cannot ride along as an ordinary character or style LoRA on another preset.
        var standard = PresetSnapshot("standard");
        var other = new AiVideoJobRequest(2, Guid.NewGuid(), standard, []);
        var smuggled = new H3RecipeEvidence(H3GenerationRecipes.Models(other).Append(new("loras", H3PikaFlexRecipe.AdapterFile) { Bytes = 10, Sha256 = H3PikaFlexRecipe.AdapterHash })
            .Select(m => m.Sha256 is null ? m with { Bytes = 10, Sha256 = new('A', 64) } : m).ToArray(), new(new Dictionary<string, string?>(), new Dictionary<string, string?>()));
        Assert.Throws<WorkspaceStoreException>(() => H3PikaFlexRecipe.ValidateIdentity(standard, H3GenerationRecipes.Capture(other, smuggled)));
    }
}
