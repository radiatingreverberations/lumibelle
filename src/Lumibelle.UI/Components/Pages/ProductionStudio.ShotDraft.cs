using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

// Draft this shot: one shot drafted in place from its scene, beside the scene's other shots.
public partial class ProductionStudio
{
    private lumibelle.Components.AI.TextAssistance? _shotDraftAssist;
    private TextModelSelectionState? _shotDraftModel;
    private string _shotDraftDirections = "";
    private double _shotDraftMaximum = 15;
    private bool _shotDraftSubmitting, _shotDraftOpen;
    private AiJobSubmission? _pendingShotDraft;
    private string? _shotDraftError;
    private Guid? _shotDraftOrigin, _shotDraftReviewOwner;
    private int _shotDraftCloses;
    private AiJobHeader? _shotDraftReviewJob;
    private ShotPlanningResult? _shotDraftResult;
    private string _shotDraftRaw = "";
    private ElementReference _shotDraftOriginElement;
    // Requests applied to or dismissed from their shot, remembered per project so they stop asking for review.
    private HashSet<Guid> _resolvedShotDrafts = [];

    private static bool SingleShotJob(AiJobHeader job) => job.Kind == AiJobKind.ShotPlanning && job.Target.ShotId is not null;
    private AiJobHeader? ShotDraftJob(Shot shot) => AiJobs.View.Jobs.Where(j => SingleShotJob(j) && j.Target.ProjectId == Id && j.Target.ShotId == shot.Id)
        .OrderByDescending(j => TextRequestPresentation.IsActive(j)).ThenByDescending(j => j.CreatedUtc).FirstOrDefault();
    private bool ShotDraftActive => SourceShot is { } shot && ShotDraftJob(shot) is { } job && job.LocksTarget;
    private bool ShotDraftLocked => _shotDraftSubmitting || _pendingShotDraft is not null || ShotDraftActive;
    private TextRequestPresentation? ShotDraftPresentation => SourceShot is { } shot && ShotDraftJob(shot) is { } job
        ? new(job, "Drafting shot…", _resolvedShotDrafts.Contains(job.Id) ? TextRequestOutcome.Resolved
            : job.State == AiJobState.NeedsAttention ? TextRequestOutcome.Invalid : TextRequestOutcome.Proposal, "Draft shot") : null;
    private IReadOnlyList<Shot> OtherSceneShots(Shot shot) => SceneShots(shot).Where(s => s.Id != shot.Id).ToList();
    private static bool HasCoverage(Shot shot) => !string.IsNullOrWhiteSpace(shot.Description) || shot.Dialogue.Count > 0;

    private void LoadResolvedShotDrafts() => _resolvedShotDrafts = [.. Place<List<Guid>>(Id, "shots", "resolvedShotDrafts", [])];
    private async Task ResolveShotDraft(Guid job)
    {
        if (_resolvedShotDrafts.Add(job)) await Remember(Id, "shots", "resolvedShotDrafts", _resolvedShotDrafts.TakeLast(200).ToList());
    }

    private async Task PrepareShotDraft()
    {
        _shotDraftError = null;
        // Directions stay for a retry after a failure; a fresh shot starts empty.
        if (SourceShot is { } shot && ShotDraftJob(shot) is null) _shotDraftDirections = "";
        await RefreshSavedSource();
    }
    private string? ShotDraftIssue(Shot shot) =>
        shot.SceneId is not { } scene || Scenes.All(s => s.Id != scene) ? "Choose this shot's scene from the saved script first." : null;

