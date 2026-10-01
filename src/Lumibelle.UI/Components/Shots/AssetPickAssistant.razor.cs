using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Shots;

public partial class AssetPickAssistant : IAsyncDisposable
{
    [Inject] public AiJobCoordinator Jobs { get; set; } = null!;
    [Inject] public IAiJobStore JobStore { get; set; } = null!;
    [Inject] public AiTextJobCapture Requests { get; set; } = null!;
    [Inject] public IAssetStore AssetStore { get; set; } = null!;
    [Inject] public IAiReviewGate Reviews { get; set; } = null!;
    [Parameter, EditorRequired] public Shot Shot { get; set; } = null!;
    [Parameter, EditorRequired] public AssetLibrary Library { get; set; } = null!;
    [Parameter] public string Prompt { get; set; } = "";
    [Parameter] public string DirectingNotes { get; set; } = "";
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public Guid? RequestedJobId { get; set; }
    [Parameter] public EventCallback<AssetPickStage> Staged { get; set; }
    private lumibelle.Components.AI.TextAssistance? _assist;
    private TextModelSelectionState? _model;
    private readonly CancellationTokenSource _lifetime = new();
    private AssetLibrary? _catalogueLibrary;
    private long _catalogueRevision = -1;
    private AssetPickCatalogue? _catalogue;
    private string _catalogueFingerprint = "";
    private AssetPickRequest? _request;
    private AssetPickResult? _result;
    private AssetPickDraft? _plan;
    private AiJobSubmission? _pending;
    private AiJobHeader? _job, _active, _observed;
    private IReadOnlyList<AiJobHeader> _history = [];
    private Guid? _selectedJob, _loadedJob, _resultJob, _handledRequested;
    // Starts collapsed so the references stay in view; only a link to this shot's own selection request opens it.
    private bool _openForRequest;
    private bool _submitting, _staging, _disposed, _refreshing, _refreshAgain, _reviewVisible, _staged;
    private bool _replaceExisting;
    private string _instructions = "", _raw = "";
    private string? _error, _planError, _operationError;
    private readonly HashSet<string> _checked = new(StringComparer.Ordinal);
    private bool Working => _submitting || _staging;
    private bool CanStart => !Disabled && !Working && _active is null && _pending is null && _model?.Ready == true && _catalogue?.Candidates.Count > 0;
    private bool Stale => _request is not null && (_request.ContextFingerprint != CurrentFingerprint ||
        _request.CatalogueFingerprint != _catalogueFingerprint || _request.ReplaceExisting != _replaceExisting || _request.Instructions != _instructions);
    private string CurrentFingerprint => AssetPickCatalog.ContextFingerprint(Library.ProjectId, Shot, Prompt, DirectingNotes);
    private bool CanStage => !Disabled && !Working && _active is null && !_staged && !Stale && _error is null &&
        _job is { State: AiJobState.Completed, CancelRequested: false } && _result is not null && _plan is not null && _checked.Count > 0;
    private IReadOnlyList<AssetPickItem> CheckedItems => _result?.Selections.Where(s => _checked.Contains(s.CandidateId)).ToArray() ?? [];

