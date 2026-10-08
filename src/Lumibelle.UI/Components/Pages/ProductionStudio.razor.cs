using System.Globalization;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;
namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    [Inject] public AiJobCoordinator AiJobs { get; set; } = null!;
    [Inject] public IAiJobStore AiJobStore { get; set; } = null!;
    [Inject] public AiTextJobCapture TextRequests { get; set; } = null!;
    [Inject] public IAiReviewGate AiReviews { get; set; } = null!;
    [Inject] public IGenerationSetupStore GenerationSetups { get; set; } = null!;
    [Inject] public ISnackbar Snackbar { get; set; } = null!;
    // Confirmations use the shared snackbar, as in the other studios, rather than a
    // plain line above the workspace that is easy to miss.
    private void Notify(string message) => Snackbar.Add(message, Severity.Success);
    private void Warn(string message) => Snackbar.Add(message, Severity.Warning);
    [Parameter] public Guid Id { get; set; }
    [Parameter] public Guid? RequestedJobId { get; set; }
    [Parameter] public Guid? RequestedShotId { get; set; }
    [Parameter] public Guid? RequestedCompositionId { get; set; }
    [Parameter] public string? RequestedView { get; set; }
    private string? _handledView;
    private bool _openShotView;
    private lumibelle.Components.Layout.StudioWorkspace? _workspace;
    private ProjectInfo? _project;
    private ShotDocument _doc = new();
    private AssetLibrary _assets = new() { ProjectId = Guid.Empty };
    private ProductionDocument _production = new();
    private ProductionComposition? _savedComposition;
    private Guid? _selected, _compositionId, _handledCompositionJob, _handledShot, _handledComposition;
    private ProductionComposition? Current => _production.Compositions.FirstOrDefault(c => c.Id == _compositionId);
    private Shot? Selected => Current?.Shot;
    private Shot? SourceShot => _doc.Shots.FirstOrDefault(s => s.Id == _selected);
    private IEnumerable<(Shot Shot, ProductionComposition Composition)> ReferenceCopySources =>
        _doc.Shots.Where(s => s.Id != _selected).Select(s => (Shot: s, Composition: _production.Compositions.FirstOrDefault(c => c.ShotId == s.Id && !c.Archived)))
            .Where(s => s.Composition is not null).Select(s => (s.Shot, s.Composition!));
    private (Shot Shot, ProductionComposition Composition)? PreviousReferenceSource
    {
        get
        {
            if (_selected is not { } selected) return null;
            var previousIndex = _doc.Shots.FindIndex(x => x.Id == selected) - 1;
            if (previousIndex < 0) return null;
            var source = ReferenceCopySources.FirstOrDefault(s => _doc.Shots.IndexOf(s.Shot) == previousIndex);
            return source.Shot is null ? null : source;
        }
    }
    private readonly Stack<(ProductionComposition? Setup, ShotEditState Coverage)> _undo = [];
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CancellationTokenSource? _saveDelay;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed, _dirty, _saving, _starting, _mediaBusy, _interactive, _showArchived;
    // Reload and download only help after a failed save that retained the draft.
    private bool _saveFailed;
    // The shot documents could not be opened; the workspace is replaced by LoadFailure.
    private Exception? _loadFailure;
    private long _version;
    private string _saveStatus = "Saved", _filter = "";
    private string? _error, _notice, _reviewError;
    private H3Configuration? _configuration;
    private IReadOnlyList<VideoRun> _runs = [];
    private Guid? _reviewTakeId, _undoTrash;
    private VideoRun? _reviewRun;
    private bool _referenceOpen, _reviewOpen, _focusReview, _restoreReviewFocus, _restoreFrameFocus;
    private (Guid TakeId, int Index)? _frameDestination;
    private SavedAssetImage? _savedFrameImage;
    private readonly HashSet<Guid> _missing = [], _autoReviewed = [];
    private ShotTake? ReviewTake => _doc.Takes.FirstOrDefault(t => t.Id == _reviewTakeId);
    private DialogOptions LargeDialog => new() { MaxWidth = MaxWidth.ExtraLarge, FullWidth = true, CloseOnEscapeKey = true, BackdropClick = false };
    private string SettingsUrl => AiSettingsNavigation.Link(Navigation, "video", Id, "shots");
    private string EffectiveAspect(Shot s) => _project is null ? s.Aspect : ShotVideoDefaults.Aspect(s, _project);
    private string LatentsSize(Shot s)
    {
        var size = VideoResolutions.Size(EffectiveAspect(s), VideoResolutions.Selected(s));
        var frames = H3Policy.Frames(s.Duration is >= 1 and <= 15 and var seconds ? seconds : 5);
        return (RefinementPackages.EstimatedBytes(frames, size.Width, size.Height) / 1e6).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB";
    }
    private string PromptPreview => Current?.Prompt ?? "";
    private ShotReferenceGuidance Guidance(ShotImageBinding b) => ShotReferences.Resolve(b, _assets, _doc);
    private bool SourceChanged => Current is { } c && SourceShot is { } source && ProductionPolicy.SourceFingerprint(source) != c.SourceFingerprint;
    private string? CompositionIssue => Current is { } c && _project is not null ? ProductionPolicy.Issue(c, _assets, _doc, _project) : "Choose a composition.";
    private string? GenerationIssue => ActiveGlobalSetup?.Archived == true ? "Restore this global setup or choose another before generating." : CompositionIssue ?? LoraIssue ?? (_configuration is null ? null : Selected is { } s && !_configuration.Ready(s) ? _configuration.Issue(s) : null);
    private IEnumerable<List<Shot>> VisibleGroups => _doc.Shots.Where(MatchesPromptStatus).Where(s => s.Title.Contains(_filter, StringComparison.OrdinalIgnoreCase) || SceneLabel(s).Contains(_filter, StringComparison.OrdinalIgnoreCase)).GroupBy(s => s.SceneId).Select(g => g.ToList());
    private bool TakeMatches(ShotTake take, Shot shot) => Current is { } c && take.Snapshot.Production is { } p && p.CompositionId == c.Id && p.Revision.Id == c.AcceptedRevisionId && CompositionIssue is null;
    protected override void OnInitialized() { AiJobs.Changed += JobChanged; AiJobs.Changed += PlanningQueueChanged; }
    protected override async Task OnParametersSetAsync()
    {
        if (_doc.ProjectId != Id) {
            _handledView = null; _promptStatusFilter = "all";
            _takeScriptSources.Clear(); _takeCurrentScript = null;
            _selected = Place<Guid?>(Id, "shots", "shot");
            // Consult the old selection only when importing into an empty global library.
            var earlierSelections = Place<Dictionary<Guid, Guid>>(Id, "shots", "setups", []);
            _compositionId = _selected is { } remembered ? earlierSelections.GetValueOrDefault(remembered) : null;
            _filter = Place(Id, "shots", "search", ""); LoadCollapsedScenes(); LoadResolvedShotDrafts();
            _takeSetupFilter = Place(Id, "shots", "takeFilter", "");
            _takeInputFilter = Place(Id, "shots", "takeInputFilter", "all");
            _showArchived = Place(Id, "shots", "archived", false);
            if (RequestedShotId is not null || RequestedCompositionId is not null || RequestedJobId is not null) { _filter = ""; _takeSetupFilter = ""; }
            await ResetVideoProjectAsync();
            _project = await Projects.GetAsync(Id, _lifetime.Token);
            await ResetPlanningProjectAsync(); await Reload(); await AiJobs.RefreshAsync(_lifetime.Token); if (_loadFailure is null) await RefreshMedia(); await RefreshPlanningJobsAsync(); _planningInitialized = true;
        }
        if (RequestedShotId is null) _handledShot = null;
        if (RequestedCompositionId is null) _handledComposition = null;
        if (RequestedShotId is { } shot && _handledShot != shot && _doc.Shots.Any(s => s.Id == shot)) { _handledShot = shot; _promptStatusFilter = "all"; _filter = ""; _takeSetupFilter = ""; await Select(shot); }
        if (RequestedCompositionId is { } composition && _handledComposition != composition && _production.Compositions.FirstOrDefault(c => c.Id == composition) is { } requested) { _handledComposition = composition; _promptStatusFilter = "all"; _filter = ""; _takeSetupFilter = ""; if (requested.Archived) _showArchived = true; await SelectComposition(composition); }
    }
    private async Task RetryLoadAsync() { await Reload(); if (_loadFailure is null) await RefreshMedia(); }
    private async Task Reload()
    {
        try {
            _loadFailure = null; _saveDelay?.Cancel();
            try {
                _doc = await Store.LoadAsync(Id, _lifetime.Token); _assets = await AssetStore.LoadAsync(Id, _lifetime.Token);
                _production = await Production.InitializeAsync(Id, _lifetime.Token);
                _doc = await Store.LoadAsync(Id, _lifetime.Token); _approved = await Scripts.CaptureSourceAsync(Id, cancellationToken: _lifetime.Token);
            }
            catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { _loadFailure = e; return; }
            Baseline(_doc); _coverageDirty = false;
            if (!_doc.Shots.Any(s => s.Id == _selected)) _selected = _doc.Shots.FirstOrDefault()?.Id;
            if (Current is null || Current.ShotId != _selected || Current.Archived && !_showArchived) _compositionId = _production.Compositions.FirstOrDefault(c => c.ShotId == _selected && !c.Archived)?.Id;
            await ApplySelectedGlobalSetup();
            ValidateTakeFilter();
            _savedComposition = Current?.Copy(); _dirty = false; _saveStatus = "Saved"; _error = null; _saveFailed = false; _undo.Clear();
            await RefreshCompositionResult(); await RefreshH3LorasAsync();
        } catch (Exception e) { _error = e.Message; }
    }
    private async Task<bool> SaveForCloseAsync()
    {
        if (_compositionBusy || _applyingProposal || _imageEditorSaving || BulkGenerateLocked) return false;
        if (_promptEditor is not null) await _promptEditor.FlushAsync();
        _planningDraftDelay?.Cancel();
        return await SavePlanningDraftAsync() && !_planningDraftDirty && await Save();
    }
    private async Task<bool> Save() { _saveDelay?.Cancel(); await _saveGate.WaitAsync(); try { return await SaveLocked(); } finally { _saveGate.Release(); } }
    private async Task<bool> SaveLocked()
    {
        if (!_dirty && !_coverageDirty) return true;
        _saving = true;
        try {
            await SaveCoverageLocked();
            while (_dirty && Current is { } current) {
                var captured = current.Copy(); var version = _version;
                var saved = await Production.SaveAsync(Id, captured, _savedComposition?.Version ?? 0, _lifetime.Token);
                var next = saved.Compositions.Single(c => c.Id == captured.Id);
                _savedComposition = next.Copy();
                if (version == _version) { _production = saved; _dirty = false; }
                else { current.Version = next.Version; current.GenerationSetupVersion = next.GenerationSetupVersion; current.History = next.History; current.AcceptedRevisionId = next.AcceptedRevisionId; }
                _globalSetups = await GenerationSetups.LoadAsync(_lifetime.Token);
            }
            _saveStatus = "Saved"; _error = null; _saveFailed = false; return true;
        } catch (Exception e) { _error = e.Message; _saveFailed = true; _saveStatus = "Save failed · draft retained"; return false; }
        finally { _saving = false; }
    }
    private void Remember() { if (_undo.Count >= 40) { var keep = _undo.Take(39).Reverse().ToArray(); _undo.Clear(); foreach (var item in keep) _undo.Push(item); } _undo.Push((Current?.Copy(), EditState)); }
    private void Changed() { _dirty = true; _version++; _saveStatus = "Unsaved"; _saveDelay?.Cancel(); _saveDelay?.Dispose(); _saveDelay = new(); _ = DelayedSave(_saveDelay.Token); }
    private async Task DelayedSave(CancellationToken ct) { try { await Task.Delay(800, ct); await InvokeAsync(async () => { if (!_disposed) { await Save(); StateHasChanged(); } }); } catch (OperationCanceledException) { } }
    private void EditComposition(Action<ProductionComposition> edit) { if (Current is not { } c) return; Remember(); edit(c); Changed(); }
    private void Edit(Action<Shot> edit) => EditComposition(c => { ShotReferences.RetainCharacters(c.Shot); edit(c.Shot); });
    private async Task UndoEdit() { if (_undo.Count == 0) return; var undo = _undo.Pop(); _doc.Shots = undo.Coverage.Shots; _doc.SceneSetups = undo.Coverage.SceneSetups; if (undo.Setup is { } setup && Current?.Id == setup.Id) { setup.GenerationSetupVersion = Current.GenerationSetupVersion; _production.Compositions[_production.Compositions.IndexOf(Current)] = setup; } _notice = null; CoverageChanged(); await RefreshCompositionResult(); }
    private void ValidateTakeFilter() { if (!TakeSetupOptions.Any(s => s.Id == _takeSetupFilter)) _takeSetupFilter = ""; }
    private bool _selectingShot;
    private async Task Select(Guid id)
    {
        if (_selectingShot) return;
        _selectingShot = true;
        try {
            if (_promptEditor is not null) await _promptEditor.FlushAsync();
            if (!await Save()) return;
            _selected = id;
            _compositionId = _production.Compositions.FirstOrDefault(c => c.ShotId == id && !c.Archived)?.Id;
            await ApplySelectedGlobalSetup(); ValidateTakeFilter();
            _savedComposition = Current?.Copy(); _undo.Clear(); await RefreshCompositionResult();
        } catch (Exception e) { _error = e.Message; }
        finally { _selectingShot = false; }
    }
    private async Task SelectComposition(Guid id) { if (_promptEditor is not null) await _promptEditor.FlushAsync(); if (!await Save()) return; _compositionId = id; _selected = Current?.ShotId; ValidateTakeFilter(); _savedComposition = Current?.Copy(); _undo.Clear(); await RefreshCompositionResult(); }
    private async Task RefreshSource() { if (SourceShot is not { } source) return; EditComposition(c => { ProductionPolicy.CopyCoverage(source, c.Shot); c.SourceFingerprint = ProductionPolicy.SourceFingerprint(source); }); await Save(); }
    private async Task BeforeNavigation(LocationChangingContext e) { if (!await SaveForCloseAsync()) e.PreventNavigation(); }
    private async Task DownloadDraft() => await JS.InvokeVoidAsync("lumibelleShots.download", "unsaved-composition.json", Json(Current));
    private async Task CopyPrompt() { try { await JS.InvokeVoidAsync("navigator.clipboard.writeText", PromptPreview); Notify("Prompt copied."); } catch (JSException) { _error = "Select and copy the prompt text."; } }
    private void ChangeQuality(ChangeEventArgs e) { var key = Text(e); if (H3Presets.Keys.Contains(key)) Edit(s => { s.GenerationPreset = key; s.Turbo = key is "turbo4" or "turbo8"; s.TurboSteps = key == "turbo8" ? 8 : 4; }); }
    private static string Invariant(double? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static string Text(ChangeEventArgs e) => e.Value?.ToString() ?? "";
    private static string DurationLabel(Shot s) => s.Duration is >= 1 and <= 15 ? $"{H3Policy.Seconds(s.Duration.Value):0.###} s" : "Set duration in Shots";
    private static string Bytes(long size) => $"{size / (1024d * 1024):0.##} MB";
    private async Task RefreshDefaults() { try { _assets = await AssetStore.LoadAsync(Id, _lifetime.Token); _project = await Projects.GetAsync(Id, _lifetime.Token); await RefreshMedia(); Notify("Assets refreshed. Review the composition before generating."); } catch (Exception e) { _error = e.Message; } }
    private void AddVoice(ChangeEventArgs e)
    {
        if (!Guid.TryParse(Text(e), out var id) || _assets.Voices.FirstOrDefault(v => v.Id == id) is not { } v) return;
        Edit(s => { if (s.Voices.Count < 3) s.Voices.Add(new() { VoiceId = v.Id, AssetId = v.AssetId, Speaker = s.Dialogue.Select(d => d.Speaker).FirstOrDefault(x => !s.Voices.Any(v => v.Speaker == x)) ?? _assets.Assets.Single(a => a.Id == v.AssetId).Name, Start = v.Start, Duration = v.ExcerptDuration }); });
    }
    private string TakeUrl(Guid id, int? frame = null) => $"/media/projects/{Id}/takes/{id}" + (frame is { } i ? $"/frames/{i}" : "");
    private static string TakeDownloadName(Guid id) => $"take-{id:N}.mp4";
    private (string? Url, Guid? TrashId, string Remaining) ResolveImage(ShotImageBinding b)
    {
        if (b.Kind == ShotImageKind.AssetImage)
        {
            var resolved = ReviewImageResolution.Resolve(_assets, new(new(b.AssetId, b.MediaId), b.Name));
            if (resolved.State == ReviewImageState.Active) return (resolved.MediaUrl, null, "");
            var t = resolved.Trash;
            return t is not null && t.CanRestore(DateTimeOffset.UtcNow) ? (resolved.MediaUrl, t.Id, t.Remaining(DateTimeOffset.UtcNow)) : (null, null, "");
        }
        return (null, null, "Standalone frame removed. Replace it with an asset image.");
    }
    private async Task RestoreReference(ShotImageBinding b, Guid trashId)
    {
        try
        {
            if (b.Kind == ShotImageKind.AssetImage) { var latest = await AssetStore.LoadAsync(Id); _assets = await ImageTrash.RestoreImagesAsync(Id, [trashId], latest.Revision); }
            _missing.Remove(b.Id);
        }
        catch (Exception e) { _error = e.Message; }
    }
    private async Task RestoreVoice(Guid id) { try { var latest = await AssetStore.LoadAsync(Id); _assets = await Voices.RestoreVoicesAsync(Id, [id], latest.Revision); } catch (Exception e) { _error = e.Message; } }
    private async Task InputsRestored() { _assets = await AssetStore.LoadAsync(Id); await RefreshMedia(); }
    private async Task CheckVideo() { try { _project = await Projects.GetAsync(Id, _lifetime.Token); _configuration = await Generator.CheckAsync(await Settings.LoadAsync(_lifetime.Token), _lifetime.Token); await RefreshH3LorasAsync(); } catch (Exception e) { _error = e.Message; } }

    private async Task RefreshMedia()
    {
        if (_disposed) return;
        // An edit made while the document loads (such as + Shot, saved meanwhile) must survive: replace it only when nothing changed.
        try { var version = _version; var latest = await Store.LoadAsync(Id, _lifetime.Token); if (latest.Revision < _doc.Revision) { /* A save landed while loading; this copy is older than the page's. */ } else if (_coverageDirty || version != _version) MergeMedia(latest); else { _doc = latest; Baseline(latest); SyncCoverage(); } await RefreshTakeSources(); await LoadVideoRunsAsync(); await OfferVideoReviewAsync(); await RefreshCompositionResult(); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { _error = e.Message; }
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Resolve explicit views against this render before awaiting persistence: a
        // slow load can populate Current during those awaits, before its tabs exist.
        if (RequestedView is null) _handledView = null;
        if (_workspace is not null && Current is not null && RequestedView is "Shot" or "Prompt" or "Takes" && _handledView != RequestedView) {
            _handledView = RequestedView; _openShotView = false;
            if (RequestedView == "Prompt") { await OpenSetupDialog("Prompt"); StateHasChanged(); }
            else await _workspace.ShowCenterAsync(RequestedView);
        }
        if (_doc.ProjectId == Id) {
            await Remember(Id, "shots", "shot", _selected);
            await Remember(Id, "shots", "search", _filter);
            await Remember(Id, "shots", "takeFilter", _takeSetupFilter);
            await Remember(Id, "shots", "takeInputFilter", _takeInputFilter);
            await Remember(Id, "shots", "archived", _showArchived);
        }
        if (firstRender) { _interactive = true; StateHasChanged(); }
        await RestoreSetupFocus();
        if (_restoreCompositionFocus) {
            _restoreCompositionFocus = false;
            var badge = _compositionReturnFocus; _compositionReturnFocus = null;
            // A review opened from the shot list returns there; once applied its badge is gone.
            try { if (badge is { } fromList) await fromList.FocusAsync(); else if (_compositionResult is not null) await _compositionReviewOrigin.FocusAsync(); else if (_compositionAssist is not null) await _compositionAssist.FocusAsync(); }
            catch (Exception e) when (e is JSDisconnectedException or JSException) { }
        }
        if (_focusAfterShotDeletion) { _focusAfterShotDeletion = false; try { await JS.InvokeVoidAsync("lumibelleShots.focusAfterDeletion", _doc.Shots.Count > 0 ? _bulkDeleteButton : _addShotButton); } catch (JSDisconnectedException) { } }
        if (_focusPlanningClose && _planningOpen) { _focusPlanningClose = false; try { await _planningCloseElement.FocusAsync(); } catch (JSDisconnectedException) { } }
        await HandleRequestedPlanningAsync();
        await HandleRequestedVideoAsync();
        await HandleRequestedAssetPickAsync();
        if (_workspace is not null && _openShotView && SourceShot is not null) { _openShotView = false; await _workspace.ShowCenterAsync("Shot"); }
        if (RequestedJobId is { } id && _handledCompositionJob != id && AiJobs.View.Jobs.FirstOrDefault(j => j.Id == id && j.Kind == AiJobKind.PromptComposition && j.Target.ProjectId == Id) is { } job) {
            _handledCompositionJob = id;
            if (job.Target.CompositionId is { } c && _production.Compositions.Any(p => p.Id == c)) { await SelectComposition(c); await RefreshCompositionResult(id); await OpenSetupDialog("Prompt"); StateHasChanged(); }
        }
        if (_restoreRefinementFocus && _reviewOpen) { _restoreRefinementFocus = false; try { await _improveButton.FocusAsync(); } catch (JSDisconnectedException) { } }
        if (_focusReview && _reviewOpen) { _focusReview = false; try { await JS.InvokeVoidAsync("lumibelleShots.focusTake", _reviewTakeId?.ToString()); } catch (JSDisconnectedException) { } }
        if (_restoreReviewFocus || _restoreFrameFocus) { var review = _restoreReviewFocus; _restoreReviewFocus = _restoreFrameFocus = false; try { await JS.InvokeVoidAsync("lumibelleShots.restoreReviewFocus", review); } catch (JSDisconnectedException) { } }
    }
    private void SelectTake(Guid? id)
    {
        _reviewTakeId = id; _frameDestination = null; _continueFrame = null; _refiningTake = null;
        if (_doc.Takes.FirstOrDefault(t => t.Id == id) is { Refinement: not null } take) _refinementJob = take.RunId;
    }
    private void CloseFrameDestination() { _frameDestination = null; _restoreFrameFocus = true; }
    private async Task MutateMedia(Func<long, Task<ShotDocument>> operation)
    {
        if (!await Save()) throw new WorkspaceStoreException("Save the composition first.");
        var latest = await Store.LoadAsync(Id, _lifetime.Token);
        _doc = await operation(latest.Revision);
    }
    private async Task ToggleSelectedTake(ShotTake take)
    {
        if (!await Save()) return;
        try {
            var latest = await Store.LoadAsync(Id, _lifetime.Token);
            var source = latest.Shots.Single(s => s.Id == take.ShotId);
            source.SelectedTakeId = source.SelectedTakeId == take.Id ? null : take.Id;
            _doc = await Store.SaveAsync(Id, latest.Shots, latest.Revision, "Choose production take", _lifetime.Token);
        } catch (Exception e) { _reviewError = e.Message; }
    }
    private async Task DiscardTake(ShotTake take)
    {
        if (_mediaBusy) return; _mediaBusy = true; _reviewError = null;
        try
        {
            await MutateMedia(revision => Store.DiscardAsync(Id, take.Id, ShotTrashKind.Take, revision)); _undoTrash = _doc.Trash.Single(t => t.Take?.Id == take.Id).Id;
            _focusReview = true;
            if (_reviewTakeId == take.Id) SelectTake(_doc.Takes.Where(t => t.RunId == take.RunId && t.Candidate > take.Candidate).OrderBy(t => t.Candidate).FirstOrDefault()?.Id
                ?? _doc.Takes.Where(t => t.RunId == take.RunId).OrderByDescending(t => t.Candidate).FirstOrDefault()?.Id
                ?? _doc.Takes.FirstOrDefault(t => t.Id == take.Refinement?.ParentTakeId)?.Id
                ?? _doc.Takes.FirstOrDefault(ReviewContains)?.Id);
        }
        catch (Exception e) { _reviewError = e.Message; }
        finally { _mediaBusy = false; }
    }
    private async Task UndoDiscard() { try { if (_undoTrash is { } id) await MutateMedia(r => Store.RestoreAsync(Id, [id], r)); _undoTrash = null; Notify("Media restored."); _focusReview = true; } catch (Exception e) { _reviewError = e.Message; _error = e.Message; } }
    private async Task OpenFrameDestination(int index)
    {
        if (ReviewTake is not { } take) return;
        try { _assets = await AssetStore.LoadAsync(Id, _lifetime.Token); _frameDestination = (take.Id, index); _continueFrame = null; _reviewError = null; }
        catch (WorkspaceStoreException e) { _reviewError = e.Message; }
    }
    private async Task<SavedAssetImage> SaveFrameImage(DerivedImageRequest request)
    {
        var library = await AssetStore.LoadAsync(Id, _lifetime.Token);
        return await AssetStore.SaveDerivedImageAsync(Id, request, library.Revision, _lifetime.Token);
    }
    private void FrameImageSaved(SavedAssetImage result)
    {
        _assets = result.Library; _savedFrameImage = result; CloseFrameDestination();
        _notice = "Frame saved as an unapproved image in Assets.";
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true; AiJobs.Changed -= JobChanged; AiJobs.Changed -= PlanningQueueChanged; _planningDraftDelay?.Cancel(); await SavePlanningDraftAsync(); await ReleasePlanningReviewAsync(); _saveDelay?.Cancel(); _lifetime.Cancel(); _saveDelay?.Dispose();
        await ReleaseVideoReviewAsync(); if (_videoOriginJob is { } id) await AiReviews.CloseAsync(id);
        await ReleaseCompositionReview();
        if (_interactive) try {
            await using var promptModule = await JS.InvokeAsync<IJSObjectReference>("import", UiAssets.Module("prompt-editor.js"));
            await promptModule.InvokeVoidAsync("releaseHistory", _promptHistoryScope);
        } catch (JSDisconnectedException) { }
    }
}
