using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using lumibelle.Models;

namespace lumibelle.Services.AI;

public sealed partial class AiProviderRegistry
{
    private sealed record ImageProbeReply(string? Text, bool Rejected = false, string? Issue = null,
        string? FinishReason = null, int? TokenLimit = null, int? OutputTokens = null, int? ReasoningTokens = null, string? Provider = null);

    private async Task<OpenAiImageInputTest> ProbeOpenAiImagesAsync(AiJobContext context, AiModelTestJobRequest request,
        string? key, CancellationToken ct)
    {
        OpenAiImageInputTest Result(bool? supported, bool multiple, string detail) => new(context.Clock.GetUtcNow(), supported, multiple, detail);
        var single = await Sees(1);
        if (single.Supported != true) return Result(single.Supported, false, single.Detail);
        var batch = await Sees(2);
        return Result(true, batch.Supported == true, batch.Supported == true
            ? "The model read the synthetic images in order."
            : "Single-image input works. Multiple-image input was not confirmed. " + batch.Detail);

        async Task<(bool? Supported, string Detail)> Sees(int count)
        {
            string? exhaustedBudget = null;
            foreach (var colors in new[] { false, true })
            {
                ct.ThrowIfCancellationRequested();
                await context.ReportAsync(new(new(GenerationPhase.Finalizing, count == 1
                    ? "Checking image input…" : "Checking multiple-image input…")), true);
                var probe = ComfyTextCapabilities.CreateImageProbe(count, colors);
                var operation = $"model-test/image-{count}" + (colors ? "-colors" : "");
                var reply = await SendImageProbeAsync(context, request, key, operation, probe, ct);
                if (reply.Rejected) return (false, reply.Issue!);
                // A completed but truncated number-reading attempt may still be followed by the simpler color challenge.
                if (reply.FinishReason == "length" && reply.Issue is not null) { exhaustedBudget = reply.Issue; continue; }
                if (reply.Issue is not null) return (null, reply.Issue);
                if (probe.Matches(reply.Text)) return (true, "Image input confirmed by the model's answer.");
            }
            return (null, exhaustedBudget ?? "Image input was not confirmed: the model did not identify the numbers or colors. Check the server's vision configuration, then test again.");
        }
    }

    private async Task<ImageProbeReply> SendImageProbeAsync(AiJobContext context, AiModelTestJobRequest request, string? key,
        string operation, ComfyTextCapabilities.ImageProbe probe, CancellationToken ct)
    {
        // A capability probe is inference too. Preserve its submission and never replay an uncertain request.
        if (await context.ReadOperationAsync<StreamingTestReceipt>(operation, AiOperationArtifact.Request, ct) is not null)
            return new(null, Issue: "The image test was interrupted. Run a new model test explicitly.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.Settings.TimeoutSeconds, 5, 45)));
        using var http = clients.CreateClient(nameof(AiBackend.Vllm)); http.Timeout = Timeout.InfiniteTimeSpan;
        var content = probe.Images.Select(bytes => (object)new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(bytes) } })
            .Append(new { type = "text", text = probe.Question }).ToArray();
        var nativeVllm = OpenAiConnections.Find(request.Settings, request.Model.VllmUrl) is { } connection && OpenAiConnections.Kind(connection) == OpenAiServerKind.Vllm;
        // Compatible servers may count hidden reasoning against max_tokens. A 256-token probe can finish
        // without any answer even when both images were received. Keep native vLLM probes short with thinking off.
        var tokenLimit = nativeVllm ? 256 : Math.Clamp(request.Test.MaxOutputTokens, 2048, 8192);
        var body = new Dictionary<string, object> {
            ["model"] = request.Model.Model, ["messages"] = new[] { new { role = "user", content } },
            ["max_tokens"] = tokenLimit, ["temperature"] = .01, ["stream"] = false
        };
        if (nativeVllm)
            body["chat_template_kwargs"] = new { enable_thinking = false };
        using var message = new HttpRequestMessage(HttpMethod.Post, VllmConnection.NormalizeUrl(request.Model.VllmUrl!) + "/chat/completions")
            { Content = JsonContent.Create(body) };
        if (!string.IsNullOrWhiteSpace(key)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        await context.SaveOperationAsync(operation, AiOperationArtifact.Request, new StreamingTestReceipt(true), ct);
        ImageProbeReply outcome;
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var rejected = false;
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity)
                {
                    try {
                        using var error = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
                        var detail = TestString(TestObject(error.RootElement, "error"), "message") ?? "";
                        rejected = new[] { "does not support image", "doesn't support image", "not support multimodal", "not a multimodal model", "image input is not supported", "image inputs are not supported" }
                            .Any(term => detail.Contains(term, StringComparison.OrdinalIgnoreCase));
                    }
                    catch (JsonException) { }
                }
                outcome = new(null, rejected, rejected ? "The server explicitly rejected image input for this model."
                    : $"Image input could not be tested (HTTP {(int)response.StatusCode}). Check the connection and test again.");
            }
            else
            {
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
                var root = json.RootElement;
                var choice = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 ? choices[0] : default;
                var answer = TestString(TestObject(choice, "message"), "content");
                var finish = TestString(choice, "finish_reason");
                var usage = TestObject(root, "usage");
                var issue = !string.IsNullOrWhiteSpace(answer) ? null : finish == "length"
                    ? "The image test reached its output-token limit without returning an answer. Image support remains unverified; increase the test reply limit and test again."
                    : "The image test returned no readable answer. Image support remains unverified; check the provider's response and test again.";
                outcome = new(answer?[..Math.Min(answer.Length, 4096)], Issue: issue, FinishReason: finish, TokenLimit: tokenLimit,
                    OutputTokens: TestCount(usage, "completion_tokens"), ReasoningTokens: TestCount(TestObject(usage, "completion_tokens_details"), "reasoning_tokens"),
                    Provider: TestString(root, "provider"));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { outcome = new(null, Issue: "The image test timed out. Image support remains unverified."); }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException or InvalidOperationException)
        { outcome = new(null, Issue: "The image test was unavailable or unreadable. Image support remains unverified."); }
        await context.SaveOperationAsync(operation, AiOperationArtifact.Output, outcome, ct);
        return outcome;
    }
}
