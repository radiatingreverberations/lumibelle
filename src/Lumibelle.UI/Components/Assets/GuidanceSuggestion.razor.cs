using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;

namespace lumibelle.Components.Assets;

public partial class GuidanceSuggestion
{
    [Inject] public IGuidanceAssistant Assistant { get; set; } = null!;
    [Inject] public AiJobCoordinator Jobs { get; set; } = null!;
    [Inject] public IAiJobStore Store { get; set; } = null!;
    [Inject] public AiTextJobCapture Requests { get; set; } = null!;
    [Inject] public TextRequestReviews ReviewOutcomes { get; set; } = null!;
    [Inject] public IAiReviewGate Reviews { get; set; } = null!;
    [Parameter] public GuidanceContext? Context { get; set; }
    [Parameter, EditorRequired] public AssetLibrary Library { get; set; } = default!;
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public bool Embedded { get; set; }
    [Parameter] public Guid? RequestedJobId { get; set; }
    [Parameter] public EventCallback<string> Apply { get; set; }
    private static readonly DialogOptions Options = new() { MaxWidth = MaxWidth.Large, FullWidth = true, BackdropClick = false, CloseOnEscapeKey = true };
    private bool _open, _inspect, _disposed, _applying, _submitting, _refreshing, _refreshAgain, _restoreFocus, _initialized, _focusClose;
    private string _imageKey = "", _raw = "", _suggestion = "";
    private string? _error, _inlineError, _baseline;
    private lumibelle.Components.AI.TextAssistance? _assist;
    private TextModelSelectionState? _model;
    private GuidanceRequest? _request;
    private PromptEnhancementResult? _result;
    private AiJobHeader? _job, _active, _latest;
    private AiJobSubmission? _pending;
    private Guid? _loadedJob, _resultJob, _handledRequested, _reserved;
    private readonly CancellationTokenSource _lifetime = new();
    private ElementReference _origin, _closeButton;
    private string? TargetKey => Context is null ? null : new AiJobTarget(Context.Target.ProjectId, Context.Target.AssetId,
        GuidanceScope: Context.Target.Scope, LookId: Context.Target.LookId, ImageId: Context.Target.ImageId).LockKey(AiJobKind.Guidance);
    private AiJobHeader? InspectionJob => Jobs.View.Jobs.FirstOrDefault(j => j.Kind == AiJobKind.Guidance && j.Target.LockKey(j.Kind) == TargetKey && TextRequestPresentation.IsActive(j)) ?? _latest;
    private Guid? _appliedJob;
    private PromptEnhancementResult? _latestResult;
    private TextRequestPresentation? GuidancePresentation => InspectionJob is { } job ? new(job, "Suggesting guidance…",
        _appliedJob == job.Id ? TextRequestOutcome.Resolved : _latestResult is { Kind: not PromptEnhancementKind.Prompt } ? TextRequestOutcome.Response : TextRequestOutcome.Proposal, AssistanceTitle) : null;
    private Task InspectGuidance() => InspectionJob is { } job ? Review(job) : Task.CompletedTask;
    private const string AssistanceTitle = "Suggest guidance";
    private const string DescriptionField = "guidance";
    private bool _busy => _submitting || _active is not null;
    private static string Key(Guid asset, Guid image) => $"{asset}/{image}";
    private AssetImageReference? InspectionImage => Library.Assets.SelectMany(a => a.Images.Select(i => new AssetImageReference(a.Id, i.Id))).FirstOrDefault(i => Key(i.AssetId, i.ImageId) == _imageKey);
    private string? VisionIssue => !_inspect ? null
        : _model is not { SupportsImages: true } || !TextVisionPolicy.SupportsBackend(_model.Model.Backend) ? TextVisionPolicy.SetupHint
        : InspectionImage is null ? "Choose an active image to inspect." : null;
    private bool CanStart => !Disabled && !_busy && !_applying && _savedChanges.Count == 0 && _pending is null && Context is not null && _model?.Ready == true && VisionIssue is null;
    // Changes since the request, compared with the target as edited here and, once Apply has
    // checked, as saved elsewhere. Apply anyway accepts the changes the review names (AssistedApply).
    private IReadOnlyList<string> _savedChanges = [];
    private IReadOnlyList<string> Changes => _request is null || Context is null ? [] : [.. GuidanceAssistant.Changes(_request.Context, Context).Union(_savedChanges)];
    private AssistedInputsChangedException? Changed => Superseded is null && _error is null && _result?.Kind == PromptEnhancementKind.Prompt && Changes is { Count: > 0 } changes
        ? new(changes, GuidanceAssistant.Subject(_request!.Context.Target.Scope), "this suggestion") : null;
    // Applying anyway replaces the field as it is now, not the captured original.
    private bool ReplacesEdited => _request is not null && Context is not null && Context.Guidance != _request.Context.Guidance;
    // A newer suggestion for the same field supersedes this one, which is then never applied.
    private string? Superseded => _job is { } job && Jobs.View.Jobs.Any(j => j.Kind == AiJobKind.Guidance && j.Target.LockKey(j.Kind) == TargetKey && j.CreatedUtc > job.CreatedUtc)
        ? "A newer suggestion replaced this one. Open the latest suggestion to apply it." : null;
    private bool CanApply => !Disabled && !_busy && !_applying && Superseded is null && _error is null && _job?.State == AiJobState.Completed && _result?.Kind == PromptEnhancementKind.Prompt && !string.IsNullOrWhiteSpace(_suggestion);
    private void ModelChanged(TextModelSelectionState model) => _model = model;
    protected override async Task OnInitializedAsync()
    {
        Jobs.Changed += QueueChanged;
        try { await RefreshJobs(); _initialized = true; }
        catch (WorkspaceStoreException e) { _inlineError = e.Message; }
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed) return;
        if (_initialized && !string.IsNullOrEmpty(_origin.Id) && RequestedJobId is { } requested && _handledRequested != requested)
        {
            var job = Jobs.View.Jobs.FirstOrDefault(j => j.Id == requested && j.Kind == AiJobKind.Guidance && j.Target.LockKey(j.Kind) == TargetKey);
            if (job is not null) { _handledRequested = requested; await Review(job); }
        }
        if (_focusClose && _open && !string.IsNullOrEmpty(_closeButton.Id))
        {
            _focusClose = false;
            try { await _closeButton.FocusAsync(); } catch (Exception e) when (e is JSException or JSDisconnectedException) { }
        }
        if (_restoreFocus && !_open)
        {
            _restoreFocus = false;
            try { if (_assist is not null) await _assist.FocusAsync(); } catch (Exception e) when (e is JSException or JSDisconnectedException) { }
        }
    }
    private Task Open() => _assist?.OpenAsync() ?? Task.CompletedTask;
    private void PrepareComposer()
    {
        _open = false; _savedChanges = []; _error = null;
    }
    private async Task Start()
    {
        if (!CanStart || _assist is null || !await _assist.PrepareSubmitAsync()) return;
        _submitting = true; _focusClose = true; _error = _inlineError = null; _result = null; _resultJob = null; _raw = _suggestion = "";
        var request = new GuidanceRequest(Context!.Capture(), _model!.Model, _inspect ? InspectionImage : null, _model.FollowsDefault);
        _request = request; _job = null; _loadedJob = null;
        try
        {
            if (_reserved is { } reserved) { await Reviews.CloseAsync(reserved); _reserved = null; }
            _pending = await Requests.GuidanceAsync(Guid.NewGuid(), await Reviews.TabIdAsync(), request, _lifetime.Token);
            _pending = _assist.Attribute(_pending); _pendingUsesOverride = true;
            await EnqueuePending();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { if (!_disposed) _error = Failure(e); }
        finally { _submitting = false; if (!_disposed) await RefreshJobs(); }
    }
    private bool _pendingUsesOverride;
    private async Task RetryCaptured()
    {
        if (_busy || _applying || Disabled || Changes.Count > 0 || _job is null) return;
        _submitting = true;
        try {
            var captured = AiTextJobHandler.Read(_job, await Store.ReadSnapshotAsync(_job.Id, _lifetime.Token));
            _pending = AiTextJobCapture.Reissue(_job, captured, await Reviews.TabIdAsync());
            _pendingUsesOverride = false; _loadedJob = null; _error = null; _result = null;
            await EnqueuePending();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { _error = Failure(e); }
        finally { _submitting = false; if (!_disposed) await RefreshJobs(); }
    }
    private async Task EnqueuePending()
    {
        if (_pending is null) return;
        try
        {
            _job = await Jobs.EnqueueAsync(_pending, _lifetime.Token); _loadedJob = null;
            _pending = null; _inlineError = null; if (_pendingUsesOverride && _assist is not null) await _assist.QueuedAsync(); await LoadJob(_job);
        }
        catch (WorkspaceStoreException e) { _error = e.Message + " Retry saving this exact request; it will not generate twice."; }
    }
    private async Task RetryEnqueue()
    {
        if (_submitting) return; _submitting = true;
        try { await EnqueuePending(); } finally { _submitting = false; if (!_disposed) await RefreshJobs(); }
    }
    private void QueueChanged() { if (!_disposed) _ = InvokeAsync(RefreshJobs); }
    private async Task RefreshJobs()
    {
        if (_disposed) return;
        if (_refreshing) { _refreshAgain = true; return; } _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                var matches = Jobs.View.Jobs.Where(j => j.Kind == AiJobKind.Guidance && j.Target.LockKey(j.Kind) == TargetKey).OrderByDescending(j => j.CreatedUtc).ToArray();
                _active = matches.FirstOrDefault(j => j.LocksTarget); _latest = matches.FirstOrDefault();
                if (_latest is { State: AiJobState.Completed } latest) {
                    var result = await Store.ReadArtifactAsync<AiTextJobResult>(latest.Id, AiJobArtifact.Result, _lifetime.Token);
                    if (latest.Target.LockKey(latest.Kind) != TargetKey) return;
                    if (await ReviewOutcomes.IsApplied(latest.Id, _lifetime.Token)) _appliedJob = latest.Id;
                    _latestResult = result is { Complete: true, Error: null } ? result.Read<PromptEnhancementResult>() : null;
                } else _latestResult = null;
                if (_pending is { } pending && matches.FirstOrDefault(j => j.Id == pending.Id) is { } accepted)
                { _pending = null; _job = accepted; }
                if (_job is { } prior && matches.FirstOrDefault(j => j.Id == prior.Id) is { } current)
                { _job = current; if (_open) await LoadJob(current); }
            } while (_refreshAgain && !_disposed);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { if (!_disposed) _inlineError = Failure(e); }
        finally { _refreshing = false; if (!_disposed) StateHasChanged(); }
    }
    private AiJobHeader? _observedResultJob;
    private async Task LoadJob(AiJobHeader job)
    {
        if (_loadedJob != job.Id)
        {
            var snapshot = AiTextJobHandler.Read(job, await Store.ReadSnapshotAsync(job.Id, _lifetime.Token));
            if (_disposed || _job?.Id != job.Id) return;
            _request = snapshot.Payload<GuidanceRequest>(); _baseline = snapshot.GuidanceBaseline; _loadedJob = job.Id;
            _inspect = _request.InspectionImage is not null;
            _imageKey = _request.InspectionImage is { } input ? Key(input.AssetId, input.ImageId) : "";
            _raw = _suggestion = ""; _error = null; _savedChanges = []; _result = null; _resultJob = null;
        }
        var result = await Store.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result, _lifetime.Token);
        if (_disposed || _job?.Id != job.Id) return;
        _observedResultJob = job;
        _raw = result?.Raw ?? "";
        _error = job.CancelRequested || job.State == AiJobState.Cancelled ? "Suggestion cancelled. Your guidance is unchanged." : result?.Error ?? job.Error;
        _result = job.State == AiJobState.Completed && _error is null && result?.Complete == true ? result.Read<PromptEnhancementResult>() : null;
        // A read acknowledgement or unrelated job's progress cannot reset author edits.
        if (_result?.Kind == PromptEnhancementKind.Prompt && _resultJob != job.Id) { _resultJob = job.Id; _suggestion = _result.Text; }

    }
    private async Task Review(AiJobHeader job)
    {
        try
        {
            if (!await Reviews.TryOpenAsync(job, _origin, automatic: false)) { _inlineError = "Close the other dialog before opening this suggestion."; return; }
            _reserved = job.Id; _job = job; await LoadJob(job); _open = true;

            if (!_disposed) StateHasChanged();
        }
        catch (Exception e) { _inlineError = Failure(e); await Reviews.CloseAsync(job.Id); _reserved = null; }
    }
    private async Task ApplySuggestion()
    {
        if (!CanApply || _request is null) return; _applying = true;
        try
        {
            var named = Changes; var request = _request;
            // Saved changes this review does not already show were made elsewhere, such as another tab.
            var saved = await Assistant.ReadTargetAsync(request.Context.Target, _lifetime.Token);
            var elsewhere = saved.Fingerprint() != _baseline && saved.Fingerprint() != request.Context.Fingerprint()
                ? GuidanceAssistant.Changes(request.Context, saved).Except(Context is null ? [] : GuidanceAssistant.Changes(request.Context, Context)).ToList() : [];
            if (request.InspectionImage is { } image)
            {
                try { await Assistant.ValidateImageAsync(request.Context.Target.ProjectId, image, _lifetime.Token); }
                catch (AiGenerationException) { elsewhere.Add("the inspected image was removed"); }
            }
            _savedChanges = elsewhere;
            var changes = Changes;
            AssistedApply.Allows(changes, automatic: false, acceptChangedInputs: changes.All(named.Contains), GuidanceAssistant.Subject(request.Context.Target.Scope));
            if (Superseded is not null || Disabled || !_open || _disposed || _busy || _request != request) return;
            await Apply.InvokeAsync(_suggestion); _appliedJob = _job?.Id; _savedChanges = [];
            try { if (_appliedJob is { } id) await ReviewOutcomes.Applied(id, _lifetime.Token); }
            catch (WorkspaceStoreException e) { _inlineError = "Changes applied, but review status could not be saved. " + e.Message; }
            await Close();
        }
        // The review now names the changes and offers Apply anyway.
        catch (AssistedInputsChangedException) { }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { _error = Failure(e); }
        finally { _applying = false; }
    }
    private async Task CancelRequest()
    {
        if (_active is null) return;
        try { await Jobs.CancelAsync(_active.Id, _lifetime.Token); await RefreshJobs(); }
        catch (WorkspaceStoreException e) { _error = _inlineError = e.Message; }
    }
    private async Task Close()
    {
        _open = false; _restoreFocus = true;
        if (_reserved is { } id) { await Reviews.CloseAsync(id); _reserved = null; }
        // Closing a preview never cancels application-owned work or reopens it later.
    }
    private Task DialogKeyDown(KeyboardEventArgs e) => e.Key == "Escape" ? Close() : Task.CompletedTask;
    private Task VisibleChanged(bool visible) => visible ? Task.CompletedTask : Close();
    private static string Failure(Exception e) => e is AiGenerationException or WorkspaceStoreException or ProjectStoreException ? e.Message : "Guidance could not be read or saved. Your notes are unchanged; retry when ready.";
    public async ValueTask DisposeAsync()
    {
        _disposed = true; Jobs.Changed -= QueueChanged; _lifetime.Cancel();
        if (_reserved is { } id) await Reviews.CloseAsync(id);
    }
}
