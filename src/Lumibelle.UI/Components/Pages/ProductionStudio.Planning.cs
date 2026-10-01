using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    [Inject] public IAiJobReviewStore ReviewDrafts { get; set; } = null!;
    private lumibelle.Components.AI.TextAssistance? _planningAssist;
    private Task OpenPlanning() => _planningAssist?.OpenAsync() ?? Task.CompletedTask;
    private AiJobHeader? _planningJob, _activePlanning;
    private AiJobSubmission? _pendingPlanning;
    private ShotPlanningRequest? _planningRequest;
    private ShotPlanningResult? _planningOriginal;
    private Guid? _planningOriginJob, _planningAttempted, _planningLoadedJob, _planningRequestedJob, _planningReviewOwner;
    private ElementReference _planningOriginElement, _planningDialogElement, _planningCloseElement;
    private bool _focusPlanningClose, _planningInitialized, _planningComposerPrepared;
    private bool _planningSubmitting, _planningRefreshing, _planningRefreshAgain, _planningSuppressed, _applyingProposal;
    private string _planningRaw = "";
    private bool _planningDraftDirty, _planningDraftConflict;
    private long _planningDraftRevision;
    private string? _planningDraftFingerprint, _planningDraftError;
    private CancellationTokenSource? _planningDraftDelay;
    private readonly SemaphoreSlim _planningDraftGate = new(1, 1);
    private bool PlanningApplied => _planningJob is { } job && _doc.PlanningReviews.Any(r => r.JobId == job.Id);
    private bool PlanningComposerLocked => _planning || _planningSubmitting || _pendingPlanning is not null || _applyingProposal;
    private AiJobHeader? PlanningStatusJob => AiJobs.View.Jobs.FirstOrDefault(j => j.Kind == AiJobKind.ShotPlanning && j.Target.ShotId is null && j.Target.ProjectId == Id && TextRequestPresentation.IsActive(j)) ?? _activePlanning ?? _planningJob ?? AiJobs.View.Jobs.Where(j => j.Kind == AiJobKind.ShotPlanning && j.Target.ShotId is null && j.Target.ProjectId == Id).OrderByDescending(j => j.CreatedUtc).FirstOrDefault();
    private TextRequestPresentation? PlanningPresentation => PlanningStatusJob is { } job ? new(job, "Drafting shots…",
        _doc.PlanningReviews.Any(r => r.JobId == job.Id) ? TextRequestOutcome.Resolved : TextRequestOutcome.Proposal, "Draft shots") : null;
    private Task InspectPlanning() => PlanningStatusJob is { } job ? ReviewPlanningJobAsync(job) : Task.CompletedTask;
    private string PlanningActionLabel => _activePlanning is { State: AiJobState.Waiting } ? "Queued · View breakdown" : _activePlanning is not null ? "Working · View breakdown" : "Draft shots";
    private IEnumerable<ScriptSection> PlanningScenes => (_planningRequest?.Script ?? _approved) is { } source
        ? ScriptStructure.Sections(source.Blocks).Where(s => s.Kind == ScriptBlockKind.Scene) : [];

    private async Task ResetPlanningProjectAsync()
    {
        _planningInitialized = _planningComposerPrepared = false;
        await ReleasePlanningReviewAsync();
        _planningDraftDelay?.Cancel();
        if (_planningOriginJob is { } origin) await AiReviews.CloseAsync(origin);
        _planningJob = _activePlanning = null; _pendingPlanning = null; _planningRequest = null; _proposal = _planningOriginal = null;
        _planningLoadedJob = _planningRequestedJob = _planningOriginJob = _planningAttempted = null;
        _planningOpen = _planning = _planningDraftDirty = _planningDraftConflict = false;
        _planningDraftFingerprint = _planningDraftError = _planningError = null; _planningDraftRevision = 0; _planningRaw = "";
        _sceneSelection = []; _instructions = ""; _maximum = 15;
    }
    private void PlanningQueueChanged() { if (!_disposed && _doc.ProjectId == Id) _ = InvokeAsync(RefreshPlanningJobsAsync); }
    // Instructions are cleared after each request; earlier drafting requests keep them in their captured input.
    private IReadOnlyList<string> _recentInstructions = [];
    private string? _recentInstructionJobs;
    private const int RecentInstructionLimit = 5;
    private async Task LoadRecentInstructionsAsync(IReadOnlyList<AiJobHeader> jobs)
    {
        var newest = jobs.OrderByDescending(j => j.CreatedUtc).ToArray();
        var key = Id + ":" + string.Join(",", newest.Select(j => j.Id));
        if (key == _recentInstructionJobs) return;
        _recentInstructionJobs = key;
        var found = new List<string>();
        foreach (var job in newest)
        {
            if (found.Count == RecentInstructionLimit) break;
            try
            {
                var request = AiTextJobHandler.Read(job, await AiJobStore.ReadSnapshotAsync(job.Id));
                if (request.Payload<ShotPlanningRequest>().Instructions.Trim() is { Length: > 0 } text && !found.Contains(text, StringComparer.Ordinal)) found.Add(text);
            }
            // A pruned or unreadable request only means it cannot be offered again.
            catch (Exception e) when (e is WorkspaceStoreException or JsonException or IOException) { }
        }
        _recentInstructions = found;
    }
    private void UseRecentInstructions(string text) => _instructions = text;
    private async Task RefreshPlanningJobsAsync()
    {
        if (_disposed) return;
        if (_planningRefreshing) { _planningRefreshAgain = true; return; }
        _planningRefreshing = true;
        try
        {
            do
            {
                _planningRefreshAgain = false;
                var matches = AiJobs.View.Jobs.Where(j => j.Kind == AiJobKind.ShotPlanning && j.Target.ShotId is null && j.Target.ProjectId == Id).ToArray();
                await LoadRecentInstructionsAsync(matches);
                _activePlanning = matches.FirstOrDefault(j => j.LocksTarget);
                _planning = _activePlanning is not null;
                if (_planningJob is { } selected) _planningJob = matches.FirstOrDefault(j => j.Id == selected.Id);
                if (_planningJob is null && _activePlanning is { } active && !_planningSubmitting) await LoadPlanningJobAsync(active);
                if (_planningJob is { } job) await ReadPlanningResultAsync(job);
                if (_pendingPlanning is { } pending && matches.Any(j => j.Id == pending.Id)) _pendingPlanning = null;
                if (_planningOriginJob is { } origin && _planningAttempted != origin && matches.FirstOrDefault(j => j.Id == origin) is { } complete &&
                    complete.State is not (AiJobState.Waiting or AiJobState.Running))
                {
                    _planningAttempted = origin;
                    if (!_planningOpen && !_planningSuppressed && !complete.CancelRequested && await AiReviews.TryOpenAsync(complete, _planningOriginElement, automatic: true))
                        await ShowPlanningJobAsync(complete);
                }
            } while (_planningRefreshAgain && !_disposed);
            await RefreshShotDraftAsync();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { if (!_disposed) _planningError = e.Message; }
        finally { _planningRefreshing = false; if (!_disposed) StateHasChanged(); }
    }
    private async Task PreparePlanning()
    {
        if (_planningSubmitting || _applyingProposal || !await Save() || !await SavePlanningDraftAsync()) return;
        try
        {
            await AiJobs.RefreshAsync(_lifetime.Token); await RefreshPlanningJobsAsync();
            // Draft starts a new request. Historical failures and unapplied results
            // remain available through AI activity; only running work is resumed here.
            if (_activePlanning is not null) return;
            if (_planningComposerPrepared && _planningRequest is null) { _planningOpen = false; return; }
            await ReleasePlanningReviewAsync();
            if (_planningRequest is not null) { _instructions = ""; _maximum = 15; }
            _planningJob = null; _planningLoadedJob = null; _planningRequest = null; _proposal = _planningOriginal = null; _planningRaw = "";
            _planningOriginJob = _planningAttempted = null;
            _planningDraftFingerprint = _planningDraftError = _planningError = null; _planningDraftRevision = 0; _planningDraftDirty = _planningDraftConflict = false;
            _approved = await Scripts.CaptureSourceAsync(Id, cancellationToken: _lifetime.Token);
            _sceneSelection = Scenes.Select(s => s.Id).ToHashSet(); _planningSuppressed = false; _planningOpen = false; _planningComposerPrepared = true;
            if (RequestedJobId is not null)
                Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("jobId", (string?)null), replace: true);
        }
        catch (Exception e) { _error = e.Message; }
    }
    private void ChooseScene(Guid id, bool selected) { if (selected) _sceneSelection.Add(id); else _sceneSelection.Remove(id); }
    private async Task Draft()
    {
        if (_planningAssist is null || !await _planningAssist.PrepareSubmitAsync()) return;
        if (_planning || _planningSubmitting || _applyingProposal || _pendingPlanning is not null || _model?.Ready != true ||
            (_planningRequest?.Script ?? _approved) is not { } approved || !await SavePlanningDraftAsync()) return;
        _planningSubmitting = true; _planningError = null;
        var project = Id;
        try
        {
            var id = Guid.NewGuid();
            var request = new ShotPlanningRequest(ShotCopy.Of(approved), _assets.Copy(), _sceneSelection.ToArray(), _maximum, _instructions, _model.Model) { CoverageOnly = true };
            var followsDefault = _model.FollowsDefault;
            var pending = await TextRequests.PlanShotsAsync(id, await AiReviews.TabIdAsync(), request, followsDefault, _lifetime.Token);
            if (_disposed || Id != project) return;
            _pendingPlanning = _planningAssist.Attribute(pending);
            await ReleasePlanningReviewAsync();
            _planningSuppressed = false; _planningOriginJob = id;
            await EnqueuePlanningAsync();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { _planningError = e.Message; }
        finally { _planningSubmitting = false; if (!_disposed) await RefreshPlanningJobsAsync(); }
    }
    private async Task EnqueuePlanningAsync()
    {
        if (_pendingPlanning is null) return;
        try
        {
            var job = await AiJobs.EnqueueAsync(_pendingPlanning, _lifetime.Token);
            _pendingPlanning = null;
            if (_planningAssist is not null) await _planningAssist.QueuedAsync();
            if (_disposed || job.Target.ProjectId != Id) return;
            await LoadPlanningJobAsync(job);
            if (_planningOpen && !_planningSuppressed && await AiReviews.TryOpenAsync(job, _planningDialogElement, automatic: false)) { _planningReviewOwner = job.Id; _focusPlanningClose = true; }
        }
        catch (WorkspaceStoreException e) { _planningError = e.Message + " Retry saving this exact request; it will not generate twice."; }
    }
    private async Task RetryPlanningEnqueueAsync()
    {
        if (_planningSubmitting) return;
        _planningSubmitting = true;
        try { await EnqueuePlanningAsync(); } finally { _planningSubmitting = false; await RefreshPlanningJobsAsync(); }
    }
    private async Task LoadPlanningJobAsync(AiJobHeader job)
    {
        if (job.State == AiJobState.NeedsAttention)
        {
            job = await AiJobStore.ReviewShotPlanningAsync(Id, job.Id, _lifetime.Token);
            await AiJobs.RefreshAsync(_lifetime.Token);
        }
        if (_planningLoadedJob != job.Id)
        {
            if (!await SavePlanningDraftAsync()) throw new WorkspaceStoreException("Save or resolve the current review before opening another breakdown.");
            var captured = AiTextJobHandler.Read(job, await AiJobStore.ReadSnapshotAsync(job.Id, _lifetime.Token));
            if (_disposed || job.Target.ProjectId != Id) return;
            _planningJob = job; _planningLoadedJob = job.Id; _planningRequest = captured.Payload<ShotPlanningRequest>();
            _sceneSelection = _planningRequest.SceneIds.ToHashSet(); _maximum = _planningRequest.MaximumSeconds; _instructions = _planningRequest.Instructions;
            _proposal = _planningOriginal = null; _planningRaw = ""; _planningError = null;
            _planningDraftRevision = 0; _planningDraftDirty = _planningDraftConflict = false; _planningDraftFingerprint = _planningDraftError = null;
        }
        await ReadPlanningResultAsync(job);
    }
    private AiJobHeader? _observedPlanningJob;
    private async Task ReadPlanningResultAsync(AiJobHeader job)
    {
        var result = await AiJobStore.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result, _lifetime.Token);
        if (_disposed || job.Target.ProjectId != Id || _planningJob?.Id != job.Id) return;
        _observedPlanningJob = job;
        _planningRaw = result?.Raw ?? "";
        _planningError = job.CancelRequested || job.State == AiJobState.Cancelled ? job.Error ?? "Request cancelled. No shots were added." : result?.Error ?? job.Error;
        if (job.State == AiJobState.Completed && result?.Complete == true && _proposal is null)
        {
            _planningOriginal = result.Read<ShotPlanningResult>();
            _proposal = _planningOriginal is null ? null : ShotCopy.Of(_planningOriginal);
            if (_proposal is { Error: null }) await LoadPlanningDraftAsync();
        }
    }
    private async Task ShowPlanningJobAsync(AiJobHeader job)
    {
        try
        {
            await LoadPlanningJobAsync(job);
            if (_disposed || job.Target.ProjectId != Id) { await AiReviews.CloseAsync(job.Id); return; }
            _planningReviewOwner = job.Id; _planningOpen = true;
            StateHasChanged();
        }
        catch { await AiReviews.CloseAsync(job.Id); throw; }
    }
    private async Task ReviewPlanningJobAsync(AiJobHeader job)
    {
        if (!await Save() || !await SavePlanningDraftAsync()) return;
        if (!await AiReviews.TryOpenAsync(job, _planningOriginElement, automatic: false)) { _error = "Close the other dialog before opening this breakdown."; return; }
        await ShowPlanningJobAsync(job);
    }
    private async Task HandleRequestedPlanningAsync()
    {
        if (!_interactive || !_planningInitialized || string.IsNullOrEmpty(_planningOriginElement.Id) || RequestedJobId is not { } id || _planningRequestedJob == id || _doc.ProjectId != Id) return;
        _planningRequestedJob = id;
        try
        {
            var job = AiJobs.View.Jobs.FirstOrDefault(j => j.Id == id && j.Kind == AiJobKind.ShotPlanning && j.Target.ShotId is null && j.Target.ProjectId == Id);
            if (job is not null) await ReviewPlanningJobAsync(job);
            else if (AiJobs.View.Jobs.FirstOrDefault(j => j.Id == id && SingleShotJob(j) && j.Target.ProjectId == Id) is { Target.ShotId: { } shotId } single &&
                _doc.Shots.Any(s => s.Id == shotId))
            {
                if (_selected != shotId) await Select(shotId);
                if (_selected == shotId) await InspectShotDraft();
            }
        }
        catch (Exception e) { _error = e.Message; StateHasChanged(); }
    }
    private async Task CancelPlanning()
    {
        if (_activePlanning is not { } job) return;
        try { await AiJobs.CancelAsync(job.Id, _lifetime.Token); await RefreshPlanningJobsAsync(); }
        catch (WorkspaceStoreException e) { _planningError = e.Message; }
    }
    private Task PlanningVisibility(bool open) => open ? Task.CompletedTask : ClosePlanning();
    private Task PlanningKeyDown(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e) => e.Key == "Escape" ? ClosePlanning() : Task.CompletedTask;
    private async Task ReleasePlanningReviewAsync()
    {
        if (_planningReviewOwner is { } id) await AiReviews.CloseAsync(id);
        _planningReviewOwner = null;
    }
    private async Task ClosePlanning()
    {
        if (_applyingProposal || !await SavePlanningDraftAsync()) return;
        _planningOpen = false; _planningSuppressed = true; await ReleasePlanningReviewAsync();
    }
    private string PlanningDraftJson => JsonSerializer.Serialize(new ShotPlanningReviewDraft(_proposal?.Shots ?? []), AtomicJsonFile.Options);
    private string PlanningDownloadUrl => "data:application/json;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(PlanningDraftJson));
    private void ReviewAppearances(Shot shot, List<ShotCharacter> characters)
    {
        if (_applyingProposal || PlanningApplied) return;
        if (_planningRequest?.CoverageOnly == true)
            foreach (var character in characters)
                if (ShotReferences.Characters(shot).FirstOrDefault(c => c.Id == character.Id) is { } prior && prior.Name != character.Name)
                    foreach (var line in shot.Dialogue.Where(d => string.Equals(d.Speaker, prior.Name, StringComparison.OrdinalIgnoreCase))) line.Speaker = character.Name;
        shot.Characters = characters; _planningDraftDirty = PlanningDraftJson != _planningDraftFingerprint;
        _planningDraftDelay?.Cancel(); _planningDraftDelay?.Dispose(); _planningDraftDelay = new();
        _ = DelayPlanningDraftAsync(_planningDraftDelay.Token);
    }
    private async Task DelayPlanningDraftAsync(CancellationToken ct)
    {
        try { await Task.Delay(450, ct); if (!_disposed) await InvokeAsync(async () => { await SavePlanningDraftAsync(); if (!_disposed) StateHasChanged(); }); }
        catch (OperationCanceledException) { }
    }
    private async Task<bool> SavePlanningDraftAsync(bool force = false)
    {
        if (_proposal is not { Error: null } || PlanningApplied || !_planningDraftDirty && !force) return true;
        if (_planningDraftConflict) return false;
        await _planningDraftGate.WaitAsync();
        try
        {
            var job = _planningJob!.Id; var content = PlanningDraftJson;
            if (!force && content == _planningDraftFingerprint) { _planningDraftDirty = false; return true; }
            var saved = await ReviewDrafts.SaveAsync(job, JsonSerializer.Deserialize<ShotPlanningReviewDraft>(content, AtomicJsonFile.Options)!, _planningDraftRevision);
            if (_planningJob?.Id != job) return true;
            _planningDraftRevision = saved.Revision; _planningDraftFingerprint = content; _planningDraftDirty = PlanningDraftJson != content; _planningDraftError = null; return true;
        }
        catch (WorkspaceConflictException) { _planningDraftConflict = _planningDraftDirty = true; _planningDraftError = "Another tab saved different review decisions. Download your copy or load the saved review before continuing."; return false; }
        catch (WorkspaceStoreException e) { _planningDraftDirty = true; _planningDraftError = e.Message; return false; }
        finally { _planningDraftGate.Release(); }
    }
    private Shot ComparableReviewSource(Shot shot)
    {
        var copy = shot.Copy();
        if (_planningRequest?.CoverageOnly == true && _planningOriginal?.Shots.FirstOrDefault(s => s.Id == shot.Id) is { } original)
            foreach (var line in copy.Dialogue)
                if (original.Dialogue.FirstOrDefault(d => d.Id == line.Id) is { } prior &&
                    ShotReferences.Character(original, prior.Speaker) is { } originalSpeaker &&
                    ShotReferences.Characters(shot).FirstOrDefault(c => c.Id == originalSpeaker.Id) is { } renamed && line.Speaker == renamed.Name)
                    line.Speaker = prior.Speaker;
        copy.Characters = []; return copy;
    }
    private async Task LoadPlanningDraftAsync()
    {
        try
        {
            var job = _planningJob!.Id; var draft = await ReviewDrafts.LoadAsync(job, _lifetime.Token);
            if (_disposed || _planningJob?.Id != job || _planningOriginal is null) return;
            if (draft.Read<ShotPlanningReviewDraft>() is { } saved)
            {
                // Reviews may change appearances, including adding silent characters, but never generated source or dialogue.
                if (saved.Shots is null || saved.Shots.Any(s => s is null || s.Characters is null || s.Characters.Any(c => c is null)) ||
                    Json(saved.Shots.Select(ComparableReviewSource).ToList()) != Json(_planningOriginal.Shots.Select(ComparableReviewSource).ToList()))
                    throw new WorkspaceStoreException("The saved review does not match this breakdown. Its content has not been replaced.");
                _proposal = _planningOriginal with { Shots = ShotCopy.Of(saved.Shots) };
            }
            _planningDraftRevision = draft.Revision; _planningDraftFingerprint = PlanningDraftJson;
            _planningDraftDirty = _planningDraftConflict = false; _planningDraftError = null;
        }
        catch (Exception e) when (e is WorkspaceStoreException or JsonException) { _planningDraftError = e.Message; _planningDraftConflict = true; }
    }
    private async Task ApplyProposal()
    {
        if (_proposal is not { Error: null, Shots.Count: > 0 } proposal || PlanningComposerLocked || PlanningApplied || _planningJob is not { State: AiJobState.Completed } job) return;
        _applyingProposal = true; _planningError = null;
        await _saveGate.WaitAsync();
        try
        {
            if (!await SavePlanningDraftAsync(force: true) || !await SaveLocked()) return;
            var added = ShotCopy.Of(proposal.Shots); var before = EditState; var version = _version;
            var saved = await Store.ApplyPlanningAsync(Id, job.Id, added, _doc.Revision, _lifetime.Token);
            Baseline(saved);
            if (version == _version) { Remember(); _coverageDirty = false; _doc = saved; _dirty = false; }
            else
            {
                // A background acknowledgement must preserve newer editor keystrokes.
                _undo.Push((Current?.Copy(), before)); _doc.Shots.AddRange(added.Where(s => _doc.Shots.All(current => current.Id != s.Id))); MergeMedia(saved);
            }
            _selected = added[0].Id; _openShotView = true; await SaveCoverageLocked(); _saveStatus = _dirty ? "Unsaved" : "Saved";
            _planningDraftDirty = false; _planningOpen = false; await ReleasePlanningReviewAsync();
        }
        catch (Exception e) { _planningError = e.Message; }
        finally { _applyingProposal = false; _saveGate.Release(); }
    }
}
