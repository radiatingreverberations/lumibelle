using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

/// <summary>
/// Opt-in planning-only replay of a saved sequence on its captured OpenAI-compatible
/// model. Keeps private inputs and outputs in the explicitly selected local folder;
/// never changes the saved job, starts screenplay writing or applies a proposal.
/// </summary>
public sealed class LiveSequencePlanningTests
{
    [Fact]
    public async Task CurrentPlanningPromptProducesAUsableBriefOnTheCapturedModel()
    {
        var directory = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_SEQUENCE_JOB");
        var outputDirectory = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_SEQUENCE_OUT");
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(outputDirectory))
            Assert.Skip("Set LUMIBELLE_LIVE_SEQUENCE_JOB and LUMIBELLE_LIVE_SEQUENCE_OUT for a private planning-only replay.");
        var ct = TestContext.Current.CancellationToken;
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "request.json"), ct));
        var original = saved.RootElement.GetProperty("request").Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(AiBackend.Vllm, original.Model.Backend);
        var payload = original.Payload<ScriptAssistantRequest>();
        Assert.NotNull(payload.Adaptation);
        var sourceDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        Assert.False(destination.Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(sourceDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Choose an output folder outside the saved job so its artifacts remain untouched.");
        Assert.False(File.Exists(Path.Combine(destination, "request.json")), "Choose a fresh output folder to preserve earlier probe responses.");
        var replyLimit = int.TryParse(Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_SEQUENCE_REPLY_TOKENS"), out var limit) ? limit :
            ScriptScenePlanning.PlanningReplyBudget(payload.Adaptation, original.Model.MaxOutputTokens ?? ScriptScenePlanning.DefaultDraftReplyTokens);
        Assert.InRange(replyLimit, 1, 32768);
        var request = original with {
            Messages = ScriptSequenceDrafting.PlanningMessages(payload).Select(AiTextMessage.Capture).ToArray(),
            ScenePlanMessages = null, ScenePlanReplyTokens = null,
            Model = original.Model with { MaxOutputTokens = replyLimit },
            ScenePlanResponseSchema = null, ScenePlanResponseGrammar = null,
            ResponseSchema = original.ResponseSchema is null ? null : ScriptScenePlanning.PlanningSchemaFor(payload.Adaptation.Passages.SelectMany(p => p.Sources)),
            ResponseGrammar = original.ResponseGrammar is null ? null : ScriptScenePlanning.PlanningGrammarFor(payload.Adaptation.Passages.SelectMany(p => p.Sources))
        };
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "request.json"), JsonSerializer.Serialize(request, AtomicJsonFile.Options), ct);
        var providers = new AiProviderRegistry(new TestHttpFactory(new HttpClientHandler()), new FakeAiSettingsStore(), TestComfy.Monitor());
        using var client = await providers.CreateAsync(request.Model.Backend, request.Model.Model, request.Settings, ct);
        var output = new StringBuilder(); string? finish = null; double? firstText = null; long? outputTokens = null;
        var timer = Stopwatch.StartNew(); var checkpoint = TimeSpan.FromSeconds(-2);
        async Task Save(bool complete)
        {
            var saveToken = complete ? CancellationToken.None : ct;
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "response.txt"), output.ToString(), saveToken);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "progress.json"), JsonSerializer.Serialize(new {
                Complete = complete, ElapsedSeconds = timer.Elapsed.TotalSeconds, FirstTextSeconds = firstText,
                Characters = output.Length, FinishReason = finish, MaxOutputTokens = request.Model.MaxOutputTokens, OutputTokens = outputTokens
            }, AtomicJsonFile.Options), saveToken);
        }
        await Save(false);
        try {
            await foreach (var update in client.GetStreamingResponseAsync(request.Messages.Select(m => m.ToMessage()), TextGenerationOptions.Captured(request), ct)) {
                if (update.FinishReason is { } reason) finish = reason.ToString();
                if (update.Contents.OfType<UsageContent>().LastOrDefault() is { } usage) outputTokens = usage.Details.OutputTokenCount ?? outputTokens;
                if (update.Text is { Length: > 0 } text) { firstText ??= timer.Elapsed.TotalSeconds; output.Append(text); }
                if (timer.Elapsed - checkpoint >= TimeSpan.FromSeconds(2)) { await Save(false); checkpoint = timer.Elapsed; }
            }
        }
        finally { await Save(true); }
        var inspection = ScriptScenePlanning.Inspect(output.ToString(), finish);
        var plan = inspection.Plan;
        var grounded = plan is null ? null : ScriptScenePlanning.Ground(plan, payload.Adaptation);
        var headings = plan?.Beats.Sum(b => Regex.Matches(b, @"(?m)^\s*(?:INT\.|EXT\.|INT\./EXT\.)").Count);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "report.json"), JsonSerializer.Serialize(new {
            FinishReason = finish, ElapsedSeconds = timer.Elapsed.TotalSeconds, MaxOutputTokens = request.Model.MaxOutputTokens, OutputTokens = outputTokens,
            Characters = output.Length, inspection.CanUse, inspection.Errors, inspection.Warnings,
            Beats = plan?.Beats.Length, BeatCharacters = plan?.Beats.Select(b => b.Length).ToArray(), ScreenplayHeadingsInBeats = headings,
            Continuity = plan?.Continuity.Length, Details = plan?.Details?.Length, Withhold = plan?.Withhold.Length,
            SourceHighlights = plan?.Evidence?.Length ?? 0, VerifiedSourceHighlights = grounded?.Evidence?.Length ?? 0
        }, AtomicJsonFile.Options), ct);
        Assert.True(inspection.CanUse, string.Join(" ", inspection.Errors));
        Assert.Empty(inspection.Warnings);
        Assert.Equal(0, headings);
        // Optional unverified highlights are omitted in production; their counts
        // remain in the report rather than turning an otherwise usable brief fatal.
        Assert.True(ScriptScenePlanning.Valid(grounded));
    }

    [Fact]
    public async Task SavedPlanningResponseUsesProductionGroundingWithoutAnotherModelCall()
    {
        var directory = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_SEQUENCE_JOB");
        var responsePath = Environment.GetEnvironmentVariable("LUMIBELLE_SAVED_SEQUENCE_RESPONSE");
        var reportPath = Environment.GetEnvironmentVariable("LUMIBELLE_SAVED_SEQUENCE_REPORT");
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(responsePath) || string.IsNullOrWhiteSpace(reportPath))
            Assert.Skip("Set the saved job, response and report paths for read-only planning validation.");
        var sourceDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var reportFile = Path.GetFullPath(reportPath);
        Assert.False(reportFile.Equals(Path.GetFullPath(responsePath), StringComparison.OrdinalIgnoreCase) ||
            reportFile.StartsWith(sourceDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "The validation report must be separate from the response and original job.");
        Assert.False(File.Exists(reportFile), "Choose a fresh validation report path.");
        var ct = TestContext.Current.CancellationToken;
        var raw = await File.ReadAllTextAsync(responsePath, ct);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "request.json"), ct));
        var original = saved.RootElement.GetProperty("request").Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var inspection = ScriptScenePlanning.Inspect(raw);
        Assert.True(inspection.CanUse, string.Join(" ", inspection.Errors));
        Assert.Empty(inspection.Warnings);
        var grounded = ScriptScenePlanning.Ground(inspection.Plan!, original.Payload<ScriptAssistantRequest>().Adaptation!);
        Assert.True(ScriptScenePlanning.Valid(grounded));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new {
            Mode = "Saved response validation; no inference or application",
            Valid = true, RawResponseHash = hash, Beats = grounded.Beats.Length,
            SourceHighlights = inspection.Plan!.Evidence?.Length ?? 0,
            VerifiedSourceHighlights = grounded.Evidence?.Length ?? 0,
            OmittedSourceHighlights = grounded.UnverifiedEvidenceCount
        }, AtomicJsonFile.Options), ct);
        Assert.Equal(raw, await File.ReadAllTextAsync(responsePath, ct));
    }
}
