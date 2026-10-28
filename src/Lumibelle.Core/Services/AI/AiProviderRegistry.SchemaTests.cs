using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using lumibelle.Models;

namespace lumibelle.Services.AI;

public sealed partial class AiProviderRegistry
{
    private sealed record SchemaProbeReply(OpenAiStructuredOutputTest Test, int TokenLimit, string? Answer = null,
        string? FinishReason = null, int? OutputTokens = null, int? ReasoningTokens = null, string? Provider = null);

    private async Task<OpenAiStructuredOutputTest> ProbeOpenAiSchemaAsync(AiJobContext context, AiModelTestJobRequest request, string? key, CancellationToken ct)
    {
        const string operation = "model-test/schema";
        OpenAiStructuredOutputTest Result(bool? supported, string detail) => new(context.Clock.GetUtcNow(), supported, detail);
        if (await context.ReadOperationAsync<JsonElement>(operation, AiOperationArtifact.Request, ct) is { ValueKind: JsonValueKind.Object })
            return Result(null, "The JSON-schema test was interrupted. Run a new model test explicitly.");
        await context.ReportAsync(new(new(GenerationPhase.Finalizing, "Checking JSON-schema output…")), true);
        var code = Guid.NewGuid().ToString("N")[..12];
        var schema = new {
            type = "object", properties = new {
                code = new { type = "string", @enum = new[] { code } }, count = new { type = "integer", @enum = new[] { 2 } }
            }, required = new[] { "code", "count" }, additionalProperties = false
        };
        var tokenLimit = Math.Clamp(request.Test.MaxOutputTokens, 2048, 8192);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.Settings.TimeoutSeconds, 5, 45)));
        using var http = clients.CreateClient(nameof(AiBackend.Vllm)); http.Timeout = Timeout.InfiniteTimeSpan;
        using var message = new HttpRequestMessage(HttpMethod.Post, VllmConnection.NormalizeUrl(request.Model.VllmUrl!) + "/chat/completions") {
            Content = JsonContent.Create(new {
                model = request.Model.Model,
                // The random code is supplied only through response_format. Ignoring that field cannot pass this check.
                messages = new[] { new { role = "user", content = "Reply with the plain word ready. Do not write a JSON object." } },
                max_tokens = tokenLimit, temperature = .01, stream = false,
                response_format = new { type = "json_schema", json_schema = new { name = "lumibelle_capability", strict = true, schema } }
            })
        };
        if (!string.IsNullOrWhiteSpace(key)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        await context.SaveOperationAsync(operation, AiOperationArtifact.Request, new { Started = true, TokenLimit = tokenLimit, Schema = schema }, ct);
        SchemaProbeReply outcome;
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var rejected = false;
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
                {
                    try {
                        using var error = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
                        var detail = TestString(TestObject(error.RootElement, "error"), "message") ?? "";
                        rejected = new[] { "json_schema", "response_format", "structured output" }.Any(s => detail.Contains(s, StringComparison.OrdinalIgnoreCase)) &&
                            new[] { "not support", "unsupported", "not allowed" }.Any(s => detail.Contains(s, StringComparison.OrdinalIgnoreCase));
                    } catch (JsonException) { }
                }
                outcome = new(Result(rejected ? false : null, rejected ? "The server explicitly rejected JSON-schema output for this model."
                    : $"JSON-schema output could not be tested (HTTP {(int)response.StatusCode})."), tokenLimit);
            }
            else
            {
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
                var root = json.RootElement;
                var choice = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 ? choices[0] : default;
                var answer = TestString(TestObject(choice, "message"), "content");
                var finish = TestString(choice, "finish_reason");
                var matches = false;
                if (answer is not null && finish == "stop")
                {
                    try {
                        using var parsed = JsonDocument.Parse(answer);
                        matches = parsed.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.EnumerateObject().Count() == 2 &&
                            TestString(parsed.RootElement, "code") == code && TestCount(parsed.RootElement, "count") == 2;
                    } catch (JsonException) { }
                }
                var usage = TestObject(root, "usage");
                outcome = new(Result(matches ? true : null, matches ? "The model returned the exact object constrained by the response schema."
                    : finish == "length" ? "The JSON-schema test reached its output-token limit. Increase the test reply limit and test again."
                    : "The API accepted the schema, but its answer did not confirm JSON-schema output."), tokenLimit,
                    answer?[..Math.Min(answer.Length, 512)], finish, TestCount(usage, "completion_tokens"),
                    TestCount(TestObject(usage, "completion_tokens_details"), "reasoning_tokens"), TestString(root, "provider"));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { outcome = new(Result(null, "The JSON-schema test timed out; support remains unverified."), tokenLimit); }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException or InvalidOperationException)
        { outcome = new(Result(null, "The JSON-schema test was unavailable or unreadable; support remains unverified."), tokenLimit); }
        await context.SaveOperationAsync(operation, AiOperationArtifact.Output, outcome, ct);
        return outcome.Test;
    }
}
