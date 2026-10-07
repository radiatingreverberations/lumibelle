using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private static JsonNode PresetCatalog()
    {
        var root = PerformanceCatalog();
        var extra = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/h3-presets-contract.json")))!.AsObject();
        foreach (var pair in extra) root[pair.Key] = pair.Value!.DeepClone();
        return root;
    }
    private static VideoSnapshot PresetSnapshot(string key, bool archive = false, string aspect = "16:9", string resolution = "preview", H3AttentionBackend attention = H3AttentionBackend.ServerDefault)
    {
        var shot = Ready(); shot.GenerationPreset = key; shot.SaveLosslessFrames = archive; shot.Aspect = aspect;
        shot.NativeResolution = resolution == "native"; shot.UpscalePreview = resolution == "upscaled";
        var settings = new H3Settings { LatentUpscaler = "upscaler.safetensors", Performance = new() { Attention = attention, SolAttention = true, ArchiveCompression = H3ArchiveCompression.Compact } };
        var size = H3Policy.Size(aspect, shot.NativeResolution);
        return new(Guid.NewGuid(), 1, shot, H3Policy.Compile(shot), H3Policy.Fingerprint(shot), "http://localhost:8188", settings, size.Width, size.Height, H3Policy.Frames(shot.Duration!.Value), H3Policy.Profile)
        { Preset = H3Presets.Capture(shot, settings), OutputPolicy = new(archive), Sampling = H3Policy.Sampling(shot, settings),
            Performance = H3Performance.Capture(H3Presets.NewPerformance(settings)),
            PreviewUpscale = shot.UpscalePreview ? H3PreviewUpscaling.Capture(H3UpscalerImplementation.Plus, settings.LatentUpscaler, aspect) : null };
    }
    [Fact]
    public void RetiredPresetsAreOnlyListedForShotsThatAlreadyUseThem()
    {
        Assert.Equal(["standard", "beta", "euler-beta", "larry", "pdd", "turbo4", "hyperflow"], H3Presets.Offered);
        Assert.Equal(H3Presets.Offered, H3Presets.Choices("larry"));
        Assert.Equal(["standard", "beta", "euler-beta", "larry", "pdd", "turbo4", "turbo8", "hyperflow"], H3Presets.Choices("turbo8"));
        Assert.Equal("Turbo · 8 steps · Retired", H3Presets.ChoiceLabel("turbo8"));
        Assert.Equal("Larry · 6 steps", H3Presets.ChoiceLabel("larry"));
        Assert.Equal("Standard · 20 steps", H3Presets.ChoiceLabel("standard"));
        // Retired keys stay valid, so existing shots and captured requests keep working.
        foreach (var key in new[] { "spectrum", "turbo8", "turbo4-075" })
        {
            Assert.Contains(key, H3Presets.Keys);
            var shot = Ready(); shot.GenerationPreset = key;
            H3Policy.Validate(shot, true);
        }
    }
    [Theory]
    [InlineData("standard")][InlineData("beta")][InlineData("euler-beta")][InlineData("larry")][InlineData("pdd")][InlineData("spectrum")][InlineData("turbo4")][InlineData("turbo4-075")][InlineData("turbo8")]
    public void PresetsPreserveResolutionAudioAndArchiveChoices(string key)
    {
        foreach (var aspect in new[] { "16:9", "9:16", "1:1" })
        foreach (var resolution in new[] { "preview", "native", "upscaled" })
        foreach (var archive in new[] { false, true })
        foreach (var attention in Enum.GetValues<H3AttentionBackend>())
        {
            var snapshot = PresetSnapshot(key, archive, aspect, resolution, attention);
            var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 42, "test", [])).GetProperty("prompt");
            var sampler = graph.GetProperty("10").GetProperty("inputs");
            Assert.Single(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == "SamplerCustomAdvanced");
            Assert.Equal(archive, graph.TryGetProperty("15", out _)); Assert.Equal(archive, graph.TryGetProperty("17", out _));
            Assert.Equal(resolution == "upscaled", graph.TryGetProperty("41", out _)); Assert.False(graph.TryGetProperty("31", out _));
            Assert.Equal(resolution == "upscaled" ? "42" : "10", graph.GetProperty("12").GetProperty("inputs").GetProperty("samples")[0].GetString());
            if (key == "pdd") { Assert.False(graph.TryGetProperty("9", out _)); Assert.Equal("51", sampler.GetProperty("sigmas")[0].GetString()); Assert.Equal(1, sampler.GetProperty("sigmas")[1].GetInt32()); }
            if (key == "larry") Assert.Equal("MiniMaxH3TurboSampler", graph.GetProperty("8").GetProperty("class_type").GetString());
            if (key is "larry" or "pdd" or "spectrum")
            {
                var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/h3-presets-recipes.json")))![key]!;
                var actual = JsonNode.Parse(graph.GetProperty("51").GetProperty("inputs").GetRawText())!; actual.AsObject().Remove("model");
                Assert.True(JsonNode.DeepEquals(expected, actual), key);
            }
        }
    }
    [Theory]
    [InlineData("standard")][InlineData("beta")][InlineData("euler-beta")][InlineData("larry")][InlineData("pdd")][InlineData("spectrum")][InlineData("turbo4")][InlineData("turbo4-075")][InlineData("turbo8")]
    public void PresetsKeepOptionalLoraOrderAndAttentionWithoutStackingAccelerators(string key)
    {
        var snapshot = WithLoras(PresetSnapshot(key, attention: H3AttentionBackend.Sage));
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 42, "test", [])).GetProperty("prompt");
        string Link(string node, string input) => graph.GetProperty(node).GetProperty("inputs").GetProperty(input)[0].GetString()!;
        Assert.Equal(key is "larry" or "pdd" ? "51" : H3Presets.UsesTurboLora(key) ? "16" : "1", Link("lora_1", "model"));
        Assert.Equal("lora_1", Link("lora_2", "model"));
        Assert.Equal(key == "turbo8" ? "18" : "lora_2", Link("30", "model"));
        Assert.Equal(key == "spectrum" ? "51" : "30", Link("6", "model"));
        if (key == "spectrum") { Assert.Equal("30", Link("51", "model")); Assert.Equal("51", Link("9", "model")); }
        Assert.Equal(key is "larry" or "pdd" or "spectrum", graph.TryGetProperty("51", out _));
        Assert.Equal(H3Presets.UsesTurboLora(key), graph.TryGetProperty("16", out _));
        Assert.False(graph.TryGetProperty("31", out _));
    }
    [Theory]
    [InlineData("larry")][InlineData("pdd")][InlineData("spectrum")]
    public void PresetContractsValidateInstalledFilesAndFailIndependently(string key)
    {
        var root = PresetCatalog(); var settings = new H3Settings(); var shot = Ready(); shot.GenerationPreset = key;
        H3Configuration Check() => ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), settings);
        Assert.True(Check().Ready(shot), Check().Issue(shot));
        root[H3Presets.Node(key)]!["output"] = new JsonArray("WRONG");
        Assert.False(Check().Ready(shot)); Assert.True(Check().Ready(Ready()));
        Assert.Contains(H3Presets.Node(key), Check().Issue(shot));
    }
    [Theory]
    [InlineData("missing-file")][InlineData("range")][InlineData("range-type")][InlineData("new-required")][InlineData("missing-sampler")][InlineData("file-subfolder")]
    public void LarryRequiresExactLoaderAndFileContract(string problem)
    {
        var root = PresetCatalog(); var settings = new H3Settings(); var shot = Ready(); shot.GenerationPreset = "larry";
        var fields = root["MiniMaxH3TurboLoRA"]!["input"]!["required"]!;
        switch (problem)
        {
            case "missing-file": settings.LarryLora = "missing.safetensors"; break;
            case "range": fields["strength"]![1]!["max"] = .5; break;
            case "range-type": fields["strength"]![1]!["max"] = "unknown"; break;
            case "new-required": fields["new_parameter"] = new JsonArray("STRING"); break;
            case "missing-sampler": root.AsObject().Remove("MiniMaxH3TurboSampler"); break;
            case "file-subfolder": settings.LarryLora = "h3/" + H3Presets.Checkpoint("larry", settings); fields["lora_name"]![0]!.AsArray().Add(settings.LarryLora); break;
        }
        var c = ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), settings);
        Assert.Equal(problem == "file-subfolder", c.Ready(shot)); Assert.True(c.Ready(Ready()));
    }
    [Theory]
    [InlineData("beta", "res_multistep", "beta", 20, null)][InlineData("euler-beta", "euler", "beta", 20, null)][InlineData("turbo4-075", "res_multistep", "simple", 4, .75)]
    public void SchedulerAndStrengthVariantsUseNativeNodes(string key, string sampler, string scheduler, int steps, double? strength)
    {
        var snapshot = PresetSnapshot(key);
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 42, "test", [])).GetProperty("prompt");
        JsonElement Input(string node, string field) => graph.GetProperty(node).GetProperty("inputs").GetProperty(field);
        Assert.Equal(sampler, Input("8", "sampler_name").GetString());
        Assert.Equal(scheduler, Input("9", "scheduler").GetString()); Assert.Equal(steps, Input("9", "steps").GetInt32());
        Assert.Equal("1", Input("9", "model")[0].GetString());
        Assert.Equal(strength is not null, graph.TryGetProperty("16", out _));
        if (strength is { } s) { Assert.Equal(s, Input("16", "strength_model").GetDouble()); Assert.Equal(new H3Settings().TurboLora, Input("16", "lora_name").GetString()); }
        Assert.True(H3Presets.Experimental(key));
        // A queued request keeps its captured recipe; a tampered one is rejected.
        Assert.Throws<WorkspaceStoreException>(() => ComfyH3Video.BuildWorkflow(snapshot with { Sampling = snapshot.Sampling! with { Scheduler = "normal" } }, 42, "test", []));
        Assert.Throws<WorkspaceStoreException>(() => H3Presets.Validate(snapshot with { Preset = snapshot.Preset! with { Key = "standard" } }));
    }
    [Theory]
    [InlineData("beta")][InlineData("euler-beta")][InlineData("turbo4-075")]
    public void SchedulerAndStrengthVariantsReportMissingNativeSupport(string key)
    {
        var root = PresetCatalog(); var settings = new H3Settings(); var shot = Ready(); shot.GenerationPreset = key;
        H3Configuration Check() => ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), settings);
        Assert.True(Check().Ready(shot), Check().Issue(shot));
        if (key == "turbo4-075")
        {
            settings.TurboLora = "missing.safetensors";
            Assert.False(Check().Ready(shot)); Assert.Contains("4-step Turbo LoRA", Check().Issue(shot));
            return;
        }
        var options = root["BasicScheduler"]!["input"]!["required"]!["scheduler"]![1]!["options"]!.AsArray();
        options.Remove(options.Single(o => o!.GetValue<string>() == "beta"));
        Assert.False(Check().Ready(shot)); Assert.Equal("Update ComfyUI: missing beta scheduler.", Check().Issue(shot));
        Assert.True(Check().Ready(Ready()));
    }
    [Fact]
    public void NewCaptureIsImmutableAndLegacyFingerprintsStayStable()
    {
        var legacy = Ready(); var original = H3Policy.Fingerprint(legacy);
        var roundtrip = ShotCopy.Of(legacy); Assert.Equal(original, H3Policy.Fingerprint(roundtrip));
        Assert.DoesNotContain("generationPreset", JsonSerializer.Serialize(legacy, AtomicJsonFile.Options));
        Assert.DoesNotContain("saveLosslessFrames", JsonSerializer.Serialize(legacy, AtomicJsonFile.Options));
        var snapshot = PresetSnapshot("larry"); H3Presets.Validate(ShotCopy.Of(snapshot));
        Assert.False(snapshot.Performance!.SolAttention); Assert.False(snapshot.OutputPolicy!.SaveLosslessFrames);
        Assert.Throws<WorkspaceStoreException>(() => H3Presets.Validate(snapshot with { Settings = snapshot.Settings with { LarryLora = "changed" } }));
        Assert.Throws<WorkspaceStoreException>(() => H3Presets.Validate(snapshot with { OutputPolicy = new(true) }));
        var old = Snapshot(Guid.NewGuid(), legacy); H3Presets.Validate(old);
        Assert.True(new ShotTake { Snapshot = old }.HasLosslessFrames);
    }
    [Theory]
    [InlineData("larry", false)][InlineData("pdd", false)][InlineData("spectrum", false)][InlineData("standard", true)]
    public async Task PresetAndOutputSurviveQueueRecoveryAndAdditionalCandidates(string key, bool archive)
    {
        using var f = await QueuedVideoFixture.Create(this);
        f.Shot.GenerationPreset = key; f.Shot.SaveLosslessFrames = archive;
        var doc = await f.Shots.LoadAsync(f.Project.Id, _ct); await f.Shots.SaveAsync(f.Project.Id, [f.Shot], doc.Revision, ct: _ct);
        var submission = await f.Capture(); var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        var context = await f.Claim(submission); f.Adapter.FailTransfer = true;
        await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, submission.Snapshot, _ct));
        f.Settings.Value = new(); f.Shot.GenerationPreset = "standard";
        f.Adapter.FailTransfer = false;
        await f.Worker.ExecuteAsync(f.Context(context.Job, true), submission.Snapshot, _ct);
        var take = Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        Assert.Equal(key, take.Snapshot.Preset!.Key); Assert.Equal(archive, take.HasLosslessFrames);
        Assert.Equal(archive ? take.FrameCount : 0, take.Frames.Count);
        Assert.Single(f.Graphs); Assert.Equal(request.Snapshot.Preset!.Checkpoint, take.Snapshot.Preset.Checkpoint);
        await f.Jobs.UpdateAsync(submission.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var extension = await f.Jobs.ExtendBatchAsync(submission.Id, Guid.NewGuid(), submission.OriginTabId, _ct);
        var extendedSnapshot = await f.Jobs.ReadSnapshotAsync(extension.Job.Id, _ct);
        Assert.True(JsonElement.DeepEquals(submission.Snapshot, extendedSnapshot));
        var claimed = (await f.Jobs.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!;
        await f.Worker.ExecuteAsync(f.Context(claimed, false), extendedSnapshot, _ct);
        var takes = (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes;
        Assert.Equal(2, takes.Count); Assert.Equal(2, f.Graphs.Count);
        Assert.All(takes, t => { Assert.Equal(key, t.Snapshot.Preset!.Key); Assert.Equal(archive, t.HasLosslessFrames); });
    }
}
