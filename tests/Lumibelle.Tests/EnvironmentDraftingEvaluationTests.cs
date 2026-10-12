using System.Diagnostics;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

// Exactly four opt-in provider calls. Attempt markers prevent interrupted runs from retrying.
public sealed class EnvironmentDraftingEvaluationTests
{
    private sealed record CorpusScene(string Name, string Origin, List<ScriptBlock> Blocks);
    private sealed record Frozen(string Name, ShotPlanningRequest Request, IReadOnlyList<AiTextMessage> Messages);
    [Fact]
    public async Task FourFrozenEnvironmentDrafts()
    {
        var phase = Environment.GetEnvironmentVariable("LUMIBELLE_ENVIRONMENT_EVAL");
        if (phase is not ("freeze" or "run" or "parse")) Assert.Skip("Opt-in four-request environment pilot.");
        var ct = TestContext.Current.CancellationToken;
        var root = Environment.GetEnvironmentVariable("LUMIBELLE_ENVIRONMENT_EVAL_ROOT")!;
        var repo = Environment.GetEnvironmentVariable("LUMIBELLE_ENVIRONMENT_EVAL_REPO")!;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "requests.json");
        if (phase == "freeze") {
            Assert.False(File.Exists(path), "Do not overwrite frozen requests.");
            var source = Environment.GetEnvironmentVariable("LUMIBELLE_ENVIRONMENT_EVAL_SOURCE")!;
            var scene = JsonSerializer.Deserialize<List<CorpusScene>>(await File.ReadAllTextAsync(Path.Combine(repo, "docs/evaluations/shot-direction-2026-10-12/corpus.json"), ct), AtomicJsonFile.Options)![0];
            var library = JsonSerializer.Deserialize<AssetLibrary>(await File.ReadAllTextAsync(Path.Combine(source, "assets.json"), ct), AtomicJsonFile.Options)!;
            var owner = library.Assets.Single(a => a.Name == "Noxian Main Plaza");
            var reel = library.Reels.Single(r => r.AssetId == owner.Id);
            var model = JsonSerializer.Deserialize<TextModelReference>(await File.ReadAllTextAsync(Path.Combine(repo, "docs/evaluations/shot-direction-2026-10-12/model.json"), ct), AtomicJsonFile.Options)!;
            var pictures = new Dictionary<string, byte[]>(); List<EnvironmentViewCandidate> views = [];
            foreach (var index in new[] { 0, 132, 237 }) {
                var frame = ShotCopy.Of(reel.Keyframes!.Frames.Single(f => f.Frame.Index == index));
                var file = Path.Combine(source, "reference-videos", reel.Media.Id.ToString(), $"frame-{frame.Frame.Source}-{index:000000}.png");
                var png = await File.ReadAllBytesAsync(file, ct);
                var hash = lumibelle.Services.Production.ReelViewSuggestions.Hash(png);
                pictures.Add(hash, png);
                await File.WriteAllBytesAsync(Path.Combine(root, $"view-{index}.png"), png, ct);
                frame.Name = $"Captured view {views.Count + 1}"; frame.Notes = "";
                var neutralReel = ShotCopy.Of(reel) with { Name = "Environment coverage", Keyframes = null };
                views.Add(new($"reel/{reel.Id}/{frame.Id}", frame.Name, false, "", null, neutralReel, frame, hash));
            }
            var script = new ScriptSourceSnapshot(scene.Blocks[0].Id, library.ProjectId, 1, DateTimeOffset.Parse("2026-10-12T00:00:00Z"), scene.Blocks);
            const string scale = "Riley is shorter than the podium and cannot see over its top while standing on its existing base. Preserve this scale; do not invent a platform, shrink the podium, enlarge Riley or change the story. Important reactions must remain visible through purposeful coverage.";
            List<Frozen> frozen = [];
            foreach (var condition in new[] { "full", "incomplete" }) {
                var selected = condition == "full" ? views : views.Take(1).ToList();
                var snapshot = new DraftingEnvironmentSnapshot(Guid.NewGuid(), scene.Blocks[0].Id, Guid.NewGuid(), owner.Id, owner.Name, "", scale, selected,
                    DraftingEnvironments.Semantic(owner.Id, "", scale, selected));
                foreach (var visual in new[] { false, true }) {
                    var request = new ShotPlanningRequest(script, new AssetLibrary { ProjectId = library.ProjectId }, [scene.Blocks[0].Id], 15, "", model) {
                        CoverageOnly = true, Style = DirectingStyle.Auto, Environments = [snapshot],
                        InspectionImageHashes = visual ? selected.Select(v => v.Sha256).ToArray() : []
                    };
                    var messages = ShotPlanner.BuildMessages(request, pictures).Select(AiTextMessage.Capture).ToArray();
                    frozen.Add(new($"{condition}-{(visual ? "pictures" : "text")}", request, messages));
                }
            }
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(frozen, AtomicJsonFile.Options), ct);
            await File.WriteAllTextAsync(Path.Combine(root, "preflight.md"), "# Frozen pilot\n\nFour requests only: full text/pictures and incomplete text/pictures. Both pairs use the new v5 contract, identical screenplay, authored scale constraint, Auto, 15 seconds and Opus. Within each pair only the image manifest and attachments differ. No image-derived assessment is supplied. Captured names are neutral and notes are empty.\n\nThe incomplete pair withholds views 132 and 237 and their descriptions. Its opening view does not establish the route between the defendant's standing side of the podium and the open passage, or a usable reaction viewpoint behind the oversized podium. The script still requires that action. An unseen angle must be a proposal, not an observed fact. Full evidence is complementary footage, not a certified consistent layout.\n\nScale preservation is a regression check because both arms receive the scale note. No drafts are applied and no media is generated.\n", ct);
            return;
        }
        var captured = JsonSerializer.Deserialize<List<Frozen>>(await File.ReadAllTextAsync(path, ct), AtomicJsonFile.Options)!;
        Assert.Equal(4, captured.Count);
        if (phase == "parse") {
            foreach (var entry in captured) {
                using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, entry.Name + ".json"), ct));
                var parsed = ShotPlanner.Parse(result.RootElement.GetProperty("raw").GetString()!, entry.Request);
                await File.WriteAllTextAsync(Path.Combine(root, entry.Name + "-parsed.json"), JsonSerializer.Serialize(parsed, AtomicJsonFile.Options), ct);
                Assert.Null(parsed.Error); Assert.Single(parsed.Strategies); Assert.All(parsed.Shots, s => Assert.NotEmpty(s.DraftingEnvironment!.Assessment.Summary));
            }
            return;
        }
        using var settingsJson = JsonDocument.Parse(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("LUMIBELLE_ENVIRONMENT_EVAL_SETTINGS")!, ct));
        var settings = settingsJson.RootElement.GetProperty("settings").Deserialize<AiSettings>(AtomicJsonFile.Options)!;
        var registry = new AiProviderRegistry(new NoHttp(), new FakeAiSettingsStore(), TestComfy.Monitor(),
            new CodexClient(new CodexTransportFactory(), TimeProvider.System), new ClaudeCodeClient(new ClaudeCodeProcessFactory()));
        foreach (var entry in captured) {
            var output = Path.Combine(root, entry.Name + ".json");
            if (File.Exists(output)) continue;
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { status = "attempted", entry.Name }, AtomicJsonFile.Options), ct);
            var timer = Stopwatch.StartNew(); string raw = ""; string? error = null; object? usage = null; ShotPlanningResult? parsed = null;
            try {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(8));
                using var client = await registry.CreateAsync(entry.Request.Selection.Backend, entry.Request.Selection.Model, settings, timeout.Token);
                var response = await client.GetResponseAsync(entry.Messages.Select(m => m.ToMessage()).ToList(), TextGenerationOptions.Create(entry.Request.Selection.Backend, settings, selection: entry.Request.Selection), timeout.Token);
                raw = response.Text; usage = response.Usage; parsed = ShotPlanner.Parse(raw, entry.Request);
            } catch (Exception e) { error = e.Message; }
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { entry.Name, status = "finished", model = entry.Request.Selection, elapsedSeconds = timer.Elapsed.TotalSeconds, raw, parsed, error, usage }, AtomicJsonFile.Options), CancellationToken.None);
        }
    }
    private sealed class NoHttp : IHttpClientFactory { public HttpClient CreateClient(string name) => throw new InvalidOperationException("CLI pilot only."); }
}
