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
/// Manual check against a real ComfyUI: replays a saved composition job's references through both steps.
/// Set LUMIBELLE_LIVE_COMFY_JOB to the job directory (read only) and optionally LUMIBELLE_LIVE_OUT to a report path.
/// Uses the ComfyUI server and model the job was captured with, so it occupies that GPU for a few minutes.
/// </summary>
public sealed class LiveTwoStepCompositionTests
{
    [Fact]
    public async Task SavedCompositionRunsInTwoStepsOnARealComfyUI()
    {
        var directory = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_COMFY_JOB");
        if (string.IsNullOrEmpty(directory)) Assert.Skip("Set LUMIBELLE_LIVE_COMFY_JOB to a saved composition job directory.");
        var ct = TestContext.Current.CancellationToken;
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "request.json"), ct));
        var original = saved.RootElement.GetProperty("request").Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var composition = original.Payload<PromptCompositionRequest>();
        var attached = original.Messages.SelectMany(m => m.Parts).Where(p => p.Image is not null).Select(p => p.Image!).ToArray();
        var pictures = attached.Take(composition.Images.Count).ToArray();
        var frames = ResolvedReferences.For(composition.Shot).Videos.Where(v => v.Reel.EffectiveVisuals == ReelVisuals.RefMod)
            .SelectMany(v => v.Reel.RefMod!.Recipe.FrameHashes.Select((hash, i) => (Video: v, Hash: hash, Frame: i + 1)))
            .Select((f, n) => new RefModInspectionFrame(f.Video.Number, f.Frame, f.Video.Reel.Name, "", f.Hash, attached[pictures.Length + n])).ToArray();

        var model = original.Model;
        var side = ComfyTextSettings.BatchImageSide(model, original.Settings);
        var reply = original.Settings.MaxOutputTokens;
        using var http = new HttpClient { BaseAddress = new(AiProviderRegistry.NormalizeComfyUrl(model.ComfyUrl!) + "/"), Timeout = Timeout.InfiniteTimeSpan };
        var monitor = new ComfyExecutionMonitor(new ClientComfyWebSocketFactory(), TimeProvider.System);
        var report = new StringBuilder();

        // LUMIBELLE_LIVE_BRIEF reuses a saved brief to repeat only step two; LUMIBELLE_LIVE_VARIANT=thinking tries thinking mode.
        var thinking = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_VARIANT") == "thinking";
        async Task<string> Run(string step, IReadOnlyList<AiTextMessage> messages, int maxTokens, float temperature, bool think = false)
        {
            var input = ComfyTextVision.Capture(messages.Select(m => m.ToMessage()), nativeSystemPrompt: true);
            var size = ComfyTextCapacity.Estimate(model.Model, input, maxTokens, side)!;
            report.AppendLine($"== {step}: ≈{size.PromptTokens:N0} prompt tokens ({ComfyTextCapacity.Describe(size)}), reply limit {maxTokens:N0}");
            var uploaded = await ComfyTextVision.UploadAsync(http, model.Model, ComfyVisionInput.ImageBatch, input.Images, ct, side);
            var clock = Stopwatch.StartNew(); string? text = null; double tokens = 0;
            object Workflow(string client)
            {
                var workflow = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(ComfyTextVision.BuildWorkflow(model.Model, input.Transcript, maxTokens, temperature,
                    Random.Shared.NextInt64(1, long.MaxValue), client, uploaded, input.SystemPrompt)))!;
                workflow["prompt"]!["2"]!["inputs"]!["thinking"] = think;
                return workflow;
            }
            await foreach (var update in monitor.ExecuteAsync(http, Workflow, ComfyChatClient.ExecutionOptions, ct, ct))
            {
                if (update.Progress.Unit == "tokens" && update.Progress.Current is { } current) tokens = current;
                if (update.Complete && update.Job is { } job) ComfyChatClient.TryReadText(job, out text);
            }
            report.AppendLine($"   {clock.Elapsed.TotalSeconds:N0} s, {tokens:N0} tokens generated");
            report.AppendLine(text).AppendLine();
            return text ?? "";
        }

        try
        {
            var savedBrief = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_BRIEF") is { Length: > 0 } file ? await File.ReadAllTextAsync(file, ct) : null;
            var brief = PromptComposer.ReadBrief(savedBrief ?? await Run("Step 1 (visual brief)",
                PromptComposer.BuildBriefMessages(composition, pictures, frames).Select(AiTextMessage.Capture).ToArray(), PromptComposer.BriefTokens, .3f));
            Assert.NotNull(brief);
            var messages = PromptComposer.WithBrief(PromptComposer.BuildMessages(composition, [], [], visualBrief: true).Select(AiTextMessage.Capture).ToArray(), brief);
            var raw = await Run("Step 2 (composition)", messages, thinking ? 4096 : reply, .7f, thinking);
            var parsed = AiTextResults.Parse(original with { Messages = messages, BriefMessages = null }, raw, "stop");
            if (parsed.Error is not null) report.AppendLine("== Parse error: " + parsed.Error);
            else try
            {
                var note = ProductionPolicy.ValidatePrompt(parsed.Read<PromptCompositionResult>()!.Prompt, composition.Shot);
                report.AppendLine("== Valid H3 prompt; passes review" + (note is null ? "" : " · note: " + note));
            }
            catch (WorkspaceStoreException e) { report.AppendLine("== Parsed, review issue: " + e.Message); }
            Assert.False(string.IsNullOrWhiteSpace(raw));
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_OUT") is { Length: > 0 } output) await File.WriteAllTextAsync(output, report.ToString(), CancellationToken.None);
        }
    }
}
