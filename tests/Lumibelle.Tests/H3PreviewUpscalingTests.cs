using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private const string UpscaleCheckpoint = "minimax_h3_latent_upscaler_3d_fp16.safetensors";
    private static JsonNode UpscaleCatalog(bool lbh = false)
    {
        var root = TurboCatalog();
        foreach (var entry in JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/h3-upscale-plus-contract.json")))!.AsObject())
            root[entry.Key] = entry.Value!.DeepClone();
        if (lbh)
        {
            var required = root[H3PreviewUpscaling.Node]!["input"]!["required"]!.AsObject();
            required.Remove("keep_proportion"); required.Remove("offload_after_upscale");
            required["enable_temporal_chunking"] = JsonNode.Parse("[\"BOOLEAN\",{\"default\":true}]");
            required["force_unload"] = JsonNode.Parse("[\"BOOLEAN\",{\"default\":true}]");
        }
        return root;
    }
    private static VideoSnapshot UpscaledSnapshot(string aspect = "16:9", int steps = 20, bool lbh = false)
    {
        var shot = Ready(); shot.UpscalePreview = true; shot.Aspect = aspect; shot.Turbo = steps != 20; shot.TurboSteps = steps == 8 ? 8 : 4;
        var s = Snapshot(Guid.NewGuid(), shot); var size = H3Policy.Size(aspect, false);
        s = s with { Width = size.Width, Height = size.Height, Settings = s.Settings with { LatentUpscaler = UpscaleCheckpoint } };
        return s with { Sampling = H3Policy.Sampling(shot, s.Settings), PreviewUpscale = H3PreviewUpscaling.Capture(lbh ? H3UpscalerImplementation.Lbh : H3UpscalerImplementation.Plus, UpscaleCheckpoint, aspect) };
    }
    [Theory]
    [InlineData("16:9", 20, false)] [InlineData("16:9", 4, false)]
    [InlineData("9:16", 20, false)] [InlineData("9:16", 4, false)]
    [InlineData("1:1", 20, false)] [InlineData("1:1", 4, false)]
    [InlineData("16:9", 20, true)] [InlineData("16:9", 4, true)]
    [InlineData("9:16", 20, true)] [InlineData("9:16", 4, true)]
    [InlineData("1:1", 20, true)] [InlineData("1:1", 4, true)]
    public void UpscaledPreviewUsesOneSamplerAndPreservesAudio(string aspect, int steps, bool lbh)
    {
        var s = UpscaledSnapshot(aspect, steps, lbh); var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(s, 42, "run", [])).GetProperty("prompt");
        JsonElement Input(string node, string field) => graph.GetProperty(node).GetProperty("inputs").GetProperty(field);
        var size = H3Policy.Size(aspect, true);
        Assert.Equal(size, H3PreviewUpscaling.OutputSize(s));
        Assert.Equal(H3Policy.Size(aspect, false).Width, Input("5", "width").GetInt32());
        Assert.Equal(steps, Input("9", "steps").GetInt32());
        Assert.Single(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == "SamplerCustomAdvanced");
        Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString()!.StartsWith("LumibelleH3"));
        Assert.Equal("[\"10\",1]", Input("40", "av_latent").GetRawText());
        Assert.Equal("[\"40\",0]", Input("41", "latent").GetRawText());
        Assert.Equal("[\"40\",1]", Input("42", "audio_latent").GetRawText());
        Assert.Equal("[\"41\",0]", Input("42", "video_latent").GetRawText());
        Assert.Equal("42", Input("11", "samples")[0].GetString()); Assert.Equal("42", Input("12", "samples")[0].GetString());
        Assert.Equal(size.Width, Input("41", "mode.width").GetInt32()); Assert.Equal(size.Height, Input("41", "mode.height").GetInt32());
        Assert.Equal("fp16", Input("41", "precision").GetString()); Assert.Equal("cuda", Input("41", "device").GetString());
        Assert.True(Input("41", lbh ? "force_unload" : "offload_after_upscale").GetBoolean());
        if (lbh) Assert.True(Input("41", "enable_temporal_chunking").GetBoolean()); else Assert.False(Input("41", "keep_proportion").GetBoolean());
        Assert.True(Input("15", "lossless").GetBoolean());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void UpscalingReadinessIsIndependentOfSavedRefinement(bool lbh)
    {
        var check = ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(UpscaleCatalog(lbh)), new() { LatentUpscaler = UpscaleCheckpoint });
        Assert.False(check.PackageCaptureReady); Assert.NotNull(check.RefinementIssue);
        var s = UpscaledSnapshot(lbh: lbh); Assert.True(check.Ready(s.Shot), check.Issue(s.Shot));
        H3PreviewUpscaling.CheckSubmission(s, check);
        Assert.Throws<WorkspaceStoreException>(() => H3PreviewUpscaling.CheckSubmission(UpscaledSnapshot(lbh: !lbh), check));
    }
    [Theory]
    [InlineData("missing")] [InlineData("split")] [InlineData("concat")] [InlineData("unknown-required")]
    [InlineData("mixed")] [InlineData("precision")] [InlineData("dimension-range")] [InlineData("dimension-type")]
    [InlineData("output")] [InlineData("list-output")] [InlineData("flag-type")] [InlineData("mode-type")]
    [InlineData("dimension-malformed")] [InlineData("mode-key")] [InlineData("split-wildcard")]
    [InlineData("list-count")] [InlineData("node-malformed")] [InlineData("mode-contract")]
    public void UpscalerContractFailuresLeaveNormalGenerationAvailable(string problem)
    {
        var root = UpscaleCatalog(); var required = root[H3PreviewUpscaling.Node]!["input"]!["required"]!;
        switch (problem)
        {
            case "missing": root.AsObject().Remove(H3PreviewUpscaling.Node); break;
            case "split": root.AsObject().Remove("LTXVSeparateAVLatent"); break;
            case "concat": root["LTXVConcatAVLatent"]!["output"] = new JsonArray("IMAGE"); break;
            case "unknown-required": required["new_behavior"] = new JsonArray("BOOLEAN"); break;
            case "mixed": required["force_unload"] = new JsonArray("BOOLEAN"); break;
            case "precision": required["precision"]![1]!["options"] = new JsonArray("fp32"); break;
            case "dimension-range": required["mode"]![1]!["options"]![1]!["inputs"]!["required"]!["width"]![1]!["max"] = 1000; break;
            case "dimension-type": required["mode"]![1]!["options"]![1]!["inputs"]!["required"]!["width"]![0] = "STRING"; break;
            case "output": root[H3PreviewUpscaling.Node]!["output"] = new JsonArray("IMAGE"); break;
            case "list-output": root[H3PreviewUpscaling.Node]!["output_is_list"] = new JsonArray(true); break;
            case "flag-type": required["offload_after_upscale"] = new JsonArray("STRING"); break;
            case "mode-type": required["mode"] = new JsonArray("COMBO", "invalid"); break;
            case "dimension-malformed": required["mode"]![1]!["options"]![1]!["inputs"]!["required"]!["width"]![1]!["max"] = "invalid"; break;
            case "mode-key": required["mode"]![1]!["options"]![1]!["key"] = 123; break;
            case "split-wildcard": root["LTXVSeparateAVLatent"]!["output"] = new JsonArray("*", "*"); break;
            case "list-count": root[H3PreviewUpscaling.Node]!["output_is_list"] = new JsonArray(); break;
            case "node-malformed": root[H3PreviewUpscaling.Node] = "invalid"; break;
            case "mode-contract": required["mode"]![0] = "COMBO"; break;
        }
        var check = ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), new() { LatentUpscaler = UpscaleCheckpoint });
        Assert.True(check.Ready(Ready())); Assert.False(check.Ready(UpscaledSnapshot().Shot)); Assert.NotNull(check.PreviewUpscaling.Issue);
    }
    [Fact]
    public void UpscalerCheckpointUsesExactCatalogIdentity()
    {
        var root = UpscaleCatalog(); var required = root[H3PreviewUpscaling.Node]!["input"]!["required"]!;
        required["model_name"]![1]!["options"] = new JsonArray("folder/" + UpscaleCheckpoint);
        Assert.NotNull(H3PreviewUpscaling.Inspect(JsonSerializer.SerializeToElement(root), new() { LatentUpscaler = UpscaleCheckpoint }).Issue);
        Assert.Null(H3PreviewUpscaling.Inspect(JsonSerializer.SerializeToElement(root), new() { LatentUpscaler = "folder/" + UpscaleCheckpoint }).Issue);
    }
    [Fact]
    public void UpscalingIsOptInAndProfilesRejectConflictingOrChangedSettings()
    {
        var shot = Ready(); var before = H3Policy.Fingerprint(shot);
        Assert.DoesNotContain("upscalePreview", JsonSerializer.Serialize(shot, AtomicJsonFile.Options));
        shot.UpscalePreview = true; Assert.NotEqual(before, H3Policy.Fingerprint(shot));
        shot.UpscalePreview = false; Assert.Equal(before, H3Policy.Fingerprint(shot));
        shot.UpscalePreview = true; shot.NativeResolution = true; Assert.Throws<WorkspaceStoreException>(() => H3Policy.Validate(shot));
        var s = UpscaledSnapshot(); H3PreviewUpscaling.Validate(ShotCopy.Of(s));
        foreach (var invalid in new[] { s with { PreviewUpscale = null }, s with { CaptureRefinementData = true }, s with { Width = 1344 },
            s with { PreviewUpscale = s.PreviewUpscale! with { Precision = "fp32" } }, s with { Settings = s.Settings with { LatentUpscaler = "changed.safetensors" } } })
            Assert.Throws<WorkspaceStoreException>(() => H3PreviewUpscaling.Validate(invalid));
    }
    [Fact]
    public async Task UpscaledQueuedBatchKeepsItsProfileThroughReloadExtensionAndTransferRetry()
    {
        using var f = await QueuedVideoFixture.Create(this, 4);
        f.Shot.UpscalePreview = true;
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], (await f.Shots.LoadAsync(f.Project.Id, _ct)).Revision, ct: _ct);
        var submission = await f.Capture(2); var context = await f.Claim(submission);
        var persisted = await f.Jobs.ReadSnapshotAsync(submission.Id, _ct);
        var snapshot = persisted.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot;
        Assert.NotNull(snapshot.PreviewUpscale); Assert.False(snapshot.CaptureRefinementData);
        f.Settings.Value = f.Settings.Value with { H3 = f.Settings.Value.H3 with { LatentUpscaler = "changed.safetensors" } };
        f.Shot.UpscalePreview = false;
        f.Adapter.FailTransfer = true;
        await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, persisted, _ct));
        f.Adapter.FailTransfer = false;
        await f.Jobs.ExtendBatchAsync(submission.Id, Guid.NewGuid(), submission.OriginTabId, _ct);
        await f.Worker.ExecuteAsync(context, persisted, _ct);
        // Two grouped initial takes plus one appended take are submitted as two prompts.
        var takes = (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes; Assert.Equal(3, takes.Count); Assert.Equal(2, f.Graphs.Count);
        Assert.All(takes, take => { Assert.Equal((1344, 768), (take.Width, take.Height)); Assert.Equal(snapshot.PreviewUpscale, take.Snapshot.PreviewUpscale);
            Assert.Null(take.RefinementPackage); Assert.True(take.Timings!.PartialObservation); Assert.Null(take.Timings.UpscalingSeconds); });
    }
    [Fact]
    public async Task UpscalerSwapIsRejectedBeforeUploadOrPromptSubmission()
    {
        var f = Fixture(); var root = UpscaleCatalog(true);
        using var handler = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(root.ToJsonString()) }));
        var generator = new ComfyH3Video(new TestHttpFactory(handler), new BenchmarkComfyMonitor(), f.Assets, f.Assets, f.Shots, new MockMediaTools(39));
        var adapter = new ComfyVideoJobAdapter(generator);
        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => adapter.ValidateAsync(new(1, Guid.NewGuid(), UpscaledSnapshot(), []), _ct));
        Assert.Contains("changed after", error.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Path.Contains("prompt") || r.Path.Contains("upload"));
    }
}
