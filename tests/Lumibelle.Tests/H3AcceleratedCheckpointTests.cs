using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData("10Eros_Max_h3_8stepTURBO_beta7_HQ.safetensors")]
    [InlineData("fasth3_omniref_8step_pruned_int8_convrot.safetensors")]
    public void BakedCheckpointsRequireTheirRecipeAndHistoricalRefinementRecordsStayReadable(string model)
    {
        var snapshot = PresetSnapshot("standard"); snapshot.Settings.Model = model;
        Assert.Throws<WorkspaceStoreException>(() => H3Presets.Capture(snapshot.Shot, snapshot.Settings));
        var package = new H3RefinementPackage(Guid.NewGuid(), 10, new('A', 64), snapshot.Width, snapshot.Height, snapshot.FrameCount);
        var refinement = new TakeRefinement(Guid.NewGuid(), package, TakeRefinementMode.Refine, snapshot.Width, snapshot.Height, "upscaler.safetensors", H3UpscalerImplementation.Plus);
        Assert.Throws<WorkspaceStoreException>(() => RefinementPolicy.Validate(refinement, snapshot));
        RefinementPolicy.Validate(refinement, snapshot, historical: true);
    }

    [Fact]
    public void CompactUsesItsOwnCompleteCheckpointAndFixedRecipeWithoutExtraAccelerationLora()
    {
        var original = PresetSnapshot(H3AcceleratedCheckpoints.Compact, attention: H3AttentionBackend.Sage);
        var snapshot = H3AcceleratedCheckpoints.Effective(original);
        Assert.NotEqual(H3AcceleratedCheckpoints.CompactFile, original.Settings.Model);
        Assert.Equal(H3AcceleratedCheckpoints.CompactFile, snapshot.Settings.Model);
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 42, "test", [])).GetProperty("prompt");
        JsonElement Inputs(string node) => graph.GetProperty(node).GetProperty("inputs");
        Assert.Equal(snapshot.Settings.Model, Inputs("1").GetProperty("unet_name").GetString());
        Assert.Equal("euler", Inputs("8").GetProperty("sampler_name").GetString());
        Assert.Equal(8, Inputs("9").GetProperty("steps").GetInt32());
        Assert.Equal("18", Inputs("9").GetProperty("model")[0].GetString());
        Assert.Equal("compact-attention", Inputs("6").GetProperty("model")[0].GetString());
        Assert.Equal("vsa", Inputs("compact-vsa").GetProperty("selection").GetString());
        Assert.Equal("exact_kv_and_rows", Inputs("compact-vsa").GetProperty("sink_conditioning").GetString());
        Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() is "LoraLoaderModelOnly" or "PathchSageAttentionKJ");
        Assert.DoesNotContain(H3GenerationRecipes.Models(new(2, Guid.NewGuid(), snapshot, [])), m => m.Category == "loras");
        var ordinary = Ready(); ordinary.GenerationPreset = "standard";
        Assert.Throws<WorkspaceStoreException>(() => H3Presets.Capture(ordinary, snapshot.Settings));
        Assert.True(H3AcceleratedCheckpoints.Baked(snapshot));
        Assert.Throws<WorkspaceStoreException>(() => RefinementPolicy.Validate(null!, snapshot));
    }

    [Fact]
    public void CompactDiscoveryChecksItsSpecificFileAndDynamicAttentionContract()
    {
        var root = PresetCatalog();
        var native = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/h3-native-acceleration-contract.json")))!.AsObject();
        foreach (var pair in native) root[pair.Key] = pair.Value!.DeepClone();
        root["UNETLoader"]!["input"]!["required"]!["unet_name"]![0]!.AsArray().Add(H3AcceleratedCheckpoints.CompactFile);
        var settings = new H3Settings();
        H3PresetSetup Check() => H3AcceleratedCheckpoints.Inspect(JsonSerializer.SerializeToElement(root), settings, null);
        Assert.Null(Check().Issue); Assert.Single(Check().Files);
        H3Requirements.Select(H3Requirements.TenErosCompact, settings, "sub/" + H3AcceleratedCheckpoints.CompactFile);
        Assert.NotEqual(settings.Model, settings.TenErosCompactModel);
        Assert.NotNull(Check().Issue);
        settings.TenErosCompactModel = null;
        root["BlockSparseAttention"]!["input"]!["required"]!["extra_tokens"]![1]!["max"] = 128;
        Assert.Contains("extra_tokens", Check().Issue);
        Assert.True(LoraPolicy.ReservedH3("SMD-MiniMax-H3-Ref2VA-4step.safetensors", settings));
    }

    [Fact]
    public void CompactRejectsRenamedCheckpointContentsEvenWithAnInternallyConsistentRecipe()
    {
        var snapshot = H3AcceleratedCheckpoints.Effective(PresetSnapshot(H3AcceleratedCheckpoints.Compact));
        var request = new AiVideoJobRequest(2, Guid.NewGuid(), snapshot, []);
        var models = H3GenerationRecipes.Models(request).Select(m => m with { Sha256 = m.Category == "diffusion_models" ? H3AcceleratedCheckpoints.CompactHash : new('A', 64), Bytes = 10 }).ToArray();
        var evidence = new H3RecipeEvidence(models, new(new Dictionary<string, string?>(), new Dictionary<string, string?>()));
        H3GenerationRecipes.Validate(request with { Recipe = H3GenerationRecipes.Capture(request, evidence) });
        var renamed = PresetSnapshot("standard"); renamed.Settings.Model = "renamed-baked-model.safetensors";
        var ordinary = new AiVideoJobRequest(2, Guid.NewGuid(), renamed, []);
        var renamedEvidence = evidence with { Models = H3GenerationRecipes.Models(ordinary).Select(m => m with {
            Bytes = 10, Sha256 = m.Category == "diffusion_models" ? H3AcceleratedCheckpoints.CompactHash : new('A', 64) }).ToArray() };
        Assert.Throws<WorkspaceStoreException>(() => H3GenerationRecipes.Validate(ordinary with { Recipe = H3GenerationRecipes.Capture(ordinary, renamedEvidence) }));
        evidence = evidence with { Models = models.Select(m => m with { Sha256 = new('B', 64) }).ToArray() };
        Assert.Throws<WorkspaceStoreException>(() => H3GenerationRecipes.Validate(request with { Recipe = H3GenerationRecipes.Capture(request, evidence) }));
    }
}
