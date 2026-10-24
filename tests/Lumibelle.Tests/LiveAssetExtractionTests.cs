using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

// Explicit opt-in. Private artifacts stay outside the original job. No library
// writes, queue mutations or automatic application are part of this probe.
public sealed class LiveAssetExtractionTests
{
    [Fact]
    public async Task SavedExtractionResponsePassesCurrentValidationWithoutAnotherCall()
    {
        var source = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_EXTRACTION_JOB");
        var responsePath = Environment.GetEnvironmentVariable("LUMIBELLE_SAVED_EXTRACTION_RESPONSE");
        var reportPath = Environment.GetEnvironmentVariable("LUMIBELLE_SAVED_EXTRACTION_REPORT");
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(responsePath) || string.IsNullOrWhiteSpace(reportPath))
            Assert.Skip("Set the saved extraction input and a fresh validation report path for offline inspection.");
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        reportPath = Path.GetFullPath(reportPath);
        Assert.False(reportPath.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(reportPath)); Assert.NotEqual(Path.GetFullPath(responsePath), reportPath);
        var ct = TestContext.Current.CancellationToken;
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(source, "request.json"), ct));
        var request = saved.RootElement.GetProperty("request").Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var payload = request.Payload<AssetExtractionRequest>();
        var raw = await File.ReadAllTextAsync(responsePath, ct);
        var parsed = AiTextResults.Parse(request, raw, "stop"); Assert.Null(parsed.Error);
        var result = parsed.Read<AssetExtractionResult>()!;
        var checkedLibrary = AssetExtractionApply.Apply(payload.Library, result.Proposals, DateTimeOffset.UtcNow);
        var sections = ScriptStructure.Sections(payload.Script.Blocks).Where(s => s.Kind == ScriptBlockKind.Scene && payload.SceneIds.Contains(s.Id))
            .ToDictionary(s => s.Id, s => Normalize(ScriptStructure.Markdown(payload.Script.Blocks.Skip(s.Start).Take(s.Count))));
        var evidence = result.Proposals.SelectMany(p => p.Evidence.Concat(p.Looks.SelectMany(l => l.Evidence))).ToArray();
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new {
            Valid = true, Mode = "Offline validation; no inference or application", ResponseHash = AiTextRepairs.Hash(raw),
            Suggestions = result.Proposals.Count, Looks = result.Proposals.Sum(p => p.Looks.Count), Evidence = evidence.Length,
            EvidenceWithSelectedScene = evidence.Count(e => e.SceneId is { } id && sections.ContainsKey(id)),
            ExactSupportingExcerpts = evidence.Count(e => e.SceneId is { } id && sections.TryGetValue(id, out var text) && text.Contains(Normalize(e.Excerpt), StringComparison.OrdinalIgnoreCase)),
            LibraryApplicationStructurallyValid = checkedLibrary.Assets.All(a => !LookPolicy.Invalid(a))
        }, AtomicJsonFile.Options), ct);
        Assert.Equal(raw, await File.ReadAllTextAsync(responsePath, ct));
        static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
    }

    [Fact]
    public async Task CurrentExtractionPromptUsesSelectedSceneReferencesOnCapturedModel()
    {
        var source = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_EXTRACTION_JOB");
        var destination = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_EXTRACTION_OUT");
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
            Assert.Skip("Set LUMIBELLE_LIVE_EXTRACTION_JOB and LUMIBELLE_LIVE_EXTRACTION_OUT to run a private extraction probe.");
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        Assert.False(destination.Equals(source, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(destination), "Use a fresh output folder.");
        var ct = TestContext.Current.CancellationToken;
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(source, "request.json"), ct));
        var original = document.RootElement.GetProperty("request").Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(AiJobKind.AssetExtraction, original.Kind); Assert.Equal(AiBackend.Vllm, original.Model.Backend);
        var payload = original.Payload<AssetExtractionRequest>();
        var request = original with { Messages = AssetExtractor.BuildMessages(payload).Select(AiTextMessage.Capture).ToArray(), Profile = "asset-extraction-v2" };
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "request.json"), JsonSerializer.Serialize(request, AtomicJsonFile.Options), ct);
        var providers = new AiProviderRegistry(new TestHttpFactory(new HttpClientHandler()), new FakeAiSettingsStore(), TestComfy.Monitor());
        using var client = await providers.CreateAsync(request.Model.Backend, request.Model.Model, request.Settings, ct);
        var timer = Stopwatch.StartNew(); var checkpoint = TimeSpan.FromSeconds(-2);
        var output = new StringBuilder(); string? finish = null; long? tokens = null;
        async Task Save(bool complete) {
            await File.WriteAllTextAsync(Path.Combine(destination, "response.txt"), output.ToString(), CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(destination, "progress.json"), JsonSerializer.Serialize(new {
                Complete = complete, ElapsedSeconds = timer.Elapsed.TotalSeconds, Characters = output.Length, FinishReason = finish, OutputTokens = tokens
            }, AtomicJsonFile.Options), CancellationToken.None);
        }
        try {
            await foreach (var update in client.GetStreamingResponseAsync(request.Messages.Select(m => m.ToMessage()), TextGenerationOptions.Captured(request), ct)) {
                if (update.FinishReason is { } reason) finish = reason.ToString();
                if (update.Contents.OfType<UsageContent>().LastOrDefault() is { } usage) tokens = usage.Details.OutputTokenCount ?? tokens;
                output.Append(update.Text);
                if (timer.Elapsed - checkpoint >= TimeSpan.FromSeconds(2)) { await Save(false); checkpoint = timer.Elapsed; }
            }
        }
        finally { await Save(true); }
        var parsed = AiTextResults.Parse(request, output.ToString(), finish);
        var assets = parsed.Read<AssetExtractionResult>();
        await File.WriteAllTextAsync(Path.Combine(destination, "report.json"), JsonSerializer.Serialize(new {
            FinishReason = finish, ElapsedSeconds = timer.Elapsed.TotalSeconds, Characters = output.Length, OutputTokens = tokens,
            parsed.Error, Issues = assets?.ValidationIssues, Suggestions = assets?.Proposals.Count,
            Categories = assets?.Proposals.GroupBy(p => p.Category).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            Looks = assets?.Proposals.Sum(p => p.Looks.Count), Evidence = assets?.Proposals.Sum(p => p.Evidence.Count)
        }, AtomicJsonFile.Options), ct);
        Assert.Null(parsed.Error); Assert.Equal("stop", finish); Assert.NotNull(assets);
    }
}
