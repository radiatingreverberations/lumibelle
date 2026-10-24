using lumibelle.Models;
using lumibelle.Services.Story;
using System.Text.Json;

namespace lumibelle.Services.AI;

public static class OpenAiConnections
{
    public static readonly Guid LegacyId = new("82458f86-b132-4f69-adb9-e7d0a8d45c62");
    public static IReadOnlyList<OpenAiConnectionSettings> All(AiSettings settings) => settings.OpenAiConnections ??
        [new() { Id = LegacyId, Name = "vLLM", Url = VllmConnection.NormalizeUrl(settings.VllmUrl),
            Concurrency = settings.VllmConcurrency, HasApiKey = settings.HasVllmKey, Kind = OpenAiServerKind.Vllm }];

    public static OpenAiConnectionSettings? Find(AiSettings settings, string? server) => server is null ? null :
        All(settings).FirstOrDefault(c => TextModelPolicy.SameVllmServer(c.Url, server));

    public static string Name(AiSettings settings, string? server) => Find(settings, server)?.Name ?? server ?? "OpenAI-compatible";
    public static OpenAiServerKind Kind(OpenAiConnectionSettings connection) => connection.Kind == OpenAiServerKind.Auto
        ? connection.DetectedKind ?? OpenAiServerKind.Standard : connection.Kind;

    public static AiSettings WithDiscovery(AiSettings settings, AiConnectionCheck check) => settings with
    {
        OpenAiConnections = All(settings).Select(c => c.Kind == OpenAiServerKind.Auto &&
            check.Connections?.FirstOrDefault(s => s.Success && TextModelPolicy.SameVllmServer(s.Server, c.Url)) is { } found
                ? c with { DetectedKind = found.Kind } : c).ToArray()
    };

    // Scope runtime settings to the captured endpoint. The remaining connections are configuration,
    // not fallback destinations; a queued request can never move to a different server.
    public static AiSettings Capture(TextModelReference model, AiSettings settings)
    {
        if (model.Backend != AiBackend.Vllm) return settings;
        var connection = Find(settings, model.VllmUrl) ?? throw new AiGenerationException("This OpenAI-compatible connection was removed. Choose an enabled connection.");
        if (!connection.Enabled) throw new AiGenerationException($"Enable {connection.Name} in Connections before using this model.");
        return Scope(settings, connection);
    }

    public static AiSettings Scope(AiSettings settings, OpenAiConnectionSettings connection) => settings with
    {
        VllmUrl = VllmConnection.NormalizeUrl(connection.Url), VllmConcurrency = connection.Concurrency,
        HasVllmKey = connection.HasApiKey, OpenAiConnections = [connection]
    };

    public static OpenAiConnectionSettings Normalize(OpenAiConnectionSettings connection) => connection with
    { Name = connection.Name.Trim(), Url = VllmConnection.NormalizeUrl(connection.Url) };

    public static string? CapturedServer(JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object) return null;
        if (snapshot.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object &&
            model.TryGetProperty("vllmUrl", out var endpoint) && endpoint.ValueKind == JsonValueKind.String)
            return VllmConnection.NormalizeUrl(endpoint.GetString()!);
        if (snapshot.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object &&
            settings.TryGetProperty("vllmUrl", out var legacy) && legacy.ValueKind == JsonValueKind.String)
            return VllmConnection.NormalizeUrl(legacy.GetString()!);
        return null;
    }

    public static void Validate(AiSettings settings)
    {
        if (settings.OpenAiConnections is not { } connections) return;
        if (connections.Count > 32 || connections.Any(c => c is null || c.Id == Guid.Empty || string.IsNullOrWhiteSpace(c.Name) ||
            c.Name.Trim().Length > 120 || c.Concurrency is < 1 or > 8 || !Enum.IsDefined(c.Kind) ||
            c.DetectedKind is { } kind && (!Enum.IsDefined(kind) || kind == OpenAiServerKind.Auto) || c.ContextLength is < 1 or > 100_000_000))
            throw new WorkspaceStoreException("Check each connection's name, server, concurrency (1–8), and capability overrides.");
        foreach (var connection in connections) VllmConnection.ValidateUrl(connection.Url);
        if (connections.Select(c => c.Id).Distinct().Count() != connections.Count ||
            connections.Select(c => VllmConnection.NormalizeUrl(c.Url)).Distinct(StringComparer.Ordinal).Count() != connections.Count)
            throw new WorkspaceStoreException("Each OpenAI-compatible connection needs its own server URL and identity.");
    }
}
