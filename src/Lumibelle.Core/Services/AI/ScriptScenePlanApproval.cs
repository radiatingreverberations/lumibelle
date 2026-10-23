using System.Security.Cryptography;
using System.Text;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed record ScriptBriefApproval(string ResponseFingerprint, DateTimeOffset ApprovedUtc);
public sealed record ScriptBriefCorrection(Guid Id, string OriginalFingerprint, string Raw, DateTimeOffset CreatedUtc);
public sealed record ScriptBriefResponse(AiTextJobResult Original, ScriptBriefCorrection? Correction)
{
    public string Raw => Correction?.Raw ?? Original.Raw;
    public string? FinishReason => Correction is null ? Original.FinishReason : "stop";
}

public static class ScriptScenePlanApproval
{
    public static string Fingerprint(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    public static async Task<ScriptBriefResponse?> ReadAsync(IAiJobStore store, Guid id, CancellationToken ct = default)
    {
        var original = await store.ReadOperationAsync<AiTextJobResult>(id, "scene-plan", AiOperationArtifact.Output, ct);
        if (original is null) return null;
        var corrections = await store.ReadArtifactAsync<ScriptBriefCorrection[]>(id, AiJobArtifact.SequenceBriefCorrections, ct) ?? [];
        return new(original, corrections.LastOrDefault(c => c.OriginalFingerprint == Fingerprint(original.Raw)));
    }

    public static async Task SaveCorrectionAsync(IAiJobStore store, Guid id, string raw, CancellationToken ct = default)
    {
        await CheckedJobAsync(store, id, ct);
        var inspected = ScriptScenePlanning.Inspect(raw);
        if (!inspected.CanUse) throw new WorkspaceStoreException(string.Join(" ", inspected.Errors));
        using var gate = await ProjectFiles.LockAsync(Path.Combine(store.DirectoryFor(id), "brief-correction-edit"), ct);
        var response = await ReadAsync(store, id, ct) ?? throw new WorkspaceStoreException("The original planning response is unavailable.");
        var corrections = await store.ReadArtifactAsync<ScriptBriefCorrection[]>(id, AiJobArtifact.SequenceBriefCorrections, ct) ?? [];
        if (corrections.LastOrDefault()?.Raw == raw) return;
        await store.WriteArtifactAsync(id, AiJobArtifact.SequenceBriefCorrections,
            corrections.Append(new ScriptBriefCorrection(Guid.NewGuid(), Fingerprint(response.Original.Raw), raw, DateTimeOffset.UtcNow)).ToArray(), ct);
    }

    private static async Task CheckedJobAsync(IAiJobStore store, Guid id, CancellationToken ct)
    {
        var job = (await store.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == id) ?? throw new WorkspaceStoreException("The planning request is unavailable.");
        if (job.State != AiJobState.NeedsAttention || job.CancelRequested || job.RemoteUnconfirmed) throw new WorkspaceStoreException("This planning request cannot be changed in its current state.");
        var request = AiTextJobHandler.Read(job, await store.ReadSnapshotAsync(id, ct));
        if (request.ScenePlanMessages is null) throw new WorkspaceStoreException("This request has no sequence planning stage.");
    }

    // Approval records the exact saved response; it never edits the model's output
    // or starts inference. The normal queue command explicitly continues afterward.
    public static async Task ApproveAsync(IAiJobStore store, Guid id, CancellationToken ct = default)
    {
        await CheckedJobAsync(store, id, ct);
        var response = await ReadAsync(store, id, ct);
        if (response is null || !response.Original.Complete || response.FinishReason != "stop" || !ScriptScenePlanning.Inspect(response.Raw).CanUse)
            throw new WorkspaceStoreException("Only a complete brief can be approved. Missing fields, malformed JSON and unfinished responses need correction.");
        await store.WriteArtifactAsync(id, AiJobArtifact.SequenceBriefApproval, new ScriptBriefApproval(Fingerprint(response.Raw), DateTimeOffset.UtcNow), ct);
        await store.UpdateAsync(id, current => current.State == AiJobState.NeedsAttention && !current.CancelRequested && !current.RemoteUnconfirmed
            ? current with { Recovery = AiJobRecovery.ContinueRequest }
            : throw new WorkspaceStoreException("The planning request changed before approval could be recorded."), ct);
    }
}
