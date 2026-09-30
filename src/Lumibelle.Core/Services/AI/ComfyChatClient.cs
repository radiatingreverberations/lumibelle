using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.AI;

public sealed class ComfyChatClient(HttpClient http, string model, IComfyExecutionMonitor monitor,
    ComfyVisionInput visionInput = ComfyVisionInput.Disabled, IReadOnlyCollection<string>? systemPromptVersions = null,
    int batchImageSide = ComfyTextVision.BatchMaximumSide) : IChatClient, IProgressReportingChatClient
{
    internal static readonly ComfyExecutionOptions ExecutionOptions = new(
        new Dictionary<string, ComfyNodeStage>
        {
            ["1"] = new(GenerationPhase.Preparing, "Opening the text model…"),
            ["2"] = new(GenerationPhase.Preparing, "Preparing the text model…", "Generating text", "tokens"),
            ["3"] = new(GenerationPhase.Finalizing, "Finalizing the response…")
        },
        "ComfyUI rejected the text workflow. Refresh models and check that CLIPLoader, TextGenerate, and PreviewAny are available.",
        "ComfyUI could not execute the text workflow. Check model compatibility, GPU memory, and the ComfyUI console.",
        "Text generation timed out before ComfyUI accepted the job.",
        "Text generation timed out.",
        "ComfyUI returned an unreadable response. Check the server version and text workflow.",
        "The connection to ComfyUI failed during text generation.")
    {
        OutOfMemoryMessage = OutOfMemoryMessage
    };
    internal const string OutOfMemoryMessage = "ComfyUI ran out of GPU memory while processing this text request. Make the prompt smaller " +
        "(fewer references, a smaller image size or reduced script context) or lower the reply limit. The model test shows how large a prompt fits.";

    public static object BuildWorkflow(string model, string prompt, int maxTokens, float temperature, long seed, string? clientId = null,
        string? systemPrompt = null)
    {
        var inputs = new Dictionary<string, object>
        {
            ["clip"] = new object[] { "1", 0 }, ["prompt"] = prompt, ["max_length"] = maxTokens,
            ["sampling_mode"] = "on", ["sampling_mode.temperature"] = temperature,
            ["sampling_mode.top_k"] = 64, ["sampling_mode.top_p"] = 0.95,
            ["sampling_mode.min_p"] = 0.05, ["sampling_mode.repetition_penalty"] = 1.05,
            ["sampling_mode.seed"] = seed, ["sampling_mode.presence_penalty"] = 0.0,
            ["thinking"] = false, ["use_default_template"] = true
        };
        // Only honored with the default template, which the model test confirmed for this model and version.
        if (!string.IsNullOrEmpty(systemPrompt)) inputs["system_prompt"] = systemPrompt;
        return new
        {
            prompt = new Dictionary<string, object>
            {
                ["1"] = new { class_type = "CLIPLoader", inputs = new { clip_name = model, type = "stable_diffusion", device = "default" } },
                ["2"] = new { class_type = "TextGenerate", inputs },
                ["3"] = new { class_type = "PreviewAny", inputs = new { source = new object[] { "2", 0 } } }
            },
            client_id = clientId ?? Guid.NewGuid().ToString("D")
        };
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        string? responseId = null;
        await foreach (var update in GetStreamingResponseWithProgressAsync(messages, options, cancellationToken))
        {
            if (update.Response is null) continue;
            text.Append(update.Response.Text);
            responseId = update.Response.ResponseId ?? responseId;
        }
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text.ToString())) { ModelId = model, ResponseId = responseId };
    }

    public async IAsyncEnumerable<ProgressingChatUpdate> GetStreamingResponseWithProgressAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var input = ComfyTextVision.Capture(messages,
            await ComfyTextCapabilities.UseSystemPromptAsync(http, systemPromptVersions ?? [], cancellationToken));
        ComfyTextVision.ValidateCount(visionInput, input.Images.Count);
        if (input.Images.Count > 0) yield return new(Progress: new(GenerationPhase.Preparing, "Preparing ComfyUI vision inputs…"));
        var uploaded = await ComfyTextVision.UploadAsync(http, model, visionInput, input.Images, cancellationToken, batchImageSide);
        var maxTokens = options?.MaxOutputTokens ?? 2048;
        var temperature = options?.Temperature ?? 0.7f;
        var seed = Random.Shared.NextInt64(1, long.MaxValue);
        await foreach (var update in monitor.ExecuteAsync(http,
            clientId => ComfyTextVision.BuildWorkflow(model, input.Transcript, maxTokens, temperature, seed, clientId, uploaded, input.SystemPrompt),
            ExecutionOptions, cancellationToken, cancellationToken))
        {
            yield return new(Progress: update.Progress);
            if (!update.Complete || update.Job is not { } job) continue;
            var text = ReadText(job);
            if (string.IsNullOrWhiteSpace(text)) throw new AiGenerationException("ComfyUI returned an empty response.");
            yield return new(new ChatResponseUpdate(ChatRole.Assistant, text)
            {
                ModelId = model,
                ResponseId = update.PromptId,
                FinishReason = ChatFinishReason.Stop
            });
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in GetStreamingResponseWithProgressAsync(messages, options, cancellationToken))
            if (update.Response is not null) yield return update.Response;
    }

    internal static bool TryReadText(JsonElement job, out string text)
    {
        if (job.TryGetProperty("outputs", out var outputs) && outputs.TryGetProperty("3", out var preview) &&
            preview.TryGetProperty("text", out var texts))
        {
            text = texts.ValueKind == JsonValueKind.Array
                ? string.Join("\n", texts.EnumerateArray().Select(item => item.GetString()))
                : texts.GetString() ?? string.Empty;
            return true;
        }
        text = string.Empty;
        return false;
    }

    private static string ReadText(JsonElement job)
    {
        if (TryReadText(job, out var text)) return text;
        if (job.TryGetProperty("status", out var status) && status.TryGetProperty("completed", out var completed) && completed.ValueKind == JsonValueKind.True)
            throw new AiGenerationException("ComfyUI finished without the expected text output.");
        return string.Empty;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => http.Dispose();
}
