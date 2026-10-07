using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private const string Upscaler = "minimax_h3_latent_upscaler_3d_fp16.safetensors";
    // A current ComfyUI with the stock latent nodes, plus the recorded Upscaler-Plus contract.
    private static JsonNode RefinementCatalog()
    {
        var root = TurboCatalog();
        foreach (var entry in JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/h3-upscale-plus-contract.json")))!.AsObject())
            root[entry.Key] = entry.Value!.DeepClone();
        root["SaveLatent"] = JsonNode.Parse("""{"input":{"required":{"samples":["LATENT"],"filename_prefix":["STRING",{"default":"latents/ComfyUI"}]}},"output":["LATENT"]}""");
        root["LoadLatent"] = JsonNode.Parse("""{"input":{"required":{"latent":[[]]}},"output":["LATENT"]}""");
        return root;
    }

    [Theory]
    [InlineData("none")] [InlineData("capture")] [InlineData("load")] [InlineData("upscaler")] [InlineData("dimensions")] [InlineData("file")]
    public void RefinementReadinessChecksStockNodesAndTheUpscalerWithoutDisablingNormalVideo(string problem)
    {
        var root = RefinementCatalog(); var settings = new H3Settings { LatentUpscaler = Upscaler };
        switch (problem)
        {
            case "capture": root.AsObject().Remove("SaveLatent"); break;
            case "load": root.AsObject().Remove("LoadLatent"); break;
            case "upscaler": root.AsObject().Remove(H3PreviewUpscaling.Node); break;
            case "dimensions": root[H3PreviewUpscaling.Node]!["input"]!["required"]!["mode"]![1]!["options"]!.AsArray()
                .Single(o => o!["key"]!.GetValue<string>() == "target dimensions")!["inputs"]!["required"]!.AsObject().Remove("height"); break;
            case "file": settings.LatentUpscaler = "learned.safetensors"; break;
        }
        var check = ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), settings);
        Assert.True(check.StandardReady);
        Assert.Equal(problem != "capture", check.PackageCaptureReady);
        if (problem == "none") Assert.Null(check.RefinementIssue);
        else Assert.False(string.IsNullOrWhiteSpace(check.RefinementIssue));
    }

    [Theory]
    [InlineData(20)] [InlineData(4)] [InlineData(8)]
    public async Task OlderComfyAcceptsNormalBatchesButRejectsMissingPromisedCapture(int steps)
    {
        var f = Fixture(); var shot = Ready(); shot.Turbo = steps != 20; shot.TurboSteps = steps == 8 ? 8 : 4;
        using var handler = new ScriptedHttpHandler((r, _) => {
            Assert.Equal("/object_info", r.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TurboCatalog().ToJsonString()) });
        });
        var video = new ComfyH3Video(new TestHttpFactory(handler), new BenchmarkComfyMonitor(), f.Assets, f.Assets, f.Shots, new MockMediaTools(39));
        var adapter = new ComfyVideoJobAdapter(video);
        var snapshot = Snapshot(f.Project.Id, shot);
        var request = new AiVideoJobRequest(1, Guid.NewGuid(), snapshot, []);
        await adapter.ValidateAsync(request, _ct);
        var failure = await Assert.ThrowsAsync<WorkspaceStoreException>(() => adapter.ValidateAsync(request with { Snapshot = snapshot with { CaptureRefinementData = true } }, _ct));
        Assert.Contains("refinement data", failure.Message);
    }

    [Fact]
    public async Task LatentTransferRetriesWithoutDownloadingVideoOrFramesAgain()
    {
        var f = Fixture(); var snapshot = Snapshot(f.Project.Id, Ready()) with { CaptureRefinementData = true };
        var run = new VideoRun { Snapshot = snapshot }; var candidate = new VideoCandidate { Number = 1, TakeId = Guid.NewGuid() };
        Directory.CreateDirectory(_root);
        var videoLatent = Path.Combine(_root, "remote-video.latent"); var audioLatent = Path.Combine(_root, "remote-audio.latent");
        await MockRefinementPackage.WriteLatentAsync(videoLatent, RefinementPackages.VideoShape(snapshot.FrameCount, snapshot.Width, snapshot.Height), _ct);
        await MockRefinementPackage.WriteLatentAsync(audioLatent, RefinementPackages.AudioShape(snapshot.FrameCount), _ct);
        candidate.Output = JsonSerializer.SerializeToElement(new { outputs = new Dictionary<string, object> {
            ["14"] = new { images = new[] { new { filename = "video.mp4" } } },
            ["15"] = new { images = new[] { new { filename = "frames-0.webp" }, new { filename = "frames-1.webp" } } },
            ["21"] = new { latents = new[] { new { filename = "latent-video_00001_.latent", subfolder = "lumibelle/x", type = "output" } } },
            ["22"] = new { latents = new[] { new { filename = "latent-audio_00001_.latent", subfolder = "lumibelle/x", type = "output" } } }
        } });
        var first = await MockFrameArchive.WebpAsync(32, 32, 24, ct: _ct);
        var second = await MockFrameArchive.WebpAsync(32, 32, snapshot.FrameCount - 24, 24, ct: _ct);
        bool fail = true; Dictionary<string, int> requests = [];
        using var handler = new ScriptedHttpHandler(async (r, ct) => {
            var key = System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)["filename"]!;
            requests[key] = requests.GetValueOrDefault(key) + 1;
            if (key.StartsWith("latent-audio") && fail) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            byte[] body = key switch { "video.mp4" => [1, 2, 3], "frames-0.webp" => first, "frames-1.webp" => second,
                _ => await File.ReadAllBytesAsync(key.StartsWith("latent-video") ? videoLatent : audioLatent, ct) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        });
        var generator = new ComfyH3Video(new TestHttpFactory(handler), new BenchmarkComfyMonitor(), f.Assets, f.Assets, f.Shots, new MockMediaTools(snapshot.FrameCount));
        var stage = Path.Combine(await f.Shots.RunDirectoryAsync(f.Project.Id, run.Id, _ct), "candidate-1");
        await Assert.ThrowsAsync<HttpRequestException>(() => generator.DownloadAsync(run, candidate, stage, _ => Task.CompletedTask, _ct));
        Assert.True(File.Exists(Path.Combine(stage, "video.mp4"))); Assert.True(File.Exists(Path.Combine(stage, "archive-0001.webp")));
        Assert.Empty((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        fail = false; var take = await generator.DownloadAsync(run, candidate, stage, _ => Task.CompletedTask, _ct);
        var package = take.RefinementPackage!;
        Assert.Equal(candidate.TakeId, package.Id);
        Assert.Equal(package, await RefinementPackages.InspectAsync(Path.Combine(stage, H3RefinementPackage.FileName), snapshot, null, _ct));
        Assert.Equal(2, requests["latent-audio_00001_.latent"]);
        Assert.All(requests.Where(r => r.Key is "video.mp4" or "frames-0.webp" or "frames-1.webp"), r => Assert.Equal(1, r.Value));
        Assert.Equal(3 + take.Frames.DistinctBy(f => f.FileName).Sum(f => f.Bytes) + package.Bytes, take.Bytes);
        Assert.Empty(Directory.GetFiles(stage, "*.tmp"));
    }

    [Fact]
    public async Task RefinementLocksEachParentWhileOriginalBatchCanKeepRunning()
    {
        using var f = await QueuedVideoFixture.Create(this, 8);
        var original = await f.Capture(); var running = await f.Claim(original);
        await f.Worker.ExecuteAsync(running, original.Snapshot, _ct); // Keep its job lease active.
        var source = Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        var request = await f.CaptureService.CaptureRefinementAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, source.Id,
            TakeRefinementMode.Refine, source.Width, source.Height, _ct);
        Assert.Equal(source.Id, request.Target.TakeId);
        await f.Jobs.EnqueueAsync(request, _ct);
        var duplicate = await f.CaptureService.CaptureRefinementAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, source.Id,
            TakeRefinementMode.Rework, source.Width, source.Height, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Jobs.EnqueueAsync(duplicate, _ct));
        Assert.NotEqual(original.Target.LockKey(AiJobKind.Video), request.Target.LockKey(AiJobKind.Video));
        Assert.False(JsonSerializer.SerializeToElement(original.Target, AtomicJsonFile.Options).TryGetProperty("takeId", out _));
    }
}