    private async Task DraftShot()
    {
        if (_shotDraftAssist is null || !await _shotDraftAssist.PrepareSubmitAsync()) return;
        if (SourceShot is null || ShotDraftLocked || _shotDraftModel?.Ready != true) return;
        if (_promptEditor is not null) await _promptEditor.FlushAsync();
        if (!await Save() || !await RefreshSavedSource() || _approved is null) return;
        // Saving can replace the document with its saved copy, so read the shot again after it.
        if (SourceShot is not { } shot) return;
        if (ShotDraftIssue(shot) is { } issue) { _shotDraftError = issue; return; }
        _shotDraftSubmitting = true; _shotDraftError = null;
        var project = Id;
        try
        {
            var scene = SceneShots(shot).ToList();
            var single = new SingleShotDraft(shot.Id, scene.FindIndex(s => s.Id == shot.Id), [.. OtherSceneShots(shot).Select(SceneShotSummary.From)],
                HasCoverage(shot) ? SceneShotSummary.From(shot) : null);
            var request = new ShotPlanningRequest(ShotCopy.Of(_approved), _assets.Copy(), [shot.SceneId!.Value], _shotDraftMaximum, _shotDraftDirections.Trim(), _shotDraftModel.Model)
                { CoverageOnly = true, SingleShot = single };
            var id = Guid.NewGuid();
            var pending = await TextRequests.PlanShotsAsync(id, await AiReviews.TabIdAsync(), request, _shotDraftModel.FollowsDefault, _lifetime.Token);
            if (_disposed || Id != project) return;
            _pendingShotDraft = _shotDraftAssist.Attribute(pending); _shotDraftOrigin = id;
            await EnqueueShotDraftAsync();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { _shotDraftError = e.Message; }
        finally { _shotDraftSubmitting = false; }
    }
    private async Task EnqueueShotDraftAsync()
    {
        if (_pendingShotDraft is null) return;
        try
        {
            await AiJobs.EnqueueAsync(_pendingShotDraft, _lifetime.Token);
            _pendingShotDraft = null;
            if (_shotDraftAssist is not null) await _shotDraftAssist.QueuedAsync();
        }
        catch (WorkspaceStoreException e) { _shotDraftError = e.Message + " Retry saving this exact request; it will not generate twice."; }
    }
    private async Task RetryShotDraftEnqueue() { if (!_shotDraftSubmitting) { _shotDraftSubmitting = true; try { await EnqueueShotDraftAsync(); } finally { _shotDraftSubmitting = false; } } }
    private async Task CancelShotDraft()
    {
        if (SourceShot is not { } shot || ShotDraftJob(shot) is not { LocksTarget: true } job) return;
        try { await AiJobs.CancelAsync(job.Id, _lifetime.Token); } catch (WorkspaceStoreException e) { _shotDraftError = e.Message; }
    }

    // A finished request this tab submitted opens its review while its shot is still selected.
    private async Task RefreshShotDraftAsync()
    {
        if (_shotDraftOrigin is not { } origin || _shotDraftOpen) return;
        if (AiJobs.View.Jobs.FirstOrDefault(j => j.Id == origin) is not { } job || TextRequestPresentation.IsActive(job)) return;
        _shotDraftOrigin = null;
        if (job.CancelRequested || job.State == AiJobState.Cancelled || SourceShot?.Id != job.Target.ShotId) return;
        var closes = _shotDraftCloses;
        if (await AiReviews.TryOpenAsync(job, _shotDraftOriginElement, automatic: true)) await ShowShotDraftAsync(job, closes);
    }
    private async Task InspectShotDraft()
    {
        if (SourceShot is not { } shot || ShotDraftJob(shot) is not { } job) return;
        var closes = _shotDraftCloses;
        if (!await AiReviews.TryOpenAsync(job, _shotDraftOriginElement, automatic: false)) { _shotDraftError = "Close the other dialog before opening this draft."; return; }
        await ShowShotDraftAsync(job, closes);
    }
    private async Task ShowShotDraftAsync(AiJobHeader job, int closes)
    {
        try
        {
            var result = await AiJobStore.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result, _lifetime.Token);
            // An opening that began before the author closed the review must not show it again.
            if (_disposed || closes != _shotDraftCloses) { if (!_shotDraftOpen) await AiReviews.CloseAsync(job.Id); return; }
            _shotDraftReviewJob = job; _shotDraftRaw = result?.Raw ?? "";
            _shotDraftResult = job.State == AiJobState.Completed && result?.Complete == true ? result.Read<ShotPlanningResult>() : null;
            _shotDraftError = job.CancelRequested || job.State == AiJobState.Cancelled ? job.Error ?? "Request cancelled. The shot is unchanged." : _shotDraftResult?.Error ?? result?.Error ?? job.Error;
            _shotDraftReviewOwner = job.Id; _shotDraftOpen = true;
            StateHasChanged();
        }
        catch { await AiReviews.CloseAsync(job.Id); throw; }
    }
    private async Task CloseShotDraft()
    {
        _shotDraftOpen = false; _shotDraftCloses++;
        if (_shotDraftReviewOwner is { } owner) await AiReviews.CloseAsync(owner);
        _shotDraftReviewOwner = null;
    }
    private Shot? ShotDraftProposal => _shotDraftResult is { Error: null, Shots: [var draft] } ? draft : null;
    private bool ShotDraftTargetSelected => _shotDraftReviewJob is { } job && SourceShot?.Id == job.Target.ShotId;

    private async Task ApplyShotDraft()
    {
        if (ShotDraftProposal is not { } draft || _shotDraftReviewJob is not { } job || !ShotDraftTargetSelected) return;
        EditCoverage(s =>
        {
            var copy = ShotCopy.Of(draft);
            s.Title = copy.Title; s.Description = copy.Description; s.Duration = copy.Duration;
            s.Dialogue = copy.Dialogue; s.Atmosphere = copy.Atmosphere; s.Music = copy.Music;
            s.ApprovedScriptId = copy.ApprovedScriptId; s.SceneId = copy.SceneId; s.SceneTitle = copy.SceneTitle;
            s.SourceBlockIds = copy.SourceBlockIds; s.SourceExcerpt = copy.SourceExcerpt; s.Planning = copy.Planning;
            // Keep the existing cast, with their looks and reference links, and add anyone new.
            foreach (var character in copy.Characters.Where(c => !s.Characters.Any(e => string.Equals(e.Name, c.Name, StringComparison.OrdinalIgnoreCase))))
                s.Characters.Add(character);
        });
        await ResolveShotDraft(job.Id);
        await CloseShotDraft();
        Notify("Applied the drafted shot. You can undo this.");
    }
    private async Task DismissShotDraft()
    {
        if (_shotDraftReviewJob is { } job) await ResolveShotDraft(job.Id);
        await CloseShotDraft();
    }
}
