using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

// Refine-from-video batches: one ordinary generation job per source clip. Media is prepared in the
// background without the GPU slot; ready items are submitted to the ComfyUI queue one at a time.
public sealed partial class RefinementBatchWorkflow(FileRefinementBatchStore store, AiVideoJobCapture shotCapture, AiReelCapture reelCapture,
    IAiJobStore jobs, AiJobCoordinator queue, IShotStore shots, IAssetStore assets, IAssetReelStore reels, IAiSettingsStore settings,
    IVideoGenerator generator, IReferenceVideoStore media, TimeProvider clock)
{
    private static readonly TimeSpan CheckLifetime = TimeSpan.FromMinutes(5), UnreachableLifetime = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Created, Task<H3Configuration> Check)> _checks = new();
    public event Action<Guid> Changed { add => store.Changed += value; remove => store.Changed -= value; }

    public Task<RefinementBatches> LoadAsync(Guid project, CancellationToken ct = default) => store.LoadAsync(project, ct);

    // One model discovery per server and H3 configuration, shared by the dialog and background preparation.
    public Task<H3Configuration> CheckModelsAsync(AiSettings configured, bool refresh = false, CancellationToken ct = default)
    {
        var key = AiProviderRegistry.NormalizeComfyUrl(configured.ComfyUrl) + "\n" + JsonSerializer.Serialize(configured.H3, AtomicJsonFile.Options);
        var now = clock.GetUtcNow();
        // A failed discovery (ComfyUI starting or restarting) is only reused briefly.
        if (!refresh && _checks.TryGetValue(key, out var cached) && !cached.Check.IsFaulted && !cached.Check.IsCanceled &&
            now - cached.Created < (cached.Check.IsCompletedSuccessfully && !cached.Check.Result.DiscoverySucceeded ? UnreachableLifetime : CheckLifetime))
            return cached.Check.WaitAsync(ct);
        // Shared by every caller, so one caller's cancellation (a closed dialog) must not cancel it for the others.
        var check = generator.CheckAsync(ShotCopy.Of(configured), CancellationToken.None);
        _checks[key] = (now, check);
        return check.WaitAsync(ct);
    }

    public async Task<RefinementBatch> CreateAsync(Guid project, Guid tab, RefinementBatchRequest request, CancellationToken ct = default)
    {
        if (request.BatchId == Guid.Empty || tab == Guid.Empty || request.Items is not { Count: > 0 and <= FileRefinementBatchStore.MaximumItems })
            throw new WorkspaceStoreException($"Choose one to {FileRefinementBatchStore.MaximumItems} clips to refine.");
        // A repeated Queue click returns the batch it already created.
        if ((await store.LoadAsync(project, ct)).Runs.SingleOrDefault(r => r.Id == request.BatchId) is { } existing) return existing;
        List<RefinementBatchItem> items = [];
        foreach (var entry in request.Items)
        {
            var options = ShotCopy.Of(entry.Options) with { Seed = entry.Options.Seed ?? RandomNumberGenerator.GetInt32(int.MaxValue) };
            if (MotionReferencePolicy.Issue(options) is { } issue) throw new WorkspaceStoreException($"{entry.Name}: {issue}");
            if ((entry.Kind == RefinementSourceKind.Reel) != (options.Draft.Reel is not null) ||
                (entry.Kind == RefinementSourceKind.Take ? options.Draft.Source.TakeId : options.Draft.Source.ReelId) != entry.SourceId)
                throw new WorkspaceStoreException($"{entry.Name}: The reviewed settings belong to another clip.");
            // The guide is the independent copy made during review; the source take or reel is no longer needed.
            var guide = options.Draft.Shot.Videos.Single(v => v.Id == options.Draft.Source.BindingId);
            await media.ValidateAsync(project, [guide], ct);
            items.Add(new() { Id = Guid.NewGuid(), Kind = entry.Kind, SourceId = entry.SourceId, Name = entry.Name.Trim(), Options = options,
                OwnerId = options.Draft.Reel?.Recipe.AssetId ?? options.Draft.Shot.Id });
        }
        var run = new RefinementBatch { Id = request.BatchId, TabId = tab, CreatedUtc = clock.GetUtcNow(), Items = items,
            Name = string.IsNullOrWhiteSpace(request.Name) ? $"Refine {items.Count} clip{(items.Count == 1 ? "" : "s")}" : request.Name.Trim(),
            State = request.StartNow ? RefinementBatchState.Running : RefinementBatchState.Held, UseRefinedFrames = request.UseRefinedFrames };
        var saved = await store.UpdateAsync(project, doc => Task.FromResult(doc.Runs.Any(r => r.Id == run.Id) ? doc : doc with { Runs = [.. doc.Runs, run] }), ct);
        return saved.Runs.Single(r => r.Id == request.BatchId);
    }

    // A clip waiting for an earlier clip's refined take is skipped until that clip finishes, so it holds no preparation slot.
    public async Task<IReadOnlyList<(Guid Batch, Guid Item)>> PreparingAsync(Guid project, CancellationToken ct = default)
    {
        var runs = (await store.LoadAsync(project, ct)).Runs;
        if (!runs.Any(r => r.Items.Any(i => i.State == RefinementItemState.Preparing))) return [];
        var queued = runs.Any(r => r.Items.Any(i => i.WaitingForItemId is not null)) ? (await jobs.ReadAsync(ct)).Jobs : [];
        return runs.SelectMany(r => r.Items.Where(i => i.State == RefinementItemState.Preparing &&
            !(i.WaitingForItemId is { } waiting && r.Items.SingleOrDefault(d => d.Id == waiting) is { } dependency && StillPending(r, i, dependency, queued)))
            .Select(i => (r.Id, i.Id))).ToArray();
    }

    // Restart-safe: an interrupted preparation never reached the queue, so its partial inputs are discarded and rebuilt.
    public async Task PrepareAsync(Guid project, Guid batchId, Guid itemId, CancellationToken ct = default)
    {
        var run = (await store.LoadAsync(project, ct)).Runs.SingleOrDefault(r => r.Id == batchId);
        if (run?.Items.SingleOrDefault(i => i.Id == itemId) is not { State: RefinementItemState.Preparing } item) return;
        AiJobSubmission? submission = null; string? error = null;
        var frames = await ResolveRefinedFramesAsync(project, run, item, ct);
        if (frames.WaitFor is { } dependency)
        {
            await store.UpdateAsync(project, doc =>
            {
                var current = doc.Runs.SingleOrDefault(r => r.Id == batchId);
                if (current?.Items.SingleOrDefault(i => i.Id == itemId) is not { State: RefinementItemState.Preparing } latest) return Task.FromResult(doc);
                latest = latest with { WaitingForItemId = dependency.Id, Notice = $"Waiting for the refined take of {dependency.Name}." };
                return Task.FromResult(FileRefinementBatchStore.Replace(doc, FileRefinementBatchStore.Replace(current, latest)));
            }, ct);
            return;
        }
        try
        {
            var check = await CheckModelsAsync(await settings.LoadAsync(ct), ct: ct);
            if (!check.DiscoverySucceeded)
            {
                // ComfyUI is unreachable for now: stay preparing and try again, rather than failing every clip of an overnight batch.
                var notice = "Waiting for ComfyUI: " + check.Issue(item.Options.Draft.Shot);
                await store.UpdateAsync(project, doc =>
                {
                    var current = doc.Runs.SingleOrDefault(r => r.Id == batchId);
                    if (current?.Items.SingleOrDefault(i => i.Id == itemId) is not { State: RefinementItemState.Preparing } latest) return Task.FromResult(doc);
                    return Task.FromResult(FileRefinementBatchStore.Replace(doc, FileRefinementBatchStore.Replace(current, latest with { WaitingForItemId = null, Notice = notice })));
                }, ct);
                return;
            }
            if (item.Kind == RefinementSourceKind.Take)
            {
                await shotCapture.DiscardUnqueuedInputsAsync(project, item.Id, ct);
                submission = await shotCapture.CaptureMotionReferenceAsync(item.Id, run.TabId, project, frames.Options, ct, check: check, frozen: true,
                    keepFrames: FramesUsedLater(run, item));
            }
            else
            {
                await reelCapture.DiscardUnqueuedInputsAsync(project, item.Id, ct);
                submission = await reelCapture.CaptureMotionReferenceAsync(item.Id, run.TabId, project, item.Options, ct, check: check, frozen: true);
            }
        }
        // Anything but shutdown fails the clip visibly with Retry, instead of capturing it again on every pass.
        catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
        { error = e is WorkspaceStoreException or AiGenerationException or ProjectStoreException or IOException or InvalidOperationException or HttpRequestException or UnauthorizedAccessException
            ? e.Message : $"Preparation failed unexpectedly: {e.Message}"; }
        await store.UpdateAsync(project, doc =>
        {
            var current = doc.Runs.SingleOrDefault(r => r.Id == batchId);
            // Cancelled while preparing: keep the cancellation; the unqueued inputs are unused.
            if (current?.Items.SingleOrDefault(i => i.Id == itemId) is not { State: RefinementItemState.Preparing } latest) return Task.FromResult(doc);
            latest = submission is null ? latest with { State = RefinementItemState.Failed, Error = error, WaitingForItemId = null, Notice = null }
                : latest with { State = RefinementItemState.Ready, Submission = submission, Error = null, WaitingForItemId = null, Notice = null,
                    Options = frames.Options, Adjustments = frames.Adjustments };
            return Task.FromResult(FileRefinementBatchStore.Replace(doc, FileRefinementBatchStore.Replace(current, latest)));
        }, ct);
    }

    public async Task AdvanceAsync(Guid project, CancellationToken ct = default)
    {
        var saved = await store.LoadAsync(project, ct);
        if (!saved.Runs.Any(r => r.Active || r.Items.Any(i => i.State == RefinementItemState.Submitted && i.ResultId is null))) return;
        await store.UpdateAsync(project, async doc =>
        {
            var queued = (await jobs.ReadAsync(ct)).Jobs;
            foreach (var run in doc.Runs) doc = FileRefinementBatchStore.Replace(doc, await AdvanceAsync(project, run, queued, ct));
            return doc;
        }, ct);
    }

    private async Task<RefinementBatch> AdvanceAsync(Guid project, RefinementBatch run, IReadOnlyList<AiJobHeader> queued, CancellationToken ct)
    {
        // A completed job has published its result. One that cannot be found (published straight to Trash after its
        // shot or asset was removed) fails here, so clips waiting for its frames fall back to their drafts.
        foreach (var item in run.Items.Where(i => i.State == RefinementItemState.Submitted && i.ResultId is null))
            if (queued.SingleOrDefault(j => j.Id == item.Id) is { State: AiJobState.Completed })
                run = FileRefinementBatchStore.Replace(run, await ResultAsync(project, item, ct) is { } result ? item with { ResultId = result }
                    : item with { State = RefinementItemState.Failed, Notice = null,
                        Error = item.Kind == RefinementSourceKind.Take ? "The refined take is not in its shot. Look in Trash." : "The refined reel is not in its asset. Look in Trash." });
        if (!run.Active) return run;
        // One render at a time. A submitted request is never submitted again from here: a missing
        // job was removed from the queue, and Retry requeues the saved request explicitly.
        if (run.Items.Any(i => i.State == RefinementItemState.Submitted && Unfinished(queued.SingleOrDefault(j => j.Id == i.Id)))) return run;
        if (run.State == RefinementBatchState.Running && run.PauseRequested) return run with { State = RefinementBatchState.Paused, PauseRequested = false };
        if (run.State == RefinementBatchState.Running && run.Items.FirstOrDefault(i => i.State == RefinementItemState.Ready) is { Submission: { } submission } next)
        {
            var key = AiJobLocks.Key(submission.Kind, submission.Target, submission.Batch);
            if (queued.Any(j => j.Id != next.Id && j.LocksTarget && AiJobLocks.Key(j) == key))
                return FileRefinementBatchStore.Replace(run, next with { Notice = next.Kind == RefinementSourceKind.Take
                    ? "Waiting for another request on this shot to finish." : "Waiting for another request on this reel to finish." });
            try
            {
                // Idempotent by ID and fingerprint: an unacknowledged enqueue is confirmed on the next pass.
                await queue.EnqueueAsync(submission, ct);
                return FileRefinementBatchStore.Replace(run, next with { State = RefinementItemState.Submitted, Notice = null });
            }
            catch (WorkspaceStoreException e) { return FileRefinementBatchStore.Replace(run, next with { Notice = e.Message }); }
        }
        return run.Items.Any(i => i.State is RefinementItemState.Preparing or RefinementItemState.Ready)
            ? run : run with { State = RefinementBatchState.Finished, PauseRequested = false };
    }

    private static bool Unfinished(AiJobHeader? job) => job is { State: AiJobState.Waiting or AiJobState.Running } || job?.RemoteUnconfirmed == true;

    private async Task<Guid?> ResultAsync(Guid project, RefinementBatchItem item, CancellationToken ct) => item.Kind == RefinementSourceKind.Take
        ? (await shots.LoadAsync(project, ct)).Takes.FirstOrDefault(t => t.AiJobId == item.Id)?.Id
        : (await assets.LoadAsync(project, ct)).Reels.FirstOrDefault(r => r.Generation?.JobId == item.Id)?.Id;

    private Task<RefinementBatches> UpdateRunAsync(Guid project, Guid batch, Func<RefinementBatch, Task<RefinementBatch>> update, CancellationToken ct) =>
        store.UpdateAsync(project, async doc =>
            FileRefinementBatchStore.Replace(doc, await update(doc.Runs.SingleOrDefault(r => r.Id == batch) ?? throw new WorkspaceStoreException("This refinement batch is unavailable."))), ct);
    private static RefinementBatchItem Item(RefinementBatch run, Guid item) =>
        run.Items.SingleOrDefault(i => i.Id == item) ?? throw new WorkspaceStoreException("This clip is no longer in the batch.");

    public Task<RefinementBatches> StartAsync(Guid project, Guid batch, CancellationToken ct = default) => UpdateRunAsync(project, batch, run =>
        Task.FromResult(run.State is RefinementBatchState.Held or RefinementBatchState.Paused || run.PauseRequested
            ? run with { State = RefinementBatchState.Running, PauseRequested = false } : run), ct);

    // The current render finishes; nothing further is submitted until the batch is started again.
    public Task<RefinementBatches> PauseAsync(Guid project, Guid batch, CancellationToken ct = default) => UpdateRunAsync(project, batch, async run =>
    {
        if (run.State != RefinementBatchState.Running) return run;
        var queued = (await jobs.ReadAsync(ct)).Jobs;
        return run.Items.Any(i => i.State == RefinementItemState.Submitted && Unfinished(queued.SingleOrDefault(j => j.Id == i.Id)))
            ? run with { PauseRequested = true } : run with { State = RefinementBatchState.Paused };
    }, ct);

    // Clips not yet submitted are cancelled. The current render continues unless cancelled explicitly.
    public Task<RefinementBatches> CancelWaitingAsync(Guid project, Guid batch, CancellationToken ct = default) => UpdateRunAsync(project, batch, run =>
        Task.FromResult(run with { Items = run.Items.Select(i => i.State is RefinementItemState.Preparing or RefinementItemState.Ready
            ? i with { State = RefinementItemState.Cancelled, Notice = null } : i).ToArray() }), ct);

    public async Task CancelCurrentAsync(Guid project, Guid batch, CancellationToken ct = default)
    {
        var run = (await store.LoadAsync(project, ct)).Runs.SingleOrDefault(r => r.Id == batch) ?? throw new WorkspaceStoreException("This refinement batch is unavailable.");
        var queued = (await jobs.ReadAsync(ct)).Jobs;
        foreach (var item in run.Items.Where(i => i.State == RefinementItemState.Submitted && Unfinished(queued.SingleOrDefault(j => j.Id == i.Id))))
            await queue.CancelAsync(item.Id, ct);
    }

    // Retries reuse the frozen settings and seed. A request that needs attention is requeued as saved;
    // a cancelled one is prepared again from the secured guide under a new job ID. Changing settings needs a new request.
    public async Task RetryAsync(Guid project, Guid batch, Guid itemId, CancellationToken ct = default)
    {
        var job = (await jobs.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == itemId);
        var requeue = false;
        await UpdateRunAsync(project, batch, run =>
        {
            var item = Item(run, itemId);
            var fresh = item with { Id = Guid.NewGuid(), State = RefinementItemState.Preparing, Submission = null, ResultId = null, Error = null, Notice = null, WaitingForItemId = null,
                ReviewedUtc = null, AcceptedUtc = null };
            item = item.State switch {
                // Its job finished but the result is gone: a new request, since the old ID belongs to that job.
                RefinementItemState.Failed when item.Submission is not null => fresh,
                RefinementItemState.Failed => item with { State = RefinementItemState.Preparing, Error = null },
                RefinementItemState.Submitted when job is null && item.ResultId is null => fresh,
                RefinementItemState.Cancelled when item.Submission is null => item with { State = RefinementItemState.Preparing, Error = null },
                RefinementItemState.Cancelled => item with { State = RefinementItemState.Ready, Error = null },
                RefinementItemState.Submitted when job is { State: AiJobState.NeedsAttention, CancelRequested: false, RemoteUnconfirmed: false } => item,
                RefinementItemState.Submitted when job is { State: AiJobState.Cancelled, RemoteUnconfirmed: false } && item.ResultId is null => fresh,
                _ => throw new WorkspaceStoreException("This clip has no retryable request.")
            };
            requeue = item.Id == itemId && item.State == RefinementItemState.Submitted;
            return Task.FromResult(run with {
                Items = run.Items.Select(i => i.Id == itemId ? item : i).ToArray(),
                State = run.State == RefinementBatchState.Finished ? RefinementBatchState.Running : run.State });
        }, ct);
        if (requeue) await queue.ResumeAsync(itemId, ct);
    }

    public Task<RefinementBatches> MarkReviewedAsync(Guid project, Guid batch, Guid itemId, bool reviewed, CancellationToken ct = default) => UpdateRunAsync(project, batch, run =>
    {
        var item = Item(run, itemId);
        if (item.AcceptedUtc is not null && !reviewed) throw new WorkspaceStoreException("An accepted result stays reviewed.");
        return Task.FromResult(FileRefinementBatchStore.Replace(run, item with { ReviewedUtc = reviewed ? item.ReviewedUtc ?? clock.GetUtcNow() : null }));
    }, ct);

    // Shots select the new take. Reels mark the final as preferred; shots keep their reel until replaced.
    public async Task AcceptAsync(Guid project, Guid batch, Guid itemId, CancellationToken ct = default)
    {
        var item = Item((await store.LoadAsync(project, ct)).Runs.SingleOrDefault(r => r.Id == batch) ?? throw new WorkspaceStoreException("This refinement batch is unavailable."), itemId);
        if (item.ResultId is not { } result) throw new WorkspaceStoreException("Wait for this clip's result before accepting it.");
        if (item.Kind == RefinementSourceKind.Take)
        {
            var doc = await shots.LoadAsync(project, ct);
            var take = doc.Takes.SingleOrDefault(t => t.Id == result) ?? throw new WorkspaceStoreException("The refined take was discarded. Restore it before accepting.");
            var shot = doc.Shots.SingleOrDefault(s => s.Id == take.ShotId) ?? throw new WorkspaceStoreException("The refined take's shot is unavailable.");
            if (shot.SelectedTakeId != take.Id) { shot.SelectedTakeId = take.Id; await shots.SaveAsync(project, doc.Shots, doc.Revision, "Accept refined take", ct); }
        }
        else
        {
            var library = await assets.LoadAsync(project, ct);
            if (!library.Reels.Any(r => r.Id == result)) throw new WorkspaceStoreException("The refined reel was moved to Trash. Restore it before accepting.");
            if (library.Reels.SingleOrDefault(r => r.Id == item.SourceId) is { } source && source.ReplacedByReelId != result)
                await reels.SaveReelAsync(project, source with { ReplacedByReelId = result }, library.Revision, ct);
        }
        await UpdateRunAsync(project, batch, run =>
        {
            var latest = Item(run, itemId); var now = clock.GetUtcNow();
            return Task.FromResult(FileRefinementBatchStore.Replace(run, latest with { ReviewedUtc = latest.ReviewedUtc ?? now, AcceptedUtc = latest.AcceptedUtc ?? now }));
        }, ct);
    }

    public static IReadOnlyList<RefinementItemView> View(RefinementBatch run, IReadOnlyList<AiJobHeader> queued) => run.Items.Select(item =>
    {
        var job = queued.SingleOrDefault(j => j.Id == item.Id);
        var stage = item.State switch {
            RefinementItemState.Preparing => RefinementItemStage.Preparing,
            RefinementItemState.Ready => run.State == RefinementBatchState.Running ? RefinementItemStage.Waiting : RefinementItemStage.Ready,
            RefinementItemState.Failed => RefinementItemStage.Failed,
            RefinementItemState.Cancelled => RefinementItemStage.Cancelled,
            _ => job?.State switch {
                AiJobState.Waiting => RefinementItemStage.Waiting,
                AiJobState.Running => RefinementItemStage.Generating,
                AiJobState.Completed => RefinementItemStage.Completed,
                AiJobState.Cancelled => RefinementItemStage.Cancelled,
                AiJobState.NeedsAttention => RefinementItemStage.NeedsAttention,
                _ => item.ResultId is null ? RefinementItemStage.NeedsAttention : RefinementItemStage.Completed
            }
        };
        return new RefinementItemView(item, stage, job);
    }).ToArray();

    public static RefinementBatchCounts Count(IReadOnlyList<RefinementItemView> items) => new(
        items.Count(i => i.Stage == RefinementItemStage.Completed),
        items.Count(i => i.Stage is RefinementItemStage.Failed or RefinementItemStage.NeedsAttention),
        items.Count(i => i.Stage is RefinementItemStage.Preparing or RefinementItemStage.Ready or RefinementItemStage.Waiting or RefinementItemStage.Generating),
        items.Count(i => i.Item.ReviewedUtc is not null), items.Count);
}