    protected override void OnParametersSet()
    {
        CaptureCatalogue();
        if (!_staged) RefreshPlan();
    }
    private void CaptureCatalogue()
    {
        if (!ReferenceEquals(_catalogueLibrary, Library) || _catalogueRevision != Library.Revision)
        {
            _catalogueLibrary = Library; _catalogueRevision = Library.Revision;
            _catalogue = AssetPickCatalog.Capture(Library); _catalogueFingerprint = AssetPickCatalog.Hash(_catalogue);
        }
    }
    protected override async Task OnInitializedAsync()
    {
        CaptureCatalogue();
        Jobs.Changed += QueueChanged;
        await RefreshJobs();
    }
    private void QueueChanged() { if (!_disposed) _ = InvokeAsync(RefreshJobs); }
    private async Task Start()
    {
        if (!CanStart || _assist is null || !await _assist.PrepareSubmitAsync() || !CanStart) return;
        _submitting = true; _operationError = null;
        try
        {
            var submission = await Requests.PickAssetsAsync(Guid.NewGuid(), await Reviews.TabIdAsync(), Library.ProjectId,
                Shot, Prompt, DirectingNotes, _instructions, _replaceExisting, _catalogueFingerprint,
                _model!.Model, _model.FollowsDefault, _lifetime.Token);
            if (_disposed) return;
            _pending = _assist.Attribute(submission);
            await EnqueuePending();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { if (!_disposed) _operationError = e.Message; }
        finally { _submitting = false; if (!_disposed) await RefreshJobs(); }
    }
    private async Task EnqueuePending()
    {
        if (_pending is not { } pending) return;
        try
        {
            var job = await Jobs.EnqueueAsync(pending, _lifetime.Token);
            if (_disposed) return;
            _pending = null; _selectedJob = job.Id; _loadedJob = null; _reviewVisible = true;
            await AcknowledgeQueued(job.Id);
            _operationError = null;
        }
        catch (WorkspaceStoreException e)
        {
            _operationError = e.Message + " Retry saving the captured request; this will not generate it twice.";
        }
    }
    private readonly HashSet<Guid> _queueAcknowledged = [];
    private async Task AcknowledgeQueued(Guid id)
    {
        if (_assist is null || !_queueAcknowledged.Add(id)) return;
        // The enqueue acknowledgement and a queue notification can race.
        // A request-scoped model override is consumed only once.
        await _assist.QueuedAsync();
    }
    private async Task RetryEnqueue()
    {
        if (Working || Disabled) return;
        _submitting = true;
        try { await EnqueuePending(); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { if (!_disposed) _operationError = e.Message; }
        finally { _submitting = false; if (!_disposed) await RefreshJobs(); }
    }
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
                var matches = Jobs.View.Jobs.Where(j => j.Kind == AiJobKind.AssetPicking && j.Target.ProjectId == Library.ProjectId && j.Target.ShotId == Shot.Id)
                    .OrderByDescending(j => j.CreatedUtc).ThenByDescending(j => j.Id).ToArray();
                _history = matches; _active = matches.FirstOrDefault(j => j.LocksTarget);
                if (_pending is { } pending && matches.Any(j => j.Id == pending.Id))
                {
                    _pending = null; _selectedJob = pending.Id; _loadedJob = null; _reviewVisible = true;
                    await AcknowledgeQueued(pending.Id);
                }
                if (RequestedJobId is { } requested && _handledRequested != requested && matches.Any(j => j.Id == requested))
                {
                    _handledRequested = requested; _selectedJob = requested; _reviewVisible = true; _openForRequest = true;
                }
                var job = matches.FirstOrDefault(j => j.Id == _selectedJob) ?? matches.FirstOrDefault();
                _job = job;
                if (job is not null && (_reviewVisible || job.LocksTarget)) await LoadJob(job);
            } while (_refreshAgain && !_disposed);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { if (!_disposed) _operationError = e.Message; }
        finally { _refreshing = false; if (!_disposed) StateHasChanged(); }
    }
    private async Task LoadJob(AiJobHeader job)
    {
        if (_loadedJob != job.Id)
        {
            var snapshot = AiTextJobHandler.Read(job, await JobStore.ReadSnapshotAsync(job.Id, _lifetime.Token));
            if (_disposed || _job?.Id != job.Id) return;
            _request = snapshot.Payload<AssetPickRequest>(); _loadedJob = job.Id;
            _replaceExisting = _request.ReplaceExisting; _instructions = _request.Instructions;
            _raw = ""; _error = null; _result = null; _resultJob = null; _plan = null; _planError = null;
            _staged = false; _checked.Clear();
        }
        var artifact = await JobStore.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result, _lifetime.Token);
        if (_disposed || _job?.Id != job.Id) return;
        _observed = job; _raw = artifact?.Raw ?? "";
        _error = job.CancelRequested || job.State == AiJobState.Cancelled ? "Selection cancelled. Your references are unchanged." : artifact?.Error ?? job.Error;
        _result = job is { State: AiJobState.Completed, CancelRequested: false } && artifact is { Complete: true, Error: null }
            ? artifact.Read<AssetPickResult>() : null;
        if (_result is not null && _resultJob != job.Id)
        {
            _resultJob = job.Id; _checked.Clear();
            foreach (var item in _result.Selections) _checked.Add(item.CandidateId);
            RefreshPlan();
        }
    }
    private async Task ShowReview()
    {
        _reviewVisible = true;
        await RefreshJobs();
    }
    private async Task ChooseHistory(ChangeEventArgs e)
    {
        if (Working || !Guid.TryParse(e.Value?.ToString(), out var id) || !_history.Any(j => j.Id == id)) return;
        _selectedJob = id; _reviewVisible = true; _loadedJob = null; await RefreshJobs();
    }
    private void Toggle(string id, bool selected)
    {
        if (Working || _staged) return;
        if (selected) _checked.Add(id); else _checked.Remove(id);
        RefreshPlan();
    }
    private void RefreshPlan()
    {
        _plan = null; _planError = null;
        if (_request is null || _result is null || Stale || _checked.Count == 0) return;
        try { _plan = AssetPickPlan.Create(_request, CheckedItems, Shot, Prompt, DirectingNotes, Library); }
        catch (WorkspaceStoreException e) { _planError = e.Message; }
    }
    private async Task Stage()
    {
        if (!CanStage || _request is not { } request) return;
        var items = CheckedItems; var jobId = _job!.Id;
        _staging = true; _operationError = null;
        try
        {
            var library = await AssetStore.LoadAsync(request.ProjectId, _lifetime.Token);
            if (_disposed || Disabled || Stale || _job?.Id != jobId) return;
            var plan = AssetPickPlan.Create(request, items, Shot, Prompt, DirectingNotes, library);
            await Staged.InvokeAsync(new(request.ContextFingerprint, request.CatalogueFingerprint, plan, request.ReplaceExisting));
            _staged = true;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { if (!_disposed) _operationError = e.Message; }
        finally { _staging = false; }
    }
    private async Task CancelRequest()
    {
        if (_active is not { } job) return;
        try { await Jobs.CancelAsync(job.Id, _lifetime.Token); await RefreshJobs(); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { if (!_disposed) _operationError = e.Message; }
    }
    private async Task ResumeRequest()
    {
        if (Working || _job is not { CanRetryCaptured: true } job) return;
        _submitting = true;
        try { await Jobs.ResumeAsync(job.Id, _lifetime.Token); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { if (!_disposed) _operationError = e.Message; }
        finally { _submitting = false; if (!_disposed) await RefreshJobs(); }
    }
    private AssetPickCandidate? Candidate(AssetPickItem item) => _request?.Catalogue.Candidates.FirstOrDefault(c => c.Id == item.CandidateId);
    private string Owner(AssetPickCandidate c) => _request?.Catalogue.Assets.FirstOrDefault(a => a.Id == c.AssetId)?.Name ?? "Asset";
    public async ValueTask DisposeAsync()
    {
        _disposed = true; Jobs.Changed -= QueueChanged;
        // Cancelling a component's reads must not cancel application-owned work.
        await _lifetime.CancelAsync(); _lifetime.Dispose();
    }
}
