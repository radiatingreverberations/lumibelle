using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class FileAiJobStore
{
    private static bool BatchEquals(AiBatchDefinition? a, AiBatchDefinition? b) => a is null ? b is null :
        b is not null && a.RootId == b.RootId && a.Candidates.SequenceEqual(b.Candidates);
    private static void ValidateBatches(AiQueueDocument document)
    {
        foreach (var group in document.Jobs.Where(j => j.Batch is not null).GroupBy(j => j.Batch!.RootId))
        {
            var root = group.SingleOrDefault(j => j.Id == group.Key);
            if (root is null || root.Kind is not (AiJobKind.ImageCreate or AiJobKind.ImageEdit or AiJobKind.Video or AiJobKind.ReelVideo))
                throw new WorkspaceStoreException("The batch has no original image or video request.");
            foreach (var job in group)
                if (job.Kind != root.Kind || job.Target != root.Target || job.Backend != root.Backend ||
                    job.Batch!.Candidates is null || job.Batch.Candidates.Count == 0 ||
                    job.Batch.Candidates.Any(c => c is null || c.Id == Guid.Empty || c.Number < 1 || c.Seed < 0 || c.AppendCommandId == Guid.Empty) ||
                    !job.Batch.Candidates.Select(c => c.Number).SequenceEqual(job.Batch.Candidates.Select(c => c.Number).Order()))
                    throw new WorkspaceStoreException("The saved batch candidates or their target are invalid.");
            var all = group.SelectMany(j => j.Batch!.Candidates).ToArray();
            if (!all.Select(c => c.Number).Order().SequenceEqual(Enumerable.Range(1, all.Length)) ||
                all.Select(c => c.Id).Distinct().Count() != all.Length || all.Select(c => c.Seed).Distinct().Count() != all.Length ||
                all.Where(c => c.AppendCommandId is not null).Select(c => c.AppendCommandId).Distinct().Count() != all.Count(c => c.AppendCommandId is not null))
                throw new WorkspaceStoreException("Batch numbering, seeds or append identities are duplicated.");
        }
    }

    public async Task<AiBatchAppend> ExtendBatchAsync(Guid rootId, Guid commandId, Guid originTabId, CancellationToken ct = default)
    {
        if (rootId == Guid.Empty || commandId == Guid.Empty || originTabId == Guid.Empty) throw new WorkspaceStoreException("A batch extension needs exact request identities.");
        using var gate = await ProjectFiles.LockAsync(Index, ct);
        var document = await ReadIndexAsync(ct);
        var root = document.Jobs.SingleOrDefault(j => j.Id == rootId && j.Batch?.RootId == rootId)
            ?? throw new WorkspaceStoreException("The original batch is unavailable.");
        foreach (var job in document.Jobs)
            if (job.Batch?.Candidates.FirstOrDefault(c => c.AppendCommandId == commandId) is { } previous)
            {
                if (job.Batch.RootId != rootId) throw new WorkspaceStoreException("This append identity was already used for another batch.");
                return new(job, previous, job.Id != rootId);
            }
        var related = document.Jobs.Where(j => j.Batch?.RootId == rootId).ToArray();
        if (related.Any(j => j.RemoteUnconfirmed)) throw new WorkspaceStoreException("Check the uncertain remote request before adding another candidate.");
        var active = document.Jobs.FirstOrDefault(j => j.LocksTarget && AiJobLocks.Key(j) == AiJobLocks.Key(root));
        if (active is not null && (active.Batch?.RootId != rootId || active.State is not (AiJobState.Waiting or AiJobState.Running)))
            throw new WorkspaceStoreException("This target has another active request. View or cancel it before extending this batch.");
        var candidates = related.SelectMany(j => j.Batch!.Candidates).ToArray();
        var usedSeeds = candidates.Select(c => c.Seed).ToHashSet();
        long seed; do { seed = Random.Shared.NextInt64(1, long.MaxValue); } while (usedSeeds.Contains(seed));
        var candidate = new AiBatchCandidate(Guid.NewGuid(), checked(candidates.Max(c => c.Number) + 1), seed, commandId);
        if (active is not null)
        {
            var updated = active with { Batch = active.Batch! with { Candidates = [.. active.Batch!.Candidates, candidate] }, Version = active.Version + 1 };
            await PublishAsync(document with { Jobs = document.Jobs.Select(j => j.Id == updated.Id ? updated : j).ToArray() }, ct);
            return new(updated, candidate, updated.Id != rootId);
        }
        if (AiJobLocks.IsImage(root.Kind) && AiJobLocks.ActiveImageBatches(document.Jobs, root.Target.ProjectId, root.Target.AssetId) >= AiJobLocks.MaxActiveImageBatchesPerAsset)
            throw new WorkspaceStoreException(AiJobLocks.ImageLimitMessage);
        // A completed batch's continuation goes to the end of this provider's queue.
        // Only scheduling metadata changes; its original semantic input remains exact.
        var payload = await ReadSnapshotAsync(root, ct);
        var request = new AiJobSubmission(Guid.NewGuid(), root.Kind, root.Backend, root.Target, root.ProjectName, root.TargetName,
            originTabId, payload, new(rootId, [candidate]));
        var fingerprint = Fingerprint(request);
        var continuation = new AiJobHeader { Id = request.Id, Kind = root.Kind, Backend = root.Backend, Target = root.Target,
            ProjectName = root.ProjectName, TargetName = root.TargetName, OriginTabId = originTabId, RequestFingerprint = fingerprint,
            Batch = request.Batch, CreatedUtc = clock.GetUtcNow() };
        await AtomicJsonFile.WriteAsync(SnapshotPath(request.Id), new AiJobSnapshot(fingerprint, payload, request.Batch), ct);
        await PublishAsync(document with { Jobs = [.. document.Jobs, continuation] }, ct);
        return new(continuation, candidate, true);
    }
}
