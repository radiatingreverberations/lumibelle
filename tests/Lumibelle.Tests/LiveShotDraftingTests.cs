using System.Diagnostics;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

// Opt-in provider checks use neutral synthetic material and never alter a project.
public sealed class LiveShotDraftingTests
{
    internal static ShotPlanningRequest Request(bool single, TextModelReference model)
    {
        var blocks = new List<ScriptBlock> {
            ScriptBlock.Create(ScriptBlockKind.Scene, "INT. WORKSHOP — DAY"),
            ScriptBlock.Create(ScriptBlockKind.Action, "A technician holds a sensor on the first marked pad. The camera stays on this measurement for eight uninterrupted seconds."),
            ScriptBlock.Create(ScriptBlockKind.Character, "TECHNICIAN"),
            ScriptBlock.Create(ScriptBlockKind.Dialogue, "The café display reads \"ready\".")
        };
        if (!single) blocks.AddRange([
            ScriptBlock.Create(ScriptBlockKind.Action, "After the first measurement ends, the technician moves to the second pad. Show a second uninterrupted eight-second measurement."),
            ScriptBlock.Create(ScriptBlockKind.Action, "After the second measurement ends, the technician moves to the third pad. Show a third uninterrupted eight-second measurement."),
            ScriptBlock.Create(ScriptBlockKind.Character, "TECHNICIAN"),
            ScriptBlock.Create(ScriptBlockKind.Dialogue, "Nu är vi klara.")
        ]);
        var script = ScriptFixtures.Approved(blocks);
        return new(script, new() { ProjectId = script.ProjectId }, [blocks[0].Id], 10,
            "Preserve the specified on-screen measurement holds and all dialogue. Keep supplementary direction concise.", model) {
            CoverageOnly = true, SingleShot = single ? new(Guid.NewGuid(), 0, [], null) : null
        };
    }

