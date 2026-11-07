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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _watched = new();
    private int _listening;

    /// <summary>Advances the approved plans of every project, picking up whatever a restart interrupted. Called once at startup.</summary>
    public async Task AdvanceAllAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _listening, 1) == 0) store.Changed += (project, _) => _watched[project] = 0;
        foreach (var project in (await projects.ListAsync(ct)).Projects) _watched[project.Id] = 0;
        await AdvanceWatchedAsync(settledOnly: false, ct);
    }

    /// <summary>
    /// Advances the approved plans of projects known to have had one. Called whenever the queue changes, which is often while a
    /// render reports progress, so with <paramref name="settledOnly"/> a plan is only read in full once one of its renders has left the queue.
    /// </summary>
    public async Task AdvanceWatchedAsync(bool settledOnly = true, CancellationToken ct = default)
    {
        foreach (var project in _watched.Keys)
        {
            IReadOnlyList<ReviewPlan> plans;
            try { plans = await store.ListAsync(project, ct); }
            catch (WorkspaceStoreException e) { logger.LogWarning(e, "Review plans of project {Project} could not be read.", project); _watched.TryRemove(project, out _); continue; }
            if (!plans.Any(p => p.Active)) { _watched.TryRemove(project, out _); continue; }
            foreach (var plan in plans.Where(p => p.Active))
                try
                {
                    if (settledOnly && !plan.Steps.Any(s => s.State == ReviewStepState.Running && renders.State(s.JobId ?? Guid.Empty) is not (AiJobState.Waiting or AiJobState.Running or AiJobState.NeedsAttention))) continue;
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
        // Held across the change, so a pass in progress finishes queueing before the renders are collected.
        await _gate.WaitAsync(ct);
        IReadOnlyList<Guid> queued; ReviewPlan stopped;
        try
        {
            queued = (await store.GetAsync(projectId, planId, ct)).Steps.Where(s => s.State == ReviewStepState.Running).Select(s => s.JobId).OfType<Guid>().ToArray();
            stopped = await store.UpdateAsync(projectId, planId, plan => ReviewPlans.Stopped(plan, who, clock.GetUtcNow()), ct: ct);
        }
        finally { _gate.Release(); }
        foreach (var job in queued) await renders.CancelAsync(job, ct);
        return stopped;
    }

    /// <summary>The director's choice among the takes or images a step produced.</summary>
    public async Task<ReviewPlan> PickAsync(Guid projectId, Guid planId, Guid stepId, Guid choice, string who, CancellationToken ct = default)
    {
        var plan = await store.GetAsync(projectId, planId, ct);
        var step = plan.Step(stepId) ?? throw new WorkspaceStoreException("That step is not in this plan.");
        if (!plan.Active || step.State != ReviewStepState.Pick) throw new WorkspaceStoreException("This step has nothing waiting for a pick.");
        if (!step.Produced.Contains(choice)) throw new WorkspaceStoreException("Pick one of the takes or images this step produced.");
        if (step.Kind == ReviewStepKind.EditImage)
        {
            await KeepImageAsync(projectId, step.AssetId!.Value, choice, ct);
            await DiscardCandidatesAsync(projectId, step, step.ProducedImageIds.Where(i => i != choice).ToArray(), ct);
            await store.UpdateAsync(projectId, planId, p => Set(p, stepId, s => s with { State = ReviewStepState.Done, NewImageId = choice, FinishedUtc = clock.GetUtcNow() }, who, "Picked an image."), ct: ct);
        }
        else
        {
            await SelectAsync(projectId, step.ShotId!.Value, choice, ct);
            await store.UpdateAsync(projectId, planId, p => Set(p, stepId, s => s with { State = ReviewStepState.Done, TakeId = choice, FinishedUtc = clock.GetUtcNow() }, who, "Picked a take."), ct: ct);
        }
        return await AdvanceAsync(projectId, planId, ct);
    }

    /// <summary>None of a step's results will do. Steps that depended on the pick are skipped, and the rest of the plan goes on.</summary>
    public async Task<ReviewPlan> RejectPickAsync(Guid projectId, Guid planId, Guid stepId, string who, CancellationToken ct = default)
    {
        var plan = await store.GetAsync(projectId, planId, ct);
        var step = plan.Step(stepId) ?? throw new WorkspaceStoreException("That step is not in this plan.");
        if (!plan.Active || step.State != ReviewStepState.Pick) throw new WorkspaceStoreException("This step has nothing waiting for a pick.");
        if (step.Kind == ReviewStepKind.EditImage) await DiscardCandidatesAsync(projectId, step, step.ProducedImageIds, ct);
        var message = step.Kind == ReviewStepKind.EditImage ? "None of the candidates was picked. They are in Trash." : "None of the takes was picked. They stay in Shots.";
        await store.UpdateAsync(projectId, planId, p => Set(p, stepId, s => s with { State = ReviewStepState.Skipped, Message = message, FinishedUtc = clock.GetUtcNow() }, who, message), ct: ct);
        return await AdvanceAsync(projectId, planId, ct);
    }

    private async Task KeepImageAsync(Guid projectId, Guid assetId, Guid imageId, CancellationToken ct)
    {
        var library = await assets.LoadAsync(projectId, ct);
        if (library.Assets.FirstOrDefault(a => a.Id == assetId)?.Images.FirstOrDefault(i => i.Id == imageId) is not { } image) throw new WorkspaceStoreException("That image is no longer in Assets. It may be in Trash.");
        if (image.PendingReview == false) return;
        await assets.SaveAsync(library with { Assets = library.Assets.Select(a => a.Id != assetId ? a : a with { Images = a.Images.Select(i => i.Id == imageId ? i with { PendingReview = false } : i).ToList() }).ToList() }, library.Revision, ct);
    }

    // The candidates not chosen go to Trash, where they can be restored. Failing to move them never undoes a pick.
    private async Task DiscardCandidatesAsync(Guid projectId, ReviewPlanStep step, IReadOnlyCollection<Guid> images, CancellationToken ct)
    {
        var library = await assets.LoadAsync(projectId, ct);
        var pending = images.Where(id => library.Assets.Any(a => a.Id == step.AssetId && a.Images.Any(i => i.Id == id && i.PendingReview == true))).Select(id => new AssetImageReference(step.AssetId!.Value, id)).ToArray();
        if (pending.Length == 0) return;
        try { await assets.DiscardPendingImagesAsync(projectId, Guid.NewGuid(), pending, library.Revision, ct); }
        catch (Exception e) when (e is WorkspaceStoreException or WorkspaceConflictException) { logger.LogWarning(e, "Image candidates of step {Step} could not be moved to Trash.", step.Id); }
    }

    /// <summary>Picks the recommended take of every step that is waiting with a recommendation.</summary>
    public async Task<ReviewPlan> AcceptRecommendationsAsync(Guid projectId, Guid planId, string who, IReadOnlyCollection<Guid>? stepIds = null, CancellationToken ct = default)
    {
        var plan = await store.GetAsync(projectId, planId, ct);
        var ready = plan.Steps.Where(s => s.State == ReviewStepState.Pick && s.RecommendedTakeId is not null && (stepIds is null || stepIds.Contains(s.Id))).ToArray();
        if (ready.Length == 0) throw new WorkspaceStoreException("No step is waiting with a recommended take.");
        foreach (var step in ready) plan = await PickAsync(projectId, planId, step.Id, step.RecommendedTakeId!.Value, who, ct);
        return plan;
    }

    // A queued render that finished becomes a pick; one that was cancelled or lost fails its step.
    private async Task<bool> SettleRenderAsync(ProjectReviewSnapshot snapshot, ReviewPlan plan, CancellationToken ct)
    {
        foreach (var step in plan.Steps.Where(s => s.State == ReviewStepState.Running))
        {
            var job = renders.State(step.JobId ?? Guid.Empty);
            var image = step.Kind == ReviewStepKind.EditImage;
            var takes = image
                ? snapshot.Assets.Assets.Where(a => a.Id == step.AssetId).SelectMany(a => a.Images).Where(i => i.Generation?.AiJobId == step.JobId && i.PendingReview == true).Select(i => i.Id).ToArray()
                : snapshot.Shots.Takes.Where(t => t.AiJobId == step.JobId && t.ShotId == step.ShotId).Select(t => t.Id).ToArray();
            if (job is AiJobState.Waiting or AiJobState.Running or AiJobState.NeedsAttention) continue;
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
            try
            {
                if (step.Kind == ReviewStepKind.GenerateTakes) await RenderAsync(snapshot, plan, step, ct);
                else if (step.Kind == ReviewStepKind.EditImage) await EditAsync(snapshot, plan, step, ct);
                else if (await ApplyAsync(snapshot, Resolved(plan, step), ct) is { } held) await Finish(step, ReviewStepState.Held, held);
                else await Finish(step, ReviewStepState.Done, null);
            }
            // Another save landed between reading and writing: read again and retry this step, a few times at most.
            catch (WorkspaceConflictException)
            {
                conflicts[step.Id] = conflicts.GetValueOrDefault(step.Id) + 1;
                if (conflicts[step.Id] >= 3) await Finish(step, ReviewStepState.Failed, "Something else kept saving this shot while the step was applied. Close other editors of the shot, then start a new plan for this step.");
            }
            catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException) { await Finish(step, ReviewStepState.Failed, e.Message); }
            return true;
        }
        return false;
        Task Finish(ReviewPlanStep step, ReviewStepState state, string? message) => store.UpdateAsync(snapshot.ProjectId, plan.Id,
            p => Set(p, step.Id, s => s with { State = state, Message = message, FinishedUtc = state == ReviewStepState.Held ? null : clock.GetUtcNow() }, ReviewPlans.Director,
                $"{ReviewPlans.Describe(snapshot, step)}: {state switch { ReviewStepState.Done => "done", ReviewStepState.Held => "held", ReviewStepState.Skipped => "skipped", _ => "failed" }}{(message is null ? "" : ". " + message)}"), ct: ct);
    }

    /// <summary>The step this one has to wait for, if any: still open means wait, anything else means it cannot run.</summary>
    internal static ReviewPlanStep? Blocker(ReviewPlan plan, ReviewPlanStep step)
    {
        static bool Stops(ReviewPlanStep s) => s.State is ReviewStepState.Rejected or ReviewStepState.Failed or ReviewStepState.Skipped;
        var index = plan.Steps.ToList().FindIndex(s => s.Id == step.Id);
        // A frame from another shot's new take needs that render picked.
        if (step.Kind == ReviewStepKind.AddShotFrame && step.Frame?.FromShotId is { } from &&
            plan.Steps.FirstOrDefault(s => s.Kind == ReviewStepKind.GenerateTakes && s.ShotId == from) is { State: not ReviewStepState.Done } render) return render;
        // A picture from an edit needs that edit picked.
        if (step.FromStepId is { } edit && plan.Step(edit) is { State: not ReviewStepState.Done } producer) return producer;
        // A render follows the changes proposed for its shot before it.
        if (step.Kind == ReviewStepKind.GenerateTakes)
            return plan.Steps.Take(index).Where(s => s.ShotId == step.ShotId && s.Kind != ReviewStepKind.UseTake).FirstOrDefault(s => s.Open || Stops(s));
        // The cut is pointed at the picks once every pick is made.
        if (step.Kind == ReviewStepKind.UpdateCut) return plan.Steps.FirstOrDefault(s => s.Id != step.Id && s.Kind != ReviewStepKind.UpdateCut && s.Open);
        return null;
    }

    private async Task RenderAsync(ProjectReviewSnapshot snapshot, ReviewPlan plan, ReviewPlanStep step, CancellationToken ct)
    {
        var setup = await RenderSetupAsync(snapshot.ProjectId, step, ct);
        var id = Guid.NewGuid();
        var request = await renders.CaptureAsync(id, snapshot.ProjectId, setup, step.Takes, step.Resolution, ct);
        // Recorded before it is queued: a crash in between leaves a step that reports the lost render, never a second render.
        await store.UpdateAsync(snapshot.ProjectId, plan.Id, p => Set(p, step.Id, s => s with { State = ReviewStepState.Running, JobId = id }, ReviewPlans.Director,
            $"{ReviewPlans.Describe(snapshot, step)}: queued."), ct: ct);
        await renders.EnqueueAsync(request, ct);
    }

    private static ReviewPlanStep Resolved(ReviewPlan plan, ReviewPlanStep step) =>
        step.FromStepId is { } edit && plan.Step(edit) is { } producer ? step with { NewAssetId = producer.AssetId, NewImageId = producer.NewImageId } : step;

    private async Task EditAsync(ProjectReviewSnapshot snapshot, ReviewPlan plan, ReviewPlanStep step, CancellationToken ct)
    {
        var owner = snapshot.Assets.Assets.FirstOrDefault(a => a.Id == step.AssetId); var image = owner?.Images.FirstOrDefault(i => i.Id == step.ImageId);
        if (owner is null || image is null) throw new WorkspaceStoreException("The image to edit is no longer in Assets. It may be in Trash.");
        var id = Guid.NewGuid();
        // Candidates wait for the director, and keep the source's proportions and look. The model is the project's image default.
        var request = await renders.CaptureEditAsync(id, owner.Id, new ReferenceEditRequest
        {
            ProjectId = snapshot.ProjectId, SourceAssetId = owner.Id, SourceImageId = image.Id, Prompt = step.After!, AspectRatio = ImageAspectPolicy.FromImage1,
            Count = step.Takes, PendingReview = true, Look = LookPolicy.Capture(owner, image.LookId)
        }, [new(owner.Id, image.Id)], ct);
        // Recorded before it is queued, as a render is.
        await store.UpdateAsync(snapshot.ProjectId, plan.Id, p => Set(p, step.Id, s => s with { State = ReviewStepState.Running, JobId = id }, ReviewPlans.Director, $"{ReviewPlans.Describe(snapshot, step)}: queued."), ct: ct);
        await renders.EnqueueAsync(request, ct);
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
    Task<AiJobSubmission> CaptureAsync(Guid id, Guid projectId, ProductionComposition setup, int takes, VideoResolution? resolution, CancellationToken ct);
    /// <summary>Captures an image edit with the same checks as the Assets studio.</summary>
    Task<AiJobSubmission> CaptureEditAsync(Guid id, Guid assetId, ReferenceEditRequest request, IReadOnlyList<AssetImageReference> references, CancellationToken ct);
    Task EnqueueAsync(AiJobSubmission request, CancellationToken ct);
    Task CancelAsync(Guid jobId, CancellationToken ct);
    /// <summary>The request's state, or null when the queue no longer lists it.</summary>
    AiJobState? State(Guid jobId);
}

public sealed class ReviewRenderQueue(AiVideoJobCapture capture, AiImageJobCapture images, AiJobCoordinator jobs) : IReviewRenderQueue
{
    /// <summary>The queue's origin for requests a plan started; no browser tab owns them.</summary>
    public static readonly Guid Origin = new("7d0c5a3e-2f0b-4c57-9a55-0c1e5f6b7a11");
    public Task<AiJobSubmission> CaptureAsync(Guid id, Guid projectId, ProductionComposition setup, int takes, VideoResolution? resolution, CancellationToken ct) => resolution is { } size
        ? capture.CaptureCompositionWithPresetAsync(id, Origin, projectId, setup.Id, setup.Version, takes, setup.Seed, H3Presets.Key(setup.Shot), size, ct: ct, generationSetupVersion: setup.GenerationSetupVersion)
        : capture.CaptureCompositionAsync(id, Origin, projectId, setup.Id, setup.Version, takes, setup.Seed, ct, setup.GenerationSetupVersion);
    public Task<AiJobSubmission> CaptureEditAsync(Guid id, Guid assetId, ReferenceEditRequest request, IReadOnlyList<AssetImageReference> references, CancellationToken ct) =>
        images.EditAsync(id, Origin, assetId, request, references, ct);
    public async Task EnqueueAsync(AiJobSubmission request, CancellationToken ct) => await jobs.EnqueueAsync(request, ct);
    public Task CancelAsync(Guid jobId, CancellationToken ct) => jobs.CancelAsync(jobId, ct);
    public AiJobState? State(Guid jobId) => jobs.View.Jobs.FirstOrDefault(j => j.Id == jobId)?.State;
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
