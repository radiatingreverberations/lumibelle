using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using lumibelle.Services.AI;
using MudBlazor;
using Microsoft.JSInterop;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    private lumibelle.Components.AI.TextAssistance? _extractionAssist;
    private bool _extractionComposerPrepared;
    private bool _ignoreExtractionReminders;
    private int OutstandingExtractionScenes => CoverageScenes.Count(s => ExtractionCoverage.State(s, _library!.ExtractionReviews) != SceneCoverageState.Current);
    private string? ExtractionCoverageNotice => !_ignoreExtractionReminders && OutstandingExtractionScenes > 0
        ? $"{OutstandingExtractionScenes} {(OutstandingExtractionScenes == 1 ? "scene needs" : "scenes need")} extraction review. Open extraction for details." : null;
    private async Task IgnoreExtractionRemindersChanged(bool ignored)
    {
        _ignoreExtractionReminders = ignored;
        await Remember(Id, "assets", "ignoreExtractionReminders", ignored);
    }
    private async Task PrepareExtractionComposerAsync() {
        if (_extracting) return;
        if (!_extractionComposerPrepared || _reviewRequest is not null) await PrepareExtractionAsync();
        _extractOpen = false; _extractionComposerPrepared = true;
    }
    private ScriptSourceSnapshot? _coverageScript;
    private AssetExtractionRequest? _reviewRequest;
    private Guid _reviewId;
    private bool _finishingReview, _unapprovedScriptChanges;
    private bool _restoreExtractionFocus;
    private string EvidenceStatus(AssetSourceEvidence evidence) => evidence.SceneId is { } scene && _coverageScript is not null && CoverageScenes.All(s => s.SceneId != scene)
        ? "Source scene removed from the latest saved script" : evidence.ApprovedScriptId is null ? "Historical source" : evidence.ApprovedScriptId == _latestApprovalId ? "Current saved script" : "Earlier saved script";
    private IReadOnlyList<ReviewedAssetScene> CoverageScenes { get; set; } = [];
    private string CoverageSummary => _coverageScript is null ? "No saved scenes" : $"{CoverageScenes.Count(s => ExtractionCoverage.State(s, _library!.ExtractionReviews) == SceneCoverageState.Current)} / {CoverageScenes.Count} scenes up to date";
    private bool ExtractionReadyForReview => _extractionResult is { ValidationError: null };
    private ExtractionDecisionSummary ReviewDecisions => ExtractionCoverage.Summary(_extractionResult?.Proposals ?? []);
    private string ExtractionDialogTitle => ExtractionReadyForReview ? "Review asset suggestions" : "Extract assets from script";
    private string ApplyExtractionLabel => _finishingReview ? "Applying…" : ExtractionFinished ? "Applied to library" :
        ReviewDecisions.Merged > 0 ? "Apply to library" : ReviewDecisions.Created > 0 ? "Add to library" : "Finish review";
    private string ReviewSummary
    {
        get
        {
            var d = ReviewDecisions;
            var changes = new List<string>();
            if (d.Created > 0) changes.Add($"{d.Created} {(d.Created == 1 ? "asset" : "assets")} to add");
            if (d.Merged > 0) changes.Add($"{d.Merged} {(d.Merged == 1 ? "asset" : "assets")} to update");
            if (d.Skipped > 0) changes.Add($"{d.Skipped} skipped");
            if (d.LooksCreated > 0) changes.Add($"{d.LooksCreated} {(d.LooksCreated == 1 ? "look" : "looks")} to add");
            if (d.LooksMerged > 0) changes.Add($"{d.LooksMerged} {(d.LooksMerged == 1 ? "look" : "looks")} to update");
            return changes.Count == 0 ? "No library changes selected" : string.Join(" · ", changes);
        }
    }
    private async Task RefreshCoverageAsync()
    {
        var draft = await Scripts.LoadAsync(Id);
        _coverageScript = await Scripts.CaptureSourceAsync(Id);
        CoverageScenes = _coverageScript is null ? [] : ExtractionCoverage.Scenes(_coverageScript);
        _latestApprovalId = _coverageScript?.Id;
        _unapprovedScriptChanges = _coverageScript is not null && ScriptStructure.Fingerprint(draft.Blocks) != ScriptStructure.Fingerprint(_coverageScript.Blocks);
    }
    private async Task OpenExtractionAsync()
    {
        if (_finishingReview || !await SaveNowAsync()) return;
        if (!_extractOpen && _assetsModule is not null) await _assetsModule.InvokeVoidAsync("rememberExtractionFocus");
        await InitializeExtractionJobsAsync();
        var job = _activeExtraction ?? (_extractionJob is not null && !ExtractionFinished ? _extractionJob : null) ??
            AiJobs.View.Jobs.Where(j => j.Kind == AiJobKind.AssetExtraction && j.Target.ProjectId == Id && !j.CancelRequested && j.State != AiJobState.Cancelled &&
                !_library!.ExtractionReviews.Any(r => r.Id == j.Id)).OrderByDescending(j => j.CreatedUtc).FirstOrDefault();
        if (job is not null) { await ReviewExtractionJobAsync(job); return; }
        if (_extractionAssist is not null) await _extractionAssist.OpenAsync();
    }
    private async Task PrepareExtractionAsync()
    {
        if (!await SaveReviewDraftAsync()) return;
        await ReleaseExtractionReviewAsync();
        _extractionJob = null; _extractionLoadedJob = null; _reviewId = Guid.Empty; _reviewDraftRevision = 0;
        _extractOpen = false; _extractionResult = null; _extractionError = null; _extractionProgress = null; _reviewRequest = null;
        _extractionRaw = ""; _reviewDraftFingerprint = null; _reviewDraftError = null;
        try
        {
            await RefreshCoverageAsync(); _approvedScript = _coverageScript; _settings = await SettingsStore.LoadAsync();
            _selectedScenes = CoverageScenes.Where(s => ExtractionCoverage.State(s, _library!.ExtractionReviews) != SceneCoverageState.Current).Select(s => s.SceneId).ToHashSet();
        }
        catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException) { _extractionError = e.Message; }
    }
    private AssetExtractionRequest ExtractionRequest => new(_approvedScript! with { Blocks = _approvedScript!.Blocks.Select(b => b.Copy()).ToList() }, _library!.Copy(),
        ExtractionCoverage.Scenes(_approvedScript).Where(s => _selectedScenes.Contains(s.SceneId)).Select(s => s.SceneId).ToArray(), _extractionModel!.Backend, ExtractionModel, _extractionModel);
    private string ExtractionModel => _extractionModel?.Model ?? "";
    private int ExtractionTokens => _approvedScript is null || _settings is null || _library is null || _extractionModel is null ? 0 : AssetExtractor.EstimateInputTokens(ExtractionRequest);
    private bool _extractionReviewSuppressed;
    private Microsoft.AspNetCore.Components.ElementReference _extractionDialogElement;
    private async Task ExtractAsync()
    {
        if (_extractionAssist is null || !await _extractionAssist.PrepareSubmitAsync()) return;
        if (_extracting || _finishingReview || _pendingExtraction is not null || !_extractionReady || _extractionModel is null || _approvedScript is null || _settings is null || _library is null || _selectedScenes.Count == 0) return;
        _extractionSubmitting = _extracting = true; _extractionError = null; _extractionResult = null; _extractionReviewSuppressed = false;
        _reviewId = Guid.NewGuid(); _reviewRequest = ExtractionRequest;
        var project = Id; var request = _reviewRequest; var id = _reviewId; var followsDefault = _extractionFollowsDefault;
        try
        {
            var pending = await TextRequests.ExtractAsync(id, await AiReviews.TabIdAsync(), request, followsDefault, _extractionLifetime.Token);
            if (_disposed || Id != project) return;
            _pendingExtraction = _extractionAssist.Attribute(pending);
            _extractionOriginJob = _reviewId;
            await EnqueueExtractionAsync();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { _extractionError = ExtractionFailure(e); }
        finally { _extractionSubmitting = false; if (!_disposed) await RefreshExtractionJobsAsync(); }
    }
    private async Task EnqueueExtractionAsync()
    {
        if (_pendingExtraction is null) return;
        try
        {
            var job = await AiJobs.EnqueueAsync(_pendingExtraction, _extractionLifetime.Token);
            _pendingExtraction = null; _extractionError = null; _extractionJob = job;
            if (_extractionAssist is not null) await _extractionAssist.QueuedAsync();
            if (_disposed) return;
            await LoadExtractionJobAsync(job);
            if (_extractOpen && !_extractionReviewSuppressed && await AiReviews.TryOpenAsync(job, _extractionDialogElement, automatic: false)) _extractionReviewOwner = job.Id;
        }
        catch (WorkspaceStoreException e) { _extractionError = e.Message + " Retry saving this exact request; it will not generate twice."; }
    }
    private async Task RetryExtractionEnqueueAsync()
    {
        if (_extractionSubmitting) return;
        _extractionSubmitting = _extracting = true;
        try { await EnqueueExtractionAsync(); }
        finally { _extractionSubmitting = false; await RefreshExtractionJobsAsync(); }
    }
    private async Task CancelExtraction()
    {
        if (_activeExtraction is not { } job) return;
        try { await AiJobs.CancelAsync(job.Id, _extractionLifetime.Token); await RefreshExtractionJobsAsync(); }
        catch (WorkspaceStoreException e) { _extractionError = e.Message; }
    }
    private async Task StartNewExtractionAsync() { if (_finishingReview || _extracting) return; await PrepareExtractionAsync(); if (_extractionAssist is not null) await _extractionAssist.OpenAsync(); }
    private Task RetryExtraction() => StartNewExtractionAsync();
    private async Task ReleaseExtractionReviewAsync()
    {
        if (_extractionReviewOwner is { } id) await AiReviews.CloseAsync(id);
        _extractionReviewOwner = null;
    }
    private async Task CloseExtraction()
    {
        if (_finishingReview || !await SaveReviewDraftAsync()) return;
        _extractOpen = false; _restoreExtractionFocus = true; _extractionReviewSuppressed = true; _extractionCloses++;
        await ReleaseExtractionReviewAsync();
    }
    private Task HandleExtractionKey(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs args) => args.Key == "Escape" ? CloseExtraction() : Task.CompletedTask;
    private async Task ApplyExtractionAsync()
    {
        if (_finishingReview || _extracting || ExtractionFinished || _library is null || _reviewRequest is null || _extractionResult is null || _extractionResult.ValidationError is not null || _extractionJob?.State != AiJobState.Completed) return;
        _finishingReview = true; _extractionError = null;
        await _saveGate.WaitAsync();
        try
        {
            if (!await SaveReviewDraftAsync(force: true)) return;
            if (!await SavePendingLockedAsync()) throw new WorkspaceStoreException(_saveError ?? "Save asset changes before finishing the review.");
            var version = _editVersion;
            var input = new ExtractionReviewInput(_reviewId, _reviewRequest.Script.Id, _reviewRequest.SceneIds, _extractionResult.Proposals);
            var saved = await AssetStore.FinishExtractionAsync(Id, input, _library.Revision);
            ApplyStoredMutation(saved, version, []);
            _selectedAssetId ??= _library.Assets.FirstOrDefault()?.Id;
            _extractOpen = false; _restoreExtractionFocus = true; _saveStatus = _dirty ? "Unsaved" : "Saved"; _reviewDraftDirty = false;
            await ReleaseExtractionReviewAsync();
            Snackbar.Add($"Reviewed {input.SceneIds.Count} saved scenes.", Severity.Success);
            try { await RefreshCoverageAsync(); }
            catch (WorkspaceStoreException) { Snackbar.Add("Review saved. Reopen Extract from script to refresh coverage.", Severity.Warning); }
        }
        catch (WorkspaceConflictException e) { _extractionError = e.Message; _conflict = true; }
        catch (WorkspaceStoreException e) { _extractionError = e.Message; }
        finally { _finishingReview = false; _saveGate.Release(); }
    }
}
