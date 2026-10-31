using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private static JsonNode TurboCatalog()
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/h3-contract.json")))!;
        root["MiniMaxH3SigmaShift"] = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/h3-shift-contract.json")));
        root["LoraLoaderModelOnly"]!["input"]!["required"]!["lora_name"]![0]!.AsArray().Add(new H3Settings().Turbo8StepLora);
        return root;
    }

    [Theory]
    [InlineData(20, "res_multistep", "1")]
    [InlineData(4, "res_multistep", "16")]
    public void SamplingModesUseSeparateWeightsAndExactSchedules(int steps, string sampler, string guiderModel)
    {
        var shot = Ready(); shot.Turbo = steps != 20; shot.TurboSteps = steps == 8 ? 8 : 4;
        var snapshot = Snapshot(Guid.NewGuid(), shot);
        var sampling = H3Policy.Sampling(shot, snapshot.Settings);
        snapshot = snapshot with { Sampling = sampling };
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 42, "run", [])).GetProperty("prompt");
        JsonElement Input(string node, string field) => graph.GetProperty(node).GetProperty("inputs").GetProperty(field);
        Assert.Equal(sampler, Input("8", "sampler_name").GetString());
        Assert.Equal(steps, Input("9", "steps").GetInt32());
        Assert.Equal("simple", Input("9", "scheduler").GetString());
        Assert.Equal(guiderModel, Input("6", "model")[0].GetString());
        Assert.Equal(steps == 8 ? "18" : "1", Input("9", "model")[0].GetString());
        Assert.Equal("10", Input("12", "samples")[0].GetString()); // Joint audio decode remains connected.
        if (steps == 20) Assert.False(graph.TryGetProperty("16", out _));
        else
        {
            Assert.Equal(steps == 8 ? snapshot.Settings.Turbo8StepLora : snapshot.Settings.TurboLora, Input("16", "lora_name").GetString());
            Assert.Equal(1, Input("16", "strength_model").GetDouble());
        }
        if (steps == 8)
        {
            Assert.Equal("MiniMaxH3SigmaShift", graph.GetProperty("18").GetProperty("class_type").GetString());
            Assert.Equal("16", Input("18", "model")[0].GetString());
            Assert.Equal(12, Input("18", "shift_video").GetDouble());
            Assert.Equal(3, Input("18", "shift_audio").GetDouble());
            Assert.Equal("h3-ref2v-turbo8-v1", sampling.Profile);
        }
        else Assert.False(graph.TryGetProperty("18", out _));
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("missing-node")]
    [InlineData("missing-input")]
    [InlineData("shift-range")]
    [InlineData("euler")]
    [InlineData("four-step")]
    [InlineData("fl2v")]
    public void EightStepReadinessFailuresDoNotDisableExistingModes(string problem)
    {
        var root = TurboCatalog(); var settings = new H3Settings();
        var installed = root["LoraLoaderModelOnly"]!["input"]!["required"]!["lora_name"]![0]!.AsArray();
        switch (problem)
        {
            case "missing-file": settings.Turbo8StepLora = "missing.safetensors"; break;
            case "missing-node": root.AsObject().Remove("MiniMaxH3SigmaShift"); break;
            case "missing-input": root["MiniMaxH3SigmaShift"]!["input"]!["required"]!.AsObject().Remove("shift_audio"); break;
            case "shift-range": root["MiniMaxH3SigmaShift"]!["input"]!["required"]!["shift_video"]![1]!["max"] = 10; break;
            case "euler": root["KSamplerSelect"]!["input"]!["required"]!["sampler_name"]![1]!["options"] = new JsonArray("res_multistep"); break;
            case "four-step": settings.Turbo8StepLora = settings.TurboLora; break;
            case "fl2v": settings.Turbo8StepLora = "minimax_h3_fl2v_turbo_8step_v1.0_768p_comfyui_bf16.safetensors"; installed.Add(settings.Turbo8StepLora); break;
        }
        var check = ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), settings);
        Assert.True(check.StandardReady); Assert.True(check.TurboReady); Assert.False(check.Turbo8StepReady);
        Assert.False(string.IsNullOrWhiteSpace(check.Turbo8StepIssue));
        var shot = Ready(); shot.Turbo = true; shot.TurboSteps = 8;
        Assert.False(check.Ready(shot)); Assert.Equal(check.Turbo8StepIssue, check.Issue(shot));
    }

    [Fact]
    public void RemovedEightStepFilesStayOutOfTheActiveCatalogEvenWhenInstalled()
    {
        var root = TurboCatalog(); var settings = new H3Settings();
        var nested = "video/h3/" + settings.Turbo8StepLora;
        root["LoraLoaderModelOnly"]!["input"]!["required"]!["lora_name"]![0] = new JsonArray(nested);
        settings.Turbo8StepLora = nested;
        var check = ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), settings);
        Assert.False(check.Turbo8StepReady); Assert.True(check.StandardReady); Assert.False(check.TurboReady);
        Assert.DoesNotContain(check.Presets, p => p.Key == "turbo8");
        Assert.Contains(nested, check.Loras);
        Assert.False(ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), settings with { Turbo8StepLora = Path.GetFileName(nested) }).Turbo8StepReady);
        Assert.False(ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), settings with { TurboLora = nested }).TurboReady);
    }

    [Fact]
    public void LegacyShotsRetainQualityAndFingerprintsAndRejectInvalidStepCounts()
    {
        var shot = Ready(); shot.Turbo = true;
        var old = JsonSerializer.SerializeToNode(shot, AtomicJsonFile.Options)!; old.AsObject().Remove("turboSteps"); old.AsObject().Remove("characters");
        old.AsObject().Remove("videos"); // Historical shots predate video attachments.
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(old, AtomicJsonFile.Options)));
        var loaded = JsonSerializer.Deserialize<Shot>(old, AtomicJsonFile.Options)!;
        Assert.Equal(4, loaded.TurboSteps); Assert.Equal(fingerprint, H3Policy.Fingerprint(loaded));
        loaded.TurboSteps = 8; Assert.NotEqual(fingerprint, H3Policy.Fingerprint(loaded));
        Assert.Equal(8, loaded.Copy().TurboSteps);
        loaded.TurboSteps = 6; Assert.Throws<WorkspaceStoreException>(() => H3Policy.Validate(loaded));
        var legacySettings = JsonSerializer.Deserialize<H3Settings>("{}", AtomicJsonFile.Options)!;
        Assert.Contains("8step_v1.0_768p", legacySettings.Turbo8StepLora);
    }

    [Fact]
    public async Task SubmissionRechecksEightStepCapabilitiesBeforeQueuing()
    {
        var f = Fixture(); var root = TurboCatalog(); root.AsObject().Remove("MiniMaxH3SigmaShift");
        using var handler = new ScriptedHttpHandler((request, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(root.ToJsonString()) }));
        var generator = new ComfyH3Video(new TestHttpFactory(handler), new BenchmarkComfyMonitor(), f.Assets, f.Assets, f.Shots, new MockMediaTools(39));
        var shot = Ready(); shot.Turbo = true; shot.TurboSteps = 8;
        var run = new VideoRun { Snapshot = Snapshot(f.Project.Id, shot), InputsPrepared = true };
        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => generator.SubmitAsync(run, new() { Number = 1 }, _ct));
        Assert.Contains("removed", error.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Path.Contains("prompt") || r.Path.Contains("upload"));
    }
}
