using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Story;
using lumibelle.Services.AI;
using lumibelle.Components.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;

namespace lumibelle.Components.Pages;

public partial class SourceStudio
{
    [Parameter] public Guid Id { get; set; }
    [Parameter] public Guid? JobId { get; set; }
    private ProjectInfo? _project;
    private SourceDocument? _doc;
    private string _title = "", _text = "", _direction = "";
    private string? _error, _sourcePreview, _sourcePreviewTitle;
    private long _sourceRevision, _reviewRevision;
    private Guid? _runId, _outlineRun, _handledJob, _inspectedJob;
    private string? _response;
    private bool _busy, _outlineDirty, _editingOutline, _disposed;
    private SourceOutline? _outline;
    private readonly Dictionary<SourceOutlineScene, string> _sourceEdits = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SourceOutlineScene, string> _sourceErrors = new(ReferenceEqualityComparer.Instance);
    private StudioWorkspace? _workspace;
    private ElementReference _materialTab, _outlineTab;
    private string _center = "source";
    private int? _selectedAct, _selectedScene, _selectedSection;
    private string CenterSelection => _center switch {
        "source" => "source",
        "section" => $"{_runId}:section:{_selectedSection}",
        _ => $"{_runId}:outline:{_selectedAct}:{_selectedScene}"
    };
    private TextModelPicker? _picker;
    private TextModelSelectionState? _selection;
    private Guid? _pickerRun;
    private TextModelReference? _pickerModel;
    private readonly CancellationTokenSource _life = new();
    private Task? _poll;
    private SourceAnalysisRun? Run => _doc?.Runs.FirstOrDefault(r => r.Id == _runId);
    private bool Dirty => _doc is not null && (_title != _doc.Title || _text != _doc.Text || _direction != _doc.Direction);
    private bool Active => _doc?.Runs.Any(r => r.State is SourceRunState.Running or SourceRunState.Paused) == true;
    private bool CanReplan => !Active && Run is { State: SourceRunState.Review or SourceRunState.Accepted, Proposal: not null } run &&
        run.Notes.Count == run.Chunks.Count && run.Text == _text;
    protected override async Task OnInitializedAsync()
    {
        Jobs.Changed += QueueChanged;
        await Load();
        _poll = Poll();
    }
    private void QueueChanged()
    {
        if (_disposed) return;
        _ = InvokeAsync(() => {
            if (!_disposed && Run?.Pending is not null) StateHasChanged();
        });
    }
    protected override async Task OnParametersSetAsync()
    {
        if (JobId is not { } id || _handledJob == id || _outlineDirty) return;
        if (_doc?.Runs.FirstOrDefault(r => r.Jobs.Contains(id)) is not { } run) return;
        _handledJob = id; _runId = run.Id; _outline = null; _editingOutline = false; LoadOutline();
        await Inspect(id);
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_picker is null || Run is not { State: SourceRunState.Running or SourceRunState.Paused } run ||
            _pickerRun == run.Id && _pickerModel == run.Model) return;
        // Initialize from the captured run, not a project default that may have changed.
        // Polling must not overwrite a replacement the user is choosing while paused.
        _pickerRun = run.Id; _pickerModel = run.Model;
        await _picker.UseRequestModelAsync(run.Model);
        StateHasChanged();
    }
    private async Task Load()
    {
        try {
            _project = await Projects.GetAsync(Id, _life.Token);
            if (_project is null) throw new WorkspaceStoreException("Project not found.");
            _doc = await Sources.LoadAsync(Id, _life.Token);
            _title = _doc.Title; _text = _doc.Text; _direction = _doc.Direction; _sourceRevision = _doc.SourceRevision;
            _runId ??= _doc.Runs.LastOrDefault(r => JobId is not null && r.Jobs.Contains(JobId.Value))?.Id ?? _doc.Runs.LastOrDefault()?.Id;
            LoadOutline(); _error = null;
        } catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { _error = e.Message; }
    }
    private void LoadOutline()
    {
        if (_outlineDirty || Run?.Id == _outlineRun && Run?.ReviewRevision == _reviewRevision && _outline is not null) return;
        if (_outlineRun != Run?.Id) ResetCenter();
        _outlineRun = Run?.Id;
        var outline = Run?.Reviewed ?? Run?.Proposal;
        _outline = outline is null ? null : JsonSerializer.Deserialize<SourceOutline>(JsonSerializer.Serialize(outline, AtomicJsonFile.Options), AtomicJsonFile.Options);
        _sourceEdits.Clear(); _sourceErrors.Clear();
        if (_selectedAct >= _outline?.Acts.Count || _selectedAct is { } act && _selectedScene >= _outline?.Acts.ElementAtOrDefault(act)?.Scenes.Count)
            _selectedAct = _selectedScene = null;
        _reviewRevision = Run?.ReviewRevision ?? 0;
    }
    private async Task Poll()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try {
            while (await timer.WaitForNextTickAsync(_life.Token)) {
                var doc = await Sources.LoadAsync(Id, _life.Token);
                await InvokeAsync(() => {
                    if (_disposed || _busy) return;
                    // Keep local edits and their original concurrency revision when another tab saves.
                    var changedElsewhere = _doc?.SourceRevision != doc.SourceRevision;
                    _doc = doc;
                    if (changedElsewhere) _error = "Source changed in another window. Your local text is preserved; reload the page to review the saved version.";
                    _runId ??= doc.Runs.LastOrDefault()?.Id;
                    LoadOutline();
                    StateHasChanged();
                });
            }
        } catch (OperationCanceledException) { }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { if (!_disposed) await InvokeAsync(() => { _error = e.Message; StateHasChanged(); }); }
    }
    private async Task Execute(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; _error = null;
        try { await action(); }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or AiGenerationException or IOException or DecoderFallbackException) { _error = e.Message; }
        finally { _busy = false; }
    }
    private async Task SaveCore()
    {
        if (_doc is null || !Dirty) return;
        _doc = await Sources.SaveAsync(Id, _sourceRevision, _title, _text, _direction, _life.Token);
        _sourceRevision = _doc.SourceRevision;
    }
    private Task Save() => Execute(SaveCore);
    private Task Start() => Execute(async () => {
        if (_picker is null || !await _picker.PrepareSubmitAsync() || _selection is not { Ready: true } selection) return;
        await SaveCore();
        if (_doc is null) return;
        _doc = await Workflow.StartAsync(Id, _sourceRevision, selection.Model, await Reviews.TabIdAsync(), _life.Token);
        _runId = _doc.Runs.Last().Id; _outlineDirty = false; _editingOutline = false; _outline = null; LoadOutline();
    });
    private Task Replan() => Execute(async () => {
        if (!CanReplan || Run is not { } previous || _picker is null ||
            !await _picker.PrepareSubmitAsync() || _selection is not { Ready: true } selection) return;
        await SaveCore();
        _doc = await Workflow.ReplanAsync(Id, _sourceRevision, previous.Id, selection.Model, await Reviews.TabIdAsync(), _life.Token);
        _runId = _doc.Runs.Last().Id; _outlineDirty = false; _editingOutline = false; _outline = null; LoadOutline();
    });
    private void ViewAnalysisOrigin()
    {
        if (Run?.AnalysisFromRunId is not { } id) return;
        SelectRun(new ChangeEventArgs { Value = id.ToString() });
    }
    private Task Pause() => Execute(async () => { if (Run is { } r) _doc = await Workflow.PauseAsync(Id, r.Id, _life.Token); });
    private Task Resume() => Execute(async () => {
        if (Run is not { } r || _picker is null || !await _picker.PrepareSubmitAsync() || _selection is not { Ready: true } selection) return;
        _doc = await Workflow.ResumeAsync(Id, r.Id, selection.Model, _life.Token);
    });
    private Task Discard() => Execute(async () => {
        if (Run is { } r) { _doc = await Workflow.DiscardAsync(Id, r.Id, _life.Token); _outlineDirty = false; _editingOutline = false; _outline = null; _outlineRun = null; LoadOutline(); }
    });
    private Task SaveOutline(bool accept) => Execute(async () => {
        if (_doc is null || Run is null || _outline is null) return;
        if (_sourceErrors.Count > 0) throw new WorkspaceStoreException("Correct the source section links before saving this outline.");
        _doc = await Sources.ReviewAsync(Id, _reviewRevision, Run.Id, _outline, accept, _life.Token);
        _reviewRevision = Run!.ReviewRevision; _outlineDirty = false; _editingOutline = false;
        _sourceEdits.Clear();
    });
    private string SourceInput(SourceOutlineScene scene) => _sourceEdits.GetValueOrDefault(scene) ?? string.Join(", ", scene.Sources);
    private void EditSourceSections(SourceOutlineScene scene, string text)
    {
        _sourceEdits[scene] = text; _outlineDirty = true;
        try {
            scene.Sources = SourceAnalysis.ParseSourceSections(text, Run!.Chunks.Count);
            _sourceErrors.Remove(scene);
        } catch (WorkspaceStoreException e) { _sourceErrors[scene] = e.Message; }
    }
    private void OutlineChanged() => _outlineDirty = true;
    private void SetOmission(int number, bool omitted)
    {
        if (_outline is null || Run?.State is not (SourceRunState.Review or SourceRunState.Accepted)) return;
        _outline.ReviewedOmissions = omitted ? _outline.ReviewedOmissions.Append(number).Distinct().Order().ToArray()
            : _outline.ReviewedOmissions.Where(n => n != number).ToArray();
        _outlineDirty = true;
    }
    private void Move<T>(List<T> values, int index, int delta) { (values[index], values[index + delta]) = (values[index + delta], values[index]); _outlineDirty = true; }
    private void MoveAct(int index, int delta)
    {
        Move(_outline!.Acts, index, delta);
        if (_selectedAct == index) _selectedAct += delta;
    }
    private void MoveScene(int act, int index, int delta)
    {
        Move(_outline!.Acts[act].Scenes, index, delta);
        if (_selectedScene == index) _selectedScene += delta;
    }
    private Task ShowMaterial()
    {
        _center = "source";
        return _workspace?.RevealCenterAsync() ?? Task.CompletedTask;
    }
    private void SelectNavigationMode(bool outline)
    {
        if (outline == (_center == "outline")) return;
        _center = outline ? "outline" : "source";
        if (outline) {
            _selectedAct = _selectedScene = null;
            _editingOutline = Run?.State != SourceRunState.Discarded && _outlineDirty;
        }
    }
    private async Task NavigationModeKey(KeyboardEventArgs e, bool outline)
    {
        var next = e.Key switch { "ArrowLeft" or "ArrowRight" => !outline, "Home" => false, "End" => true, _ => outline };
        if (next == outline) return;
        SelectNavigationMode(next);
        await (next ? _outlineTab : _materialTab).FocusAsync();
    }
    private Task ShowOutline(int? act = null, int? scene = null)
    {
        _center = "outline"; _selectedAct = act; _selectedScene = scene;
        _editingOutline = Run?.State != SourceRunState.Discarded && (act is not null || _outlineDirty);
        return _workspace?.RevealCenterAsync() ?? Task.CompletedTask;
    }
    private Task ShowSection(int number)
    {
        _center = "section"; _selectedSection = number;
        return _workspace?.RevealCenterAsync() ?? Task.CompletedTask;
    }
    private Task ShowAssistant() => _workspace?.ShowToolsAsync() ?? Task.CompletedTask;
    private void SelectRun(ChangeEventArgs e)
    {
        _runId = Guid.TryParse(e.Value?.ToString(), out var id) ? id : null;
        _inspectedJob = null; _response = null; _editingOutline = false; _outline = null;
        ResetCenter(); LoadOutline();
    }
    private void ResetCenter() { _center = "source"; _selectedAct = _selectedScene = _selectedSection = null; }
    private Task InspectLatest() => _inspectedJob is null && Run?.Jobs.LastOrDefault() is { } id && id != Guid.Empty ? Inspect(id) : Task.CompletedTask;
    private Task InspectSelected(ChangeEventArgs e) => Guid.TryParse(e.Value?.ToString(), out var id) ? Inspect(id) : Task.CompletedTask;
    private async Task Inspect(Guid id)
    {
        _inspectedJob = id; _response = null;
        try {
            var result = await JobStore.ReadArtifactAsync<AiTextJobResult>(id, AiJobArtifact.Result, _life.Token);
            if (_inspectedJob == id) _response = result?.Raw;
        } catch (WorkspaceStoreException e) { _error = e.Message; }
    }
    private void ViewSource(int number)
    {
        if (Run is not { } run || run.Chunks.FirstOrDefault(c => c.Number == number) is not { } chunk) return;
        _sourcePreviewTitle = $"Section {number} · {chunk.Heading}";
        _sourcePreview = run.Text.Substring(chunk.Start, chunk.Length);
    }
    private Task Import(InputFileChangeEventArgs e) => Execute(async () => {
        await using var stream = e.File.OpenReadStream(2_000_000, _life.Token);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        var text = await reader.ReadToEndAsync(_life.Token);
        if (text.Length > SourceAnalysis.MaximumText) throw new WorkspaceStoreException("Source text can contain up to 500,000 characters.");
        _text = text;
        if (string.IsNullOrWhiteSpace(_title)) _title = Path.GetFileNameWithoutExtension(e.File.Name);
    });
    private async Task BeforeNavigation(LocationChangingContext context)
    {
        if (!Dirty && !_outlineDirty) return;
        if (_busy) { context.PreventNavigation(); return; }
        if (Dirty) await Save();
        if (_outlineDirty) await SaveOutline(false);
        if (_error is not null || Dirty || _outlineDirty) context.PreventNavigation();
    }
    private static string Status(SourceAnalysisRun r) => SourceAnalysisProgressView.Status(r);
    public async ValueTask DisposeAsync()
    {
        _disposed = true; Jobs.Changed -= QueueChanged; await _life.CancelAsync();
        if (_poll is not null) await _poll;
        _life.Dispose();
    }
}
