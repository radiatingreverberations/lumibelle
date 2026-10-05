using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;

namespace lumibelle.Components.Assets;

public partial class PromptEnhancementPanel
{
    [Inject] public IPromptEnhancer Enhancer { get; set; } = null!;
    [Inject] public AiJobCoordinator Jobs { get; set; } = null!;
    [Inject] public IAiJobStore Store { get; set; } = null!;
    [Inject] public AiTextJobCapture Requests { get; set; } = null!;
    [Inject] public TextRequestReviews ReviewOutcomes { get; set; } = null!;
    [Inject] public IAiReviewGate Reviews { get; set; } = null!;
    [Inject] public IJSRuntime JS { get; set; } = null!;
    [Parameter, EditorRequired] public PromptEnhancementContext Context { get; set; } = null!;
    [Parameter] public Guid? RequestedJobId { get; set; }
    [Parameter] public EventCallback<Guid> RequestedJobHandled { get; set; }
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public EventCallback<bool> BusyChanged { get; set; }
    [Parameter, EditorRequired] public Func<PromptEnhancementContext, string, Task<bool>> Apply { get; set; } = (_, _) => Task.FromResult(false);
    [Parameter] public Func<PromptEnhancementContext, bool, Task<bool>> Restore { get; set; } = (_, _) => Task.FromResult(false);

    private lumibelle.Components.AI.TextAssistance? _assist;
    private bool _freshRequest;
    private TextModelSelectionState? _model;
    private PromptEnhancementRequest? _request;
    private PromptEnhancementResult? _result;
    private PromptEnhancementContext? _undoContext;
    private string _undoText = "", _suggestion = "", _raw = "";
    private string? _error, _inlineError;
    private bool _busy, _open, _inspect, _disposed, _applying, _restoreFocus, _submitting, _refreshing, _refreshAgain;
    private AiJobHeader? _job, _active;
    private AiJobSubmission? _pending;
    private Guid? _loadedJob, _originJob, _attempted, _handledRequested, _reviewedJob, _resultJob;
    private string? _resultText;
    private readonly CancellationTokenSource _lifetime = new();
    private ElementReference _origin;
    private static readonly DialogOptions Options = new() { MaxWidth = MaxWidth.Large, FullWidth = true, CloseOnEscapeKey = true, BackdropClick = false };
    private AiJobHeader? InspectionJob => Jobs.View.Jobs.FirstOrDefault(j => j.Kind == AiJobKind.PromptEnhancement && j.Target.LockKey(j.Kind) == TargetKey && TextRequestPresentation.IsActive(j)) ?? _job;
    private Guid? _resolvedJob;
    private TextRequestPresentation? EnhancementPresentation => InspectionJob is { } job ? new(job, "Enhancing prompt…",
        _resolvedJob == job.Id ? TextRequestOutcome.Resolved : _result is { Kind: not PromptEnhancementKind.Prompt } ? TextRequestOutcome.Response : TextRequestOutcome.Proposal, "Enhance prompt") : null;
    private Task InspectEnhancement() => InspectionJob is { } job ? Review(job) : Task.CompletedTask;
    private string TargetKey => new AiJobTarget(Context.ProjectId, Context.AssetId).LockKey(AiJobKind.PromptEnhancement);
    private string? VisionIssue => !_inspect || !Context.IsEdit ? null : _model is { SupportsImages: true } && TextVisionPolicy.SupportsBackend(_model.Model.Backend)
        ? null : TextVisionPolicy.SetupHint + " Or turn off inspection to use text context.";
    private bool Blocked => Disabled || Jobs.View.Jobs.Any(j => j.Kind != AiJobKind.PromptEnhancement && j.LocksTarget && j.Target.LockKey(j.Kind) == TargetKey);
    private bool CanEnhance => !Blocked && !_busy && _pending is null && _model?.Ready == true && !string.IsNullOrWhiteSpace(Context.Prompt) && VisionIssue is null;
    // Changes since the request, compared with the prompt setup as it is now and, once Apply has
    // checked, with the saved library. Apply anyway accepts the changes the review names (AssistedApply).
    private IReadOnlyList<string> _savedChanges = [];
    private IReadOnlyList<string> Changes => _request is null ? [] : [.. PromptEnhancer.Changes(_request.Context, Context).Union(_savedChanges)];
    private AssistedInputsChangedException? Changed => Superseded is null && _error is null && _result?.Kind == PromptEnhancementKind.Prompt && Changes is { Count: > 0 } changes
        ? new(changes, "the prompt setup") : null;
    // Applying anyway replaces the prompt as it is now, not the captured original.
    private bool ReplacesEdited => _request is not null && Context.Prompt != _request.Context.Prompt;
    // A newer enhancement for this asset supersedes this one, which is then never applied.
    private string? Superseded => _job is { } job && Jobs.View.Jobs.Any(j => j.Kind == AiJobKind.PromptEnhancement && j.Target.LockKey(j.Kind) == TargetKey && j.CreatedUtc > job.CreatedUtc)
        ? "A newer enhancement replaced this one. Open the latest enhancement to apply it." : null;
    private bool CanApply => !Blocked && !_busy && !_applying && Superseded is null && _error is null && _job?.State == AiJobState.Completed &&
        _result?.Kind == PromptEnhancementKind.Prompt && !string.IsNullOrWhiteSpace(_suggestion);

