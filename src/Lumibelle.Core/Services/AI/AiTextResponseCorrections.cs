using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed record AiTextResponseCorrection(Guid Id, string RequestFingerprint, string OriginalHash,
    AiTextJobResult Result, DateTimeOffset CreatedUtc);

// Local corrections are separate from the immutable provider output and request.
// A correction becomes a reviewable proposal, never an application to the library.
public static class AiTextResponseCorrections
{
    public static async Task<AiTextJobResult?> ReadAsync(IAiJobStore store, AiJobHeader job, CancellationToken ct = default)
    {
        var original = await store.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result, ct);
        if (job.Kind != AiJobKind.AssetExtraction || original is null || job.CancelRequested || job.RemoteUnconfirmed ||
            job.State != AiJobState.Completed) return original;
        var corrections = await store.ReadArtifactAsync<AiTextResponseCorrection[]>(job.Id, AiJobArtifact.TextResponseCorrections, ct) ?? [];
        return corrections.LastOrDefault(c => c.RequestFingerprint == job.RequestFingerprint &&
            c.OriginalHash == AiTextRepairs.Hash(original.Raw) && c.Result is { Complete: true, Error: null })?.Result ?? original;
    }

    public static async Task SaveAsync(IAiJobStore store, Guid id, string json, bool edited, CancellationToken ct = default)
    {
        using var gate = await ProjectFiles.LockAsync(Path.Combine(store.DirectoryFor(id), "response-correction-edit"), ct);
        var job = (await store.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == id)
            ?? throw new WorkspaceStoreException("The saved request is unavailable.");
        if (job.Kind != AiJobKind.AssetExtraction || job.CancelRequested || job.RemoteUnconfirmed ||
            job.State is not (AiJobState.NeedsAttention or AiJobState.Completed) ||
            job.State == AiJobState.NeedsAttention && job.Recovery != AiJobRecovery.GenerateAgain)
            throw new WorkspaceStoreException("Only a finished asset response can be corrected. Stop or resolve the active request first.");
        var original = await store.ReadArtifactAsync<AiTextJobResult>(id, AiJobArtifact.Result, ct)
            ?? throw new WorkspaceStoreException("The original response is unavailable.");
        if (!original.Complete || !edited && original.FinishReason != "stop")
            throw new WorkspaceStoreException("This response did not finish normally. Edit a complete JSON array explicitly, or start a new request.");
        if (string.IsNullOrWhiteSpace(json) || json.Length > AiTextRepairs.MaximumResponseCharacters)
            throw new WorkspaceStoreException("Supply a complete JSON array of up to 500,000 characters. The original response is unchanged.");
        var request = AiTextJobHandler.Read(job, await store.ReadSnapshotAsync(id, ct));
        var corrections = await store.ReadArtifactAsync<AiTextResponseCorrection[]>(id, AiJobArtifact.TextResponseCorrections, ct) ?? [];
        var hash = AiTextRepairs.Hash(original.Raw);
        var previous = corrections.LastOrDefault(c => c.RequestFingerprint == job.RequestFingerprint && c.OriginalHash == hash);
        // Retry a failed acknowledgement using the same proposal IDs. A completed
        // review cannot acquire a different correction and overwrite author choices.
        if (job.State == AiJobState.Completed && previous?.Result.Raw != json)
            throw new WorkspaceStoreException("This request already has a reviewable proposal. Edit its review choices or start a new extraction.");
        if (previous?.Result.Raw != json) {
            var result = AiTextResults.Parse(request, json, "stop") with { LocallyCorrected = true };
            if (result.Error is { } error) throw new WorkspaceStoreException(error);
            await store.WriteArtifactAsync(id, AiJobArtifact.TextResponseCorrections,
                corrections.Append(new AiTextResponseCorrection(Guid.NewGuid(), job.RequestFingerprint, hash, result, DateTimeOffset.UtcNow)).ToArray(), ct);
        }
        await store.UpdateAsync(id, current => current.RequestFingerprint == job.RequestFingerprint &&
            current.State is AiJobState.NeedsAttention or AiJobState.Completed && !current.CancelRequested && !current.RemoteUnconfirmed
                ? current with { State = AiJobState.Completed, Error = null, Recovery = AiJobRecovery.None }
                : throw new WorkspaceStoreException("The request changed before the correction could be saved."), ct);
    }
}
