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
    public void SmdUsesItsCompatibleBaseSingleAdapterAndFixedSigmasWithOptionalLorasAfterTheAdapter()
    {
        var ordinary = PresetSnapshot(H3SmdRecipe.Key);
        ordinary.Settings.Model = H3AcceleratedCheckpoints.CompactFile;
        // SMD's own base selection is independent even if the ordinary slot
        // currently contains another recipe's complete checkpoint.
        H3Presets.Capture(ordinary.Shot, ordinary.Settings);
        var snapshot = WithLoras(H3SmdRecipe.Effective(ordinary));
        Assert.Equal(H3AcceleratedCheckpoints.CompactFile, ordinary.Settings.Model);
        Assert.Equal(H3SmdRecipe.BaseFile, snapshot.Settings.Model);
        Assert.Equal(4, H3Policy.Quality(snapshot.Shot));
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 42, "test", [])).GetProperty("prompt");
        JsonElement Inputs(string id) => graph.GetProperty(id).GetProperty("inputs");
        Assert.Equal("ManualSigmas", graph.GetProperty("9").GetProperty("class_type").GetString());
        Assert.Equal(H3SmdRecipe.Sigmas, Inputs("9").GetProperty("sigmas").GetString());
        Assert.Equal("euler", Inputs("8").GetProperty("sampler_name").GetString());
        Assert.Equal("BasicGuider", graph.GetProperty("6").GetProperty("class_type").GetString());
        Assert.Equal(H3SmdRecipe.AdapterFile, Inputs("smd-adapter").GetProperty("lora_name").GetString());
        Assert.Equal(1, Inputs("smd-adapter").GetProperty("strength_model").GetDouble());
        Assert.Equal("smd-shift", Inputs("lora_1").GetProperty("model")[0].GetString());
        Assert.False(graph.TryGetProperty("16", out _));
        Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == "BasicScheduler");
        var identities = H3GenerationRecipes.Models(new(2, Guid.NewGuid(), snapshot, []));
        Assert.Equal(H3SmdRecipe.BaseFile, identities[0].FileName);
        Assert.Equal(H3SmdRecipe.AdapterFile, identities.First(m => m.Category == "loras").FileName);
    }

    [Fact]
    public void SmdChecksSpecificNativeContractsAndPinsBothBaseAndAdapterContents()
    {
        var root = PresetCatalog();
        var native = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/h3-native-acceleration-contract.json")))!.AsObject();
        foreach (var pair in native) root[pair.Key] = pair.Value!.DeepClone();
        root["LoraLoaderModelOnly"]!["input"]!["required"]!["lora_name"]![0]!.AsArray().Add(H3SmdRecipe.AdapterFile);
        var settings = new H3Settings();
        H3PresetSetup Check() => H3SmdRecipe.Inspect(JsonSerializer.SerializeToElement(root), settings, null);
        Assert.Null(Check().Issue);
        settings.SmdBaseModel = "incompatible.safetensors"; Assert.NotNull(Check().Issue); settings.SmdBaseModel = null;
        root["ManualSigmas"]!["output"] = new JsonArray("MODEL"); Assert.NotNull(Check().Issue);
        var snapshot = H3SmdRecipe.Effective(PresetSnapshot(H3SmdRecipe.Key));
        var request = new AiVideoJobRequest(2, Guid.NewGuid(), snapshot, []);
        var evidence = new H3RecipeEvidence(H3GenerationRecipes.Models(request).Select(m => m with { Bytes = 10,
            Sha256 = m.Category == "diffusion_models" ? H3SmdRecipe.BaseHash : m.Category == "loras" ? H3SmdRecipe.AdapterHash : new('A', 64) }).ToArray(),
            new(new Dictionary<string, string?>(), new Dictionary<string, string?>()));
        H3GenerationRecipes.Validate(request with { Recipe = H3GenerationRecipes.Capture(request, evidence) });
        evidence = evidence with { Models = evidence.Models.Select(m => m.Category == "loras" ? m with { Sha256 = new('B', 64) } : m).ToArray() };
        Assert.Throws<WorkspaceStoreException>(() => H3GenerationRecipes.Validate(request with { Recipe = H3GenerationRecipes.Capture(request, evidence) }));
    }
}
