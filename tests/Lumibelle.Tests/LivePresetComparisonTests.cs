using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    // Explicit opt-in: real generations of one prompt and seed with each preset, to compare speed and look.
    // LUMIBELLE_LIVE_PRESETS lists preset keys (default: Pika H3 Flex, SMD and Standard); the server must be idle.
    [Fact]
    public async Task LivePresetsRenderTheSamePromptForComparison()
    {
        var url = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_PRESET_URL");
        var output = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_PRESET_OUT");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(output)) Assert.Skip("Set LUMIBELLE_LIVE_PRESET_URL and LUMIBELLE_LIVE_PRESET_OUT for the manual GPU comparison.");
        Directory.CreateDirectory(output);
        using var http = new HttpClient { BaseAddress = new(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
        var settings = new H3Settings();
        if (Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_PRESET_SETTINGS") is { Length: > 0 } file)
        {
            var saved = JsonDocument.Parse(await File.ReadAllTextAsync(file, _ct)).RootElement;
            settings = (saved.TryGetProperty("settings", out var inner) ? inner : saved).GetProperty("h3").Deserialize<H3Settings>(AtomicJsonFile.Options)!;
        }
        var keys = (Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_PRESETS") ?? $"{H3PikaFlexRecipe.Key},{H3SmdRecipe.Key},standard").Split(',', StringSplitOptions.TrimEntries);
        var root = await http.GetFromJsonAsync<JsonElement>("object_info", _ct);
        var check = ComfyH3Video.Inspect(root, settings);
        var shot = Ready(); shot.Duration = 5; shot.SaveLosslessFrames = false;
        shot.Description = "An adult woman in a blue jacket walks steadily from left to right through a sunlit courtyard. The camera tracks smoothly beside her at chest height. She looks ahead and says, Let's keep moving. Her footsteps continue on the paving stones. Natural daylight, realistic movement, one continuous shot.";
        shot.Dialogue = [new() { Speaker = "Riley", Text = "Let's keep moving." }];
        // A repeated graph and seed is answered from ComfyUI's cache; choose a new seed to time a real render.
        var seed = long.TryParse(Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_PRESET_SEED"), out var chosen) ? chosen : 20261031;
        var results = new List<object>();
        foreach (var key in keys)
        {
            Assert.Null(check.Presets.Single(p => p.Key == key).Issue);
            var presetShot = shot with { GenerationPreset = key, Turbo = key == "turbo4" };
            var snapshot = H3PikaFlexRecipe.Effective(H3SmdRecipe.Effective(Snapshot(Guid.NewGuid(), presetShot) with {
                Settings = settings, ComfyUrl = url, Width = 608, Height = 352, OutputPolicy = new(false),
                Preset = H3Presets.Capture(presetShot, settings), Sampling = H3Policy.Sampling(presetShot, settings),
                Performance = H3Performance.Capture(settings.Performance) }));
            var graph = ComfyH3Video.BuildWorkflow(snapshot, seed, Guid.NewGuid().ToString("D"), []);
            var evidence = Path.Combine(output, key); Directory.CreateDirectory(evidence);
            await AtomicJsonFile.WriteAsync(Path.Combine(evidence, "workflow.json"), graph, _ct);
            var queue = await http.GetFromJsonAsync<JsonElement>("queue", _ct);
            Assert.Equal(0, queue.GetProperty("queue_running").GetArrayLength()); Assert.Equal(0, queue.GetProperty("queue_pending").GetArrayLength());
            var timer = Stopwatch.StartNew();
            using var posted = await http.PostAsJsonAsync("prompt", graph, _ct);
            Assert.True(posted.IsSuccessStatusCode, await posted.Content.ReadAsStringAsync(_ct));
            var id = (await posted.Content.ReadFromJsonAsync<JsonElement>(_ct)).GetProperty("prompt_id").GetString()!;
            JsonElement entry;
            while (true)
            {
                await Task.Delay(3000, _ct);
                var history = await http.GetFromJsonAsync<JsonElement>("history/" + id, _ct);
                if (history.TryGetProperty(id, out entry) && entry.TryGetProperty("status", out var status) && status.TryGetProperty("completed", out _)) break;
                Assert.True(timer.Elapsed < TimeSpan.FromMinutes(40), $"{key} exceeded its watchdog; prompt {id} remains on the server.");
            }
            var seconds = timer.Elapsed.TotalSeconds;
            await File.WriteAllTextAsync(Path.Combine(evidence, "history.json"), entry.ToString(), _ct);
            Assert.Equal("success", entry.GetProperty("status").GetProperty("status_str").GetString());
            // Keep each preset's video beside its graph for side-by-side review.
            foreach (var node in entry.GetProperty("outputs").EnumerateObject())
                foreach (var kind in node.Value.EnumerateObject().Where(k => k.Value.ValueKind == JsonValueKind.Array))
                    foreach (var item in kind.Value.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object && i.TryGetProperty("filename", out var name) && name.GetString()!.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)))
                    {
                        var query = $"view?filename={Uri.EscapeDataString(item.GetProperty("filename").GetString()!)}&subfolder={Uri.EscapeDataString(item.GetProperty("subfolder").GetString() ?? "")}&type={item.GetProperty("type").GetString()}";
                        await File.WriteAllBytesAsync(Path.Combine(evidence, key + ".mp4"), await http.GetByteArrayAsync(query, _ct), _ct);
                    }
            Assert.True(File.Exists(Path.Combine(evidence, key + ".mp4")), $"{key} produced no MP4.");
            results.Add(new { key, label = H3Presets.Label(key), steps = snapshot.Sampling!.Steps, seconds = Math.Round(seconds, 1), prompt = id });
            Console.WriteLine($"{key}: {seconds:0.0} s");
        }
        await AtomicJsonFile.WriteAsync(Path.Combine(output, "results.json"), results, _ct);
    }
}
