using System.Net.Http.Headers;
using System.Text.Json;
using lumibelle.Models;

namespace lumibelle.Services.AI;

public sealed partial class AiProviderRegistry
{
    private async Task<AiConnectionCheck> CheckOpenAiConnectionsAsync(AiSettings settings, string? replacementKey, CancellationToken ct)
    {
        var connections = OpenAiConnections.All(settings).Where(c => c.Enabled).ToArray();
        if (connections.Length == 0) return new(false, "Add or enable an OpenAI-compatible connection in Connections.", []);
        if (replacementKey is not null && connections.Length != 1)
            throw new AiGenerationException("Choose one connection before checking a replacement API key.");
        var results = await Task.WhenAll(connections.Select(c => CheckOpenAiConnectionAsync(c, settings, replacementKey, ct)));
        var models = results.SelectMany(r => r.Models).OrderBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(m => m.ServerUrl, StringComparer.Ordinal).ToArray();
        var statuses = results.SelectMany(r => r.Connections ?? []).ToArray();
        return new(models.Length > 0, connections.Length == 1 ? results[0].Message
            : $"{statuses.Count(s => s.Success)} of {connections.Length} connections available · {models.Length:N0} served models.", models)
            { Connections = statuses };
    }

    private async Task<AiConnectionCheck> CheckOpenAiConnectionAsync(OpenAiConnectionSettings connection, AiSettings settings, string? replacementKey, CancellationToken ct)
    {
        var server = VllmConnection.NormalizeUrl(connection.Url);
        var kind = OpenAiConnections.Kind(connection);
        AiConnectionCheck Result(bool success, string message, IReadOnlyList<AiModel>? models = null) =>
            new(success, message, models ?? []) { Connections = [new(server, connection.Name, success, message, kind)] };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var http = clients.CreateClient(nameof(AiBackend.Vllm));
            http.BaseAddress = new Uri(server + "/");
            var key = replacementKey is null ? await settingsStore.ReadVllmKeyAsync(server, timeout.Token) : replacementKey.Trim();
            if (!string.IsNullOrWhiteSpace(key)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await http.GetAsync("models", timeout.Token);
            if (!response.IsSuccessStatusCode) return Result(false, AiErrors.HttpStatus((int)response.StatusCode, connection.Name));
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var models = json.RootElement.GetProperty("data").EnumerateArray().Select(item =>
            {
                var id = item.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id)) throw new JsonException("Missing model ID.");
                long? context = PositiveInteger(item, "max_model_len") ?? PositiveInteger(item, "context_length");
                if (item.TryGetProperty("meta", out var meta)) context ??= PositiveInteger(meta, "n_ctx");
                bool? advertised = item.TryGetProperty("architecture", out var architecture) && architecture.ValueKind == JsonValueKind.Object &&
                    architecture.TryGetProperty("input_modalities", out var inputs) && inputs.ValueKind == JsonValueKind.Array
                    ? inputs.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == "image") : null;
                var tested = OpenAiImageInput.Latest(settings, server, id)?.Supported;
                var images = connection.SupportsImages ?? tested ?? advertised;
                return new AiModel(id, id, SupportsImages: images == true,
                    Catalog: new(ContextLength: connection.ContextLength ?? context) { SupportedParameters = OpenAiStructuredOutput.Parameters(item) }) {
                        ServerUrl = server, ImageInputKnown = images is not null,
                        SupportsJsonSchema = OpenAiStructuredOutput.Latest(settings, server, id)?.Supported ?? OpenAiStructuredOutput.Advertised(item)
                    };
            }).DistinctBy(model => model.Id).OrderBy(model => model.Name, StringComparer.Ordinal).ToArray();
            if (connection.Kind == OpenAiServerKind.Auto) kind = await DetectOpenAiKindAsync(http, ct);
            return Result(models.Length > 0, models.Length > 0
                ? $"Connected to {connection.Name}. {models.Length:N0} served models found; no text was generated."
                : $"Connected to {connection.Name}, but the server listed no models.", models);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Result(false, $"{connection.Name}: connection check timed out."); }
        catch (HttpRequestException) { return Result(false, $"Couldn’t reach {connection.Name}. Check its address and connection."); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { return Result(false, $"{connection.Name} returned an unexpected model catalog."); }
    }

    private static long? PositiveInteger(JsonElement item, string property) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var length) && length > 0 ? length : null;

    private static async Task<OpenAiServerKind> DetectOpenAiKindAsync(HttpClient http, CancellationToken ct)
    {
        async Task<JsonDocument?> Read(string path)
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(2));
            try {
                using var response = await http.GetAsync(path, probe.Token);
                return response.IsSuccessStatusCode ? await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(probe.Token), cancellationToken: probe.Token) : null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            catch (Exception e) when (e is HttpRequestException or JsonException) { return null; }
        }
        // Optional metadata must never turn a working models endpoint into a failed connection.
        using var status = await Read("status");
        if (status?.RootElement.ValueKind == JsonValueKind.Object && status.RootElement.TryGetProperty("structured_output", out var structured) &&
            structured.ValueKind == JsonValueKind.Object && structured.TryGetProperty("method", out var method) &&
            method.ValueKind == JsonValueKind.String && method.GetString() == "prompt_and_validate") return OpenAiServerKind.Strata;
        var prefix = http.BaseAddress!.AbsoluteUri.TrimEnd('/');
        if (prefix.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) prefix = prefix[..^3];
        // Keep probes on the configured origin and proxy prefix.
        using var schema = await Read(prefix + "/openapi.json");
        return schema?.RootElement.ValueKind == JsonValueKind.Object && schema.RootElement.TryGetProperty("info", out var info) &&
            info.ValueKind == JsonValueKind.Object && info.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String &&
            title.GetString()?.Contains("vllm", StringComparison.OrdinalIgnoreCase) == true ? OpenAiServerKind.Vllm : OpenAiServerKind.Standard;
    }
}
