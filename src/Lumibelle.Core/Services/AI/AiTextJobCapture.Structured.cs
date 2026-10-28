using lumibelle.Models;

namespace lumibelle.Services.AI;

public sealed partial class AiTextJobCapture
{
    private async Task<bool> SupportsStructuredResponseAsync(TextModelReference model, AiSettings configured, CancellationToken ct)
    {
        if (model.Backend != AiBackend.Vllm || OpenAiConnections.Find(configured, model.VllmUrl) is not { } connection) return false;
        var kind = OpenAiConnections.Kind(connection);
        // Strata prompts and validates after inference instead of constraining a streaming response.
        if (kind == OpenAiServerKind.Strata) return false;
        if (kind == OpenAiServerKind.Vllm) return true;
        if (OpenAiStructuredOutput.Latest(configured, model.VllmUrl!, model.Model)?.Supported is { } tested) return tested;
        if (providers is null) return false;
        var check = await providers.CheckAsync(model.Backend, OpenAiConnections.Capture(model, configured), cancellationToken: ct);
        if (check.Connections?.Any(c => TextModelPolicy.SameVllmServer(c.Server, model.VllmUrl) && c.Kind == OpenAiServerKind.Strata) == true) return false;
        return TextModelPolicy.CatalogModel(model, check)?.SupportsJsonSchema == true;
    }
}
