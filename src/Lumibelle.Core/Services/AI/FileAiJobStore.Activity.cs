using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class FileAiJobStore
{
    public async Task<IReadOnlyList<AiJobHeader>> ChangeActivityAsync(IReadOnlyList<AiActivityObservation> observed,
        AiActivityChange change, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(change) || observed.Any(o => o.Id == Guid.Empty || o.Version < 0) || observed.Select(o => o.Id).Distinct().Count() != observed.Count)
            throw new WorkspaceStoreException("Invalid activity selection.");
        var versions = observed.ToDictionary(o => o.Id, o => o.Version);
        using var gate = await ProjectFiles.LockAsync(Index, ct);
        var document = await ReadIndexAsync(ct);
        var changed = new List<AiJobHeader>();
        var now = clock.GetUtcNow();
        var jobs = document.Jobs.Select(job =>
        {
            if (!versions.TryGetValue(job.Id, out var version) || version != job.Version) return job;
            var next = change switch
            {
                AiActivityChange.Read when job.Unread => job with { Unread = false },
                AiActivityChange.Clear when job.CanClearActivity && job.ActivityClearedUtc is null => job with { ActivityClearedUtc = now, Unread = false },
                AiActivityChange.Restore when job.ActivityClearedUtc is not null => job with { ActivityClearedUtc = null },
                _ => job
            };
            if (next == job) return job;
            next = next with { Version = job.Version + 1 };
            changed.Add(next);
            return next;
        }).ToArray();
        if (changed.Count > 0) await PublishAsync(document with { Jobs = jobs }, ct);
        return changed;
    }
}
