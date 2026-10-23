using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public static class ScriptSequenceReviewRecovery
{
    // Dispatch through RecoverAsync, which can only reuse saved outputs. A missing
    // model operation stays stopped until the user explicitly continues it.
    public static async Task PrepareRecheckAsync(IAiJobStore store, Guid id, CancellationToken ct = default)
    {
        var job = (await store.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == id) ?? throw new WorkspaceStoreException("The review request is unavailable.");
        var request = AiTextJobHandler.Read(job, await store.ReadSnapshotAsync(id, ct));
        var result = await store.ReadArtifactAsync<AiTextJobResult>(id, AiJobArtifact.Result, ct);
        var draft = await store.ReadOperationAsync<AiTextJobResult>(id, "text", AiOperationArtifact.Output, ct);
        if (request.Profile != ScriptSequenceDrafting.Profile || result?.SequenceReview is null || draft is not { Complete: true, FinishReason: "stop" })
            throw new WorkspaceStoreException("There is no completed screenplay and saved automatic review to recheck.");
        await store.UpdateAsync(id, current => current.State == AiJobState.NeedsAttention && !current.CancelRequested && !current.RemoteUnconfirmed
            ? current with { Recovery = AiJobRecovery.CheckStatus }
            : throw new WorkspaceStoreException("This review cannot be rechecked in its current state."), ct);
    }
}
