using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData(H3SmdRecipe.Key)] [InlineData(H3PtdRecipe.Key)]
    public void StandardRefinementOfAdapterTakeCapturesTheBaseWithoutReapplyingItsAccelerationAdapter(string key)
    {
        var snapshot = H3PtdRecipe.Effective(H3SmdRecipe.Effective(PresetSnapshot(key)));
        var package = new H3RefinementPackage(Guid.NewGuid(), 10, new('A', 64), snapshot.Width, snapshot.Height, snapshot.FrameCount);
        var refinement = new TakeRefinement(Guid.NewGuid(), package, TakeRefinementMode.Refine, snapshot.Width, snapshot.Height, "upscaler.safetensors", H3UpscalerImplementation.Plus);
        var request = new AiVideoJobRequest(2, Guid.NewGuid(), snapshot, []) { Refinement = refinement };
        var evidence = new H3RecipeEvidence(H3GenerationRecipes.Models(request).Select(m => m with { Bytes = 10,
            Sha256 = m.Category == "diffusion_models" ? H3SmdRecipe.BaseHash : new('A', 64) }).ToArray(),
            new(new Dictionary<string, string?>(), new Dictionary<string, string?>()));
        Assert.DoesNotContain(evidence.Models, m => m.Category is "ptd" or "loras");
        request = request with { Recipe = H3GenerationRecipes.Capture(request) };
        H3GenerationRecipes.Validate(request);
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 42, "test", [],
            refine: new(refinement, "video.latent", "audio.latent"), recipe: request.Recipe)).GetProperty("prompt");
        Assert.Equal("res_multistep", graph.GetProperty("8").GetProperty("inputs").GetProperty("sampler_name").GetString());
        Assert.False(graph.TryGetProperty("ptd-adapter", out _));
        Assert.False(graph.TryGetProperty("smd-adapter", out _));
    }

    [Theory]
    [InlineData(H3AcceleratedCheckpoints.Fast)] [InlineData(H3PtdRecipe.Key)]
    public void RemainingRecipesKeepSeparateModelsOrderedAdaptersAndTheirOwnSchedules(string key)
    {
        var original = PresetSnapshot(key);
        original.Settings.Model = "ordinary-w4.safetensors";
        var snapshot = WithLoras(H3PtdRecipe.Effective(H3AcceleratedCheckpoints.Effective(original)));
        Assert.Equal("ordinary-w4.safetensors", original.Settings.Model);
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 42, "test", [])).GetProperty("prompt");
        JsonElement Inputs(string id) => graph.GetProperty(id).GetProperty("inputs");
        Assert.Equal(snapshot.Settings.Model, Inputs("1").GetProperty("unet_name").GetString());
        Assert.False(graph.TryGetProperty("16", out _));
        Assert.Equal("BasicGuider", graph.GetProperty("6").GetProperty("class_type").GetString());
        if (key == H3AcceleratedCheckpoints.Fast) {
            Assert.Equal(H3AcceleratedCheckpoints.FastFile, snapshot.Settings.Model);
            Assert.Equal("euler", Inputs("8").GetProperty("sampler_name").GetString());
            Assert.Equal(8, Inputs("9").GetProperty("steps").GetInt32());
            Assert.Equal("", Inputs("fasth3-vsa").GetProperty("dense_blocks").GetString());
            Assert.Equal("vsa", Inputs("fasth3-vsa").GetProperty("selection").GetString());
            Assert.Equal("lora_2", Inputs("18").GetProperty("model")[0].GetString());
            Assert.Equal("fasth3-attention", Inputs("6").GetProperty("model")[0].GetString());
            Assert.Throws<WorkspaceStoreException>(() => RefinementPolicy.Validate(null!, snapshot));
        } else {
            Assert.Equal(H3SmdRecipe.BaseFile, snapshot.Settings.Model);
            Assert.Equal(H3PtdRecipe.Loader, graph.GetProperty("ptd-adapter").GetProperty("class_type").GetString());
            Assert.Equal("ptd-adapter", Inputs("lora_1").GetProperty("model")[0].GetString());
            Assert.Equal(H3PtdRecipe.Sampler, graph.GetProperty("8").GetProperty("class_type").GetString());
            Assert.Equal(H3PtdRecipe.Sigmas, graph.GetProperty("9").GetProperty("class_type").GetString());
            Assert.Equal("lora_2", Inputs("9").GetProperty("model")[0].GetString());
            Assert.Equal(6, snapshot.Sampling!.Steps);
            Assert.Equal("upstream sampler policy", snapshot.Preset!.Inputs.GetProperty("compiler_fallback").GetString());
            Assert.Equal("PTDH3CompactLoader", graph.GetProperty("ptd-adapter").GetProperty("class_type").GetString());
            Assert.Equal("exact (PTD reference)", Inputs("ptd-adapter").GetProperty("kernels").GetString());
            Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() is "BasicScheduler" or "KSamplerSelect");
        }
    }

    [Theory]
    [InlineData(H3AcceleratedCheckpoints.Fast)] [InlineData(H3PtdRecipe.Key)]
    public void RemainingRecipesCheckActualContractsAndCapturedFileContents(string key)
    {
        var root = PresetCatalog();
        foreach (var file in new[] { "h3-native-acceleration-contract.json", "h3-ptd-contract.json" })
            foreach (var pair in JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)))!.AsObject())
                root[pair.Key] = pair.Value!.DeepClone();
        root["UNETLoader"]!["input"]!["required"]!["unet_name"]![0]!.AsArray().Add(H3AcceleratedCheckpoints.FastFile);
        var settings = new H3Settings();
        H3PresetSetup Check() => key == H3PtdRecipe.Key ? H3PtdRecipe.Inspect(JsonSerializer.SerializeToElement(root), settings, null)
            : H3AcceleratedCheckpoints.Inspect(JsonSerializer.SerializeToElement(root), settings, null, key);
        Assert.Null(Check().Issue);
        settings.Model = "missing-ordinary-model.safetensors";
        var setup = ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), settings);
        Assert.False(setup.StandardReady);
        Assert.Null(setup.Presets.Single(p => p.Key == key).Issue);
        root[key == H3PtdRecipe.Key ? H3PtdRecipe.Sigmas : "ModelAttentionBackend"]!["output"] = new JsonArray("WRONG");
        Assert.NotNull(Check().Issue);
        var snapshot = H3PtdRecipe.Effective(H3AcceleratedCheckpoints.Effective(PresetSnapshot(key)));
        var request = new AiVideoJobRequest(2, Guid.NewGuid(), snapshot, []);
        var evidence = new H3RecipeEvidence(H3GenerationRecipes.Models(request).Select(m => m with { Bytes = 10,
            Sha256 = m.Category == "diffusion_models" ? key == H3PtdRecipe.Key ? H3SmdRecipe.BaseHash : H3AcceleratedCheckpoints.FastHash
                : m.Category == "ptd" ? H3PtdRecipe.AdapterHash : new('A', 64) }).ToArray(),
            new(new Dictionary<string, string?>(), new Dictionary<string, string?>()));
        request = request with { Recipe = H3GenerationRecipes.Capture(request, evidence) };
        H3GenerationRecipes.Validate(request);
        var copy = JsonSerializer.Deserialize<AiVideoJobRequest>(JsonSerializer.Serialize(request, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
        H3GenerationRecipes.Validate(copy);
        Assert.Equal(key == H3PtdRecipe.Key, copy.Recipe!.Models.Any(m => m.Category == "ptd"));
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 42, "attempt", [], recipe: H3GenerationRecipes.Capture(request))).GetProperty("prompt");
        Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString()!.StartsWith("Lumibelle"));
        evidence = evidence with { Models = evidence.Models.Select(m => m with { Sha256 = new('B', 64) }).ToArray() };
        Assert.Throws<WorkspaceStoreException>(() => H3GenerationRecipes.Validate(request with { Recipe = H3GenerationRecipes.Capture(request, evidence) }));
    }

    [Fact]
    public void OfferedPresetLabelsDescribeRecipesWithoutQualityOrExperimentalBadges()
    {
        foreach (var key in H3Presets.Offered) {
            Assert.DoesNotContain("experimental", H3Presets.Label(key), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("flicker", H3Presets.Help(key), StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(H3Requirements.All.SelectMany(r => r.Downloads).SelectMany(d => d.Tags), b => b.Label == "Experimental");
    }
}
