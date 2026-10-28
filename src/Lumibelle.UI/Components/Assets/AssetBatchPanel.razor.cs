using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Assets;

public partial class AssetBatchPanel
{
    [Inject] public IAiJobStore JobStore { get; set; } = null!;
    [Inject] public AiJobCoordinator Coordinator { get; set; } = null!;
    [Inject] public IGenerationSetupStore GenerationSetups { get; set; } = null!;
    [Parameter] public Guid ProjectId { get; set; }
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public Guid? RequestedReviewJobId { get; set; }
    [Parameter] public EventCallback<AssetLibrary> LibraryChanged { get; set; }
    private Guid _loaded, _tab = Guid.NewGuid();
    private Guid? _handledReview;
    private bool _open, _loading, _busy, _planReady, _evaluate, _disposed, _planningNew;
    private string? _error, _rawReview;
    private AssetLibrary? _library;
    private AssetBatch? _run;
    private AssetBatches? _document;
    private IReadOnlyList<AssetBatchPlanRow> _rows = [];
    private readonly Dictionary<Guid, AssetBatchItem> _drafts = [];
    private readonly HashSet<Guid> _selected = [];
    private ImageWorkflow _workflow;
    private AssetBatchKind _kind;
    private VisualStyle _projectStyle = new();
    private VisualStyle? _runStyle;
    private ReelFraming _framing = ReelFraming.CharacterNeutralTurntable;
    private GenerationSetupLibrary _presets = new();
    private Guid? _presetId;
    private AssetReferenceReel? _keyframeReel;
    private Guid _keyframeItem;
    private int _count = 2, _resolution = 1024;
    private string _aspect = "1:1";
    private AssetCategory? _categoryFilter;
    private string _additionalInstructions = "";
    private bool _enhance;
    private TextModelSelectionState? _promptModel;
    private TextModelSelectionState? _reviewer;
    private ImageReviewSession? _viewer;
    private AiQueueDocument _queue = new();
    private AiJobProgress? _progress;
    private AssetImageReference? _viewInitial;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Task? _poll;
    private AssetBatch? LaunchRun => _document?.Runs.LastOrDefault(r => r.Active || r.Pending is not null)
        ?? _document?.Runs.LastOrDefault(r => ReviewCount(r) > 0 || AttentionCount(r) > 0)
        ?? _document?.Runs.LastOrDefault();
    private int LaunchReviewCount => LaunchRun is { } run ? ReviewCount(run) : 0;
    private int LaunchAttentionCount => LaunchRun is { } run ? AttentionCount(run) : 0;
    private bool LaunchReviewReady => LaunchRun is { Active: false, Pending: null } && (LaunchReviewCount > 0 || LaunchAttentionCount > 0);
    private string? LaunchNotice => LaunchAttentionCount > 0 ? $"{LaunchAttentionCount} bulk item(s) need attention. Open the batch to review saved results or recovery options." : null;
    private string LaunchLabel => LaunchRun switch {
        { State: AssetBatchState.Paused } => "Bulk assets · Paused",
        { State: AssetBatchState.Running } run => $"Bulk assets · {run.Items.Count(i => i.State is AssetBatchItemState.Ready or AssetBatchItemState.NeedsAttention or AssetBatchItemState.Skipped)}/{run.Items.Count} finished",
        _ when LaunchReviewCount > 0 => $"Review bulk assets · {LaunchReviewCount}",
        _ when LaunchAttentionCount > 0 => "Bulk assets · Needs attention",
        _ => "Bulk assets"
    };
    private int AttentionCount(AssetBatch run) => run.Items.Count(i => i.State == AssetBatchItemState.NeedsAttention && (run.Kind != AssetBatchKind.Images || i.OutputCount == 0 || PendingCandidates(i).Count > 0));
    private int ReviewCount(AssetBatch run) => run.Items.Count(item => run.Kind == AssetBatchKind.Images
        ? PendingCandidates(item).Count > 0 : item.State != AssetBatchItemState.Skipped && VisibleReels(item).Any() && !VisibleReels(item).Any(r => r.Keyframes?.Frames.Count > 0));
    private string RunLabel => _run?.State switch { AssetBatchState.Running => _run.Pending?.Kind == AiJobKind.AssetPromptEnhancement ? "Enhancing asset prompts" : _run.Kind == AssetBatchKind.Images ? "Generating asset images" : "Generating & reviewing reference reels", AssetBatchState.Paused => "Batch paused", AssetBatchState.Completed => AttentionCount(_run) > 0 ? "Batch complete · some items need attention" : "Batch complete", _ => "Batch stopped" };
    private IEnumerable<AssetBatchPlanRow> SelectionRows => _rows.Where(r => _categoryFilter is null || r.Item.Category == _categoryFilter);
    private IEnumerable<AssetBatchPlanRow> SelectedRows => SelectionRows.Where(r => _selected.Contains(r.Item.Id));
    private int SelectedCount => SelectedRows.Count();
    private static string CategoryLabel(AssetCategory category) => category switch {
        AssetCategory.Character => "Characters", AssetCategory.Environment => "Environments", AssetCategory.Prop => "Props", _ => "References"
    };
    private bool CanStart => !_busy && SelectedCount > 0 && SelectedRows.All(r => !ItemBlocked(_drafts[r.Item.Id]) && (_drafts[r.Item.Id].ExistingReelId is not null || !string.IsNullOrWhiteSpace(_drafts[r.Item.Id].Prompt))) && (!_evaluate || _reviewer is { Ready: true, SupportsImages: true }) && (_kind != AssetBatchKind.Images || !_enhance || _promptModel is { Ready: true });
    private bool ItemBlocked(AssetBatchItem item) => _kind == AssetBatchKind.Images ? item.Category == AssetCategory.Character && item.Context.LookId is not null && item.References.Count == 0 : item.ExistingReelId is null && item.References.Count == 0;
    private static string VerdictLabel(AssetImageVerdict verdict) => verdict switch { AssetImageVerdict.Pass => "Pass", AssetImageVerdict.Concerns => "Concerns", _ => "Cannot judge" };
    private string ItemLabel(AssetBatchItem item) => _run?.Kind == AssetBatchKind.Images && item.State is AssetBatchItemState.Ready or AssetBatchItemState.NeedsAttention && item.OutputCount > 0
        ? PendingCandidates(item).Count > 0 ? "Pending review" : "Reviewed" : item.State switch { AssetBatchItemState.Enhancing => "Enhancing prompt", AssetBatchItemState.NeedsAttention => "Needs attention", AssetBatchItemState.Ready => "Ready for review", _ => item.State.ToString() };
    private bool CanRecoverOutput(AssetBatchItem item) => _queue.Jobs.Any(j => item.GenerationJobs.Contains(j.Id) && j.Recovery is AiJobRecovery.RetryOutput or AiJobRecovery.CheckStatus);
    private bool NeedsPrompt(AssetBatchItem item) => _run?.PromptModel is not null && item.EnhancedPrompt is null;
    private bool CanRetry(AssetBatchItem item) => CanRecoverOutput(item) || (NeedsPrompt(item) ? item.PromptAttempts < 2 : item.OutputCount == 0 ? item.Attempts < 2 : item.ReviewAttempts < 2);
    private string RetryLabel(AssetBatchItem item) => CanRecoverOutput(item) ? "Retrieve saved outputs" : NeedsPrompt(item) ? "Retry prompt enhancement" : item.OutputCount == 0 ? "Retry generation" : _run?.Reviewer is null ? "Keep saved candidates" : "Retry evaluation";
    private string ImageUrl(AssetImageReference image) => $"/media/projects/{ProjectId}/assets/{image.AssetId}/images/{image.ImageId}";
    private bool IsApproved(AssetImageReference image) => _library?.Assets.Any(a => a.Id == image.AssetId && a.Images.Any(i => i.Id == image.ImageId && i.IsReference)) == true;

