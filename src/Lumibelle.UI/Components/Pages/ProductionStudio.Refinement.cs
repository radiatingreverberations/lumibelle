using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private Guid? _refiningTake;
    private bool _refinementBusy;
    private string? _refinementIssue;
    private AiJobSubmission? _refinementEnqueue;
    private Guid? _refinementJob;
    private MudBlazor.MudButton? _refineButton;
    private bool _restoreRefinementFocus;
    private void CloseRefinement() { _refiningTake = null; _restoreRefinementFocus = true; }
    private ShotTake? RefinementSource => _doc.Takes.FirstOrDefault(t => t.Id == _refiningTake);
    private AiJobHeader? RefinementJob => BatchJob(_refinementJob);
    private bool RefinementLocked(ShotTake take) => AiJobs.View.Jobs.Any(j => j.Kind == AiJobKind.Video && j.Target.ProjectId == Id && j.Target.TakeId == take.Id && j.LocksTarget);
    private bool ReviewContains(ShotTake take) => _reviewRun is not null &&
        (take.RunId == _reviewRun.Id || _runs.FirstOrDefault(r => r.Id == take.RunId) is { } run && ReviewRoot(run).Id == _reviewRun.Id);
    private string? RefineIssue(ShotTake take) => take.RefinementPackage is null ? TakeDisplay.NoLatents
        : RefinementLocked(take) ? "This take is already being refined." : null;
    private bool CanRefine(ShotTake take) => RefineIssue(take) is null && !_refinementBusy && _refinementEnqueue is null;
    // Opens the take in review, where the refinement options appear above the player.
    private async Task RefineTake(ShotTake take)
    {
        if (!CanRefine(take)) return;
        if (!_reviewOpen || ReviewTake?.Id != take.Id) await OpenTake(take);
        await ConfigureRefinement();
    }
    private async Task ConfigureRefinement()
    {
        if (ReviewTake is not { RefinementPackage: not null } take || _refinementBusy || RefinementLocked(take)) return;
        _refiningTake = take.Id; _refinementIssue = "Checking refinement setup…";
        await CheckRefinementSetup();
    }
    private async Task CheckRefinementSetup()
    {
        if (RefinementSource is not { } take) return;
        _refinementBusy = true;
        try
        {
            var configured = await Settings.LoadAsync(_lifetime.Token);
            var check = await Generator.CheckAsync(new() { ComfyUrl = take.Snapshot.ComfyUrl, H3 = take.Snapshot.Settings with { LatentUpscaler = configured.H3.LatentUpscaler } }, _lifetime.Token);
            _refinementIssue = check.RefinementIssue;
        }
        catch (Exception e) { _refinementIssue = e.Message; }
        finally { _refinementBusy = false; }
    }
    private async Task QueueRefinement((TakeRefinementMode Mode, int Width, int Height) options)
    {
        if (_refinementBusy || RefinementSource is not { } take || _refinementEnqueue is not null) return;
        _refinementBusy = true; _reviewError = null;
        try
        {
            _refinementEnqueue = await VideoRequests.CaptureRefinementAsync(Guid.NewGuid(), await AiReviews.TabIdAsync(), Id, take.Id,
                options.Mode, options.Width, options.Height, _lifetime.Token);
            await EnqueueRefinement();
        }
        catch (Exception e) { _reviewError = e.Message; }
        finally { _refinementBusy = false; }
    }
    private async Task RetryRefinementEnqueue()
    {
        if (_refinementBusy || _refinementEnqueue is null) return;
        _refinementBusy = true;
        try { await EnqueueRefinement(); }
        catch (Exception e) { _reviewError = e.Message; }
        finally { _refinementBusy = false; }
    }
    private async Task EnqueueRefinement()
    {
        var request = _refinementEnqueue!;
        await AiJobs.EnqueueAsync(request, _lifetime.Token);
        _refinementEnqueue = null; _refinementJob = request.Id; _refiningTake = null; _reviewError = null;
        // This review is already open; queue completion must not open another dialog.
        _autoReviewed.Add(request.Id);
        await RefreshMedia();
    }
    private async Task RemoveFailedVersion(VideoRun run)
    {
        if (BatchJob(run.Id) is not { } job) return;
        try { await AiJobs.CancelAsync(job.Id, _lifetime.Token); await RefreshMedia(); }
        catch (Exception e) { _reviewError = e.Message; }
    }
    private async Task RetryFailedVersion(VideoRun run)
    {
        if (BatchJob(run.Id) is not { } job) return;
        try { await AiJobs.ResumeAsync(job.Id, _lifetime.Token); await RefreshMedia(); }
        catch (Exception e) { _reviewError = e.Message; }
    }
    private async Task AnotherVersion()
    {
        var root = ReviewTake is { } reviewed && (reviewed.Refinement is not null || TakeDisplay.HasExtensionReplay(reviewed)) ? reviewed.RunId : _refinementJob;
        if (_refinementBusy || root is null) return;
        _refinementBusy = true; _reviewError = null;
        if (!_videoAppendCommands.TryGetValue(root.Value, out var command)) _videoAppendCommands[root.Value] = command = Guid.NewGuid();
        try
        {
            if (ReviewTake is { } extended && TakeDisplay.HasExtensionReplay(extended)) {
                _refinementEnqueue = await VideoRequests.CaptureExtensionVersionAsync(Guid.NewGuid(), await AiReviews.TabIdAsync(), Id, extended.Id, _lifetime.Token);
                await EnqueueRefinement(); return;
            }
            if (ReviewTake is { Trim: { } trim } take && _videoRequests.GetValueOrDefault(root.Value)?.OutputTrim != new TakeTrimRange(trim.SourceStartFrame, trim.SourceEndFrameExclusive)) {
                _refinementEnqueue = await VideoRequests.CaptureTrimmedVersionAsync(Guid.NewGuid(), await AiReviews.TabIdAsync(), Id, take.Id, _lifetime.Token);
                await EnqueueRefinement();
                return;
            }
            await AiJobs.ExtendBatchAsync(root.Value, command, await AiReviews.TabIdAsync(), _lifetime.Token);
            _videoAppendCommands.Remove(root.Value); _refinementJob = root; await RefreshMedia();
        }
        catch (Exception e) { _reviewError = e.Message; }
        finally { _refinementBusy = false; }
    }
    private VideoRun ReviewRoot(VideoRun run)
    {
        HashSet<Guid> visited = [];
        while (run.Refinement is { } refinement && visited.Add(run.Id))
        {
            var parentRunId = _doc.Takes.FirstOrDefault(t => t.Id == refinement.ParentTakeId)?.RunId;
            var parent = _runs.FirstOrDefault(r => r.Candidates.Any(c => c.TakeId == refinement.ParentTakeId) || r.Id == parentRunId)
                ?? (_reviewRun?.Id == parentRunId ? _reviewRun : null);
            if (parent is null) break;
            run = parent;
        }
        return run;
    }
}
