using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Shots;

namespace Lumibelle.Tests;

/// <summary>
/// Manual check against a real ComfyUI: generates a small take that keeps its latents with stock nodes,
/// refines it at a larger size, and checks the result keeps the take's audio. Set LUMIBELLE_LIVE_COMFY_URL
/// (for example http://127.0.0.1:8188) and optionally LUMIBELLE_LIVE_MODEL and LUMIBELLE_LIVE_OUT (a folder
/// for the two MP4s). Occupies that GPU for several minutes.
/// </summary>
public sealed partial class ShotTests
{
    [Fact]
    public async Task LiveTakeIsRefinedAtALargerSizeWithStockNodes()
    {
        var url = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_COMFY_URL");
        if (string.IsNullOrEmpty(url)) Assert.Skip("Set LUMIBELLE_LIVE_COMFY_URL to a ComfyUI server with H3 and the latent upscaler.");
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient { BaseAddress = new(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
        var root = JsonSerializer.SerializeToElement(await http.GetFromJsonAsync<JsonElement>("object_info", ct));
        string Installed(string node, string field, H3Requirement requirement)
        {
            var options = ComfyH3Video.Options(root, node, field);
            return requirement.Downloads.Select(d => d.Name).FirstOrDefault(options.Contains) ?? throw new InvalidOperationException($"No suggested {requirement.Label} is installed.");
        }
        var settings = new H3Settings
        {
            Model = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_MODEL") ?? Installed("UNETLoader", "unet_name", H3Requirements.Model),
            Encoder = ComfyH3Video.Options(root, "CLIPLoader", "clip_name").FirstOrDefault(n => n.Contains("minimax_h3", StringComparison.OrdinalIgnoreCase)) ?? Installed("CLIPLoader", "clip_name", H3Requirements.Encoder),
            VideoVae = Installed("VAELoader", "vae_name", H3Requirements.VideoVae), AudioVae = Installed("VAELoader", "vae_name", H3Requirements.AudioVae),
            LatentUpscaler = ComfyH3Video.Options(root, H3PreviewUpscaling.Node, "model_name").First(n => n.Contains("fp16")),
        };
        var check = ComfyH3Video.Inspect(root, settings);
        Assert.True(check.StandardReady, check.Message); Assert.True(check.PackageCaptureReady); Assert.Null(check.RefinementIssue);
        var implementation = check.PreviewUpscaling.Implementation!.Value;

        var shot = Ready(); shot.Duration = 2; shot.Description = "A woman in white armor walks across a quiet room and waves. Footsteps, no speech.";
        var snapshot = Snapshot(Guid.NewGuid(), shot) with { Settings = settings, ComfyUrl = url, Width = 608, Height = 352, CaptureRefinementData = true };
        var generator = new ComfyH3Video(new TestHttpFactory(new HttpClientHandler()), TestComfy.Monitor(), null!, null!, null!, new ProductionMediaTools());
        var work = Path.Combine(Path.GetTempPath(), "lumibelle-live-refinement-" + Guid.NewGuid().ToString("N"));
        async Task<ShotTake> Run(VideoRun run, object workflow, string folder)
        {
            var clientId = Guid.NewGuid().ToString();
            using var posted = await http.PostAsJsonAsync("prompt", JsonSerializer.SerializeToElement(workflow), ct);
            Assert.True(posted.IsSuccessStatusCode, await posted.Content.ReadAsStringAsync(ct));
            var id = (await posted.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("prompt_id").GetString()!;
            JsonElement entry;
            while (true)
            {
                await Task.Delay(3000, ct);
                var history = await http.GetFromJsonAsync<JsonElement>("history/" + id, ct);
                if (history.TryGetProperty(id, out entry) && entry.GetProperty("status").TryGetProperty("completed", out var done) && done.ValueKind != JsonValueKind.Null) break;
            }
            Assert.Equal("success", entry.GetProperty("status").GetProperty("status_str").GetString());
            var candidate = new VideoCandidate { Number = 1, TakeId = Guid.NewGuid(), Output = entry };
            return await generator.DownloadAsync(run, candidate, Path.Combine(work, folder), _ => Task.CompletedTask, ct);
        }
        var take = await Run(new VideoRun { Snapshot = snapshot }, ComfyH3Video.BuildWorkflow(snapshot, 20260911, Guid.NewGuid().ToString(), []), "take");
        Assert.NotNull(take.RefinementPackage);

        var size = RefinementPolicy.DefaultSize(take.Width, take.Height);
        var refinement = new TakeRefinement(take.Id, take.RefinementPackage!, TakeRefinementMode.Refine, size.Width, size.Height, settings.LatentUpscaler, implementation);
        var (video, audio) = await generator.UploadRefinementAsync(url, Path.Combine(work, "take", H3RefinementPackage.FileName), Path.Combine(work, "upload"), ct);
        var refined = await Run(new VideoRun { Snapshot = snapshot, Refinement = refinement },
            ComfyH3Video.BuildWorkflow(snapshot, 812, Guid.NewGuid().ToString(), [], refine: new(refinement, video, audio)), "refined");
        if (Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_OUT") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            File.Copy(Path.Combine(work, "take", "video.mp4"), Path.Combine(output, "take.mp4"), true);
            File.Copy(Path.Combine(work, "refined", "video.mp4"), Path.Combine(output, "refined.mp4"), true);
        }
        Assert.Equal((size.Width, size.Height), (refined.Width, refined.Height));
        Assert.NotNull(refined.RefinementPackage);
        Assert.Equal(await AudioHash(Path.Combine(work, "take", "video.mp4"), ct), await AudioHash(Path.Combine(work, "refined", "video.mp4"), ct));
        Directory.Delete(work, true);
    }

    private static async Task<string> AudioHash(string video, CancellationToken ct)
    {
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-i", video, "-map", "0:a", "-f", "s16le", "-ac", "2", "-ar", "48000", "-" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(process.StandardOutput.BaseStream, ct);
        await process.WaitForExitAsync(ct);
        return Convert.ToHexString(hash);
    }
}
