using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiJobCoordinator
{
    public async Task RetryComfyAsync(Guid id, long expectedVersion, CancellationToken ct = default)
    {
        await _commands.WaitAsync(ct);
        try
        {
            // A finished worker may still be unwinding after publishing its state.
            // Record the command now; Tick waits for it to exit before reconciliation.
            await store.UpdateAsync(id, current =>
            {
                if (current.Version != expectedVersion) throw new WorkspaceConflictException();
                if (!ComfyQueuePolicy.CanRetry(current)) throw new WorkspaceStoreException("This ComfyUI request no longer needs reconnect/retry. Refresh its status.");
                return current with { ComfyControl = new(Guid.NewGuid(), current.ComfyControl?.PauseRequested == true),
                    Recovery = AiJobRecovery.CheckStatus, ActivityClearedUtc = null, FinishedUtc = null,
                    Error = "Retry requested. Reconnecting to the original ComfyUI server; existing work will be checked before anything is repeated." };
            }, ct);
            _reconciled.Remove(id);
            _progress.TryRemove(id, out _);
            // Persisting intent is enough: the execution owner performs all network
            // work. This also works when commands arrive through another UI process.
            await RefreshAsync(ct);
        }
        finally { _commands.Release(); Wake(); }
    }

    // In memory on purpose: a restart rechecks immediately, and absence is judged afresh.
    private int RecheckAttempts(Guid id) => _rechecks.TryGetValue(id, out var recheck) ? recheck.Attempts : 0;
    private void CountRecheck(Guid id) =>
        _rechecks.AddOrUpdate(id, _ => new(1), (_, recheck) => recheck with { Attempts = recheck.Attempts + 1 });
    private void ForgetSettledRechecks(AiQueueDocument document)
    {
        foreach (var id in _rechecks.Keys)
            if (!document.Jobs.Any(j => j.Id == id && j.RemoteUnconfirmed)) _rechecks.TryRemove(id, out _);
    }

    // Absence only counts while ComfyUI keeps answering. A failed connection proves
    // nothing, so it restarts the wait instead of letting work be written off as lost.
    private async Task<bool> RetireLostRemoteAsync(AiJobContext context, AiJobExecution execution)
    {
        var now = clock.GetUtcNow();
        if (context.MissingRemoteOperation is not { } operation)
        {
            _rechecks.AddOrUpdate(context.Job.Id, _ => new(), (_, recheck) => recheck with { MissingSince = null });
            return false;
        }
        var state = _rechecks.AddOrUpdate(context.Job.Id, _ => new(MissingSince: now), (_, recheck) => recheck with { MissingSince = recheck.MissingSince ?? now });
        if (now - state.MissingSince < ComfyQueuePolicy.LostRemoteGrace || execution.Submissions.Any(s => s.MayBeRunning && s.Operation != operation))
            return false;
        // Retiring only releases the reservation. The saved workflow is not replayed
        // unless the user asks; a later Check status still imports it if it reappears.
        await context.FinishRemoteAsync(operation, cancelled: true);
        logger.LogWarning("ComfyUI has no record of AI job {Job} after {Grace}; released its provider reservation", context.Job.Id, ComfyQueuePolicy.LostRemoteGrace);
        return true;
    }

    private async Task StopPausedObserversAsync(CancellationToken ct)
    {
        foreach (var job in (await store.ReadAsync(ct)).Jobs.Where(j => j.ComfyControl?.PauseRequested == true))
            if (_workers.TryGetValue(job.Id, out var worker)) worker.Cancellation.Cancel();
    }
}
