using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    [Inject] public AiVideoJobCapture VideoRequests { get; set; } = null!;
    private readonly Dictionary<Guid, AiVideoJobRequest> _videoRequests = [];
    private readonly Dictionary<Guid, Guid> _videoAppendCommands = [];
    private readonly Dictionary<Guid, AiJobSubmission> _videoEnqueues = [];
    private readonly Dictionary<Guid, AiJobProgress?> _videoProgress = [];
    private ElementReference _videoOriginElement;
    private Guid? _videoOriginJob, _videoReviewOwner, _requestedVideoJob;
    private bool _videoRefreshing, _videoRefreshAgain;
    private ShotTake? _regenerationTake;
    private bool CanRegenerate(ShotTake take) => !_starting && !_mediaBusy && TakeDisplay.RegenerationIssue(take) is null &&
        !AiJobs.View.Jobs.Any(j => j.Kind == AiJobKind.Video && j.Target.ProjectId == Id && j.Target.ShotId == take.ShotId && (j.LocksTarget || j.RemoteUnconfirmed));
    private void RegenerateTake(ShotTake take) { if (CanRegenerate(take)) _regenerationTake = take; }
    private async Task TakeRegenerationSubmitted(AiJobSubmission request)
    {
        await ReviewVisibility(false);
        _videoOriginJob = request.Id;
        Notify("Regeneration queued. Your setup and original take are unchanged.");
        await RefreshMedia();
    }
    private AiJobHeader? ActiveVideoJob => AiJobs.View.Jobs.FirstOrDefault(j => j.Kind == AiJobKind.Video && j.Target.TakeId is null && j.Target.ProjectId == Id && j.Target.ShotId == _selected && j.LocksTarget);
    private AiJobHeader? VideoAttention => AiJobs.View.Jobs.Where(j => j.Kind == AiJobKind.Video && j.Target.ProjectId == Id && j.Target.ShotId == _selected)
        .OrderByDescending(j => j.CreatedUtc).FirstOrDefault() is { State: AiJobState.NeedsAttention, CancelRequested: false } job ? job : null;
    private AiJobHeader? BatchJob(Guid? root) => root is null ? null : AiJobs.View.Jobs.Where(j => j.Batch?.RootId == root)
        .OrderByDescending(j => j.LocksTarget).ThenByDescending(j => j.Batch!.Candidates[0].Number).FirstOrDefault();
    private AiJobHeader? ReviewVideoJob => BatchJob(_reviewRun?.Id);
    private IReadOnlyList<VideoCandidate>? VideoCandidates(AiJobHeader job) => _runs.FirstOrDefault(r => r.Id == job.Batch?.RootId)?.Candidates;
    private AiJobHeader? CandidateJob(int number) => AiJobs.View.Jobs.FirstOrDefault(j => _reviewRun is not null &&
        j.Batch?.RootId == _reviewRun.Id && j.Batch!.Candidates.Any(c => c.Number == number));
    private static string? RecoveryLabel(AiJobHeader? job) => job is null || job.LocksTarget ? null :
        job.CanRecoverCancelledOutputs ? "Recover completed takes" :
        job.RemoteUnconfirmed || job.Recovery == AiJobRecovery.CheckStatus ? "Reconnect/check status" :
        job.Recovery == AiJobRecovery.RetryOutput ? "Retry download/save" : null;
    private bool CanAddVideoTake => _reviewRun is { } run && _videoRequests.TryGetValue(run.Id, out var request) &&
        request.OutputShotId == _selected && !_mediaBusy &&
        !AiJobs.View.Jobs.Any(j => j.Kind == AiJobKind.Video && j.Target.TakeId is null && j.Target.ProjectId == Id && j.Target.ShotId == request.OutputShotId &&
            (j.RemoteUnconfirmed && j.Batch?.RootId == run.Id || j.LocksTarget && (j.Batch?.RootId != run.Id || j.CancelRequested)));
    private async Task ResetVideoProjectAsync()
    {
        await ReleaseVideoReviewAsync();
        if (_videoOriginJob is { } origin) await AiReviews.CloseAsync(origin);
        _videoRequests.Clear(); _videoProgress.Clear(); _videoEnqueues.Clear(); _videoAppendCommands.Clear(); _autoReviewed.Clear();
        _videoOriginJob = _requestedVideoJob = null; _reviewRun = null; _reviewOpen = false;
        _refiningTake = _refinementJob = null; _refinementEnqueue = null;
        _regenerationTake = null;
        _selectingTakes = _moveTakesOpen = false; _moveTakeIds.Clear(); _takeSelectionShot = _moveDestination = null;
    }
    private IReadOnlyList<AiJobHeader> _observedVideoJobs = [];
    private async Task LoadVideoRunsAsync()
    {
        var project = Id; var headers = AiJobs.View.Jobs.Where(j => j.Kind == AiJobKind.Video && j.Target.ProjectId == project &&
            (j.Target.CompositionId is null || _production.Compositions.Any(c => c.Id == j.Target.CompositionId))).ToArray();
        IReadOnlyList<VideoRun> legacy = [];
        foreach (var job in headers.Where(j => j.Batch?.RootId == j.Id))
        {
            if (_videoRequests.ContainsKey(job.Id)) continue;
            // One unreadable earlier batch must not hide the rest of the shot's history.
            try { _videoRequests[job.Id] = AiVideoJobHandler.Read(job, await AiJobStore.ReadSnapshotAsync(job, _lifetime.Token)); }
            catch (Exception e) when (e is WorkspaceStoreException or JsonException) { Logger.LogWarning(e, "Skipped unreadable video batch {Job}", job.Id); }
        }
        // Progress is display-only: keep the last reading rather than failing the page
        // when a checkpoint that the worker keeps rewriting cannot be read.
        foreach (var job in headers)
            try { _videoProgress[job.Id] = await AiJobs.ReadProgressAsync(job.Id, _lifetime.Token); }
            catch (WorkspaceStoreException e) { Logger.LogWarning(e, "Kept the last progress for video job {Job}", job.Id); }
        if (_disposed || project != Id) return;
        _runs = legacy.Where(r => !_videoRequests.ContainsKey(r.Id)).Select(AiVideoBatchReview.Legacy).Concat(_videoRequests.Values.Where(r => r.Snapshot.ProjectId == project)
            .Select(r => AiVideoBatchReview.Project(r, headers, _doc, id => AiJobs.Progress(id) ?? _videoProgress.GetValueOrDefault(id))))
            .OrderByDescending(r => r.CreatedUtc).ToArray();
        _observedVideoJobs = headers;
        foreach (var id in _videoEnqueues.Where(e => headers.Any(j => j.Id == e.Value.Id)).Select(e => e.Key).ToArray()) _videoEnqueues.Remove(id);
        if (_reviewRun is not null) _reviewRun = _runs.FirstOrDefault(r => r.Id == _reviewRun.Id) ?? _reviewRun;
        if (_reviewRun is { } review && !_doc.Takes.Any(t => t.Id == _reviewTakeId && ReviewContains(t)))
            SelectTake(_doc.Takes.FirstOrDefault(t => t.RunId == review.Id)?.Id);
    }
    private async Task OfferVideoReviewAsync()
    {
        if (_videoOriginJob is not { } origin || _autoReviewed.Contains(origin)) return;
        var job = AiJobs.View.Jobs.FirstOrDefault(j => j.Id == origin);
        if (job is null || !_doc.TakePublications.Any(r => r.RunId == origin)) return;
        _autoReviewed.Add(origin);
        if (job.Target.ProjectId != Id || job.Target.ShotId != _selected || job.Target.CompositionId != Current?.Id || job.CancelRequested || _reviewOpen) return;
        if (await AiReviews.TryOpenAsync(job, _videoOriginElement, automatic: true))
        {
            if (_disposed || job.Target.ProjectId != Id || job.Target.ShotId != _selected) { await AiReviews.CloseAsync(job.Id); return; }
            _videoReviewOwner = job.Id;
            if (_runs.FirstOrDefault(r => r.Id == origin) is { } run) ShowRun(run);
        }
    }
    private async Task Generate()
    {
        if (_starting || ActiveVideoJob is not null || Selected is not { } selected || _videoEnqueues.ContainsKey(selected.Id)) return;
        _starting = true; _error = null;
        var project = Id; var shot = ShotVideoDefaults.Capture(selected, _project!); var count = Current!.TakeCount; var seed = Current.Seed;
        try
        {
            if (_promptEditor is not null) await _promptEditor.FlushAsync();
            if (!await Save() || project != Id) return;
            var tab = await AiReviews.TabIdAsync();
            var request = await VideoRequests.CaptureCompositionAsync(Guid.NewGuid(), tab, project, Current!.Id, Current.Version, count, seed, _lifetime.Token, Current.GenerationSetupVersion);
            if (!_dirty) { _production = await Production.LoadAsync(Id, _lifetime.Token); _savedComposition = Current?.Copy(); }
            _videoEnqueues[shot.Id] = request; _videoOriginJob = request.Id;
            await AiJobs.EnqueueAsync(request, _lifetime.Token);
            _videoEnqueues.Remove(shot.Id);
            await RefreshMedia();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { _error = e.Message; }
        finally { _starting = false; }
    }
    private async Task RetryVideoEnqueue()
    {
        if (_starting || _selected is not { } shot || !_videoEnqueues.TryGetValue(shot, out var request)) return;
        _starting = true;
        try { await AiJobs.EnqueueAsync(request, _lifetime.Token); _videoEnqueues.Remove(shot); _error = null; await RefreshMedia(); }
        catch (Exception e) { _error = e.Message; }
        finally { _starting = false; }
    }
    private void JobChanged()
    {
        if (_disposed) return;
        _ = InvokeAsync(async () =>
        {
            if (_videoRefreshing) { _videoRefreshAgain = true; return; }
            _videoRefreshing = true;
            try { do { _videoRefreshAgain = false; await RefreshMedia(); StateHasChanged(); } while (_videoRefreshAgain && !_disposed); }
            finally { _videoRefreshing = false; }
        });
    }
    private bool RunBelongsToShot(VideoRun run, Guid? shotId) =>
        (_videoRequests.GetValueOrDefault(run.Id)?.OutputShotId ?? run.Snapshot.Shot.Id) == shotId ||
        _doc.Takes.Any(t => t.RunId == run.Id && t.ShotId == shotId);
    private async Task OpenLatestReview()
    { if (_runs.FirstOrDefault(r => RunBelongsToShot(r, _selected)) is { } run) await OpenRun(run); }
    private void ShowRun(VideoRun run)
    {
        if (run.Refinement is not null) _refinementJob = run.Id;
        run = ReviewRoot(run);
        _reviewRun = run.Copy(); _reviewOpen = true; _autoReviewed.Add(run.Id);
        if (!_doc.Takes.Any(t => t.Id == _reviewTakeId && ReviewContains(t))) SelectTake(_doc.Takes.FirstOrDefault(t => t.RunId == run.Id)?.Id ?? _doc.Takes.FirstOrDefault(ReviewContains)?.Id);
    }
    private async Task OpenRun(VideoRun run)
    {
        if (run.Snapshot.ProjectId != Id) return;
        if (BatchJob(run.Id) is { } job)
        {
            if (!await AiReviews.TryOpenAsync(job, _videoOriginElement, automatic: false)) return;
            _videoReviewOwner = job.Id;
        }
        ShowRun(run);
    }
    private async Task OpenTake(ShotTake take)
    {
        if (_runs.FirstOrDefault(r => r.Id == take.RunId) is { } run) { await OpenRun(run); SelectTake(take.Id); return; }
        ShowRun(AiVideoBatchReview.Archived(_doc.Takes.Where(t => t.RunId == take.RunId).ToArray()));
        SelectTake(take.Id);
    }
    private async Task ReviewVisibility(bool open)
    {
        _reviewOpen = open; _restoreReviewFocus = !open;
        if (!open) { _refiningTake = null; if (_reviewRun is not null) _autoReviewed.Add(_reviewRun.Id); await ReleaseVideoReviewAsync(); }
    }
    private async Task ReleaseVideoReviewAsync()
    { if (_videoReviewOwner is { } id) { _videoReviewOwner = null; await AiReviews.CloseAsync(id); } }
    private async Task CancelGeneration(Guid? id = null)
    { if ((id ?? ActiveVideoJob?.Id) is { } target) { try { await AiJobs.CancelAsync(target); } catch (Exception e) { _error = e.Message; } } }
    private async Task OneMore()
    {
        if (!CanAddVideoTake || _reviewRun is not { } run) return;
        _mediaBusy = true; _reviewError = null;
        if (!_videoAppendCommands.TryGetValue(run.Id, out var command)) _videoAppendCommands[run.Id] = command = Guid.NewGuid();
        try
        {
            await AiJobs.ExtendBatchAsync(run.Id, command, await AiReviews.TabIdAsync(), _lifetime.Token);
            _videoAppendCommands.Remove(run.Id); await RefreshMedia();
        }
        catch (Exception e) { _reviewError = e.Message; }
        finally { _mediaBusy = false; }
    }
    private async Task RetryCandidate(int number)
    {
        var job = CandidateJob(number);
        if (job is null) return;
        try { await AiJobs.ResumeAsync(job.Id, _lifetime.Token); }
        catch (Exception e) { _reviewError = e.Message; }
    }
    private async Task RecoverVideoOutputs(Guid job)
    {
        try { await AiJobs.ResumeAsync(job, _lifetime.Token); }
        catch (Exception e) { _reviewError = e.Message; }
    }
    private async Task HandleRequestedVideoAsync()
    {
        if (!_interactive || !_planningInitialized || _doc.ProjectId != Id || RequestedJobId is not { } id || _requestedVideoJob == id) return;
        var job = AiJobs.View.Jobs.FirstOrDefault(j => j.Id == id && j.Kind == AiJobKind.Video && j.Target.ProjectId == Id);
        if (job is null) return;
        _requestedVideoJob = id;
        if (_doc.Shots.All(s => s.Id != job.Target.ShotId)) { _error = "The shot was removed. Its saved request remains inspectable in AI activity."; return; }
        if (!await Save()) return;
        if (job.Target.CompositionId is { } composition) await SelectComposition(composition); else await Select(job.Target.ShotId!.Value); await RefreshMedia();
        if (_runs.FirstOrDefault(r => r.Id == job.Batch?.RootId) is { } run) { await OpenRun(run); StateHasChanged(); }
    }
}