    [Theory]
    [InlineData(AiBackend.Codex, "gpt-6.1-sol", false)]
    [InlineData(AiBackend.Codex, "gpt-6.1-sol", true)]
    [InlineData(AiBackend.ClaudeCode, "opus", false)]
    [InlineData(AiBackend.ClaudeCode, "opus", true)]
    public async Task HostedCliModelsDraftValidPlainJsonWithPlayableDurations(AiBackend backend, string modelId, bool single)
    {
        var file = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_SHOT_DRAFT_SETTINGS");
        if (string.IsNullOrWhiteSpace(file)) Assert.Skip("Set LUMIBELLE_LIVE_SHOT_DRAFT_SETTINGS to use the local CLI sign-ins with neutral test material.");
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        var settings = new AiSettings {
            Codex = saved.RootElement.GetProperty("settings").GetProperty("codex").Deserialize<CodexSettings>(AtomicJsonFile.Options)! with { Enabled = true },
            ClaudeCode = saved.RootElement.GetProperty("settings").GetProperty("claudeCode").Deserialize<ClaudeCodeSettings>(AtomicJsonFile.Options)! with { Enabled = true }
        };
        await using var codex = new CodexClient(new CodexTransportFactory(), TimeProvider.System);
        var registry = new AiProviderRegistry(new TestHttpFactory(new HttpClientHandler()), new FakeAiSettingsStore(), TestComfy.Monitor(),
            codex, new ClaudeCodeClient(new ClaudeCodeProcessFactory()));
        await Run(registry, settings, new(backend, modelId, modelId), single, false);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NativeVllmDraftsTheSameTimedScenesWithItsCapturedSchema(bool single)
    {
        var url = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_SHOT_DRAFT_URL");
        var modelId = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_SHOT_DRAFT_MODEL");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(modelId)) Assert.Skip("Set LUMIBELLE_LIVE_SHOT_DRAFT_URL and LUMIBELLE_LIVE_SHOT_DRAFT_MODEL for neutral local-model timing checks.");
        var settings = new AiSettings { VllmUrl = url };
        var registry = new AiProviderRegistry(new TestHttpFactory(new HttpClientHandler()), new FakeAiSettingsStore(), TestComfy.Monitor());
        var model = TextModelPolicy.Reference(AiBackend.Vllm, modelId, "Local model", settings) with { MaxOutputTokens = 4096, Temperature = .2f };
        await Run(registry, settings, model, single, true);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task GrokThroughOpenRouterDraftsPlainJsonAndAcceptsTheResponseSchema(bool single, bool structured)
    {
        var file = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_SHOT_GROK_SETTINGS");
        if (string.IsNullOrWhiteSpace(file)) Assert.Skip("Set LUMIBELLE_LIVE_SHOT_GROK_SETTINGS to test Grok with the saved OpenRouter connection.");
        var services = new ServiceCollection();
        services.AddLogging(); services.AddDataProtection().SetApplicationName("Lumibelle");
        using var provider = services.BuildServiceProvider();
        var store = new FileAiSettingsStore(new ApplicationPaths(Path.GetDirectoryName(file)!),
            new global::lumibelle.WebSecretProtector(provider.GetRequiredService<IDataProtectionProvider>()));
        var settings = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(settings.HasOpenRouterKey, "The saved OpenRouter connection needs a key.");
        var registry = new AiProviderRegistry(new TestHttpFactory(new HttpClientHandler()), store, TestComfy.Monitor());
        var model = TextModelPolicy.Reference(AiBackend.OpenRouter, "x-ai/grok-4.20", "Grok 4.20", settings) with { MaxOutputTokens = 4096, Temperature = .2f };
        await Run(registry, settings, model, single, structured);
    }

    private static async Task Run(IAiProviderRegistry registry, AiSettings settings, TextModelReference model, bool single, bool structured)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(6));
        var request = Request(single, model);
        var captured = new AiTextJobRequest(3, AiJobKind.ShotPlanning, model, false, settings, ShotPlanner.Profile(request), .2f, 1,
            JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options), ShotPlanner.BuildMessages(request, structured: structured).Select(AiTextMessage.Capture).ToArray()) {
            ResponseSchema = structured ? ShotPlanner.ResponseSchemaFor(request) : null
        };
        var options = TextGenerationOptions.Captured(captured);
        if (!structured) { Assert.Null(options.ResponseFormat); Assert.Null(options.RawRepresentationFactory); }
        var report = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_SHOT_DRAFT_OUT");
        var stem = Path.Combine(report ?? Path.GetTempPath(), $"shot-draft-{model.Backend}-{(single ? "single" : "scene")}" +
            (model.Backend == AiBackend.OpenRouter && structured ? "-schema" : ""));
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        var clock = Stopwatch.StartNew(); var raw = new StringBuilder(); string? finish = null; var resolved = model.Model;
        await File.WriteAllTextAsync(stem + "-input.json", JsonSerializer.Serialize(new { request, captured.Messages, captured.ResponseSchema }, AtomicJsonFile.Options), timeout.Token);
        await File.WriteAllTextAsync(stem + "-status.json", JsonSerializer.Serialize(new { phase = "running", model = resolved, structured }, AtomicJsonFile.Options), timeout.Token);
        try {
            using var client = await registry.CreateAsync(model.Backend, model.Model, settings, timeout.Token);
            await foreach (var update in client.GetStreamingResponseAsync(captured.Messages.Select(m => m.ToMessage()), options, timeout.Token)) {
                raw.Append(update.Text); resolved = update.ModelId ?? resolved;
                if (update.FinishReason is { } reason) finish = reason.ToString();
            }
        } catch (Exception error) {
            await File.WriteAllTextAsync(stem + "-status.json", JsonSerializer.Serialize(new {
                phase = "failed", model = resolved, structured, seconds = clock.Elapsed.TotalSeconds,
                error = error is AiGenerationException ? error.Message : error.GetType().Name
            }, AtomicJsonFile.Options), CancellationToken.None);
            throw;
        }
        // Preserve neutral output even if JSON or timing validation fails.
        await File.WriteAllTextAsync(stem + "-raw.json", raw.ToString(), timeout.Token);
        var parsed = AiTextResults.Parse(captured, raw.ToString(), finish);
        var plan = parsed.Error is null ? parsed.Read<ShotPlanningResult>() : null;
        var exactDialogue = plan is not null && request.Script.Blocks.Where(b => b.Kind == ScriptBlockKind.Dialogue).Select(b => b.Text)
            .SequenceEqual(plan.Shots.SelectMany(s => s.Dialogue).Select(d => d.Text), StringComparer.Ordinal);
        await File.WriteAllTextAsync(stem + "-status.json", JsonSerializer.Serialize(new {
            phase = "finished", model = resolved, structured, finish, seconds = clock.Elapsed.TotalSeconds, error = parsed.Error,
            shots = plan?.Shots.Count, totalSeconds = plan?.Shots.Sum(s => s.Duration ?? 0), exactDialogue, uncoveredDialogue = plan?.UncoveredDialogue.Count,
            sourceNotes = plan?.SourceNotes, dialogueNotes = plan?.DialogueNotes, directionNotes = plan?.DirectionNotes, coverageNotes = plan?.CoverageNotes
        }, AtomicJsonFile.Options), timeout.Token);
        Assert.Equal("stop", finish); Assert.Null(parsed.Error); Assert.NotNull(plan);
        using var json = JsonDocument.Parse(raw.ToString()); // Plain providers must return bare, syntactically valid JSON too.
        Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
        Assert.Equal(4, json.RootElement.GetProperty("contractVersion").GetInt32());
        Assert.All(plan.Shots, shot => {
            Assert.Equal(request.SceneIds[0], shot.SceneId); Assert.InRange(shot.Duration!.Value, 1, 10);
            Assert.NotNull(shot.Direction); Assert.False(string.IsNullOrWhiteSpace(shot.Direction.Editorial.Timing));
        });
        Assert.Empty(plan.UncoveredDialogue); Assert.Empty(plan.SourceNotes); Assert.Empty(plan.DialogueNotes); Assert.Empty(plan.DirectionNotes);
        var dialogue = request.Script.Blocks.Where(b => b.Kind == ScriptBlockKind.Dialogue).Select(b => b.Text);
        Assert.Equal(dialogue, plan.Shots.SelectMany(s => s.Dialogue).Select(d => d.Text));
        if (single) { Assert.Single(plan.Shots); Assert.True(plan.Shots[0].Duration >= 8); }
        else { Assert.True(plan.Shots.Count >= 3); Assert.True(plan.Shots.Sum(s => s.Duration ?? 0) >= 24); }
    }
}
