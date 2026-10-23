using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Story;

// A batch chains ordinary script requests. Only validated additions are applied;
// the saved script itself is the continuity checkpoint for the next request.
public sealed class ScriptDraftWorkflow(FileScriptDraftBatchStore store, FileSourceStore sources, IScriptStore scripts,
    AiTextJobCapture capture, IAiSettingsStore settings, IAiJobStore jobs, AiJobCoordinator queue, IAssistantHistoryStore history)
{
    public event Action<Guid> Changed { add => store.Changed += value; remove => store.Changed -= value; }
    public Task<ScriptDraftBatches> LoadAsync(Guid project, CancellationToken ct = default) => store.LoadAsync(project, ct);

    public Task<ScriptDraftBatches> StartAsync(ScriptDocument script, Guid planId, long planRevision, TextModelReference model,
        TextModelSelectionSource selectionSource, Guid tab, Guid session, string instructions, string? projectContext,
        IReadOnlyList<AssetImageReference> images, CancellationToken ct = default, IReadOnlyList<ConversationMessage>? conversation = null) => store.UpdateAsync(script.ProjectId, async doc => {
        if (doc.Runs.Any(r => r.Active)) throw new WorkspaceStoreException("Pause or stop the existing screenplay batch before starting another.");
        if ((await jobs.ReadAsync(ct)).Jobs.Any(j => j.Kind == AiJobKind.ScriptAssistant && j.Target.ProjectId == script.ProjectId && j.LocksTarget))
            throw new WorkspaceStoreException("Wait for the current script request to finish before starting a batch.");
        var current = await scripts.LoadAsync(script.ProjectId, ct);
        if (current.Revision != script.Revision || ScriptStructure.ContextFingerprint(current) != ScriptStructure.ContextFingerprint(script)) throw new WorkspaceConflictException();
        var plan = ScriptAdaptation.Accepted(await sources.LoadAsync(script.ProjectId, ct));
        if (plan?.Id != planId || plan.ReviewRevision != planRevision) throw new WorkspaceStoreException("Refresh the accepted outline before starting a screenplay batch.");
        var added = ScriptAdaptation.InScript(script).ToHashSet();
        if (added.Any(s => s.RunId != planId || s.ReviewRevision != planRevision))
            throw new WorkspaceStoreException("Existing writing is linked to a different outline revision. Review that writing with single-sequence actions, or start with an empty script before batching the new outline.");
        var remaining = (plan.Reviewed ?? plan.Proposal)!.Acts.SelectMany((a, ai) => a.Scenes.Select((s, si) => new ScriptSourceScene(planId, planRevision, ai, si)))
            .Where(s => !added.Contains(s)).ToArray();
        if (remaining.Length == 0) throw new WorkspaceStoreException("Every outline sequence is already in the script.");
        // Append-only batches require existing linked writing to form a prefix. Filling
        // earlier gaps at the end would silently put the screenplay out of outline order.
        if (added.Any(s => s.RunId == planId && s.ReviewRevision == planRevision &&
            (s.Act > remaining[0].Act || s.Act == remaining[0].Act && s.Scene > remaining[0].Scene)))
            throw new WorkspaceStoreException("Some later sequences are already added. Fill the earlier gaps and put the script in outline order before drafting the remainder.");
        var run = new ScriptDraftBatch { PlanId = planId, PlanRevision = planRevision, Model = model, Settings = await settings.LoadAsync(ct),
            SelectionSource = selectionSource, TabId = tab, SessionId = session, Instructions = instructions, ProjectContext = projectContext,
            Images = images.ToArray(), Conversation = conversation?.ToArray() ?? [], Sequences = remaining };
        // Preflight and freeze the first request before publishing an active batch.
        run = await PrepareAsync(script.ProjectId, run, current, ct);
        return doc with { Runs = [.. doc.Runs, run] };
    }, ct);

    public Task<ScriptDraftBatches> PauseAsync(Guid project, Guid id, CancellationToken ct = default) => store.UpdateAsync(project, doc => {
        var run = Find(doc, id);
        return Task.FromResult(FileScriptDraftBatchStore.Replace(doc, run.State == ScriptDraftBatchState.Running ? run with { State = ScriptDraftBatchState.Paused } : run));
    }, ct);

    public async Task<ScriptDraftBatches> StopAsync(Guid project, Guid id, CancellationToken ct = default)
    {
        // Stop the chain first. A late provider response cannot add more writing.
        var doc = await store.UpdateAsync(project, doc => {
            var run = Find(doc, id);
            return Task.FromResult(FileScriptDraftBatchStore.Replace(doc, run.Active ? run with { State = ScriptDraftBatchState.Stopped } : run));
        }, ct);
        if (Find(doc, id).Pending is { } pending && (await jobs.ReadAsync(ct)).Jobs.Any(j => j.Id == pending.Id && j.LocksTarget))
            await queue.CancelAsync(pending.Id, ct);
        return doc;
    }

    public Task<ScriptDraftBatches> ResumeAsync(Guid project, Guid id, CancellationToken ct = default) => store.UpdateAsync(project, async doc => {
        var run = Find(doc, id);
        if (run.State != ScriptDraftBatchState.Paused) return doc;
        await PlanAsync(project, run, ct);
        if (run.Pending is { } pending) {
            var job = (await jobs.ReadAsync(ct)).Jobs.FirstOrDefault(j => j.Id == pending.Id);
            if (job?.RemoteUnconfirmed == true) throw new WorkspaceStoreException("Resolve the current provider request in AI activity before resuming the batch.");
            if (job?.State == AiJobState.Completed) {
                var script = await scripts.LoadAsync(project, ct);
                var savedRun = (await history.LoadAsync(project, ct)).Runs.Single(r => r.Id == job.Id);
                if (script.AppliedProposalIds.Contains(job.Id) || savedRun.Error is null && !savedRun.Rejected && ScriptAdaptation.Matches(script, savedRun, savedRun.Target))
                    run = await ConsumeAsync(project, run, ct);
                else run = run with { Pending = null }; // Explicit resume retries against the current saved script.
            } else if (job?.CanRetryCaptured == true) await queue.ResumeAsync(job.Id, ct);
            else if (job?.State is AiJobState.NeedsAttention or AiJobState.Cancelled) run = run with { Pending = null };
        }
        return FileScriptDraftBatchStore.Replace(doc, run with { State = run.Added == run.Sequences.Count ? ScriptDraftBatchState.Completed : ScriptDraftBatchState.Running, Error = null });
    }, ct);

    public Task<ScriptDraftBatches> AdvanceAsync(Guid project, CancellationToken ct = default) => store.UpdateAsync(project, async doc => {
        var run = doc.Runs.LastOrDefault(r => r.Active);
        if (run is null) return doc;
        try {
            if (run.Pending is { } pending) {
                var job = (await jobs.ReadAsync(ct)).Jobs.FirstOrDefault(j => j.Id == pending.Id);
                if (job is null) {
                    if (run.State == ScriptDraftBatchState.Running) {
                        await PlanAsync(project, run, ct);
                        await queue.EnqueueAsync(pending, ct);
                    }
                    return doc;
                }
                if (job.State is AiJobState.Waiting or AiJobState.Running) return doc;
                if (job.State != AiJobState.Completed) throw new WorkspaceStoreException(job.Error ?? "The sequence request stopped. Resume to retry it; completed sequences are kept.");
                run = await ConsumeAsync(project, run, ct);
            }
            if (run.Added == run.Sequences.Count) run = run with { State = ScriptDraftBatchState.Completed, Error = null };
            if (run.State != ScriptDraftBatchState.Running) return FileScriptDraftBatchStore.Replace(doc, run);
            await PlanAsync(project, run, ct);
            if ((await jobs.ReadAsync(ct)).Jobs.Any(j => j.Kind == AiJobKind.ScriptAssistant && j.Target.ProjectId == project && j.LocksTarget))
                return FileScriptDraftBatchStore.Replace(doc, run);
            run = await PrepareAsync(project, run, await scripts.LoadAsync(project, ct), ct);
        } catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or AiGenerationException) {
            run = run with { State = ScriptDraftBatchState.Paused, Error = e.Message };
        }
        return FileScriptDraftBatchStore.Replace(doc, run);
    }, ct);

    private async Task<ScriptDraftBatch> PrepareAsync(Guid project, ScriptDraftBatch run, ScriptDocument script, CancellationToken ct)
    {
        var sequence = run.Sequences[run.Added];
        if (ScriptAdaptation.InScript(script).Contains(sequence)) throw new WorkspaceStoreException("This batch sequence was added elsewhere. Stop the batch and draft the remaining sequences from the current script.");
        var request = new ScriptAssistantRequest(new() { SessionId = run.SessionId, DraftBatchId = run.Id, Operation = WritingOperation.Continue,
            SourceRevision = script.Revision, SourceFingerprint = ScriptStructure.ContextFingerprint(script),
            Target = ScriptStructure.Capture(script, ScriptScope.Document, null), Instructions = run.Instructions,
            Backend = run.Model.Backend, Model = run.Model.Model }, script, run.Conversation, run.Model) {
            OutlineSelection = new(run.PlanId, run.PlanRevision, sequence.Act, sequence.Scene, DraftScene: true),
            ProjectContext = run.ProjectContext, Images = run.Images
        };
        var pending = await capture.ScriptAsync(request.Run.Id, run.TabId, request, run.SelectionSource == TextModelSelectionSource.GlobalDefault, ct, run.Settings);
        var snapshot = pending.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)! with { SelectionSource = run.SelectionSource };
        pending = pending with { Snapshot = JsonSerializer.SerializeToElement(snapshot, AtomicJsonFile.Options),
            TargetName = $"Batch sequence {run.Added + 1}/{run.Sequences.Count} · {sequence.Act + 1}.{sequence.Scene + 1}" };
        return run with { Pending = pending, Jobs = [.. run.Jobs, pending.Id], Error = null };
    }

    private async Task<ScriptDraftBatch> ConsumeAsync(Guid project, ScriptDraftBatch batch, CancellationToken ct)
    {
        var run = (await history.LoadAsync(project, ct)).Runs.Single(r => r.Id == batch.Pending!.Id);
        var script = await scripts.LoadAsync(project, ct);
        if (!script.AppliedProposalIds.Contains(run.Id)) {
            var job = (await jobs.ReadAsync(ct)).Jobs.Single(j => j.Id == run.Id);
            var captured = AiTextJobHandler.Read(job, await jobs.ReadSnapshotAsync(run.Id, ct));
            if (captured.Profile == ScriptSequenceDrafting.Profile &&
                (await jobs.ReadArtifactAsync<AiTextJobResult>(run.Id, AiJobArtifact.Result, ct))?.SequenceReview?.State != ScriptSequenceReviewState.Passed)
                throw new WorkspaceStoreException("The sequence has not passed its automatic review. The saved screenplay has not been changed.");
            var plan = await PlanAsync(project, batch, ct);
            if (!ScriptAdaptation.Matches(script, run, run.Target))
                throw new WorkspaceStoreException("The script changed while this sequence was being drafted. Resume to draft it again using the saved script, or inspect its response in Requests.");
            var next = ScriptProposals.Apply(script, run, plan);
            await scripts.SaveAsync(next, script.Revision, batch.Added == 0 ? "Before drafting remaining sequences" : null, ct);
        }
        // A crash between script publication, history and batch checkpoints must not
        // append the same sequence again or call the provider for its replacement.
        if (!run.Applied && !run.Rejected) await history.SaveRunAsync(project, run with { Applied = true, AppliedTarget = run.Target }, ct);
        return batch with { Added = batch.Added + 1, Pending = null, Error = null };
    }
    private async Task<SourceAnalysisRun> PlanAsync(Guid project, ScriptDraftBatch run, CancellationToken ct)
    {
        var plan = ScriptAdaptation.Accepted(await sources.LoadAsync(project, ct));
        if (plan?.Id != run.PlanId || plan.ReviewRevision != run.PlanRevision)
            throw new WorkspaceStoreException("The accepted source outline changed. Stop this batch and review the existing writing's outline links before batching the new outline; added sequences are kept.");
        return plan;
    }
    private static ScriptDraftBatch Find(ScriptDraftBatches doc, Guid id) => doc.Runs.SingleOrDefault(r => r.Id == id)
        ?? throw new WorkspaceStoreException("The screenplay batch is unavailable.");
}

public sealed class ScriptDraftWorkflowWorker(IProjectStore projects, ScriptDraftWorkflow workflow, ILogger<ScriptDraftWorkflowWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken)) {
            try {
                foreach (var project in (await projects.ListAsync(stoppingToken)).Projects) {
                    try { await workflow.AdvanceAsync(project.Id, stoppingToken); }
                    catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or IOException) { logger.LogWarning(e, "Screenplay batch checkpoint unavailable for {Project}", project.Id); }
                }
            } catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or IOException) { logger.LogWarning(e, "Screenplay batch discovery unavailable"); }
        }
    }
}
