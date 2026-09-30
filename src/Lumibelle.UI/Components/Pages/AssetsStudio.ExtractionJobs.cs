using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    [Inject] public AiJobCoordinator AiJobs { get; set; } = null!;
    [Inject] public IAiJobStore AiJobStore { get; set; } = null!;
    [Inject] public AiTextJobCapture TextRequests { get; set; } = null!;
    [Inject] public IAiReviewGate AiReviews { get; set; } = null!;
    [Inject] public IAiJobReviewStore ReviewDrafts { get; set; } = null!;
    private AiJobHeader? _extractionJob, _activeExtraction;
    private AiJobSubmission? _pendingExtraction;
    private Guid? _extractionOriginJob, _extractionAttempted, _extractionLoadedJob, _extractionRequestedJob, _extractionReviewOwner;
    private bool _extractionSubmitting, _extractionRefreshing, _extractionRefreshAgain, _extractionFollowsDefault, _extractionInitialized;
    private readonly CancellationTokenSource _extractionLifetime = new();
    private ElementReference _extractionOriginElement;
    private string _extractionRaw = "";
    private bool _reviewDraftDirty, _reviewDraftConflict;
    private long _reviewDraftRevision;
    private string? _reviewDraftFingerprint, _reviewDraftError;
    private readonly SemaphoreSlim _reviewDraftGate = new(1, 1);
    private CancellationTokenSource? _reviewDraftDelay;
    private bool ExtractionFinished => _library?.ExtractionReviews.Any(r => r.Id == _reviewId) == true;
    private AiJobHeader? ExtractionStatusJob => AiJobs.View.Jobs.FirstOrDefault(j => j.Kind == AiJobKind.AssetExtraction && j.Target.ProjectId == Id && TextRequestPresentation.IsActive(j)) ?? _activeExtraction ?? _extractionJob ?? AiJobs.View.Jobs.Where(j => j.Kind == AiJobKind.AssetExtraction && j.Target.ProjectId == Id).OrderByDescending(j => j.CreatedUtc).FirstOrDefault();
    private TextRequestPresentation? ExtractionPresentation => ExtractionStatusJob is { } job ? new(job, "Finding assets…",
        _library?.ExtractionReviews.Any(r => r.Id == job.Id) == true ? TextRequestOutcome.Resolved : TextRequestOutcome.Proposal, "Extract assets") : null;
    private Task InspectExtraction() => ExtractionStatusJob is { } job ? ReviewExtractionJobAsync(job) : Task.CompletedTask;
    private string ExtractionActionLabel => _activeExtraction is { State: AiJobState.Waiting } ? "Queued · View extraction" : _activeExtraction is not null ? "Working · View extraction" :
        AiJobs.View.Jobs.Any(j => j.Kind == AiJobKind.AssetExtraction && j.Target.ProjectId == Id && j.State == AiJobState.Completed && !j.CancelRequested &&
            _library?.ExtractionReviews.Any(r => r.Id == j.Id) != true) ? "Review suggestions" : "Extract from script";

    private async Task ResetExtractionProjectAsync()
    {
        _extractionComposerPrepared = false;
        _extractionInitialized = false;
        await ReleaseExtractionReviewAsync(); _reviewDraftDelay?.Cancel();
        if (_extractionOriginJob is { } origin) await AiReviews.CloseAsync(origin);
        _extractionJob = _activeExtraction = null; _pendingExtraction = null; _reviewRequest = null; _extractionResult = null;
        _extractionOriginJob = _extractionAttempted = _extractionLoadedJob = _extractionRequestedJob = null;
        _extractOpen = _extracting = _reviewDraftDirty = _reviewDraftConflict = false;
        _reviewId = Guid.Empty; _reviewDraftRevision = 0; _reviewDraftFingerprint = _reviewDraftError = _extractionError = null; _extractionRaw = "";
    }

    private void ExtractionQueueChanged() { if (!_disposed && !_loading) _ = InvokeAsync(RefreshExtractionJobsAsync); }
    private async Task InitializeExtractionJobsAsync()
    {
        try { await AiJobs.RefreshAsync(_extractionLifetime.Token); await RefreshExtractionJobsAsync(); _extractionInitialized = !_loading && _loadedProject == Id; }
        catch (WorkspaceStoreException e) { _extractionError = e.Message; }
    }
    private async Task RefreshExtractionJobsAsync()
    {
        if (_disposed || _loading) return;
        if (_extractionRefreshing) { _extractionRefreshAgain = true; return; }
        _extractionRefreshing = true;
        try
        {
            do
            {
                _extractionRefreshAgain = false;
                var matches = AiJobs.View.Jobs.Where(j => j.Kind == AiJobKind.AssetExtraction && j.Target.ProjectId == Id).ToArray();
                _activeExtraction = matches.FirstOrDefault(j => j.LocksTarget);
                _extracting = _extractionSubmitting || _activeExtraction is not null;
                if (_extractionJob is { } selected) _extractionJob = matches.FirstOrDefault(j => j.Id == selected.Id);
                if (_activeExtraction is { } active && _reviewRequest is null && !_extractionSubmitting)
                    await LoadExtractionJobAsync(active);
                if (_extractionJob is { } job) await ReadExtractionResultAsync(job);
                if (_pendingExtraction is { } pending && matches.Any(j => j.Id == pending.Id)) _pendingExtraction = null;
                if (!_disposed && _extractionOriginJob is { } origin && _extractionAttempted != origin && matches.FirstOrDefault(j => j.Id == origin) is { } complete &&
                    complete.Target.ProjectId == Id && complete.State is not (AiJobState.Waiting or AiJobState.Running))
                {
                    _extractionAttempted = origin;
                    if (!_extractOpen && !_extractionReviewSuppressed && !complete.CancelRequested && await AiReviews.TryOpenAsync(complete, _extractionOriginElement, automatic: true))
                    {
                        // The author can open and close the review while the claim is pending; a closed review stays closed.
                        if (_extractionReviewSuppressed && !_extractOpen) await AiReviews.CloseAsync(complete.Id);
                        else if (!_extractOpen) await ShowExtractionJobAsync(complete);
                    }
                }
            } while (_extractionRefreshAgain && !_disposed);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { if (!_disposed) _extractionError = ExtractionFailure(e); }
        finally { _extractionRefreshing = false; if (!_disposed) StateHasChanged(); }
    }
    private async Task LoadExtractionJobAsync(AiJobHeader job)
    {
        if (_extractionLoadedJob != job.Id)
        {
            if (_reviewDraftDirty && !await SaveReviewDraftAsync()) return;
            var captured = AiTextJobHandler.Read(job, await AiJobStore.ReadSnapshotAsync(job.Id, _extractionLifetime.Token));
            if (_disposed || job.Target.ProjectId != Id) return;
            _extractionJob = job; _extractionLoadedJob = _reviewId = job.Id;
            _reviewRequest = captured.Payload<AssetExtractionRequest>(); _approvedScript = _reviewRequest.Script;
            _selectedScenes = _reviewRequest.SceneIds.ToHashSet(); _extractionModel = captured.Model;
            _extractionFollowsDefault = captured.FollowsDefault;
            _extractionResult = null; _extractionRaw = ""; _extractionError = null;
            _reviewDraftRevision = 0; _reviewDraftFingerprint = null; _reviewDraftError = null; _reviewDraftConflict = false;
        }
        await ReadExtractionResultAsync(job);
    }
    private AiJobHeader? _observedExtractionJob;
    private async Task ReadExtractionResultAsync(AiJobHeader job)
    {
        var result = await AiJobStore.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result, _extractionLifetime.Token);
        if (_disposed || job.Target.ProjectId != Id || _extractionJob?.Id != job.Id) return;
        _observedExtractionJob = job;
        _extractionRaw = result?.Raw ?? "";
        _extractionProgress = AiJobs.Progress(job.Id)?.Progress;
        _extractionError = job.CancelRequested || job.State == AiJobState.Cancelled ? job.Error ?? "Asset extraction cancelled. No assets were changed." : result?.Error ?? job.Error;
        if (job.State == AiJobState.Completed && result?.Complete == true && _extractionResult is null)
        {
            _extractionResult = result.Read<AssetExtractionResult>();
            if (_extractionResult is not null && _extractionResult.ValidationError is null) await LoadReviewDraftAsync();
            await RefreshCoverageAsync();
        }
    }
    private async Task ShowExtractionJobAsync(AiJobHeader job)
    {
        try
        {
            if (_reviewDraftDirty && !await SaveReviewDraftAsync()) { await AiReviews.CloseAsync(job.Id); return; }
            await LoadExtractionJobAsync(job);
            if (_disposed || job.Target.ProjectId != Id) { await AiReviews.CloseAsync(job.Id); return; }
            _extractionReviewOwner = job.Id; _extractOpen = true;

            StateHasChanged();
        }
        catch { await AiReviews.CloseAsync(job.Id); throw; }
    }
    private async Task ReviewExtractionJobAsync(AiJobHeader job)
    {
        try
        {
            if (!await SaveNowAsync()) return;
            if (!await AiReviews.TryOpenAsync(job, _extractionOriginElement, automatic: false)) { _extractionError = "Close the other dialog before opening this extraction."; return; }
            await ShowExtractionJobAsync(job);
        }
        catch (Exception e) { _extractionError = ExtractionFailure(e); }
    }
    private async Task HandleRequestedExtractionAsync()
    {
        if (!_extractionInitialized || string.IsNullOrEmpty(_extractionOriginElement.Id) || RequestedJobId is not { } id || _extractionRequestedJob == id || _loading) return;
        _extractionRequestedJob = id;
        var job = AiJobs.View.Jobs.FirstOrDefault(j => j.Id == id && j.Kind == AiJobKind.AssetExtraction && j.Target.ProjectId == Id);
        if (job is not null) await ReviewExtractionJobAsync(job);
    }

    private string ReviewDraftJson => JsonSerializer.Serialize(new AssetExtractionReviewDraft(_extractionResult?.Proposals ?? []), AtomicJsonFile.Options);
    private string ReviewDownloadUrl => "data:application/json;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ReviewDraftJson));
    private void ReviewDraftChanged()
    {
        if (ExtractionFinished || _finishingReview || _extractionResult is null || _extractionResult.ValidationError is not null) return;
        _reviewDraftDirty = ReviewDraftJson != _reviewDraftFingerprint;
        _reviewDraftDelay?.Cancel(); _reviewDraftDelay?.Dispose(); _reviewDraftDelay = new();
        _ = SaveReviewAfterDelayAsync(_reviewDraftDelay.Token);
    }
    private async Task SaveReviewAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(450, ct);
            if (!_disposed) await InvokeAsync(async () => { await SaveReviewDraftAsync(); if (!_disposed) StateHasChanged(); });
        }
        catch (OperationCanceledException) { }
    }
    private async Task<bool> SaveReviewDraftAsync(bool force = false)
    {
        if (_extractionResult is null || _extractionResult.ValidationError is not null || ExtractionFinished || !_reviewDraftDirty && !force) return true;
        if (_reviewDraftConflict) return false;
        await _reviewDraftGate.WaitAsync();
        try
        {
            var job = _reviewId; var content = ReviewDraftJson;
            if (!force && content == _reviewDraftFingerprint) { _reviewDraftDirty = false; return true; }
            var value = JsonSerializer.Deserialize<AssetExtractionReviewDraft>(content, AtomicJsonFile.Options)!;
            var saved = await ReviewDrafts.SaveAsync(job, value, _reviewDraftRevision);
            if (_reviewId != job) return true;
            _reviewDraftRevision = saved.Revision; _reviewDraftFingerprint = content;
            _reviewDraftDirty = ReviewDraftJson != content; _reviewDraftError = null; return true;
        }
        catch (WorkspaceConflictException) { _reviewDraftConflict = true; _reviewDraftDirty = true; _reviewDraftError = "Another tab saved different review decisions. Download your copy or load the saved review before continuing."; return false; }
        catch (WorkspaceStoreException e) { _reviewDraftDirty = true; _reviewDraftError = e.Message; return false; }
        finally { _reviewDraftGate.Release(); }
    }
    private async Task LoadReviewDraftAsync()
    {
        try
        {
            var job = _reviewId; var draft = await ReviewDrafts.LoadAsync(job, _extractionLifetime.Token);
            if (_disposed || job != _reviewId || _extractionResult is null) return;
            if (draft.Read<AssetExtractionReviewDraft>() is { } saved)
            {
                // Membership/evidence are immutable; only the reviewed fields can change.
                if (!ValidReviewMembership(saved.Proposals, _extractionResult.Proposals)) throw new WorkspaceStoreException("The review draft does not match this response. Its saved content has not been replaced.");
                _extractionResult = _extractionResult with { Proposals = saved.Proposals };
            }
            _reviewDraftRevision = draft.Revision; _reviewDraftFingerprint = ReviewDraftJson;
            _reviewDraftDirty = _reviewDraftConflict = false; _reviewDraftError = null;
        }
        catch (Exception e) when (e is WorkspaceStoreException or JsonException) { _reviewDraftError = e.Message; _reviewDraftConflict = true; }
    }
    private static bool ValidReviewMembership(IReadOnlyList<AssetExtractionProposal> draft, IReadOnlyList<AssetExtractionProposal> source) =>
        draft is not null && draft.All(p => p is not null && p.Evidence is not null && p.Looks is not null && p.Looks.All(l => l is not null && l.Evidence is not null)) &&
        draft.Select(p => p.Id).SequenceEqual(source.Select(p => p.Id)) && draft.Zip(source).All(pair =>
            pair.First.Evidence.SequenceEqual(pair.Second.Evidence) && pair.First.Looks.Select(l => l.Id).SequenceEqual(pair.Second.Looks.Select(l => l.Id)) &&
            pair.First.Looks.Zip(pair.Second.Looks).All(look => look.First.Evidence.SequenceEqual(look.Second.Evidence)));
    private static string ExtractionFailure(Exception e) => e is AiGenerationException or WorkspaceStoreException or ProjectStoreException ? e.Message : "The extraction request could not be read or saved. Your assets and review decisions are unchanged.";
}
