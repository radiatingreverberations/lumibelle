using System.Net.Http.Headers;
using System.Text.Json;
using lumibelle.Models;

namespace lumibelle.Services.AI;

public sealed partial class AiProviderRegistry
{
    private async Task<AiConnectionCheck> CheckVllmAsync(HttpClient http, AiSettings settings, string? replacementKey, CancellationToken ct)
    {
        http.BaseAddress = new Uri(VllmConnection.NormalizeUrl(settings.VllmUrl) + "/");
        var key = replacementKey is null ? await settingsStore.ReadVllmKeyAsync(settings.VllmUrl, ct) : replacementKey.Trim();
        if (!string.IsNullOrWhiteSpace(key)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await http.GetAsync("models", ct);
        if (!response.IsSuccessStatusCode) return new(false, AiErrors.HttpStatus((int)response.StatusCode), []);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var models = json.RootElement.GetProperty("data").EnumerateArray().Select(item =>
        {
            var id = item.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id)) throw new JsonException("Missing model ID.");
            long? context = item.TryGetProperty("max_model_len", out var length) && length.ValueKind == JsonValueKind.Number &&
                length.TryGetInt64(out var value) && value > 0 ? value : null;
            // /models does not advertise image input capability. Do not guess from model names.
            return new AiModel(id, id, Catalog: new(ContextLength: context));
        }).DistinctBy(model => model.Id).OrderBy(model => model.Name, StringComparer.Ordinal).ToArray();
        return new(models.Length > 0, models.Length > 0
            ? $"Connected to vLLM. {models.Length:N0} served models found; no text was generated."
            : "Connected to vLLM, but the server listed no models.", models);
    }
}
