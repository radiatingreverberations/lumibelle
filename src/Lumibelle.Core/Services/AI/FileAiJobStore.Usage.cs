using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class FileAiJobStore
{
    public async Task WriteObservedUsageAsync(Guid id, Guid lease, OpenRouterRequestUsage usage, CancellationToken ct = default)
    {
        using var gate = await ProjectFiles.LockAsync(Index, ct);
        var job = (await ReadIndexAsync(ct)).Jobs.SingleOrDefault(j => j.Id == id);
        // Accept an already observed billing receipt during cancellation, only from the current worker.
        if (lease == Guid.Empty || job is not { Backend: AiBackend.OpenRouter, State: AiJobState.Running } || job.LeaseId != lease)
            throw new AiJobLeaseException();
        var path = ArtifactPath(id, AiJobArtifact.Result);
        using var artifactGate = await ProjectFiles.LockAsync(path, ct);
        var result = await AtomicJsonFile.ReadAsync<AiTextJobResult>(path, ct) ?? new("");
        await AtomicJsonFile.WriteAsync(path, result with { OpenRouterUsage = OpenRouterUsageParser.Merge(result.OpenRouterUsage, usage) }, ct);
    }
}
