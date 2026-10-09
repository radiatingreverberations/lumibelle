using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    // Explicit opt-in: eight real Standard generations, using a temporary project.
    // Artifacts and immutable requests survive failure for inspection and local recovery.
    [Fact]
    public async Task LiveMotionExtensionComparesSingleFrameFramesAndSavedMotionThenChainsAndRefines()
    {
        var url = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_MOTION_URL");
        var output = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_MOTION_OUT");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(output)) Assert.Skip("Set LUMIBELLE_LIVE_MOTION_URL and LUMIBELLE_LIVE_MOTION_OUT for the manual GPU comparison.");
        Directory.CreateDirectory(output);
        Assert.Empty(Directory.EnumerateFiles(output, "take.json", SearchOption.AllDirectories));
        using var http = new HttpClient { BaseAddress = new(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
        var root = await http.GetFromJsonAsync<JsonElement>("object_info", _ct);
        var system = await http.GetFromJsonAsync<JsonElement>("system_stats", _ct);
        await File.WriteAllTextAsync(Path.Combine(output, "system.json"), system.ToString(), _ct);
        var settings = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_MOTION_SETTINGS") is { Length: > 0 } file
            ? JsonDocument.Parse(await File.ReadAllTextAsync(file, _ct)).RootElement.GetProperty("settings").GetProperty("h3").Deserialize<H3Settings>(AtomicJsonFile.Options)!
            : new H3Settings();
        var check = ComfyH3Video.Inspect(root, settings);
        Assert.True(check.StandardReady, check.Message); Assert.True(check.PackageCaptureReady);
        var f = Fixture();
        var store = new FileShotStore(f.Files, _clock, mediaTools: new ProductionMediaTools());
        var generator = new ComfyH3Video(new TestHttpFactory(new HttpClientHandler()), TestComfy.Monitor(), f.Assets, null!, store, new ProductionMediaTools());
        await generator.CheckMotionAsync(url, MotionContextRoute.SavedLatents, _ct);
        await generator.CheckMotionAsync(url, MotionContextRoute.Frames, _ct);
        var projectDirectory = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        var shot = Ready(); shot.Duration = 5; shot.SaveLosslessFrames = true; shot.SaveLatents = true;
        shot.Description = "An adult woman in a blue jacket walks steadily from left to right through a sunlit courtyard. The camera tracks smoothly beside her at chest height. She looks ahead and says, Let's keep moving. Her footsteps continue on the paving stones. Natural daylight, realistic movement, one continuous shot.";
        shot.Dialogue = [new() { Speaker = "Riley", Text = "Let's keep moving." }];
        await store.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var sourceSnapshot = Snapshot(f.Project.Id, shot) with { Settings = settings, ComfyUrl = url, Width = 608, Height = 352,
            CaptureRefinementData = true, OutputPolicy = new(true), Preset = H3Presets.Capture(shot, settings),
            Sampling = H3Policy.Sampling(shot, settings), Performance = H3Performance.Capture(settings.Performance) };
        var records = new List<object>();

        async Task<ShotTake> Run(string name, VideoSnapshot snapshot, Guid runId, TakeExtensionRequest? extension = null, TakeRefinement? refinement = null, TakeTrimRange? trim = null)
        {
            var directory = await store.RunDirectoryAsync(f.Project.Id, runId, _ct);
            Directory.CreateDirectory(directory);
            var request = new AiVideoJobRequest(2, runId, snapshot, []) { Extension = extension, Refinement = refinement, OutputTrim = trim };
            if (extension is not null) await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, directory, _ct);
            var motion = await generator.UploadMotionAsync(snapshot, directory, _ct);
            ComfyH3Video.RefineSource? refine = null;
            if (refinement is not null) {
                var (video, audio) = await generator.UploadRefinementAsync(url, Path.Combine(directory, "inputs", H3RefinementPackage.FileName), Path.Combine(directory, "upload"), _ct);
                refine = new(refinement, video, audio);
            }
            var graph = ComfyH3Video.BuildWorkflow(snapshot, 20261009, Guid.NewGuid().ToString("D"), [], refine: refine, motion: motion);
            var evidence = Path.Combine(output, name); Directory.CreateDirectory(evidence);
            await AtomicJsonFile.WriteAsync(Path.Combine(evidence, "request.json"), request, _ct);
            await AtomicJsonFile.WriteAsync(Path.Combine(evidence, "workflow.json"), graph, _ct);
            var queue = await http.GetFromJsonAsync<JsonElement>("queue", _ct);
            Assert.Equal(0, queue.GetProperty("queue_running").GetArrayLength()); Assert.Equal(0, queue.GetProperty("queue_pending").GetArrayLength());
            Console.WriteLine($"Starting {name}: {snapshot.FrameCount} frames, {snapshot.Motion?.Route.ToString() ?? "source"}");
            var timer = Stopwatch.StartNew();
            using var posted = await http.PostAsJsonAsync("prompt", graph, _ct);
            Assert.True(posted.IsSuccessStatusCode, await posted.Content.ReadAsStringAsync(_ct));
            var id = (await posted.Content.ReadFromJsonAsync<JsonElement>(_ct)).GetProperty("prompt_id").GetString()!;
            await File.WriteAllTextAsync(Path.Combine(evidence, "prompt-id.txt"), id, _ct);
            JsonElement entry;
            while (true) {
                await Task.Delay(3000, _ct);
                var history = await http.GetFromJsonAsync<JsonElement>("history/" + id, _ct);
                if (history.TryGetProperty(id, out entry) && entry.TryGetProperty("status", out var status) && status.TryGetProperty("completed", out _)) break;
                Assert.True(timer.Elapsed < TimeSpan.FromMinutes(30), $"{name} exceeded its watchdog; prompt {id} remains recoverable on the server.");
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, "history.json"), entry.ToString(), _ct);
            Assert.Equal("success", entry.GetProperty("status").GetProperty("status_str").GetString());
            var candidate = new VideoCandidate { Number = 1, TakeId = Guid.NewGuid(), Seed = 20261009, Output = entry };
            var stage = Path.Combine(directory, "candidate-" + candidate.TakeId);
            var take = await generator.DownloadAsync(new VideoRun { Id = runId, Snapshot = snapshot, Refinement = refinement }, candidate, stage,
                message => { Console.WriteLine(name + ": " + message); return Task.CompletedTask; }, _ct);
            take.RetainedSource = new([]) { RefinementInput = refinement?.SourcePackage };
            Directory.CreateDirectory(Path.Combine(stage, TakeTrimming.InputsFolder));
            if (refinement is not null) await TakeTrimming.CopyVerifiedAsync(Path.Combine(directory, "inputs", H3RefinementPackage.FileName),
                Path.Combine(stage, TakeTrimming.InputsFolder, TakeTrimming.RefinementInputFile), refinement.SourcePackage.Bytes, refinement.SourcePackage.Sha256, _ct);
            await TakeBundles.CopyCapturedAsync(snapshot.Motion?.Files ?? [], Path.Combine(directory, "inputs"), Path.Combine(stage, TakeTrimming.InputsFolder), _ct);
            take.Bytes = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
            await store.PublishTakeAsync(f.Project.Id, take, stage, _ct);
            await TakeBundles.CopyAsync(take, Path.Combine(projectDirectory, "shots", "takes", take.Directory), Path.Combine(evidence, "full-generation"), _ct);
            await AtomicJsonFile.WriteAsync(Path.Combine(evidence, "full-generation.json"), take, _ct);
            if (extension is not null) take = (await store.PublishExtensionAsync(f.Project.Id, request, take.Id, _ct)).Takes.Single(t => t.Id == H3Motion.OutputId(take.Id));
            await TakeBundles.CopyAsync(take, Path.Combine(projectDirectory, "shots", "takes", take.Directory), Path.Combine(evidence, "take"), _ct);
            await AtomicJsonFile.WriteAsync(Path.Combine(evidence, "take.json"), take, _ct);
            var info = await new ProductionMediaTools().VideoInfoAsync(Path.Combine(evidence, "take", "video.mp4"), settings, _ct);
            Assert.Equal(take.FrameCount, info.Frames); Assert.True(info.HasAudio);
            records.Add(new { name, seconds = timer.Elapsed.TotalSeconds, take.Id, take.FrameCount, route = snapshot.Motion?.Route, latentHash = take.RefinementPackage!.Sha256 });
            await AtomicJsonFile.WriteAsync(Path.Combine(output, "results.json"), records, _ct);
            Console.WriteLine($"Saved {name}: {take.FrameCount} visible frames in {timer.Elapsed.TotalSeconds:0}s");
            return take;
        }

        async Task<ShotTake> Extend(string name, ShotTake source, int end, bool single = false)
        {
            var run = Guid.NewGuid();
            var (captured, motion) = await store.CaptureExtensionAsync(f.Project.Id, source.Id, run, end, 2, true, _ct);
            if (single) {
                var inputs = Path.Combine(await store.RunDirectoryAsync(f.Project.Id, run, _ct), "inputs");
                foreach (var item in motion.Files) File.Delete(Path.Combine(inputs, item.FileName));
                await using var frame = await store.OpenAsync(f.Project.Id, source.Id, ShotTrashKind.Take, end - 1, ct: _ct);
                var path = Path.Combine(inputs, "motion-frame-00.png");
                await using (var target = File.Create(path)) await frame!.Content.CopyToAsync(target, _ct);
                var bytes = await File.ReadAllBytesAsync(path, _ct);
                motion = new(source.Id, MotionContextRoute.SingleFrame, end - 1, end, 1, H3Motion.GenerationFrames(1, 2), [new("motion-frame-00.png", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)))]);
            }
            var next = AiVideoJobCapture.ExtensionShot(H3Motion.Tail(source, end).Segment.Source,
                new(source.Id, end, 2, "She keeps walking steadily right while the camera continues tracking beside her. She says, The door is just ahead. Continuous footsteps and courtyard ambience.", [new() { Speaker = "Riley", Text = "The door is just ahead." }], null, SaveLosslessFrames: true), motion.GenerationFrames);
            var snapshot = sourceSnapshot with { Shot = next, Prompt = H3Policy.Compile(next, motionContext: true), Fingerprint = H3Policy.Fingerprint(next),
                FrameCount = motion.GenerationFrames, Profile = H3Motion.Profile, Motion = motion, Sampling = H3Policy.Sampling(next, settings), Preset = H3Presets.Capture(next, settings) };
            var result = await Run(name, snapshot, run, captured);
            Assert.Equal(end + motion.GenerationFrames - motion.Frames, result.FrameCount);
            Assert.Equal(source.RefinementPackage!.Sha256, captured.Source.RefinementPackage!.Sha256);
            // Exact pixels on each side of archive boundaries and the retained endpoint.
            foreach (var index in new[] { 0, 23, 24, end - 1 }.Where(i => i < end).Distinct()) {
                await using var before = await store.OpenAsync(f.Project.Id, source.Id, ShotTrashKind.Take, index, ct: _ct);
                await using var after = await store.OpenAsync(f.Project.Id, result.Id, ShotTrashKind.Take, index, ct: _ct);
                Assert.Equal(await SHA256.HashDataAsync(before!.Content, _ct), await SHA256.HashDataAsync(after!.Content, _ct));
            }
            return result;
        }

        var original = await Run("source", sourceSnapshot, Guid.NewGuid());
        await Extend("single-frame", original, original.FrameCount, single: true);
        await Extend("frames-exact", original, original.FrameCount - 1);
        var first = await Extend("saved-motion-1", original, original.FrameCount);
        var second = await Extend("saved-motion-2", first, first.FrameCount);
        await Extend("saved-motion-3", second, second.FrameCount);
        foreach (var mode in new[] { TakeRefinementMode.Refine, TakeRefinementMode.Rework }) {
            var run = Guid.NewGuid();
            var refinement = await store.CaptureRefinementAsync(f.Project.Id, first.Id, run, mode, first.Width, first.Height, settings.LatentUpscaler, check.PreviewUpscaling.Implementation!.Value, _ct);
            var source = ShotCopy.Of(first); source.Extension = null;
            var directory = Path.Combine(await store.RunDirectoryAsync(f.Project.Id, run, _ct), H3Motion.SourceFolder);
            var manifest = new List<CapturedMotionFile>();
            foreach (var relative in TakeBundles.Files(source)) {
                await using var content = File.OpenRead(TakeBundles.Under(directory, relative));
                manifest.Add(new(relative, content.Length, Convert.ToHexString(await SHA256.HashDataAsync(content, _ct))));
            }
            var last = first.Composition!.Segments[^1];
            var prefix = first.FrameCount - (last.EndFrameExclusive - last.StartFrame);
            var result = await Run(mode.ToString().ToLowerInvariant(), first.Snapshot, run, new(source, prefix, true, manifest), refinement, new(last.StartFrame, last.EndFrameExclusive));
            Assert.Equal(first.FrameCount, result.FrameCount);
            if (mode == TakeRefinementMode.Refine) Assert.Equal(
                await AudioHash(Path.Combine(projectDirectory, "shots", "takes", last.Source.Directory, "video.mp4"), _ct),
                await AudioHash(Path.Combine(projectDirectory, "shots", "takes", result.Composition!.Segments[^1].Source.Directory, "video.mp4"), _ct));
        }
    }
}