public sealed class RefinementBatchWorker(IProjectStore projects, RefinementBatchWorkflow workflow, AiJobCoordinator queue, ILogger<RefinementBatchWorker> logger) : BackgroundService
{
    // Local FFmpeg preparation runs beside, not inside, the single ComfyUI render slot.
    public const int PreparationSlots = 2;
    private readonly ConcurrentDictionary<Guid, Task> _preparing = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void Changed(Guid _) => Wake();
        workflow.Changed += Changed; queue.Changed += Wake;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await TickAsync(stoppingToken);
                await _wake.WaitAsync(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            workflow.Changed -= Changed; queue.Changed -= Wake;
            await Task.WhenAll(_preparing.Values).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        IReadOnlyList<ProjectInfo> list;
        try { list = (await projects.ListAsync(ct)).Projects; }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or IOException)
        { logger.LogWarning(e, "Refinement batch discovery unavailable"); return; }
        foreach (var project in list)
        {
            try
            {
                await workflow.AdvanceAsync(project.Id, ct);
                foreach (var (batch, item) in await workflow.PreparingAsync(project.Id, ct))
                {
                    if (_preparing.Count >= PreparationSlots) break;
                    if (_preparing.ContainsKey(item)) continue;
                    _preparing[item] = Task.Run(() => PrepareAsync(project.Id, batch, item, ct), CancellationToken.None);
                }
            }
            catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or IOException)
            { logger.LogWarning(e, "Refinement batch checkpoint unavailable for {Project}", project.Id); }
        }
    }

    private async Task PrepareAsync(Guid project, Guid batch, Guid item, CancellationToken ct)
    {
        try { await workflow.PrepareAsync(project, batch, item, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { logger.LogWarning(e, "Refinement preparation failed for {Item}", item); }
        finally { _preparing.TryRemove(item, out _); Wake(); }
    }
}