    protected override async Task OnParametersSetAsync()
    {
        if (_loaded != ProjectId)
        {
            if (_loaded != Guid.Empty) Workflow.Changed -= Changed;
            _loaded = ProjectId; Workflow.Changed += Changed; _run = null; _open = false; _planningNew = false;
            _categoryFilter = null; _additionalInstructions = "";
            await RefreshAsync(); _poll ??= PollAsync();
        }
        if (RequestedReviewJobId is { } job && _handledReview != job)
        {
            _handledReview = job; await RefreshAsync();
            _run = _document?.Runs.LastOrDefault(r => r.Items.Any(i => i.ReviewJobId == job || i.ReviewJobs.Contains(job) || i.PromptJobs.Contains(job)));
            if (_run is not null) { _open = true; await InspectReview(job); }
        }
    }
    private void Changed(Guid project) { if (project == ProjectId && !_disposed) _ = InvokeAsync(RefreshAsync); }
    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try { while (await timer.WaitForNextTickAsync(_lifetime.Token)) if (!_disposed && (_open || LaunchRun?.Active == true || LaunchReviewCount > 0 || LaunchAttentionCount > 0)) await InvokeAsync(RefreshAsync); }
        catch (OperationCanceledException) when (_disposed) { }
    }
    private async Task RefreshAsync()
    {
        try { await _refreshGate.WaitAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_disposed) { return; }
        var project = ProjectId;
        try
        {
            var document = await Workflow.LoadAsync(project, _lifetime.Token); var library = await AssetStore.LoadAsync(project, _lifetime.Token);
            if (_disposed || project != ProjectId) return;
            var libraryChanged = _library?.Revision != library.Revision;
            _document = document; _library = library;
            if (!_planningNew) _run = _run is { } old ? document.Runs.SingleOrDefault(r => r.Id == old.Id) : document.Runs.LastOrDefault();
            if (_run is not null) {
                _queue = await JobStore.ReadAsync(_lifetime.Token);
                _progress = _run.Pending is { } pending ? await Coordinator.ReadProgressAsync(pending.Id, _lifetime.Token) : null;
            }
            if (libraryChanged) await LibraryChanged.InvokeAsync(library);
            StateHasChanged();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { _error = e.Message; }
        finally { _refreshGate.Release(); }
    }
    private async Task OpenAsync()
    {
        _open = true; _loading = true; _error = null;
        try
        {
            if (LaunchRun is { } launch) { _run = launch; _planningNew = false; }
            await RefreshAsync();
            if (_run is null)
            {
                var settings = await Settings.LoadAsync(_lifetime.Token);
                _workflow = await ProjectImageDefaults.ResolveAsync(ProjectId, null, settings, Preferences, _lifetime.Token);
                await LoadPresets();
                _projectStyle = (await Preferences.LoadAsync(ProjectId, _lifetime.Token)).VisualStyle;
                RebuildPlan();
            }
        }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { _error = e.Message; }
        finally { _loading = false; }
    }
    private void RebuildPlan()
    {
        if (_library is null) return;
        _rows = _kind == AssetBatchKind.Images ? AssetBatchPlan.Build(_library, _workflow, _projectStyle, _runStyle) : AssetBatchPlan.BuildReels(_library, _kind, _framing, _presets.Setups.SingleOrDefault(p => p.Id == _presetId), _projectStyle, _runStyle); _drafts.Clear();
        foreach (var row in _rows) _drafts[row.Item.Id] = row.Item;
        _selected.Clear();
        if (!_rows.Any(r => r.Item.Category == _categoryFilter)) _categoryFilter = null;
        _planReady = false; if (_workflow == ImageWorkflow.CodexImages) _resolution = 1024; SelectMissing();
    }
    private void ClearSelection() { foreach (var row in SelectionRows) _selected.Remove(row.Item.Id); }
    private void SelectMissing() { ClearSelection(); foreach (var row in SelectionRows.Where(r => r.MissingReference && !ItemBlocked(r.Item))) _selected.Add(row.Item.Id); }
    private void SelectAll() { ClearSelection(); foreach (var row in SelectionRows.Where(r => !ItemBlocked(r.Item))) _selected.Add(row.Item.Id); }
    private void Toggle(Guid id, bool value) { if (value) _selected.Add(id); else _selected.Remove(id); }
    private void ChangePrompt(Guid id, string prompt) => _drafts[id] = _drafts[id] with { Prompt = prompt };
    private void ReviewerChanged(TextModelSelectionState state) => _reviewer = state;
    private async Task Act(Func<Task> action)
    {
        if (_busy) return; _busy = true; _error = null;
        try { await action(); await RefreshAsync(); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or AiGenerationException) { _error = e.Message; }
        finally { _busy = false; }
    }
    private Task StartAsync() => Act(async () =>
    {
        var selected = SelectedRows.Select(r => _kind == AssetBatchKind.Images
            ? _drafts[r.Item.Id] with { AdditionalInstructions = _additionalInstructions.Trim() } : _drafts[r.Item.Id]).ToArray();
        var result = _kind == AssetBatchKind.Images
            ? await Workflow.StartAsync(ProjectId, _tab, _workflow, selected, _count, _aspect, _resolution, _evaluate ? _reviewer?.Model : null, _lifetime.Token, _enhance ? _promptModel?.Model : null)
            : await Workflow.StartReelsAsync(ProjectId, _tab, _kind, selected, _evaluate ? _reviewer?.Model : null, _lifetime.Token);
        _run = result.Runs.Last(); _rawReview = null; _planningNew = false;
    });
    private Task Command(string command) => Act(async () =>
    {
        if (_run is null) return;
        _ = command switch { "pause" => await Workflow.PauseAsync(ProjectId, _run.Id, _lifetime.Token),
            "resume" => await Workflow.ResumeAsync(ProjectId, _run.Id, _lifetime.Token), _ => await Workflow.StopAsync(ProjectId, _run.Id, _lifetime.Token) };
    });
    private Task Approve(Guid item, Guid image) => Act(async () => { await Workflow.ApproveAsync(ProjectId, _run!.Id, item, image, _lifetime.Token); });
    private Task Retry(Guid item) => Act(async () => { await Workflow.RetryAsync(ProjectId, _run!.Id, item, _lifetime.Token); });
    private Task GenerateAgain(Guid item) => Act(async () => { await Workflow.GenerateAgainAsync(ProjectId, _run!.Id, item, _lifetime.Token); });
    private Task Skip(Guid item) => Act(async () => { await Workflow.SkipAsync(ProjectId, _run!.Id, item, _lifetime.Token); });
    private Task NewPlan() => Act(async () =>
    {
        _run = null; _rawReview = null; _planningNew = true; _categoryFilter = null; _additionalInstructions = "";
        _runStyle = null; _projectStyle = (await Preferences.LoadAsync(ProjectId, _lifetime.Token)).VisualStyle;
        var settings = await Settings.LoadAsync(_lifetime.Token);
        _workflow = await ProjectImageDefaults.ResolveAsync(ProjectId, null, settings, Preferences, _lifetime.Token);
        await LoadPresets();
        RebuildPlan();
    });
    private void SelectRun(ChangeEventArgs e)
    {
        if (Guid.TryParse(e.Value?.ToString(), out var id)) { _run = _document?.Runs.SingleOrDefault(r => r.Id == id); _planningNew = false; _rawReview = null; _showDecided = false; }
    }
    private List<Guid> VisibleCandidates(AssetBatchItem item) => item.Candidates.Concat(_library?.ImagePublications.Where(p => item.GenerationJobs.Contains(p.JobId) && p.AssetId == item.Context.AssetId).Select(p => p.ImageId) ?? [])
        .Distinct().Where(id => _library?.Assets.Any(a => a.Id == item.Context.AssetId && a.Images.Any(i => i.Id == id)) == true).ToList();
    private void View(AssetBatchItem item, Guid image)
    {
        var candidates = VisibleCandidates(item);
        _viewer = new(ProjectId, item.Context.AssetId, true, candidates.Count) { IsEditBatch = item.References.Count > 0 };
        foreach (var input in item.References) _viewer.Entries.Add(new(input.Reference, "Supplied reference"));
        foreach (var (id, index) in candidates.Select((id, i) => (id, i))) { _viewer.Entries.Add(new(new(item.Context.AssetId, id), $"Candidate {index + 1}", true)); _viewer.CompletedCandidates.Add(index + 1); _viewer.PendingCandidates.Remove(index + 1); }
        _viewInitial = new(item.Context.AssetId, image); _open = false;
    }
    private async Task CloseViewer() { _viewer = null; _viewInitial = null; _open = true; await RefreshAsync(); }
    private async Task InspectReview(Guid job)
    {
        try { var result = await JobStore.ReadArtifactAsync<AiTextJobResult>(job, AiJobArtifact.Result, _lifetime.Token); _rawReview = result?.Raw ?? "The review response is not available yet."; }
        catch (WorkspaceStoreException e) { _error = e.Message; }
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true; Workflow.Changed -= Changed; await _lifetime.CancelAsync();
        if (_poll is not null) try { await _poll; } catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }
}
