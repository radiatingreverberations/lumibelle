using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Story;
using lumibelle.Components.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace lumibelle.Components.Script;

public partial class ScriptWorkspace
{
    [Parameter, EditorRequired] public ProjectInfo Project { get; set; } = null!;
    [Parameter] public Guid? RequestedJobId { get; set; }
    private ScriptEditingSession? _session;
    private ScriptCanvas? _canvas;
    private StudioWorkspace? _workspace;
    private string? _assistantStatus;
    private static readonly MudBlazor.DialogOptions RecoveryOptions = new() { MaxWidth = MudBlazor.MaxWidth.ExtraLarge, FullWidth = true, CloseOnEscapeKey = true };
    private IJSObjectReference? _writingModule, _outlineModule;
    private DotNetObjectReference<ScriptWorkspace>? _reference;
    private ScriptSelection? _selection;
    private ScriptAssistantTarget? _requestedTarget;
    private readonly HashSet<Guid> _foldedActs = [];
    private bool _hasHighlights;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private long _browserVersion = -1;
    private bool Unsaved => _session!.Dirty || _browserVersion > _editorVersion;
    private Guid? _outlineSelected, _deleteSection, _movingSection, _revealOutline;
    private string _moveDestination = "", _movePosition = "after";
    private List<ScriptBlock> _initialBlocks = [];
    private IReadOnlyList<ScriptRecovery> _recoveries = [];
    private long _editorVersion = -1, _editorSequence = -1;
    private bool _syncing, _saveRequested, _historyOpen, _confirmReload, _disposed;
    private string? _error;
    private bool _bold, _italic;
    private ScriptDocument Document => _session!.Document;
    private IReadOnlyList<ScriptSection> Sections => ScriptStructure.Sections(Document.Blocks);
    private int WordCount => string.Join(" ", Document.Blocks.Select(b => b.Text)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    private string CurrentKind => Document.Blocks.FirstOrDefault(b => b.Id == _selection?.AnchorBlockId)?.Kind.ToString() ?? "Action";
    private string SaveStatus => _session!.Conflict ? "Save conflict" : _session.Error is not null ? "Couldn’t save" : _session.Saving || _saveRequested ? "Saving…" : Unsaved ? "Unsaved" : "Saved";
    protected override async Task OnInitializedAsync() { DraftWorkflow.Changed += BatchScriptChanged; await LoadAsync(); }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_session is not null) {
            await Remember(Project.Id, "script", "outline", _outlineSelected);
            await Remember(Project.Id, "script", "folds", _foldedActs.ToArray());
            await Remember(Project.Id, "script", "selection", _selection);
        }
        if (firstRender)
        {
            _writingModule = await JS.InvokeAsync<IJSObjectReference>("import", lumibelle.UiAssets.Module("writing.js"));
            _reference = DotNetObjectReference.Create(this);
            await _writingModule.InvokeVoidAsync("registerSave", _reference);
            _outlineModule = await JS.InvokeAsync<IJSObjectReference>("import", lumibelle.UiAssets.Module("script-outline.js"));
            await _outlineModule.InvokeVoidAsync("mount", _reference);
        }
        if (_revealOutline is { } id && _outlineModule is not null) { _revealOutline = null; await _outlineModule.InvokeVoidAsync("reveal", id); }
    }
    [JSInvokable] public Task SaveFromKeyboard() => SaveAsync();
    // The script could not be opened; the studio is replaced by LoadFailure.
    private Exception? _loadFailure;
    private async Task LoadAsync()
    {
        _error = null; _loadFailure = null;
        try
        {
            var session = new ScriptEditingSession(Scripts, Clock);
            await session.LoadAsync(Project.Id);
            _session?.Dispose(); _session = session; _session.Changed += SessionChanged;
            _initialBlocks = Document.Blocks.Select(b => b.Copy()).ToList();
            _outlineSelected = Place<Guid?>(Project.Id, "script", "outline");
            if (!Document.Blocks.Any(b => b.Id == _outlineSelected)) _outlineSelected = null;
            _foldedActs.Clear(); _foldedActs.UnionWith(Place<Guid[]>(Project.Id, "script", "folds", []).Where(id => Document.Blocks.Any(b => b.Id == id && b.Kind == ScriptBlockKind.Act)));
            _selection = Place<ScriptSelection?>(Project.Id, "script", "selection");

        }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { _loadFailure = e; }
    }
    private void SessionChanged() { if (!_disposed) _ = InvokeAsync(async () => { await AcknowledgeAsync(); StateHasChanged(); }); }
    private async Task AcknowledgeAsync()
    {
        if (_session is { Dirty: false } && _canvas is not null)
            try { await _canvas.AcknowledgeAsync(_editorVersion); } catch (JSDisconnectedException) { }
    }
    private void BrowserDirty(long version) => _browserVersion = Math.Max(_browserVersion, version);
    public async Task EditorChanged(ScriptEditorState state) { if (!_syncing) { AcceptState(state); await AcknowledgeAsync(); } }
    private void AcceptState(ScriptEditorState state)
    {
        if (state.Version < _editorVersion || state.Sequence < _editorSequence) return;
        try
        {
            ScriptStructure.ValidateBlocks(state.Blocks);
            var edited = state.Version > _editorVersion;
            _editorVersion = state.Version; _editorSequence = state.Sequence; _selection = state.Selection; _bold = state.Bold; _italic = state.Italic;
            if (ScriptStructure.Fingerprint(state.Blocks) != ScriptStructure.Fingerprint(Document.Blocks))
            {
                // The editor merges adjacent equally styled spans when importing a proposal.
                // A selection-only report of that normalization must keep the applied highlights.
                if (edited) _hasHighlights = false;
                _session!.Edit(Document with { Blocks = state.Blocks });
            }
        }
        catch (WorkspaceStoreException e) { _error = e.Message; }
    }
    private async Task ReadEditorAsync() { if (_canvas is not null && await _canvas.ReadAsync() is { } state) AcceptState(state); }
    private async Task<bool> SaveForCloseAsync() { await SaveAsync(); return _session is not null && !_session.Dirty && _session.Error is null && _error is null; }
    private async Task SaveAsync()
    {
        if (_session is null) return;
        await _operations.WaitAsync(); _saveRequested = true;
        try { await ReadEditorAsync(); if (await _session.SaveAsync()) _error = null; await AcknowledgeAsync(); }
        catch (JSDisconnectedException) { _error = "Reconnect to the app, then save again. The editor still holds your draft."; }
        finally { _saveRequested = false; _operations.Release(); }
    }
    private async Task<ScriptAssistantTarget> SelectTargetAsync(ScriptScope scope)
    {
        await _operations.WaitAsync();
        try { await ReadEditorAsync(); return ScriptAssistantTarget.From(Document, scope, _selection); }
        finally { _operations.Release(); }
    }
    private async Task<ScriptRequestContext> CaptureAsync(ScriptAssistantTarget selection)
    {
        await _operations.WaitAsync(); _syncing = true;
        try
        {
            if (_canvas is not null) await _canvas.SetEditableAsync(false);
            await ReadEditorAsync();
            if (!await _session!.SaveAsync()) throw new WorkspaceStoreException("Save the script successfully before starting a request.");
            var captured = Document.Copy(); var target = selection.Capture(captured);
            await AcknowledgeAsync(); return new(captured, target);
        }
        finally { try { if (_canvas is not null) await _canvas.SetEditableAsync(true); } finally { _syncing = false; _operations.Release(); if (!_disposed) await InvokeAsync(StateHasChanged); } }
    }
    private async Task<bool> ExternalAsync(Func<Task<bool>> action)
    {
        await _operations.WaitAsync();
        _syncing = true; _error = null;
        try
        {
            if (_canvas is not null) await _canvas.SetEditableAsync(false);
            await ReadEditorAsync();
            var before = ScriptStructure.Fingerprint(Document.Blocks);
            if (!await action()) return false;
            if (_canvas is not null && before != ScriptStructure.Fingerprint(Document.Blocks))
            {
                await _canvas.ReplaceAsync(Document.Blocks);
                if (await _canvas.ReadAsync() is { } state) { _editorVersion = state.Version; _editorSequence = state.Sequence; _selection = state.Selection; }
            }
            return true;
        }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or JSException) { _error = e.Message; return false; }
        finally { try { if (_canvas is not null) await _canvas.SetEditableAsync(true); await AcknowledgeAsync(); } finally { _syncing = false; _operations.Release(); if (!_disposed) await InvokeAsync(StateHasChanged); } }
    }
    private async Task<bool> ApplyAsync(AssistantRun run)
    {
        ScriptProposalApplication? applied = null;
        var saved = await ExternalAsync(async () =>
        {
            var plan = run.SourceScene is null ? null : ScriptAdaptation.Accepted(await Sources.LoadAsync(Project.Id))
                ?? throw new WorkspaceStoreException("Accept a current source outline before applying this sequence.");
            return await _session!.MutateAsync(doc => (applied = ScriptProposals.ApplyWithChanges(doc, run, plan)).Document,
                ScriptAdaptation.IsRedraft(run) ? $"Before replacing sequence {run.SourceScene!.Act + 1}.{run.SourceScene.Scene + 1}" : "Before applying an AI proposal");
        });
        if (saved && applied is not null && _canvas is not null)
        {
            await _canvas.HighlightAsync(applied.Changes); _hasHighlights = applied.Changes.Count > 0; StateHasChanged();
        }
        return saved;
    }
    private Task<bool> AddOutlineActsAsync(SourceAnalysisRun expected) => ExternalAsync(async () =>
    {
        var plan = ScriptAdaptation.Accepted(await Sources.LoadAsync(Project.Id));
        if (plan?.Id != expected.Id || plan.ReviewRevision != expected.ReviewRevision)
            throw new WorkspaceStoreException("The accepted outline changed. Refresh it before adding its act headings.");
        return await _session!.MutateAsync(doc => ScriptAdaptation.WithActHeadings(doc, plan), "Before adding source outline acts");
    });
    private async Task HideHighlightsAsync()
    {
        _hasHighlights = false;
        if (_canvas is not null) try { await _canvas.HighlightAsync([]); } catch (JSDisconnectedException) { }
    }
    private async Task HistoryAsync()
    {
        try { _recoveries = await Scripts.ListRecoveryAsync(Project.Id); _historyOpen = true; }
        catch (WorkspaceStoreException e) { _error = e.Message; }
    }
    private async Task RestoreAsync(Guid id) { if (await ExternalAsync(() => _session!.RestoreAsync(id))) _historyOpen = false; }
    private async Task ReloadAsync()
    {
        await ExternalAsync(async () => { await _session!.LoadAsync(Project.Id); _confirmReload = false; _batchSyncNotice = null; return true; });
    }
    private async Task DeleteConfirmedAsync(Guid id) => await ExternalAsync(() => _session!.MutateAsync(doc =>
    {
        var section = ScriptStructure.Sections(doc.Blocks).Single(s => s.Id == id);
        var blocks = doc.Blocks.ToList(); blocks.RemoveRange(section.Start, section.Count);
        return doc with { Blocks = blocks };
    }, "Before deleting a script section"));
    private bool ActiveSection(ScriptSection section) => Document.Blocks.Skip(section.Start).Take(section.Count).Any(b => b.Id == _selection?.AnchorBlockId);
    private List<ScriptSection> Siblings(ScriptSection section) => Sections.Where(s => s.Kind == section.Kind && (s.Kind == ScriptBlockKind.Act || s.ActId == section.ActId)).ToList();
    private bool CanMove(ScriptSection section, int offset) { var list = Siblings(section); var i = list.FindIndex(s => s.Id == section.Id) + offset; return i >= 0 && i < list.Count; }
    private async Task MoveAsync(Guid id, int offset) => await ExternalAsync(() => _session!.MutateAsync(doc =>
    {
        var section = ScriptStructure.Sections(doc.Blocks).Single(s => s.Id == id);
        var siblings = ScriptStructure.Sections(doc.Blocks).Where(s => s.Kind == section.Kind && (s.Kind == ScriptBlockKind.Act || s.ActId == section.ActId)).ToList();
        var next = siblings.FindIndex(s => s.Id == id) + offset;
        if (next < 0 || next >= siblings.Count) return doc;
        var other = siblings[next]; var blocks = doc.Blocks.ToList(); var moved = blocks.GetRange(section.Start, section.Count);
        blocks.RemoveRange(section.Start, section.Count);
        blocks.InsertRange(offset < 0 ? other.Start : other.Start + other.Count - section.Count, moved);
        return doc with { Blocks = blocks };
    }));
    private async Task JumpAsync(Guid id)
    {
        _outlineSelected = id;
        var section = Sections.FirstOrDefault(s => s.Id == id);
        if (section is not null) _requestedTarget = new(section.Kind == ScriptBlockKind.Act ? ScriptScope.Act : ScriptScope.Scene, id);
        if (_workspace is not null) await _workspace.RevealCenterAsync();
        if (_canvas is not null) await _canvas.JumpAsync(id);
    }
    private void ToggleAct(Guid id) { if (!_foldedActs.Add(id)) _foldedActs.Remove(id); }
    private void FoldAll(bool fold) { _foldedActs.Clear(); if (fold) _foldedActs.UnionWith(Sections.Where(s => s.Kind == ScriptBlockKind.Act).Select(s => s.Id)); }
    private Task AddSceneAsync() => InsertAsync(ScriptBlockKind.Scene, _outlineSelected, "default");
    private Task AddActAsync() => InsertAsync(ScriptBlockKind.Act, _outlineSelected, "default");
    private async Task InsertAsync(ScriptBlockKind kind, Guid? anchor, string position)
    {
        Guid added = Guid.Empty;
        if (await ExternalAsync(() => _session!.MutateAsync(doc => ScriptOutline.Insert(doc, kind, anchor, position, out added))))
        { await RevealSectionAsync(added); if (_canvas is not null) await _canvas.RenameAsync(added); }
    }
    private async Task RevealSectionAsync(Guid id)
    {
        if (Sections.FirstOrDefault(s => s.Id == id)?.ActId is { } act) _foldedActs.Remove(act);
        _revealOutline = id; StateHasChanged(); await JumpAsync(id);
    }
    private async Task RenameSectionAsync(Guid id) { await RevealSectionAsync(id); if (_canvas is not null) await _canvas.RenameAsync(id); }
    private async Task DeleteAsync(Guid id)
    {
        var section = Sections.Single(s => s.Id == id);
        if (section.Kind == ScriptBlockKind.Act && section.Count > 1) { _deleteSection = id; return; }
        await DeleteConfirmedAsync(id);
    }
    private async Task ConfirmDeleteAsync() { if (_deleteSection is { } id) { await DeleteConfirmedAsync(id); _deleteSection = null; } }
    private void OpenMove(Guid id) { _movingSection = id; _moveDestination = ""; _movePosition = "after"; }
    private async Task MoveToAsync()
    {
        if (_movingSection is not { } id) return;
        Guid? anchor = Guid.TryParse(_moveDestination, out var target) ? target : null;
        if (await ExternalAsync(() => _session!.MutateAsync(doc => ScriptOutline.Move(doc, id, anchor, _movePosition)))) { _movingSection = null; await RevealSectionAsync(id); }
    }
    [JSInvokable] public async Task MoveOutline(Guid id, Guid target, string position)
    {
        if (await ExternalAsync(() => _session!.MutateAsync(doc => ScriptOutline.Move(doc, id, target, position)))) await RevealSectionAsync(id);
        StateHasChanged();
    }
    [JSInvokable] public void ExpandOutline(Guid id) { _foldedActs.Remove(id); StateHasChanged(); }

    private Task BoldAsync() => CanvasCommand("bold");
    private Task ItalicAsync() => CanvasCommand("italic");
    private Task UndoAsync() => CanvasCommand("undo");
    private Task RedoAsync() => CanvasCommand("redo");
    private Task KindChanged(ChangeEventArgs e) => CanvasCommand("kind", e.Value?.ToString());
    private async Task CanvasCommand(string name, string? value = null) { await _operations.WaitAsync(); try { if (_canvas is not null) await _canvas.CommandAsync(name, value); } finally { _operations.Release(); } }
    private async Task SplitAsync() { await _operations.WaitAsync(); try { if (!_syncing && _canvas is not null) await _canvas.SplitAsync(); } finally { _operations.Release(); } }
    private async Task ExportAsync() { await _operations.WaitAsync(); try { await ReadEditorAsync(); await DownloadContentAsync("script.md", ScriptStructure.Markdown(Document.Blocks)); } finally { _operations.Release(); } }
    private async Task DownloadAsync() { await ReadEditorAsync(); await DownloadContentAsync("unsaved-script.json", JsonSerializer.Serialize(Document, AtomicJsonFile.Options)); }
    private async Task DownloadContentAsync(string name, string text)
    { if (_writingModule is not null) await _writingModule.InvokeVoidAsync("downloadText", name, text); }
    private async Task BeforeNavigationAsync(LocationChangingContext context)
    {
        await SaveAsync();
        if (_session!.Dirty || _session.Error is not null) context.PreventNavigation();
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true; DraftWorkflow.Changed -= BatchScriptChanged;
        if (_session is not null) { await _session.SaveAsync(); _session.Dispose(); }
        try
        {
            if (_writingModule is not null) { await _writingModule.InvokeVoidAsync("syncDrawer", (string?)null); await _writingModule.InvokeVoidAsync("unregisterSave"); await _writingModule.DisposeAsync(); }
        }
        catch (JSDisconnectedException) { }
        if (_outlineModule is not null) { try { await _outlineModule.InvokeVoidAsync("dispose"); await _outlineModule.DisposeAsync(); } catch (JSDisconnectedException) { } }
        _reference?.Dispose();
    }
}