    protected override void OnParametersSet()
    {
        if (RequestedJobId is null) _handledRequested = null;
        if (_undoContext is not null && _undoContext.Fingerprint() != Context.Fingerprint()) _undoContext = null;
    }
    protected override async Task OnInitializedAsync()
    {
        Jobs.Changed += QueueChanged;
        try { await Jobs.RefreshAsync(_lifetime.Token); await RefreshJobs(); }
        catch (WorkspaceStoreException e) { _inlineError = e.Message; }
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed) return;
        if (RequestedJobId is { } requested && _handledRequested != requested)
        {
            _handledRequested = requested;
            var header = Jobs.View.Jobs.FirstOrDefault(j => j.Id == requested);
            if (header?.Kind == AiJobKind.PromptEnhancement && header.Target.LockKey(header.Kind) == TargetKey)
            {
                await RequestedJobHandled.InvokeAsync(requested);
                await Review(header);
            }
        }
        if (!_restoreFocus || _open) return;
        _restoreFocus = false;
        try { if (_assist is not null) await _assist.RestoreFocusAsync(); }
        catch (Exception e) when (e is JSException or JSDisconnectedException) { }
    }
    private void ModelChanged(TextModelSelectionState state) => _model = state;
    private async Task Start() {
        if (!CanEnhance || _assist is null || !await _assist.PrepareSubmitAsync()) return;
        _freshRequest = true;
        try { await Submit(new(Context.Capture(), _model!.Model, Context.IsEdit && _inspect, _model.FollowsDefault)); }
        finally { _freshRequest = false; }
    }
    private bool _pendingUsesOverride;
    private async Task Retry()
    {
        if (_busy || Blocked || Changes.Count > 0 || _job is null) return;
        _submitting = true; await SetBusy(true); await Close();
        try {
            var captured = AiTextJobHandler.Read(_job, await Store.ReadSnapshotAsync(_job.Id, _lifetime.Token));
            _pending = AiTextJobCapture.Reissue(_job, captured, await Reviews.TabIdAsync());
            _pendingUsesOverride = false; _originJob = _loadedJob = _pending.Id;
            _error = _inlineError = null; _raw = _suggestion = ""; _result = null; _resultText = null;
            await EnqueuePending();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { _inlineError = Failure(e); }
        finally { _submitting = false; if (!_disposed) await RefreshJobs(); }
    }
    private async Task Submit(PromptEnhancementRequest request)
    {
        _submitting = true; await SetBusy(true); await Close();
        _request = request; _error = _inlineError = null; _raw = _suggestion = ""; _result = null; _resultText = null;
        try
        {
            var id = Guid.NewGuid(); var tab = await Reviews.TabIdAsync();
            _pending = await Requests.EnhanceAsync(id, tab, request, _lifetime.Token);
            if (_freshRequest && _assist is not null) _pending = _assist.Attribute(_pending);
            _pendingUsesOverride = _freshRequest;
            _originJob = _loadedJob = id;
            await EnqueuePending();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { _inlineError = Failure(e); }
        finally { _submitting = false; if (!_disposed) await RefreshJobs(); }
    }
    private async Task EnqueuePending()
    {
        if (_pending is null) return;
        try { await Jobs.EnqueueAsync(_pending, _lifetime.Token); _pending = null; _inlineError = null; if (_pendingUsesOverride && _assist is not null) await _assist.QueuedAsync(); }
        catch (WorkspaceStoreException e) { _inlineError = e.Message + " Retry saving this exact request; it will not generate twice."; }
    }
    private async Task RetryEnqueue()
    {
        if (_submitting) return;
        _submitting = true; await SetBusy(true);
        try { await EnqueuePending(); }
        finally { _submitting = false; await RefreshJobs(); }
    }
    private void QueueChanged() { if (!_disposed) _ = InvokeAsync(RefreshJobs); }
    private async Task RefreshJobs()
    {
        if (_disposed) return;
        if (_refreshing) { _refreshAgain = true; return; }
        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                var matches = Jobs.View.Jobs.Where(j => j.Target.LockKey(j.Kind) == TargetKey).ToArray();
                _active = matches.FirstOrDefault(j => j.Kind == AiJobKind.PromptEnhancement && j.LocksTarget);
                await SetBusy(_submitting || _active is not null);
                var current = _open && _job is not null ? matches.FirstOrDefault(j => j.Id == _job.Id) :
                    matches.Where(j => j.Kind == AiJobKind.PromptEnhancement).OrderByDescending(j => j.CreatedUtc).FirstOrDefault();
                if (current is not null)
                {
                    _job = current;
                    await LoadJob(current, restore: !_submitting && _loadedJob != current.Id && current.LocksTarget);
                }
                if (_pending is { } pending && matches.Any(j => j.Id == pending.Id)) _pending = null;
                if (!_disposed && _originJob is { } origin && _attempted != origin && matches.FirstOrDefault(j => j.Id == origin) is { } finished &&
                    finished.State is not (AiJobState.Waiting or AiJobState.Running))
                {
                    _attempted = origin;
                    if (!_open && !finished.CancelRequested && await Reviews.TryOpenAsync(finished, _origin, automatic: true)) await ShowReview(finished);
                }
            } while (_refreshAgain && !_disposed);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { if (!_disposed) _inlineError = Failure(e); }
        finally { _refreshing = false; if (!_disposed) StateHasChanged(); }
    }
    private AiJobHeader? _observedResultJob;
    private async Task LoadJob(AiJobHeader job, bool restore)
    {
        if (_loadedJob != job.Id)
        {
            var captured = AiTextJobHandler.Read(job, await Store.ReadSnapshotAsync(job.Id, _lifetime.Token)).Payload<PromptEnhancementRequest>();
            _request = captured; _inspect = captured.InspectImages; _loadedJob = job.Id;
            _suggestion = _raw = ""; _error = null; _savedChanges = []; _result = null; _resultJob = null;
            if (restore) await Restore(captured.Context, true);
        }
        var result = await Store.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result, _lifetime.Token);
        if (await ReviewOutcomes.IsResolved(job.Id, _lifetime.Token)) _resolvedJob = job.Id;
        _observedResultJob = job;
        _raw = result?.Raw ?? "";
        _error = job.CancelRequested || job.State == AiJobState.Cancelled ? "Enhancement cancelled. Your prompt is unchanged." : result?.Error ?? job.Error;
        _result = job.State == AiJobState.Completed && _error is null && result?.Complete == true ? result.Read<PromptEnhancementResult>() : null;
        // Progress/read acknowledgements must never replace the author's edited suggestion.
        if (_result?.Kind == PromptEnhancementKind.Prompt && (_resultJob != job.Id || _resultText != _result.Text))
        { _resultJob = job.Id; _resultText = _result.Text; _suggestion = _result.Text; }
    }
    private async Task Review(AiJobHeader job)
    {
        try
        {
            if (!await Reviews.TryOpenAsync(job, _origin, automatic: false)) { _inlineError = "Close the other dialog before opening this enhancement."; return; }
            await ShowReview(job);
        }
        catch (Exception e) { _inlineError = Failure(e); await Reviews.CloseAsync(job.Id); }
    }
    private async Task ShowReview(AiJobHeader job)
    {
        if (_disposed) { await Reviews.CloseAsync(job.Id); return; }
        try
        {
            _job = job; await LoadJob(job, restore: false);
            if (_request is not null) await Restore(_request.Context, true);
            if (_disposed) { await Reviews.CloseAsync(job.Id); return; }
            _reviewedJob = job.Id; _open = true;

            if (!_disposed) StateHasChanged();
        }
        catch { await Reviews.CloseAsync(job.Id); throw; }
    }
    private async Task RestoreSetup()
    {
        if (_request is null || Blocked || _busy || _applying) return;
        _applying = true;
        try
        {
            await Enhancer.ValidateInputsAsync(_request.Context, _lifetime.Token);
            if (!await Restore(_request.Context, false)) _error = "The captured setup could not be loaded. Your current prompt is unchanged.";
        }
        catch (Exception e) { _error = Failure(e); }
        finally { _applying = false; }
    }
    private async Task ApplySuggestion()
    {
        if (!CanApply || _request is null) return;
        _applying = true;
        try
        {
            var named = Changes; var request = _request;
            // Captured inputs that no longer validate changed in the saved library.
            try { await Enhancer.ValidateInputsAsync(request.Context, _lifetime.Token); _savedChanges = []; }
            catch (AiGenerationException) { _savedChanges = ["the saved looks or references changed"]; }
            var changes = Changes;
            AssistedApply.Allows(changes, automatic: false, acceptChangedInputs: changes.All(named.Contains), "the prompt setup");
            if (Superseded is not null || Blocked || !_open || _disposed || _busy || _request != request) return;
            // The suggestion replaces the prompt as it is now, which Undo restores.
            var target = Context;
            if (await Apply(target, _suggestion))
            {
                _resolvedJob = _job?.Id; _undoText = target.Prompt; _undoContext = target with { Prompt = _suggestion }; _savedChanges = [];
                try { if (_resolvedJob is { } id) await ReviewOutcomes.Applied(id, _lifetime.Token); }
                catch (WorkspaceStoreException e) { _inlineError = "Changes applied, but review status could not be saved. " + e.Message; }
                await Close();
            }
            else _error = "The prompt changed. Return to the prompt and enhance again.";
        }
        // The review now names the changes and offers Apply anyway.
        catch (AssistedInputsChangedException) { }
        catch (Exception e) { _error = Failure(e); }
        finally { _applying = false; }
    }
    // Declining a suggestion resolves it like applying one, so it no longer asks for review.
    private bool CanDiscard => !_busy && !_applying && _job is { State: AiJobState.Completed } job && _resolvedJob != job.Id &&
        _result?.Kind == PromptEnhancementKind.Prompt;
    private async Task DiscardSuggestion()
    {
        if (!CanDiscard || _job is not { } job) return;
        _applying = true;
        try { await ReviewOutcomes.Discarded(job.Id, _lifetime.Token); _resolvedJob = job.Id; await Close(); }
        catch (Exception e) { _error = Failure(e); }
        finally { _applying = false; }
    }
    private async Task Undo()
    {
        if (_undoContext is null || Blocked || _busy || _undoContext.Fingerprint() != Context.Fingerprint()) return;
        if (!await Apply(_undoContext, _undoText)) _inlineError = "The prompt changed; Undo was not applied.";
        _undoContext = null;
    }
    private async Task CancelRequest()
    {
        if (_active is null) return;
        try { await Jobs.CancelAsync(_active.Id, _lifetime.Token); await RefreshJobs(); }
        catch (WorkspaceStoreException e) { _inlineError = _error = e.Message; }
    }
    // Starts over in the composer; this suggestion stays to apply or discard later.
    private async Task NewRequestFromReview() { await Close(); if (_assist is not null) await _assist.NewRequestAsync(); }
    private async Task Close()
    {
        _open = false; _restoreFocus = true;
        if (_reviewedJob is { } id) { await Reviews.CloseAsync(id); _reviewedJob = null; }
    }
    private Task DialogKeyDown(KeyboardEventArgs e) => e.Key == "Escape" ? Close() : Task.CompletedTask;
    private Task VisibilityChanged(bool visible) => visible ? Task.CompletedTask : Close();
    private async Task SetBusy(bool value) { if (_busy == value || _disposed) return; _busy = value; await BusyChanged.InvokeAsync(value); }
    private static string Failure(Exception e) => e is AiGenerationException or WorkspaceStoreException or ProjectStoreException ? e.Message : "This enhancement could not be read or saved. Your prompt is unchanged; retry when ready.";
    public async ValueTask DisposeAsync()
    {
        _disposed = true; Jobs.Changed -= QueueChanged; _lifetime.Cancel();
        if (_reviewedJob is { } id) await Reviews.CloseAsync(id);
        if (_originJob is { } origin && origin != _reviewedJob) await Reviews.CloseAsync(origin);
        // Accepted jobs belong to the application. Only an unfinished capture is cancelled.
    }
}
