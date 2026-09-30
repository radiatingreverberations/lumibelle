using System.Diagnostics;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

/// <summary>
/// Manual comparison: composes a saved composition job's shot with hosted CLI models, once with the images attached and
/// once through a visual brief, to judge whether the two-step route loses quality. Uses the local Codex and Claude Code
/// sign-ins. Set LUMIBELLE_LIVE_COMFY_JOB, LUMIBELLE_LIVE_SETTINGS (ai-settings.json) and LUMIBELLE_LIVE_OUT.
/// </summary>
public sealed class LiveCompositionComparisonTests
{
    [Theory]
    [InlineData(AiBackend.Codex, "gpt-6.1-sol")]
    [InlineData(AiBackend.ClaudeCode, "opus")]
    public async Task HostedModelsComposeWithImagesAndFromABrief(AiBackend backend, string modelId)
    {
        var directory = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_COMFY_JOB");
        var settingsFile = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_SETTINGS");
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(settingsFile)) Assert.Skip("Set LUMIBELLE_LIVE_COMFY_JOB and LUMIBELLE_LIVE_SETTINGS.");
        var ct = TestContext.Current.CancellationToken;
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "request.json"), ct));
        var original = saved.RootElement.GetProperty("request").Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(settingsFile, ct));
        // Only the CLI provider settings are needed; the saved ComfyUI data is irrelevant here.
        var settings = new AiSettings { Codex = stored.RootElement.GetProperty("settings").GetProperty("codex").Deserialize<CodexSettings>(AtomicJsonFile.Options)!,
            ClaudeCode = stored.RootElement.GetProperty("settings").GetProperty("claudeCode").Deserialize<ClaudeCodeSettings>(AtomicJsonFile.Options)! };
        var composition = original.Payload<PromptCompositionRequest>();
        var attached = original.Messages.SelectMany(m => m.Parts).Where(p => p.Image is not null).Select(p => p.Image!).ToArray();
        var pictures = attached.Take(composition.Images.Count).ToArray();
        var frames = ResolvedReferences.For(composition.Shot).Videos.Where(v => v.Reel.EffectiveVisuals == ReelVisuals.RefMod)
            .SelectMany(v => v.Reel.RefMod!.Recipe.FrameHashes.Select((hash, i) => (Video: v, Hash: hash, Frame: i + 1)))
            .Select((f, n) => new RefModInspectionFrame(f.Video.Number, f.Frame, f.Video.Reel.Name, "", f.Hash, attached[pictures.Length + n])).ToArray();

        var registry = new AiProviderRegistry(new NoHttp(), new FakeAiSettingsStore(), TestComfy.Monitor(),
            new CodexClient(new CodexTransportFactory(), TimeProvider.System), new ClaudeCodeClient(new ClaudeCodeProcessFactory()));
        var report = new StringBuilder($"#### {backend} · {modelId}\n\n");
        async Task<string> Ask(string step, IEnumerable<ChatMessage> messages)
        {
            using var client = await registry.CreateAsync(backend, modelId, settings, ct);
            var clock = Stopwatch.StartNew();
            var text = (await client.GetResponseAsync(messages.ToList(), cancellationToken: ct)).Text;
            report.AppendLine($"== {step}: {clock.Elapsed.TotalSeconds:N0} s").AppendLine(text).AppendLine();
            return text;
        }
        void Judge(string raw, IReadOnlyList<ChatMessage> messages)
        {
            var request = original with { Model = new(backend, modelId, modelId), Messages = messages.Select(AiTextMessage.Capture).ToArray() };
            var parsed = AiTextResults.Parse(request, raw, "stop");
            if (parsed.Error is not null) { report.AppendLine("== Parse error: " + parsed.Error).AppendLine(); return; }
            var prompt = parsed.Read<PromptCompositionResult>()!.Prompt;
            try { var note = ProductionPolicy.ValidatePrompt(prompt, composition.Shot); report.AppendLine("== Valid H3 prompt; passes review" + (note is null ? "" : " · note: " + note)).AppendLine(); }
            catch (WorkspaceStoreException e) { report.AppendLine("== Parsed, review issue: " + e.Message).AppendLine(); }
        }

        try
        {
            var direct = PromptComposer.BuildMessages(composition, pictures, frames);
            Judge(await Ask("Single step, images attached", direct), direct);
            var brief = PromptComposer.ReadBrief(await Ask("Step 1 (visual brief)", PromptComposer.BuildBriefMessages(composition, pictures, frames)));
            Assert.NotNull(brief);
            var twoStep = PromptComposer.WithBrief(PromptComposer.BuildMessages(composition, [], [], visualBrief: true).Select(AiTextMessage.Capture).ToArray(), brief)
                .Select(m => m.ToMessage()).ToList();
            Judge(await Ask("Step 2 (composition from brief)", twoStep), twoStep);
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_OUT") is { Length: > 0 } output)
                await File.WriteAllTextAsync(Path.ChangeExtension(output, $".{backend}.txt"), report.ToString(), CancellationToken.None);
        }
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("The CLI comparison makes no HTTP requests.");
    }
}
