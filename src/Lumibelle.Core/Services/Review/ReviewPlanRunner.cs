using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Review;

/// <summary>
/// Runs approved plans. Edits go through the same store calls the studios use; renders are ordinary queue jobs. A step runs once
/// what it depends on is done: a shot's edits before its render, and a frame from another shot's new take only after the director
/// has picked that take. Nothing here runs for a plan that is not approved.
/// </summary>
public sealed class ReviewPlanRunner(ReviewPlanStore store, ProjectReview review, IProductionStore production, IShotStore shots, ICutStore cuts, IAssetStore assets,
    IReviewRenderQueue renders, IProjectStore projects, TimeProvider clock, ILogger<ReviewPlanRunner> logger, IGenerationSetupStore? setups = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Projects that may have an approved plan: found once at startup, then kept current by the store's change event,
    // so a queue change does not open every project in the library.
    // Each project's count of plan changes seen, so a pass forgets a project only if nothing changed since it read the plans.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, long> _watched = new();
    private int _listening;
    /// <summary>Called after a pass reads a project's plans. Tests use it to change a plan in between.</summary>
    internal Action<Guid>? Listed { get; set; }
    /// <summary>Called before a pass reads a project's plans. Tests use it to make that read fail.</summary>
    internal Action<Guid>? Reading { get; set; }
    /// <summary>Called before a change a step made is recorded, with the step. Tests use it to make that record fail.</summary>
    internal Action<Guid>? Recording { get; set; }

    private void Watch(Guid project) => _watched.AddOrUpdate(project, 1, (_, seen) => seen + 1);

    /// <summary>Advances the approved plans of every project, picking up whatever a restart interrupted. Called once at startup.</summary>
    public async Task AdvanceAllAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _listening, 1) == 0) store.Changed += (project, _) => Watch(project);
        foreach (var project in (await projects.ListAsync(ct)).Projects) Watch(project.Id);
        await AdvanceWatchedAsync(settledOnly: false, ct);
    }

    /// <summary>
    /// Advances the approved plans of projects known to have had one. Called whenever the queue changes, which is often while a
    /// render reports progress, so with <paramref name="settledOnly"/> a plan is only read in full once one of its renders has left the queue,
    /// or when it has a step that could run now: one approved or released without being advanced, as when the request that approved it ended first.
    /// </summary>
    public async Task AdvanceWatchedAsync(bool settledOnly = true, CancellationToken ct = default)
    {
        foreach (var project in _watched.Keys)
        {
            if (!_watched.TryGetValue(project, out var seen)) continue;
            IReadOnlyList<ReviewPlan> plans;
            // A read that fails says nothing about whether the project still has a running plan, so it stays watched and is
            // read again on the next pass.
            try { Reading?.Invoke(project); plans = await store.ListAsync(project, ct); }
            catch (WorkspaceStoreException e) { logger.LogWarning(e, "Review plans of project {Project} could not be read. They are read again on the next pass.", project); continue; }
            Listed?.Invoke(project);
            // A plan approved after the read keeps the project watched.
            if (!plans.Any(p => p.Active)) { _watched.TryRemove(KeyValuePair.Create(project, seen)); continue; }
            foreach (var plan in plans.Where(p => p.Active))
                try
                {
                    // A plan is read in full when a render left the queue, a step could run now, or nothing is left open but
                    // the plan was never marked finished, as when the pass that ran its last step was cancelled.
                    // An interrupted pick is work too: the pass that finishes it may come long after the restart.
                    if (settledOnly && !plan.Steps.Any(s => s.State == ReviewStepState.Running && !Queued(s.JobId)) && !plan.Steps.Any(s => s.State == ReviewStepState.Pick && s.PickingChoice is not null) &&
                        !plan.Steps.Any(s => s.State == ReviewStepState.Waiting && Blocker(plan, s) is not { Open: true }) && plan.Steps.Any(s => s.Open)) continue;
                    await AdvanceAsync(project, plan.Id, ct);
                }
                catch (Exception e) when (e is WorkspaceStoreException or IOException) { logger.LogWarning(e, "Review plan {Plan} could not be advanced.", plan.Id); }
        }
    }

    /// <summary>Does everything an approved plan can do now, and returns it as it then stands.</summary>
    public async Task<ReviewPlan> AdvanceAsync(Guid projectId, Guid planId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Each pass handles one step and reads the project again, so a later step sees what the earlier one changed.
            var conflicts = new Dictionary<Guid, int>();
            for (var pass = 0; pass < 2000; pass++)
            {
                var plan = await store.GetAsync(projectId, planId, ct);
                if (!plan.Active) return plan;
                if (await ResumePicksAsync(plan, projectId, ct)) continue;
                var snapshot = await review.LoadAsync(projectId, ct);
                if (await SettleRenderAsync(snapshot, plan, ct) || await RunNextAsync(snapshot, plan, conflicts, ct)) continue;
                if (!plan.Steps.Any(s => s.Open))
                    plan = await store.UpdateAsync(projectId, planId, p => p.Active && !p.Steps.Any(s => s.Open) ? p with { State = ReviewPlanState.Finished, Log = [.. p.Log, new(clock.GetUtcNow(), ReviewPlans.Director, "Every step has run.")] } : p, ct: ct);
                return plan;
            }
            return await store.GetAsync(projectId, planId, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stops a plan: nothing more of it runs, and its waiting and running renders are cancelled. What it already changed stays.</summary>
    public async Task<ReviewPlan> StopAsync(Guid projectId, Guid planId, string who, CancellationToken ct = default)
    {
        // Held across the change, so a pass or pick in progress finishes before the renders are collected.
        await _gate.WaitAsync(ct);
        IReadOnlyList<Guid> queued; ReviewPlan stopped;
        try
        {
            // A stopped step keeps its request, so stopping again cancels whatever an earlier stop could not.
            queued = (await store.GetAsync(projectId, planId, ct)).Steps
                .Where(s => s.State == ReviewStepState.Running || s.State == ReviewStepState.Skipped && Queued(s.JobId)).Select(s => s.JobId).OfType<Guid>().ToArray();
            stopped = await store.UpdateAsync(projectId, planId, plan => ReviewPlans.Stopped(plan, who, clock.GetUtcNow()), ct: ct);
        }
        finally { _gate.Release(); }
        // The plan is already stopped, so the cancellations run to the end even if the caller goes away.
        var failed = 0;
        foreach (var job in queued)
            try { await renders.CancelAsync(job, CancellationToken.None); }
            catch (Exception e) when (e is not OperationCanceledException) { failed++; logger.LogWarning(e, "Request {Job} of review plan {Plan} could not be cancelled.", job, planId); }
        if (failed > 0) throw new WorkspaceStoreException($"The plan is stopped, but {failed} of its {(failed == 1 ? "request" : "requests")} could not be cancelled. Stop the plan again, or cancel {(failed == 1 ? "it" : "them")} in AI activity.");
        return stopped;
    }

    /// <summary>A stopped plan with a request an earlier stop could not cancel.</summary>
    public bool Uncancelled(ReviewPlan plan) => plan.State == ReviewPlanState.Stopped && plan.Steps.Any(s => s.State == ReviewStepState.Skipped && Queued(s.JobId));

    private bool Queued(Guid? job) => job is { } id && renders.State(id) is AiJobState.Waiting or AiJobState.Running or AiJobState.NeedsAttention;

    /// <summary>The director's choice among the takes or images a step produced.</summary>
    public Task<ReviewPlan> PickAsync(Guid projectId, Guid planId, Guid stepId, Guid choice, string who, CancellationToken ct = default) =>
        PickAllAsync(projectId, planId, [(stepId, choice)], who, ct);

    /// <summary>
    /// Several picks at once. Every one is checked first, including that its take or image is still there, so a refusal leaves
    /// every selection as it was; the plan goes on only once all of them are made.
    /// </summary>
    public async Task<ReviewPlan> PickAllAsync(Guid projectId, Guid planId, IReadOnlyList<(Guid Step, Guid Choice)> picks, string who, CancellationToken ct = default, bool recommended = false)
    {
        if (picks.Count == 0) throw new WorkspaceStoreException("Name at least one step and what was chosen for it.");
        if (picks.Select(p => p.Step).Distinct().Count() != picks.Count) throw new WorkspaceStoreException("Name each step or shot once, with the one take or image chosen for it.");
        // Under the gate, as stopping and recommending are: a plan stopped, or a recommendation changed, while the picks were
        // on their way gets nothing selected.
        await _gate.WaitAsync(ct);
        try
        {
            var plan = await store.GetAsync(projectId, planId, ct);
            var steps = new List<(ReviewPlanStep Step, Guid Choice)>();
            foreach (var (stepId, choice) in picks)
            {
                var step = plan.Step(stepId) ?? throw new WorkspaceStoreException("That step is not in this plan.");
                if (!plan.Active || step.State != ReviewStepState.Pick) throw new WorkspaceStoreException("This step has nothing waiting for a pick.");
                if (!step.Produced.Contains(choice)) throw new WorkspaceStoreException("Pick one of the takes or images this step produced.");
                if (recommended && step.RecommendedTakeId != choice) throw new WorkspaceStoreException("A recommendation changed after the plan was shown. Look at the picks again before accepting them.");
                steps.Add((step, choice));
            }
            // What is about to be picked is recorded first, with the take each shot had selected, so a restart part way through
            // finishes the picks instead of leaving takes selected for steps that still wait.
            var current = await shots.LoadAsync(projectId, ct);
            var intents = steps.ToDictionary(p => p.Step.Id, p => (p.Choice, From: p.Step.Kind == ReviewStepKind.EditImage ? null : current.Shots.FirstOrDefault(s => s.Id == p.Step.ShotId)?.SelectedTakeId));
            plan = await store.UpdateAsync(projectId, planId, p => p.Active && intents.Keys.All(id => p.Step(id)?.State == ReviewStepState.Pick)
                ? p with { Steps = p.Steps.Select(s => intents.TryGetValue(s.Id, out var intent) ? s with { PickingChoice = intent.Choice, PickingFrom = intent.From } : s).ToArray() }
                : throw new WorkspaceStoreException("This step has nothing waiting for a pick."), ct: CancellationToken.None);
            await CommitPicksAsync(projectId, planId, steps.Select(p => (plan.Step(p.Step.Id)!, p.Choice)).ToArray(), who);
        }
        finally { _gate.Release(); }
        return await AdvanceAsync(projectId, planId, ct);
    }

    // Writes picks recorded in their steps: the takes are selected, the images kept, the picks recorded, and only then are the
    // other candidates moved to Trash. Everything is checked before anything is written. A failure before the picks are
    // recorded puts the takes and images back where nobody saved since, clears the record, and says what is left.
    private async Task CommitPicksAsync(Guid projectId, Guid planId, IReadOnlyList<(ReviewPlanStep Step, Guid Choice)> steps, string who)
    {
        var commit = CancellationToken.None;
        var takePicks = steps.Where(s => s.Step.Kind != ReviewStepKind.EditImage).ToArray();
        var imagePicks = steps.Where(s => s.Step.Kind == ReviewStepKind.EditImage).ToArray();
        var latest = await shots.LoadAsync(projectId, commit);
        var library = await assets.LoadAsync(projectId, commit);
        var chosen = imagePicks.Select(p => new AssetImageReference(p.Step.AssetId!.Value, p.Choice)).ToHashSet();
        var before = takePicks.ToDictionary(p => p.Step.ShotId!.Value, p => p.Step.PickingFrom);
        try
        {
            foreach (var (step, choice) in takePicks)
            {
                if (latest.Shots.FirstOrDefault(s => s.Id == step.ShotId) is not { } shot || !latest.Takes.Any(t => t.Id == choice && t.ShotId == step.ShotId))
                    throw new WorkspaceStoreException("A chosen take is no longer in the project. It may be in Trash. Nothing was picked.");
                // The shot still has the take recorded as its earlier one, or this pick's take after an interrupted run. Anything
                // else is another editor's choice since, which the batch neither overwrites nor would undo to the earlier take.
                if (shot.SelectedTakeId != step.PickingFrom && shot.SelectedTakeId != choice)
                    throw new StaleSelectionException();
            }
            if (chosen.Any(c => library.Assets.FirstOrDefault(a => a.Id == c.AssetId)?.Images.Any(i => i.Id == c.ImageId) != true))
                throw new WorkspaceStoreException("A chosen image is no longer in Assets. It may be in Trash. Nothing was picked.");
        }
        catch (StaleSelectionException) { await ForgetPicksAsync(projectId, planId, steps); throw new WorkspaceStoreException("Another take was selected for one of these shots meanwhile. Nothing was picked; look at the picks again."); }
        catch (WorkspaceStoreException)
        {
            // An interrupted run may have selected takes already; they are put back before the record of what was there is cleared.
            var interrupted = takePicks.Where(p => latest.Shots.First(s => s.Id == p.Step.ShotId).SelectedTakeId == p.Choice && p.Step.PickingFrom != p.Choice).ToArray();
            if (interrupted.Length > 0 && !await RestoreSelectionsAsync(projectId, interrupted.ToDictionary(p => p.Step.ShotId!.Value, p => p.Step.PickingFrom), interrupted, latest.Revision))
            {
                logger.LogWarning("Takes selected by interrupted picks in plan {Plan} could not be put back; their record is kept for the next pass.", planId);
                throw;
            }
            await ForgetPicksAsync(projectId, planId, steps);
            throw;
        }
        var kept = library.Assets.SelectMany(a => a.Images.Where(i => i.PendingReview == true && chosen.Contains(new(a.Id, i.Id))).Select(i => new AssetImageReference(a.Id, i.Id))).ToHashSet();
        // The revision each save left, so an undo only touches a store nobody saved to since.
        long? selected = null, keptSaved = null;
        try
        {
            if (takePicks.Length > 0)
            {
                foreach (var (step, choice) in takePicks) latest.Shots.Single(s => s.Id == step.ShotId).SelectedTakeId = choice;
                selected = (await shots.SaveAsync(projectId, latest.Shots, latest.Revision, "Choose production take", commit)).Revision;
            }
            if (kept.Count > 0)
                keptSaved = (await assets.SaveAsync(library with { Assets = library.Assets.Select(a => a with { Images = a.Images.Select(i => kept.Contains(new(a.Id, i.Id)) ? i with { PendingReview = false } : i).ToList() }).ToList() }, library.Revision, commit)).Revision;
            await store.UpdateAsync(projectId, planId, p => steps.Aggregate(p, (current, pick) => pick.Step.Kind == ReviewStepKind.EditImage
                ? Picked(current, pick.Step.Id, s => s with { State = ReviewStepState.Done, NewImageId = pick.Choice, FinishedUtc = clock.GetUtcNow(), PickingChoice = null, PickingFrom = null }, who, "Picked an image.")
                : Picked(current, pick.Step.Id, s => s with { State = ReviewStepState.Done, TakeId = pick.Choice, FinishedUtc = clock.GetUtcNow(), PickingChoice = null, PickingFrom = null }, who, "Picked a take.")), ct: commit);
        }
        catch (Exception e) when (e is WorkspaceStoreException or WorkspaceConflictException or IOException or UnauthorizedAccessException)
        {
            var takesBack = selected is not { } shotsRevision || await RestoreSelectionsAsync(projectId, before, takePicks, shotsRevision);
            var imagesBack = keptSaved is not { } assetsRevision || await RestoreKeptAsync(projectId, kept, assetsRevision);
            await ForgetPicksAsync(projectId, planId, steps);
            if (selected is null && keptSaved is null) throw;
            // Say so when undoing failed too, rather than claim nothing was picked.
            throw new WorkspaceStoreException(takesBack && imagesBack ? "The picks could not all be saved, so none was made. Try them again."
                : "The picks could not all be saved, and part of what was saved could not be undone: "
                    + string.Join(" and ", new[] { takesBack ? null : "check the selected takes in Shots", imagesBack ? null : "check the images waiting for review in Assets" }.OfType<string>())
                    + ". The steps still wait for their picks; make them again once that is sorted.", e);
        }
        foreach (var (step, choice) in imagePicks) await DiscardCandidatesAsync(projectId, step, step.ProducedImageIds.Where(i => i != choice).ToArray(), commit);
    }

    private sealed class StaleSelectionException : Exception;

    // Clears the record of picks that were not made. Failing to is only logged: the next pass then finishes those picks.
    private async Task ForgetPicksAsync(Guid projectId, Guid planId, IReadOnlyList<(ReviewPlanStep Step, Guid Choice)> steps)
    {
        var ids = steps.Select(s => s.Step.Id).ToHashSet();
        try { await store.UpdateAsync(projectId, planId, p => p with { Steps = p.Steps.Select(s => ids.Contains(s.Id) && s.State == ReviewStepState.Pick ? s with { PickingChoice = null, PickingFrom = null } : s).ToArray() }, ct: CancellationToken.None); }
        catch (Exception e) when (e is WorkspaceStoreException or IOException) { logger.LogWarning(e, "The record of picks that were not made could not be cleared."); }
    }

    // Picks a restart interrupted are finished, on the director's word that was already given. If that fails, they are
    // undone as a failed pick is. Returns whether there was one.
    private async Task<bool> ResumePicksAsync(ReviewPlan plan, Guid projectId, CancellationToken ct)
    {
        var interrupted = plan.Steps.Where(s => s.State == ReviewStepState.Pick && s.PickingChoice is not null).Select(s => (s, s.PickingChoice!.Value)).ToArray();
        if (interrupted.Length == 0) return false;
        try { await CommitPicksAsync(projectId, plan.Id, interrupted, ReviewPlans.Director); }
        catch (WorkspaceStoreException e) { logger.LogWarning(e, "Picks interrupted in review plan {Plan} could not be finished.", plan.Id); }
        return true;
    }

    // Returns the images a failed batch kept to waiting for review, where nothing else changed them since.
    // Each undoes the batch's own save only while the store is still at the revision that save left: once someone else has
    // saved, what is there may be their choice, and it is left alone and reported. A save that fails for another reason is
    // tried again a few times. Returns whether the batch's change is gone.
    private const int UndoAttempts = 3;

    private async Task<bool> RestoreKeptAsync(Guid projectId, IReadOnlySet<AssetImageReference> kept, long savedRevision)
    {
        for (var attempt = 1; ; attempt++)
        try
        {
            var library = await assets.LoadAsync(projectId, CancellationToken.None);
            if (library.Revision != savedRevision) return false;
            if (!library.Assets.Any(a => a.Images.Any(i => i.PendingReview == false && kept.Contains(new(a.Id, i.Id))))) return true;
            await assets.SaveAsync(library with { Assets = library.Assets.Select(a => a with { Images = a.Images.Select(i => i.PendingReview == false && kept.Contains(new(a.Id, i.Id)) ? i with { PendingReview = true } : i).ToList() }).ToList() }, library.Revision, CancellationToken.None);
            return true;
        }
        catch (Exception e) when (e is WorkspaceStoreException or WorkspaceConflictException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "The images kept by a batch of picks that failed could not be returned to review (attempt {Attempt}).", attempt);
            if (attempt >= UndoAttempts) return false;
        }
    }

    // Puts back the selections a failed batch made, where nothing else changed them since.
    private async Task<bool> RestoreSelectionsAsync(Guid projectId, IReadOnlyDictionary<Guid, Guid?> before, IReadOnlyList<(ReviewPlanStep Step, Guid Choice)> made, long savedRevision)
    {
        for (var attempt = 1; ; attempt++)
        try
        {
            var latest = await shots.LoadAsync(projectId, CancellationToken.None); var changed = false;
            if (latest.Revision != savedRevision) return false;
            foreach (var (step, choice) in made)
                if (latest.Shots.FirstOrDefault(s => s.Id == step.ShotId) is { } shot && shot.SelectedTakeId == choice) { shot.SelectedTakeId = before[shot.Id]; changed = true; }
            if (changed) await shots.SaveAsync(projectId, latest.Shots, latest.Revision, "Choose production take", CancellationToken.None);
            return true;
        }
        catch (Exception e) when (e is WorkspaceStoreException or WorkspaceConflictException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "The takes selected by a batch of picks that failed could not be put back (attempt {Attempt}).", attempt);
            if (attempt >= UndoAttempts) return false;
        }
    }

    /// <summary>A proposer's advice on which of a step's new takes or images to use. The director still picks.</summary>
    public async Task<ReviewPlan> RecommendAsync(Guid projectId, Guid planId, Guid stepId, Guid choice, string reason, string who, CancellationToken ct = default)
    {
        reason = (reason ?? "").Trim();
        if (reason.Length is 0 or > 2000) throw new WorkspaceStoreException("Say why in up to 2000 characters.");
        // Under the gate, so a recommendation never changes between the check and the pick of an acceptance.
        await _gate.WaitAsync(ct);
        try
        {
            return await store.UpdateAsync(projectId, planId, plan =>
            {
                var step = plan.Step(stepId) ?? throw new WorkspaceStoreException("That step is not in this plan.");
                if (step.State != ReviewStepState.Pick) throw new WorkspaceStoreException("This step has nothing waiting for a pick.");
                if (!step.Produced.Contains(choice)) throw new WorkspaceStoreException("Recommend one of the takes or images this step produced.");
                return plan with { Steps = plan.Steps.Select(s => s.Id == stepId ? s with { RecommendedTakeId = choice, Recommendation = reason } : s).ToArray(),
                    Log = [.. plan.Log, new(clock.GetUtcNow(), who, $"Recommended a take for “{step.Reason}”.")] };
            }, proposal: false, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>None of a step's results will do. Steps that depended on the pick are skipped, and the rest of the plan goes on.</summary>
    public async Task<ReviewPlan> RejectPickAsync(Guid projectId, Guid planId, Guid stepId, string who, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var step = await PickableAsync(projectId, planId, stepId, ct);
            // A pick of this step a restart interrupted may have selected its take already: that is put back first.
            if (step is { PickingChoice: { } interrupted, Kind: not ReviewStepKind.EditImage })
            {
                var latest = await shots.LoadAsync(projectId, ct);
                if (latest.Shots.FirstOrDefault(s => s.Id == step.ShotId) is { } shot && shot.SelectedTakeId == interrupted)
                {
                    shot.SelectedTakeId = step.PickingFrom;
                    await shots.SaveAsync(projectId, latest.Shots, latest.Revision, "Choose production take", CancellationToken.None);
                }
            }
            // As with a pick, the rejection is recorded first and the candidates go to Trash only after, so a step that still
            // waits for its pick never lost them.
            var message = step.Kind == ReviewStepKind.EditImage ? "None of the candidates was picked. They are in Trash." : "None of the takes was picked. They stay in Shots.";
            await store.UpdateAsync(projectId, planId, p => Picked(p, stepId, s => s with { State = ReviewStepState.Skipped, Message = message, FinishedUtc = clock.GetUtcNow(), PickingChoice = null, PickingFrom = null }, who, message), ct: CancellationToken.None);
            if (step.Kind == ReviewStepKind.EditImage) await DiscardCandidatesAsync(projectId, step, step.ProducedImageIds, CancellationToken.None);
        }
        finally { _gate.Release(); }
        return await AdvanceAsync(projectId, planId, ct);
    }

    private async Task<ReviewPlanStep> PickableAsync(Guid projectId, Guid planId, Guid stepId, CancellationToken ct)
    {
        var plan = await store.GetAsync(projectId, planId, ct);
        var step = plan.Step(stepId) ?? throw new WorkspaceStoreException("That step is not in this plan.");
        if (!plan.Active || step.State != ReviewStepState.Pick) throw new WorkspaceStoreException("This step has nothing waiting for a pick.");
        return step;
    }

    // Checked again in the save itself, so a pick never overwrites a step that was settled another way.
    private ReviewPlan Picked(ReviewPlan plan, Guid stepId, Func<ReviewPlanStep, ReviewPlanStep> change, string who, string what) =>
        plan.Active && plan.Step(stepId)?.State == ReviewStepState.Pick ? Set(plan, stepId, change, who, what) : throw new WorkspaceStoreException("This step has nothing waiting for a pick.");

    // The candidates not chosen go to Trash, where they can be restored. Failing to move them never undoes a pick.
    private async Task DiscardCandidatesAsync(Guid projectId, ReviewPlanStep step, IReadOnlyCollection<Guid> images, CancellationToken ct)
    {
        // Disk errors count as well: the candidates then stay in Assets, waiting for review, and the pick is still recorded.
        try
        {
            var library = await assets.LoadAsync(projectId, ct);
            var pending = images.Where(id => library.Assets.Any(a => a.Id == step.AssetId && a.Images.Any(i => i.Id == id && i.PendingReview == true))).Select(id => new AssetImageReference(step.AssetId!.Value, id)).ToArray();
            if (pending.Length == 0) return;
            await assets.DiscardPendingImagesAsync(projectId, Guid.NewGuid(), pending, library.Revision, ct);
        }
        catch (Exception e) when (e is WorkspaceStoreException or WorkspaceConflictException or IOException or UnauthorizedAccessException)
        { logger.LogWarning(e, "Image candidates of step {Step} could not be moved to Trash.", step.Id); }
    }

    /// <summary>
    /// Picks the recommended takes the director was shown, by step. Nothing is picked if any of those recommendations changed
    /// since, so accepting never selects a take the director did not see recommended.
    /// </summary>
    public async Task<ReviewPlan> AcceptRecommendationsAsync(Guid projectId, Guid planId, string who, IReadOnlyDictionary<Guid, Guid> shown, CancellationToken ct = default)
    {
        if (shown.Count == 0) throw new WorkspaceStoreException("No step is waiting with a recommended take.");
        // Checked with the picks, under the gate, against the recommendations as they stand then.
        return await PickAllAsync(projectId, planId, shown.Select(pair => (pair.Key, pair.Value)).ToArray(), who, ct, recommended: true);
    }

    // A queued render that finished becomes a pick; one that was cancelled or lost fails its step.
    private async Task<bool> SettleRenderAsync(ProjectReviewSnapshot snapshot, ReviewPlan plan, CancellationToken ct)
    {
        foreach (var step in plan.Steps.Where(s => s.State == ReviewStepState.Running))
        {
            var job = renders.State(step.JobId ?? Guid.Empty);
            if (job is AiJobState.Waiting or AiJobState.Running or AiJobState.NeedsAttention) continue;
            // Read after the request is seen to be over: the snapshot may be older than the takes it published as it finished.
            var image = step.Kind == ReviewStepKind.EditImage;
            var takes = image
                // Including any the director already kept in Assets: picking one of those keeps it as it is.
                ? (await assets.LoadAsync(snapshot.ProjectId, ct)).Assets.Where(a => a.Id == step.AssetId).SelectMany(a => a.Images).Where(i => i.Generation?.AiJobId == step.JobId).Select(i => i.Id).ToArray()
                : (await shots.LoadAsync(snapshot.ProjectId, ct)).Takes.Where(t => t.AiJobId == step.JobId && t.ShotId == step.ShotId).Select(t => t.Id).ToArray();
            await store.UpdateAsync(snapshot.ProjectId, plan.Id, p => Set(p, step.Id, s => takes.Length > 0
                ? image ? s with { State = ReviewStepState.Pick, ProducedImageIds = takes, Message = null } : s with { State = ReviewStepState.Pick, ProducedTakeIds = takes, Message = null }
                : s with { State = ReviewStepState.Failed, FinishedUtc = clock.GetUtcNow(), Message = image
                    ? job == AiJobState.Cancelled ? "The edit was cancelled." : "The edit finished without an image. See AI activity."
                    : job == AiJobState.Cancelled ? "The render was cancelled." : "The render finished without a take. See AI activity." },
                ReviewPlans.Director, takes.Length == 0 ? $"A {(image ? "edit" : "render")} produced nothing to pick."
                    : image ? $"An edit finished with {takes.Length} {(takes.Length == 1 ? "image" : "images")} to pick from." : $"A render finished with {takes.Length} {(takes.Length == 1 ? "take" : "takes")} to pick from."), ct: ct);
            return true;
        }
        return false;
    }

    private async Task<bool> RunNextAsync(ProjectReviewSnapshot snapshot, ReviewPlan plan, Dictionary<Guid, int> conflicts, CancellationToken ct)
    {
        foreach (var step in plan.Steps.Where(s => s.State == ReviewStepState.Waiting))
        {
            if (Blocker(plan, step) is { } blocker)
            {
                if (blocker.Open) continue;
                // What it waited for was rejected, failed or skipped, so this one cannot run as proposed.
                await Finish(step, ReviewStepState.Skipped, $"Skipped because “{ReviewPlans.Describe(snapshot, blocker)}” did not run.");
                return true;
            }
            // A frame from a shot the plan does not render comes from that shot's selected take. Which take that is, is
            // recorded first, so a step applied again after an interruption adds the same frame, never one from a take
            // selected since.
            // A take this plan selects for that shot comes first: the step waited for it.
            if (step is { Kind: ReviewStepKind.AddShotFrame, Frame: { TakeId: null, FromShotId: { } source } } && Kept(plan, source) is null &&
                (Chosen(plan, source)?.TakeId ?? snapshot.Row(source)?.Coverage.SelectedTakeId) is { } selected)
            {
                await store.UpdateAsync(snapshot.ProjectId, plan.Id, p => p with { Steps = p.Steps.Select(s => s.Id == step.Id && s.Frame is { TakeId: null } frame ? s with { Frame = frame with { TakeId = selected } } : s).ToArray() }, ct: ct);
                return true;
            }
            var applied = false; string? why = null;
            try
            {
                if (step.Kind == ReviewStepKind.GenerateTakes) { if (await RenderAsync(snapshot, plan, step, ct) is { } held) await Finish(step, ReviewStepState.Held, held.Message, held.Seconds); }
                else if (step.Kind == ReviewStepKind.EditImage) await EditAsync(snapshot, plan, step, ct);
                // Once the change is saved, the caller going away must not leave the step waiting to be applied a second time.
                else { why = await ApplyAsync(snapshot, Resolved(plan, step), CancellationToken.None); applied = true; }
            }
            // Another save landed between reading and writing: read again and retry this step, a few times at most.
            catch (WorkspaceConflictException)
            {
                conflicts[step.Id] = conflicts.GetValueOrDefault(step.Id) + 1;
                if (conflicts[step.Id] >= 3) await Finish(step, ReviewStepState.Failed, "Something else kept saving this shot while the step was applied. Close other editors of the shot, then start a new plan for this step.");
            }
            catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException) { await Finish(step, ReviewStepState.Failed, e.Message); }
            // Recorded apart from applying it: failing to record a change that was saved is not the change failing. The pass
            // ends with the error, the step still waits, and the next pass applies it again, finds the change in place and
            // records it.
            if (applied)
            {
                Recording?.Invoke(step.Id);
                await Finish(step, why is null ? ReviewStepState.Done : ReviewStepState.Held, why, token: CancellationToken.None);
            }
            return true;
        }
        return false;
        Task Finish(ReviewPlanStep step, ReviewStepState state, string? message, double? heldSeconds = null, CancellationToken? token = null) => store.UpdateAsync(snapshot.ProjectId, plan.Id,
            p => Set(p, step.Id, s => s with { State = state, Message = message, FinishedUtc = state == ReviewStepState.Held ? null : clock.GetUtcNow(), HeldSeconds = heldSeconds }, ReviewPlans.Director,
                $"{ReviewPlans.Describe(snapshot, step)}: {state switch { ReviewStepState.Done => "done", ReviewStepState.Held => "held", ReviewStepState.Skipped => "skipped", _ => "failed" }}{(message is null ? "" : ". " + message)}"), ct: token ?? ct);
    }

    /// <summary>The step this one has to wait for, if any: still open means wait, anything else means it cannot run.</summary>
    internal static ReviewPlanStep? Blocker(ReviewPlan plan, ReviewPlanStep step)
    {
        static bool Stops(ReviewPlanStep s) => s.State is ReviewStepState.Rejected or ReviewStepState.Failed or ReviewStepState.Skipped;
        // A frame from another shot's new take needs the render the plan keeps for that shot picked. Without one, it waits for
        // the take this plan selects for that shot. A render that was rejected, with neither, means it cannot run as proposed.
        if (step.Kind == ReviewStepKind.AddShotFrame && step.Frame?.FromShotId is { } from)
        {
            if (Kept(plan, from) is { } render) { if (render.State != ReviewStepState.Done) return render; }
            else if (Chosen(plan, from) is { } use) { if (use.State != ReviewStepState.Done) return use; }
            else if (Render(plan, from) is { } rejected) return rejected;
        }
        // A picture from an edit needs that edit picked.
        if (step.FromStepId is { } edit && plan.Step(edit) is { State: not ReviewStepState.Done } producer) return producer;
        // Accepting a prompt records the references it was read against, so it waits for the plan's changes to the shot's
        // pictures, frames and voices; were it to run first, the change would undo the acceptance. A change that did not run
        // leaves the references as they were, and the prompt step then runs against those.
        if (step.Kind is ReviewStepKind.SetPrompt or ReviewStepKind.MarkPromptReviewed)
            return plan.Steps.FirstOrDefault(s => s.Id != step.Id && s.ShotId == step.ShotId && s.Open && s.Kind is ReviewStepKind.SetVoice or ReviewStepKind.AddShotFrame
                or ReviewStepKind.RemoveShotFrame or ReviewStepKind.ReplacePicture or ReviewStepKind.RemovePicture);
        // A render follows every change the plan makes to its shot, wherever in the plan the change was proposed.
        if (step.Kind == ReviewStepKind.GenerateTakes)
            return plan.Steps.Where(s => s.Id != step.Id && s.ShotId == step.ShotId && s.Kind is not (ReviewStepKind.UseTake or ReviewStepKind.GenerateTakes)).FirstOrDefault(s => s.Open || Stops(s));
        // The cut is pointed at the picks once every pick is made.
        if (step.Kind == ReviewStepKind.UpdateCut) return plan.Steps.FirstOrDefault(s => s.Id != step.Id && s.Kind != ReviewStepKind.UpdateCut && s.Open);
        return null;
    }

    // Returns why the render is held, or null once it is queued.
    private async Task<(string Message, double Seconds)?> RenderAsync(ProjectReviewSnapshot snapshot, ReviewPlan plan, ReviewPlanStep step, CancellationToken ct)
    {
        // The approval covered so many seconds per take. A shot made longer since then needs the director's word again.
        // Measured on the setup that is captured, which is read after the snapshot and carries the shot's length as it is now.
        var setup = await RenderSetupAsync(snapshot.ProjectId, step, ct);
        var seconds = Math.Max(ReviewPlans.Seconds(snapshot, step.ShotId), setup.Shot.Duration is { } length and >= 1 and <= 15 ? H3Policy.Seconds(length) : 0);
        if (step.ApprovedSeconds is { } approved && seconds > approved + .01)
            return ($"The shot is now {seconds:0.###} seconds long, and the plan was approved for {approved:0.###} seconds per take. Release the step to render {step.Takes * seconds:0.###} seconds of footage instead of {step.Takes * approved:0.###}.", seconds);
        var id = Guid.NewGuid();
        var request = await renders.CaptureAsync(id, snapshot.ProjectId, setup, step.Takes, step.Resolution, step.Preset, ct);
        // Recorded before it is queued: a crash in between leaves a step that reports the lost render, never a second render.
        await store.UpdateAsync(snapshot.ProjectId, plan.Id, p => Set(p, step.Id, s => s with { State = ReviewStepState.Running, JobId = id }, ReviewPlans.Director,
            $"{ReviewPlans.Describe(snapshot, step)}: queued."), ct: ct);
        // Once the step says it is running, the request is handed to the queue even if the caller goes away; dropping it here
        // would fail an approved render that was never given a chance.
        await renders.EnqueueAsync(request, CancellationToken.None);
        return null;
    }

    // What a step uses from the steps before it: the image an edit's pick produced, or the take the plan picked for a frame's
    // source shot. The take picked here, not whatever that shot has selected since in another tab.
    private static ReviewPlanStep Resolved(ReviewPlan plan, ReviewPlanStep step) =>
        step.FromStepId is { } edit && plan.Step(edit) is { } producer ? step with { NewAssetId = producer.AssetId, NewImageId = producer.NewImageId }
        : step is { Kind: ReviewStepKind.AddShotFrame, Frame: { TakeId: null, FromShotId: { } from } frame } && Render(plan, from) is { State: ReviewStepState.Done, TakeId: { } picked }
            ? step with { Frame = frame with { TakeId = picked } } : step;

    /// <summary>The last step of the plan that selects an existing take for a shot and was not rejected.</summary>
    internal static ReviewPlanStep? Chosen(ReviewPlan plan, Guid shotId) =>
        plan.Steps.LastOrDefault(s => s.Kind == ReviewStepKind.UseTake && s.ShotId == shotId && s.State != ReviewStepState.Rejected);

    /// <summary>The plan's render of a shot that was not rejected, if it has one.</summary>
    internal static ReviewPlanStep? Kept(ReviewPlan plan, Guid shotId) => Render(plan, shotId) is { State: not ReviewStepState.Rejected } kept ? kept : null;

    /// <summary>The plan's render of a shot: the one kept, when another was rejected.</summary>
    internal static ReviewPlanStep? Render(ReviewPlan plan, Guid shotId) =>
        plan.Steps.Where(s => s.Kind == ReviewStepKind.GenerateTakes && s.ShotId == shotId).OrderBy(s => s.State == ReviewStepState.Rejected).FirstOrDefault();

    private async Task EditAsync(ProjectReviewSnapshot snapshot, ReviewPlan plan, ReviewPlanStep step, CancellationToken ct)
    {
        var owner = snapshot.Assets.Assets.FirstOrDefault(a => a.Id == step.AssetId); var image = owner?.Images.FirstOrDefault(i => i.Id == step.ImageId);
        if (owner is null || image is null) throw new WorkspaceStoreException("The image to edit is no longer in Assets. It may be in Trash.");
        var id = Guid.NewGuid();
        // Candidates wait for the director, and keep the source's proportions and look.
        var request = await renders.CaptureEditAsync(id, owner.Id, new ReferenceEditRequest
        {
            ProjectId = snapshot.ProjectId, SourceAssetId = owner.Id, SourceImageId = image.Id, Prompt = step.After!, AspectRatio = ImageAspectPolicy.FromImage1,
            Count = step.Takes, PendingReview = true, Look = LookPolicy.Capture(owner, image.LookId), Workflow = step.Workflow, H3Preset = step.Preset
        }, [new(owner.Id, image.Id), .. step.ReferenceImages], ct);
        // Recorded before it is queued, and then queued whatever the caller does, as a render is.
        await store.UpdateAsync(snapshot.ProjectId, plan.Id, p => Set(p, step.Id, s => s with { State = ReviewStepState.Running, JobId = id }, ReviewPlans.Director, $"{ReviewPlans.Describe(snapshot, step)}: queued."), ct: ct);
        await renders.EnqueueAsync(request, CancellationToken.None);
    }

    // Returns why the step is held, or null when it was applied (or was already in place).
    private async Task<string?> ApplyAsync(ProjectReviewSnapshot snapshot, ReviewPlanStep step, CancellationToken ct)
    {
        var project = snapshot.ProjectId;
        if (step.Kind == ReviewStepKind.UpdateCut) { await UpdateCutAsync(project, ct); return null; }
        var shotId = step.ShotId!.Value;
        if (step.Kind == ReviewStepKind.UseTake)
        {
            if (!snapshot.Shots.Takes.Any(t => t.Id == step.TakeId && t.ShotId == shotId)) return "That take is no longer one of this shot's takes.";
            await SelectAsync(project, shotId, step.TakeId!.Value, ct); return null;
        }
        var setup = Setup(await production.LoadAsync(project, ct), shotId).Copy(); var shot = setup.Shot;
        switch (step.Kind)
        {
            case ReviewStepKind.SetPrompt:
                if (ReviewPlans.Text(setup.Prompt) != ReviewPlans.Text(step.After))
                {
                    if (ReviewPlans.Text(setup.Prompt) != ReviewPlans.Text(step.Before)) return "The prompt was edited after this step was written.";
                    setup.Prompt = step.After!;
                    setup = Setup(await production.SaveAsync(project, setup, setup.Version, ct), shotId);
                }
                await production.AcceptAsync(project, setup.Id, setup.Version, ct: ct);
                return null;
            case ReviewStepKind.MarkPromptReviewed:
                if (ReviewPlans.Text(setup.Prompt) != ReviewPlans.Text(step.Before)) return "The prompt was edited after this step was written.";
                await production.AcceptAsync(project, setup.Id, setup.Version, ct: ct);
                return null;
            case ReviewStepKind.SetVoice:
                var character = snapshot.Assets.Assets.FirstOrDefault(a => a.Id == step.CharacterAssetId) ?? throw new WorkspaceStoreException("The character is no longer in Assets.");
                var choice = ShotCopy.Of(shot.CharacterVoices?.FirstOrDefault(c => c.AssetId == character.Id) ?? CharacterVoices.Initial(shot, character, snapshot.Assets));
                if (step.VoiceId is { } voiceId)
                {
                    var voice = snapshot.Assets.Voices.FirstOrDefault(v => v.Id == voiceId && v.AssetId == character.Id) ?? throw new WorkspaceStoreException("The voice recording is no longer in Assets.");
                    if (!choice.SpeakerConfirmed || choice.Source == CharacterVoiceSource.Unselected && choice.Speaker.Length == 0)
                    {
                        // Which dialogue speaker is this character? Only an unambiguous name decides it.
                        var speakers = shot.Dialogue.Select(d => d.Speaker.Trim()).Where(s => DialogueSpeakers.Names(s, character.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        if (speakers.Length > 1) throw new WorkspaceStoreException($"More than one dialogue speaker could be {character.Name}. Choose the speaker in Manage references.");
                        choice.Speaker = speakers.FirstOrDefault() ?? ""; choice.SpeakerConfirmed = true;
                    }
                    CharacterVoices.SelectRecording(choice, voice, fromDefault: character.DefaultVoiceId == voice.Id);
                }
                else { choice.Source = CharacterVoiceSource.None; choice.Recording = null; choice.ReelBindingId = null; choice.ReelMediaId = null; choice.Excerpt = null; choice.SourceName = ""; choice.FromDefault = false; choice.SpeakerConfirmed = true; }
                CharacterVoices.Set(shot, choice, snapshot.Assets); CharacterVoices.Validate(shot);
                break;
            case ReviewStepKind.AddShotFrame:
                var source = step.Frame!;
                var takeId = source.TakeId ?? snapshot.Row(source.FromShotId!.Value)?.Coverage.SelectedTakeId ?? throw new WorkspaceStoreException("The shot supplying the frame has no selected take.");
                var take = snapshot.Shots.Takes.FirstOrDefault(t => t.Id == takeId) ?? throw new WorkspaceStoreException("The take supplying the frame is no longer in this project.");
                var frame = source.Resolve(take);
                if (frame < 0 || frame >= take.FrameCount) throw new WorkspaceStoreException($"The take supplying the frame has frames 1 to {take.FrameCount}.");
                if (shot.ShotFrames.Any(f => f.TakeId == take.Id && f.Frame == frame)) return null;
                shot.ShotFrames.Add(new(Guid.NewGuid(), take.Id, frame, source.Name));
                break;
            case ReviewStepKind.RemoveShotFrame:
                if (shot.ShotFrames.RemoveAll(f => f.TakeId == step.Frame!.TakeId && f.Frame == step.Frame.Index) == 0) return null;
                break;
            case ReviewStepKind.ReplacePicture:
                var binding = shot.Images.FirstOrDefault(i => i.Kind == ShotImageKind.AssetImage && i.AssetId == step.AssetId && i.MediaId == step.ImageId);
                if (binding is null) return shot.Images.Any(i => i.AssetId == step.NewAssetId && i.MediaId == step.NewImageId) ? null : "This shot no longer selects the picture that was to be replaced.";
                var owner = snapshot.Assets.Assets.FirstOrDefault(a => a.Id == step.NewAssetId); var image = owner?.Images.FirstOrDefault(i => i.Id == step.NewImageId);
                if (owner is null || image is null || image.PendingReview == true) throw new WorkspaceStoreException("The replacement picture is no longer in Assets.");
                // The binding keeps its place, role and guidance, so <Picture N> in the prompt still means the same subject.
                binding.AssetId = owner.Id; binding.MediaId = image.Id;
                break;
            case ReviewStepKind.RemovePicture:
                if (shot.Images.RemoveAll(i => i.Kind == ShotImageKind.AssetImage && i.AssetId == step.AssetId && i.MediaId == step.ImageId) == 0) return null;
                break;
            default: throw new WorkspaceStoreException("That kind of step is not supported.");
        }
        H3Policy.Validate(shot, ready: false, requireScene: false);
        await production.SaveAsync(project, setup, setup.Version, ct);
        return null;
    }

    // A shot keeps one setup record per global setup, each with the shot's shared prompt and references, as Shots binds them.
    // Renders use the record of the global setup the plan was approved with, so they match Generate takes in Shots.
    private async Task<ProductionComposition> RenderSetupAsync(Guid project, ReviewPlanStep step, CancellationToken ct)
    {
        var shotId = step.ShotId!.Value; var document = await production.LoadAsync(project, ct);
        var library = setups is null ? null : await setups.LoadAsync(ct);
        var preset = step.GenerationSetupId is { } id
            ? library?.Setups.FirstOrDefault(s => s.Id == id) ?? throw new WorkspaceStoreException($"The generation setup “{step.GenerationSetupName}” no longer exists. Start a new plan for this render.")
            : library?.Active();
        if (preset is null) return Setup(document, shotId);
        if (preset.Archived) throw new WorkspaceStoreException($"The generation setup “{preset.Name}” was archived after this plan was approved. Restore it, or start a new plan for this render.");
        if (document.Compositions.FirstOrDefault(c => c.ShotId == shotId && c.GenerationSetupId == preset.Id && !c.Archived) is { } bound) return bound;
        var source = (await shots.LoadAsync(project, ct)).Shots.FirstOrDefault(s => s.Id == shotId) ?? throw new WorkspaceStoreException("The shot was removed.");
        var created = new ProductionComposition { ShotId = shotId, Shot = ProductionPolicy.CoverageCopy(source), SourceFingerprint = ProductionPolicy.SourceFingerprint(source) };
        preset.Apply(created);
        return (await production.SaveAsync(project, created, 0, ct)).Compositions.First(c => c.Id == created.Id);
    }

    // The setup that carries the shot's shared prompt and references, as the review reads it.
    private static ProductionComposition Setup(ProductionDocument document, Guid shotId) =>
        document.Compositions.FirstOrDefault(c => c.ShotId == shotId && !c.Archived) ?? document.Compositions.FirstOrDefault(c => c.ShotId == shotId)
        ?? throw new WorkspaceStoreException("The shot has no setup. It may have been removed.");

    private async Task SelectAsync(Guid projectId, Guid shotId, Guid takeId, CancellationToken ct)
    {
        var latest = await shots.LoadAsync(projectId, ct);
        var shot = latest.Shots.FirstOrDefault(s => s.Id == shotId) ?? throw new WorkspaceStoreException("The shot was removed.");
        if (!latest.Takes.Any(t => t.Id == takeId && t.ShotId == shotId)) throw new WorkspaceStoreException("That take is no longer one of this shot's takes. It may be in Trash.");
        if (shot.SelectedTakeId == takeId) return;
        shot.SelectedTakeId = takeId;
        await shots.SaveAsync(projectId, latest.Shots, latest.Revision, "Choose production take", ct);
    }

    /// <summary>Replaces each clip whose shot has another take selected, and adds selected shots the cut lacks at their place in shot order.</summary>
    private async Task UpdateCutAsync(Guid projectId, CancellationToken ct)
    {
        var cut = await cuts.LoadAsync(projectId, ct); var latest = await shots.LoadAsync(projectId, ct);
        var order = latest.Shots.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
        ShotTake? Selected(Shot shot) => latest.Takes.FirstOrDefault(t => t.Id == shot.SelectedTakeId && t.ShotId == shot.Id);
        var clips = new List<CutClip>(); var changed = false;
        foreach (var clip in cut.Clips)
        {
            var shot = latest.Shots.FirstOrDefault(s => s.Id == clip.ShotId);
            if (shot is not null && Selected(shot) is { } take && take.Id != clip.TakeId)
            {
                // The same clip in the timeline, now showing the selected take in full; a trim made for the old take would not fit the new one.
                var replaced = CutClip.From(shot, take); replaced.Id = clip.Id; clips.Add(replaced); changed = true;
            }
            else clips.Add(clip);
        }
        foreach (var shot in latest.Shots.Where(s => clips.All(c => c.ShotId != s.Id)))
        {
            if (Selected(shot) is not { } take) continue;
            var after = clips.FindLastIndex(c => order.TryGetValue(c.ShotId, out var position) && position < order[shot.Id]);
            clips.Insert(after + 1, CutClip.From(shot, take)); changed = true;
        }
        if (changed) await cuts.SaveAsync(projectId, clips, cut.Revision, ct);
    }

    private ReviewPlan Set(ReviewPlan plan, Guid stepId, Func<ReviewPlanStep, ReviewPlanStep> change, string who, string what) =>
        plan with { Steps = plan.Steps.Select(s => s.Id == stepId ? change(s) : s).ToArray(), Log = [.. plan.Log, new(clock.GetUtcNow(), who, what)] };
}

/// <summary>How a plan starts a render and learns what became of it. The app's queue in production; a stand-in under test.</summary>
public interface IReviewRenderQueue
{
    /// <summary>Captures the request with the same checks as Generate takes in Shots. A refusal is a <see cref="WorkspaceStoreException"/>.</summary>
    Task<AiJobSubmission> CaptureAsync(Guid id, Guid projectId, ProductionComposition setup, int takes, VideoResolution? resolution, string? preset, CancellationToken ct);
    /// <summary>Captures an image edit with the same checks as the Assets studio.</summary>
    Task<AiJobSubmission> CaptureEditAsync(Guid id, Guid assetId, ReferenceEditRequest request, IReadOnlyList<AssetImageReference> references, CancellationToken ct);
    Task EnqueueAsync(AiJobSubmission request, CancellationToken ct);
    Task CancelAsync(Guid jobId, CancellationToken ct);
    /// <summary>
    /// The request's state, or null when the queue no longer lists it. Until the queue has been read after a restart, every
    /// request counts as waiting: an unread queue lists nothing, which must not be taken for a lost request.
    /// </summary>
    AiJobState? State(Guid jobId);
}

public sealed class ReviewRenderQueue(AiVideoJobCapture capture, AiImageJobCapture images, AiJobCoordinator jobs) : IReviewRenderQueue
{
    /// <summary>The queue's origin for requests a plan started; no browser tab owns them.</summary>
    public static readonly Guid Origin = new("7d0c5a3e-2f0b-4c57-9a55-0c1e5f6b7a11");
    public Task<AiJobSubmission> CaptureAsync(Guid id, Guid projectId, ProductionComposition setup, int takes, VideoResolution? resolution, string? preset, CancellationToken ct) => resolution is not null || preset is not null
        ? capture.CaptureCompositionWithPresetAsync(id, Origin, projectId, setup.Id, setup.Version, takes, setup.Seed, preset ?? H3Presets.Key(setup.Shot), resolution, ct: ct, generationSetupVersion: setup.GenerationSetupVersion)
        : capture.CaptureCompositionAsync(id, Origin, projectId, setup.Id, setup.Version, takes, setup.Seed, ct, setup.GenerationSetupVersion);
    public Task<AiJobSubmission> CaptureEditAsync(Guid id, Guid assetId, ReferenceEditRequest request, IReadOnlyList<AssetImageReference> references, CancellationToken ct) =>
        images.EditAsync(id, Origin, assetId, request, references, ct);
    public async Task EnqueueAsync(AiJobSubmission request, CancellationToken ct) => await jobs.EnqueueAsync(request, ct);
    public Task CancelAsync(Guid jobId, CancellationToken ct) => jobs.CancelAsync(jobId, ct);
    public AiJobState? State(Guid jobId) => jobs.Loaded ? jobs.View.Jobs.FirstOrDefault(j => j.Id == jobId)?.State : AiJobState.Waiting;
}

/// <summary>Keeps approved plans moving as renders finish, and picks them up again after a restart.</summary>
public sealed class ReviewPlanWorker(ReviewPlanRunner runner, AiJobCoordinator jobs, ILogger<ReviewPlanWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _signal = new(1, 1);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void Wake() { try { if (_signal.CurrentCount == 0) _signal.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { } }
        jobs.Changed += Wake; var first = true;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // A queue change wakes it; the timeout covers a signal lost between passes.
                await _signal.WaitAsync(TimeSpan.FromMinutes(1), stoppingToken);
                try { if (first) await runner.AdvanceAllAsync(stoppingToken); else await runner.AdvanceWatchedAsync(ct: stoppingToken); first = false; }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception e) { logger.LogError(e, "Review plans could not be advanced."); }
            }
        }
        catch (OperationCanceledException) { }
        finally { jobs.Changed -= Wake; }
    }
}
