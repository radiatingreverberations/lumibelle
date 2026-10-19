using System.Diagnostics;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

// Opt-in, text-only comparison. Captures baseline messages before planner changes.
// Each arm is attempted once per frozen scene; an attempt file prevents automatic retries.
public sealed class DirectionEvaluationTests
{
    private sealed record Scene(string Name, string Origin, List<ScriptBlock> Blocks);
    private sealed record Captured(string Name, ShotPlanningRequest Request, IReadOnlyList<AiTextMessage> Messages);
    [Fact]
    public async Task FrozenDirectionComparison()
    {
        var evaluationCancellation = TestContext.Current.CancellationToken;
        var phase = Environment.GetEnvironmentVariable("LUMIBELLE_DIRECTION_EVAL");
        if (phase is not ("capture-baseline" or "capture-updated" or "baseline" or "updated")) Assert.Skip("Opt-in direction evaluation.");
        var root = Environment.GetEnvironmentVariable("LUMIBELLE_DIRECTION_EVAL_ROOT")!;
        Directory.CreateDirectory(root);
        var corpus = JsonSerializer.Deserialize<List<Scene>>(await File.ReadAllTextAsync(Path.Combine(root, "corpus.json"), evaluationCancellation), AtomicJsonFile.Options)!;
        var settingsPath = Environment.GetEnvironmentVariable("LUMIBELLE_DIRECTION_EVAL_SETTINGS")!;
        using var settingsJson = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, evaluationCancellation));
        var settings = settingsJson.RootElement.GetProperty("settings").Deserialize<AiSettings>(AtomicJsonFile.Options)!;
        var model = JsonSerializer.Deserialize<TextModelReference>(await File.ReadAllTextAsync(Path.Combine(root, "model.json"), evaluationCancellation), AtomicJsonFile.Options)!;
        var arm = phase.Replace("capture-", "");
        var capturePath = Path.Combine(root, arm + "-requests.json");
        if (phase.StartsWith("capture-"))
        {
            Assert.False(File.Exists(capturePath), "Frozen requests must not be overwritten.");
            var captures = corpus.Select(scene => {
                var script = new ScriptSourceSnapshot(scene.Blocks[0].Id, Guid.Parse("77b4f055-0a79-462d-8193-fb782059ab99"), 1, DateTimeOffset.Parse("2026-10-12T00:00:00Z"), scene.Blocks);
                var request = new ShotPlanningRequest(script, new AssetLibrary { ProjectId = script.ProjectId }, [scene.Blocks[0].Id], 15, "", model) { CoverageOnly = true };
                return new Captured(scene.Name, request, ShotPlanner.BuildMessages(request).Select(AiTextMessage.Capture).ToArray());
            }).ToList();
            await File.WriteAllTextAsync(capturePath, JsonSerializer.Serialize(captures, AtomicJsonFile.Options), evaluationCancellation);
            return;
        }
        var captured = JsonSerializer.Deserialize<List<Captured>>(await File.ReadAllTextAsync(capturePath, evaluationCancellation), AtomicJsonFile.Options)!;
        var registry = new AiProviderRegistry(new NoHttp(), new FakeAiSettingsStore(), TestComfy.Monitor(),
            new CodexClient(new CodexTransportFactory(), TimeProvider.System), new ClaudeCodeClient(new ClaudeCodeProcessFactory()));
        await Parallel.ForEachAsync(captured.Select((c, i) => (c, i)), new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = evaluationCancellation }, async (entry, ct) => {
            var path = Path.Combine(root, $"{arm}-{entry.i + 1:00}.json");
            if (File.Exists(path)) return;
            // Publish the attempt before invoking a provider: interruption never silently repeats a request.
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { entry.c.Name, status = "attempted", model }, AtomicJsonFile.Options), ct);
            var timer = Stopwatch.StartNew(); string raw = ""; string? error = null; object? usage = null; ShotPlanningResult? parsed = null;
            try {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
                using var client = await registry.CreateAsync(model.Backend, model.Model, settings, timeout.Token);
                var response = await client.GetResponseAsync(entry.c.Messages.Select(m => m.ToMessage()).ToList(), TextGenerationOptions.Create(model.Backend, settings, selection: model), timeout.Token);
                raw = response.Text; usage = response.Usage; parsed = ShotPlanner.Parse(raw, entry.c.Request);
            }
            catch (Exception e) { error = e.Message; }
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { entry.c.Name, status = "finished", model, elapsedSeconds = timer.Elapsed.TotalSeconds, raw, parsed, error, usage }, AtomicJsonFile.Options), CancellationToken.None);
        });
    }
    private sealed class NoHttp : IHttpClientFactory { public HttpClient CreateClient(string name) => throw new InvalidOperationException("CLI comparison only."); }
}
