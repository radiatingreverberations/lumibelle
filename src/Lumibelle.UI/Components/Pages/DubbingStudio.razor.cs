using System.Text.Json;
using lumibelle.Components.Layout;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace lumibelle.Components.Pages;

public partial class DubbingStudio
{
    [Parameter] public Guid Id { get; set; }
    [Parameter] public Guid? RequestedShotId { get; set; }
    [Parameter] public Guid? RequestedTakeId { get; set; }
    [Parameter] public Guid? RequestedJobId { get; set; }
    private readonly Guid _tabId = Guid.NewGuid();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    public DubbingStudio() { _token = _lifetime.Token; }
    private (Guid, Guid?, Guid?, Guid?)? _loadedParameters;
    private ProjectInfo? _project;
    private ProjectLanguages? _languages;
    private ShotDocument _shots = new();
    private ShotDubDocument _variants = new();
    private Guid? _takeId, _translationJobId, _renderJobId, _proposalJobId, _shotFilter;
    private string _target = "", _instructions = "";
    private string? _error, _notice, _renderError;
    private bool _busy, _dirty, _disposed, _refreshing;
    private long _responseVersion = -1, _renderVersion = -1, _saveVersion;
    private TextModelSelectionState? _model;
    private ShotDubRequest? _request, _suggestionRequest;
    private ShotDubVariant? _saved;
    private IReadOnlyList<DubLine> _lines = [];
    private IReadOnlyList<string> _notes = [];
    private ShotDubTranslation? _suggestion;
    private AiTextJobResult? _rawResult;
    private AiJobSubmission? Pending;
    private bool Locked => _busy || _dirty || Pending is not null;
    private List<ShotTake> MasterTakes => _shots.Takes.Where(t => t.Snapshot.Dub is null && t.Refinement is null &&
        t.Snapshot.Shot.Dialogue.Count > 0 && (_shotFilter is null || t.ShotId == _shotFilter)).OrderByDescending(t => t.CreatedUtc).ToList();
    private List<ShotTake> DubTakes => _shots.Takes.Where(t => t.Snapshot.Dub is { } d && d.SourceTakeId == _takeId && d.Target.Code == _target).OrderByDescending(t => t.CreatedUtc).ToList();
    private AiJobHeader? TranslationJob => _translationJobId is { } id ? Jobs.View.Jobs.FirstOrDefault(j => j.Id == id) : null;
    private AiJobHeader? ActiveTranslation => Jobs.View.Jobs.FirstOrDefault(j => j.Kind == AiJobKind.ShotTranslation && j.Target.ProjectId == Id && j.Target.TakeId == _takeId && j.LocksTarget);
    private AiJobHeader? ActiveVideo => Jobs.View.Jobs.FirstOrDefault(j => j.Kind == AiJobKind.Video && j.Target.ProjectId == Id && j.Target.ShotId == _request?.ShotId && j.LocksTarget);
    private string? PreviewIssue
    {
        get { if (_request is null) return null; try { _ = ShotDubbing.Prompt(_request, _lines); return null; } catch (WorkspaceStoreException e) { return e.Message; } }
    }
    private string Preview => _request is null || PreviewIssue is not null ? "Complete every translated line to preview the language prompt." : ShotDubbing.Prompt(_request, _lines);
    protected override void OnInitialized() => Jobs.Changed += QueueChanged;
    protected override async Task OnParametersSetAsync()
    {
        var key = (Id, RequestedShotId, RequestedTakeId, RequestedJobId);
        if (_loadedParameters == key) return;
        _loadedParameters = key; _shotFilter = RequestedShotId; _takeId = RequestedTakeId;
        await Reload();
    }
    private async Task Reload()
    {
        if (Locked) return;
        _busy = true; _error = null;
        try
        {
            _project = await Projects.GetAsync(Id, _token) ?? throw new WorkspaceStoreException("Project unavailable.");
            _languages = await Dubs.LanguagesAsync(Id, _token);
            _shots = await Shots.LoadAsync(Id, _token);
            _variants = await Dubs.LoadAsync(Id, _token);
            await Jobs.RefreshAsync(_token);
            if (RequestedJobId is { } jobId && Jobs.View.Jobs.SingleOrDefault(j => j.Id == jobId && j.Kind == AiJobKind.ShotTranslation && j.Target.ProjectId == Id) is { } requested)
            {
                var input = AiTextJobHandler.Read(requested, await JobStore.ReadSnapshotAsync(jobId, _token)).Payload<ShotDubRequest>();
                _takeId = input.SourceTakeId; _shotFilter = input.ShotId; _target = input.Target.Code; _translationJobId = jobId;
            }
            if (_takeId is null) _takeId = MasterTakes.FirstOrDefault()?.Id;
            if (string.IsNullOrEmpty(_target)) _target = _languages.Dubs.FirstOrDefault()?.Code ?? "";
            await LoadSelection();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or JsonException) { _error = e.Message; }
        finally { _busy = false; }
    }
    private async Task LoadSelection()
    {
        _request = null; _saved = null; _lines = []; _notes = []; _dirty = false; _proposalJobId = null;
        _suggestion = null; _suggestionRequest = null; _rawResult = null; _responseVersion = -1;
        if (_takeId is not { } take || _languages?.Main is null || !_languages.Dubs.Any(l => l.Code == _target)) return;
        var captured = await Dubs.CaptureAsync(Id, take, _target, _instructions, _token);
        _variants = await Dubs.LoadAsync(Id, _token);
        _request = captured;
        _saved = _variants.Variants.SingleOrDefault(v => v.Id == captured.VariantId);
        _saveVersion = _saved?.Version ?? 0;
        _lines = _saved?.Translation.Lines.ToArray() ?? _request.Dialogue.Select(l => new DubLine(l.Id, "")).ToArray();
        _notes = _saved?.Translation.Notes.ToArray() ?? [];
        if (_saved is not null && _saved.Request.LanguageFingerprint != _request.LanguageFingerprint)
        { _dirty = true; _notice = "Language settings changed. Review and save this translation against the current settings, or request a new suggestion."; }
        if (_translationJobId is { } jobId) await LoadResponse(jobId);
    }
    // The selected take could not be captured for translation; the centre explains why instead of a banner.
    private bool SelectionFailed => _error is not null && _request is null && _takeId is not null && Configured;
    private bool Configured => _languages?.Main is not null && _languages.Dubs.Count > 0;
    private string SaveStatus => _request is null ? "" : _dirty ? SaveStatusText.Unsaved : _saved is null ? "Not saved" : SaveStatusText.Saved;
    private long? SavedVersion(Guid takeId) => _variants.Variants.Where(v => v.Request.SourceTakeId == takeId && v.Request.Target.Code == _target).Select(v => (long?)v.Version).Max();
    private int RenderedCount(Guid takeId) => _shots.Takes.Count(t => t.Snapshot.Dub is { } d && d.SourceTakeId == takeId && d.Target.Code == _target);
    // A disabled action explains itself beside the button.
    private string? RenderBlocker =>
        _busy ? "Working…" : Pending is not null ? "Retry the missing queue acknowledgement first." :
        _dirty ? "Save the dialogue before rendering." : _saved is null ? "Save a language version before rendering." :
        ActiveVideo is not null ? "A language take is already rendering." : null;
    private string? SuggestBlocker =>
        _busy ? "Working…" : Pending is not null ? "Retry the missing queue acknowledgement first." :
        _dirty ? "Save or discard your edits before requesting a new suggestion." :
        ActiveTranslation is not null ? "A translation of this master take is already active." :
        _model?.Ready != true ? "Choose an available text model." : null;
    private async Task SelectTakeAsync(Guid takeId)
    {
        if (Locked || takeId == _takeId) return;
        _takeId = takeId; _translationJobId = null; await ChangeSelection();
    }
    private async Task SelectLanguage(ChangeEventArgs e)
    {
        if (Locked) return;
        _target = e.Value?.ToString() ?? ""; _translationJobId = null; await ChangeSelection();
    }
    private async Task ChangeSelection()
    {
        _busy = true; _error = null; _notice = null;
        try { await LoadSelection(); }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or AiGenerationException or JsonException or IOException) { _error = e.Message; }
        finally { _busy = false; }
    }
    private void EditLine(int index, string text)
    { if (_busy) return; _lines = _lines.Select((l, i) => i == index ? l with { Text = text } : l).ToArray(); _dirty = true; _proposalJobId = null; }
    private async Task Translate()
    {
        if (Locked || _model is not { Ready: true } model || _takeId is not { } take || ActiveTranslation is not null) return;
        _busy = true; _error = null;
        try
        {
            Pending = await TextRequests.TranslateShotAsync(Guid.NewGuid(), _tabId, Id, take, _target, _instructions, model.Model, model.FollowsDefault, _token);
            _translationJobId = Pending.Id; _responseVersion = -1; _suggestion = null; _rawResult = null;
            await EnqueuePending();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or AiGenerationException) { _error = e.Message; }
        finally { _busy = false; }
    }
    private async Task EnqueuePending()
    {
        if (Pending is not { } request) return;
        await Jobs.EnqueueAsync(request, _token); Pending = null;
        _notice = "Captured request queued. It remains available in AI activity after leaving this page.";
    }
    private async Task RetryEnqueue()
    {
        if (_busy || Pending is null) return; _busy = true;
        try { await EnqueuePending(); _error = null; }
        catch (WorkspaceStoreException e) { _error = e.Message; }
        finally { _busy = false; }
    }
    private async Task LoadResponse(Guid id)
    {
        var job = Jobs.View.Jobs.SingleOrDefault(j => j.Id == id && j.Kind == AiJobKind.ShotTranslation && j.Target.ProjectId == Id);
        if (job is null || job.Version == _responseVersion || job.State is AiJobState.Waiting or AiJobState.Running) return;
        var request = AiTextJobHandler.Read(job, await JobStore.ReadSnapshotAsync(id, _token)).Payload<ShotDubRequest>();
        if (request.SourceTakeId != _takeId || request.Target.Code != _target) return;
        var result = await JobStore.ReadArtifactAsync<AiTextJobResult>(id, AiJobArtifact.Result, _token);
        _responseVersion = job.Version; _rawResult = result;
        _suggestion = null; _suggestionRequest = null;
        if (job.State == AiJobState.Completed && !job.CancelRequested && result is { Complete: true, Error: null })
        { _suggestion = result.Read<ShotDubTranslation>(); _suggestionRequest = request; }
    }
    private void UseSuggestion()
    {
        if (_dirty || _busy || _suggestion is null || _suggestionRequest is null) return;
        _request = _suggestionRequest; _lines = _suggestion.Lines.ToArray(); _notes = _suggestion.Notes.ToArray();
        _saveVersion = _request.ExpectedVariantVersion; _proposalJobId = _translationJobId; _dirty = true;
        _notice = "Suggestion staged for review. Nothing has been changed in the master script or take.";
    }
    private Task SaveClick() => Save();
    private async Task<bool> Save()
    {
        if (_busy || _request is null || PreviewIssue is not null) return false;
        _busy = true;
        try {
            _saved = await Dubs.SaveAsync(_request, new(_lines, _notes), _saveVersion, _proposalJobId, ct: _token);
            _saveVersion = _saved.Version; _dirty = false; _error = null; _notice = "Language version saved. The master prompt and references are unchanged.";
            return true;
        }
        catch (WorkspaceConflictException) { _error = "This translation changed elsewhere. Your draft is retained; copy it before discarding and reloading the saved version."; return false; }
        catch (OperationCanceledException) when (_disposed) { return false; }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or AiGenerationException or JsonException or IOException) { _error = e.Message; return false; }
        finally { _busy = false; }
    }
    private async Task Discard() { if (_busy) return; _dirty = false; await ChangeSelection(); }
    private async Task RenderDub()
    {
        if (Locked || _saved is null || ActiveVideo is not null) return;
        _busy = true; _renderError = null;
        try {
            Pending = await VideoRequests.CaptureDubAsync(Guid.NewGuid(), _tabId, Id, _saved.Id, _saved.Version, _token);
            _renderJobId = Pending.Id; _renderVersion = -1; await EnqueuePending();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or AiGenerationException or IOException) { _renderError = e.Message; }
        finally { _busy = false; }
    }
    private async Task Cancel(Guid id) { try { await Jobs.CancelAsync(id, _token); } catch (WorkspaceStoreException e) { _error = e.Message; } }
    private void QueueChanged()
    {
        if (_disposed) return;
        _ = InvokeAsync(async () => {
            if (_disposed || _refreshing) return; _refreshing = true;
            try {
                if (_translationJobId is { } id) await LoadResponse(id);
                if (_renderJobId is { } render && Jobs.View.Jobs.FirstOrDefault(j => j.Id == render) is { } job &&
                    job.Version != _renderVersion && job.State is not (AiJobState.Waiting or AiJobState.Running))
                { _renderVersion = job.Version; _shots = await Shots.LoadAsync(Id, _token); }
                if (!_disposed) StateHasChanged();
            }
            catch (OperationCanceledException) when (_disposed) { }
            catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException or JsonException) { _error = e.Message; }
            finally { _refreshing = false; }
        });
    }
    private Task BeforeNavigation(LocationChangingContext context)
    {
        if (Locked) { context.PreventNavigation(); _error = "Save or discard the dialogue draft, and resolve any pending queue acknowledgement before leaving."; }
        return Task.CompletedTask;
    }
    private Task<bool> SaveForClose() => Pending is not null || _busy ? Task.FromResult(false) : _dirty ? Save() : Task.FromResult(true);
    public ValueTask DisposeAsync() { _disposed = true; Jobs.Changed -= QueueChanged; _lifetime.Cancel(); _lifetime.Dispose(); return ValueTask.CompletedTask; }
}
