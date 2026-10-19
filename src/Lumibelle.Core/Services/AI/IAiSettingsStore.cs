using lumibelle.Models;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.AI;

public interface IAiSettingsStore
{
    Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task<AiSettings> SaveAsync(AiSettings settings, string? replacementKey = null, bool removeKey = false, CancellationToken cancellationToken = default);
    Task<string?> ReadOpenRouterKeyAsync(CancellationToken cancellationToken = default);
    Task<string?> ReadVllmKeyAsync(string serverUrl, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    Task<AiSettings> SaveVllmAsync(AiSettings settings, string? replacementKey = null, bool removeKey = false, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This settings store does not support vLLM credentials.");
}

public interface IAiProviderRegistry
{
    Task<IChatClient> CreateAsync(AiBackend backend, string model, AiSettings settings, CancellationToken cancellationToken = default);
    Task<AiConnectionCheck> CheckAsync(AiBackend backend, AiSettings settings, string? replacementKey = null, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AiModelVerificationUpdate> VerifyComfyTextModelAsync(string model, AiSettings settings,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<AiModelVerificationUpdate> TestComfyTextModelAsync(string model, AiSettings settings,
        ComfyTextModelTestRequest request, CancellationToken cancellationToken = default);
}

public interface IScriptAssistant
{
    IAsyncEnumerable<AssistantUpdate> GenerateAsync(ScriptAssistantRequest request, CancellationToken cancellationToken = default);
}

public class AiGenerationException(string message) : Exception(message);
public sealed class AiCancellationException(string message, CancellationToken token) : OperationCanceledException(message, token);
