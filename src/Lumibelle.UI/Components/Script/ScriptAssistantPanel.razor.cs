using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace lumibelle.Components.Script;

public partial class ScriptAssistantPanel
{
    [Inject] public AiJobCoordinator Jobs { get; set; } = null!;
    [Inject] public AiTextJobCapture Requests { get; set; } = null!;
    [Inject] public IAiJobStore JobStore { get; set; } = null!;
    [Inject] public IAiReviewGate Reviews { get; set; } = null!;
    [Inject] public IAssistantHistoryStore History { get; set; } = null!;
    [Inject] public ApplicationSession Session { get; set; } = null!;
    [Parameter] public Guid ProjectId { get; set; }
    [Parameter] public Guid? RequestedJobId { get; set; }
    [Parameter, EditorRequired] public ScriptDocument Document { get; set; } = null!;
    [Parameter] public ScriptSelection? Selection { get; set; }
    [Parameter] public EventCallback<string?> ActivityChanged { get; set; }
    private string? _reportedActivity;
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_loading && RequestedJobId is { } requested && _handledRequestedJob != requested)
        {
            _handledRequestedJob = requested;
            if (_runs.Any(r => r.JobId == requested)) await Review(requested);
            else { _error = "This script response is unavailable in this project. Its saved output may still be inspected from AI activity."; StateHasChanged(); }
        }
        var activity = _error ?? (!WholeDocumentOperation && _target is not null && !TargetReady ? TargetDescription : null);
        if (activity == _reportedActivity) return;
        _reportedActivity = activity; await ActivityChanged.InvokeAsync(activity);
    }
    [Parameter] public Func<ScriptAssistantTarget, Task<ScriptRequestContext>> Capture { get; set; } = null!;
    [Parameter] public Func<ScriptScope, Task<ScriptAssistantTarget>> SelectTarget { get; set; } = null!;
    [Parameter] public ScriptAssistantTarget? RequestedTarget { get; set; }
    [Parameter] public Func<AssistantRun, Task<bool>> Apply { get; set; } = _ => Task.FromResult(false);
    private List<AssistantRun> _runs = [];
    private readonly Dictionary<Guid, AssistantRun> _unsaved = [];
    private readonly SemaphoreSlim _historyGate = new(1, 1);
    private lumibelle.Components.AI.TextAssistance? _assist;
    private bool _requestsOpen, _composerOpened;
    private async Task OpenComposerAsync()
    {
        if ((!_composerOpened || _target is null) && RequestedTarget is null && !_generating && HasScript && !WholeDocumentOperation)
            await ChooseScopeAsync(_scope);
        _composerOpened = true;
    }
    private TextModelReference? _model;
    private bool _ready, _loading = true, _generating, _historyError, _disposed, _applying;
    private ScriptScope _scope = ScriptScope.Scene;
    private ScriptAssistantTarget? _target, _lastRequestedTarget;
    private bool _initialTargetChosen;
    private bool HasScript => Document.Blocks.Any(b => !string.IsNullOrWhiteSpace(b.Text));
    protected override void OnParametersSet()
    {
        if (RequestedTarget is not null && !ReferenceEquals(RequestedTarget, _lastRequestedTarget))
        { _lastRequestedTarget = RequestedTarget; _target = RequestedTarget; _scope = RequestedTarget.Scope; _initialTargetChosen = true; }
        if (!_initialTargetChosen && HasScript && Selection is not null && Document.Blocks.Any(b => b.Id == Selection.AnchorBlockId))
        {
            _initialTargetChosen = true;
            try { _target = ScriptAssistantTarget.From(Document, ScriptScope.Scene, Selection); }
            catch (WorkspaceStoreException) { /* The initial caret may be outside a scene. Require an explicit target. */ }
        }
    }
    private async Task ChooseScopeAsync(ScriptScope scope)
    {
        _initialTargetChosen = true; _scope = scope; _target = null;
        try { _target = await SelectTarget(scope); _error = null; }
        catch (WorkspaceStoreException e) { _error = e.Message; }
    }
    private WritingOperation? _chosenOperation;
    private WritingOperation Operation => _chosenOperation ?? (Document.Blocks.All(b => string.IsNullOrWhiteSpace(b.Text)) ? WritingOperation.Draft : WritingOperation.Revise);
    private bool WholeDocumentOperation => Operation is WritingOperation.Draft or WritingOperation.Outline or WritingOperation.Discuss;
    private string ActionLabel => Operation switch { WritingOperation.Draft => "Draft script", WritingOperation.Outline => "Suggest outline", WritingOperation.Discuss => "Discuss idea", _ => Operation.ToString() };
    private int _requestPage;
    private int RequestPages => Math.Max(1, (_runs.Count(r => r.Status != AssistantRunStatus.Running) + 19) / 20);
    private IEnumerable<AssistantRun> VisibleRuns => _runs.Where(r => r.Status != AssistantRunStatus.Running).OrderByDescending(r => r.CreatedUtc).Skip(_requestPage * 20).Take(20);
    private Guid? _dismissedLatestId;
    private AssistantRun? LatestRun { get { var latest = _runs.Where(r => r.Status != AssistantRunStatus.Running).OrderByDescending(r => r.CreatedUtc).FirstOrDefault(); return latest?.Id == _dismissedLatestId ? null : latest; } }
    private bool TargetHasContent(AssistantRun run) => (_reviewTarget ?? run.Target).OriginalBlocks.Any(b => !string.IsNullOrWhiteSpace(b.Text));
    private string RunStatus(AssistantRun run) => run.Applied || Document.AppliedProposalIds.Contains(run.Id) ? "Applied" : run.Rejected ? "Rejected" : run.Status == AssistantRunStatus.Completed && run.Error is not null ? "Invalid response" :
        run.Status == AssistantRunStatus.Running ? Jobs.View.Jobs.FirstOrDefault(j => j.Id == run.JobId)?.State == AiJobState.Waiting ? "Queued" : "Working" : run.Status.ToString();
    private string _instructions = "";
    private readonly HashSet<Guid> _attachedDiscussion = [];
    private string? _requestDetails;
    private IReadOnlyList<ConversationMessage> AttachedConversation => ScriptAssistant.Conversation(_runs.Where(r => _attachedDiscussion.Contains(r.Id)));
    private void Attach(Guid id, bool selected) { if (selected) _attachedDiscussion.Add(id); else _attachedDiscussion.Remove(id); }
    private static ScriptTarget ComparisonTarget(AssistantRun run, ScriptTarget target) => run.Edits is null ? target : target with { Scope = ScriptScope.Document, StartOffset = 0, EndOffset = 0 };
    private string? _error;
    private Guid? _reviewId;
    private ScriptTarget? _reviewTarget;
    private bool _compare;
    private AssistantRun? ReviewRun => _runs.FirstOrDefault(r => r.Id == _reviewId);
    private static readonly DialogOptions ReviewOptions = new() { MaxWidth = MaxWidth.ExtraLarge, FullWidth = true, CloseOnEscapeKey = true, BackdropClick = false };
    private lumibelle.Components.AI.TextRequestAction? _primaryAction;
    // Starts over in the composer while this result stays as it is, to apply or discard later.
    private async Task NewRequestAsync()
    {
        await CloseReview();
        if (_reviewId is not null) return;
        _primaryAction?.StartFresh();
        await OpenComposerAsync();
    }
    private async Task CloseReview() { if (_applying || _savingJson) return; if (_reviewId is { } id) await Reviews.CloseAsync(id); _reviewId = null; _editingJson = false; }
    private Task ReviewVisibilityChanged(bool visible) => visible ? Task.CompletedTask : CloseReview();
    private GenerationProgress? _progress;
    private AiJobProgress? _reviewProgress;
    private readonly CancellationTokenSource _lifetime = new();
    private AssistantRun? InspectionRun => Jobs.View.Jobs.FirstOrDefault(j => j.Kind == AiJobKind.ScriptAssistant && j.Target.ProjectId == ProjectId && TextRequestPresentation.IsActive(j)) is { } active
        ? _runs.FirstOrDefault(r => r.JobId == active.Id) : LatestRun;
    private TextRequestPresentation? ScriptPresentation {
        get {
            var run = InspectionRun;
            if (run is null) return null;
            // Locally corrected JSON and historical runs have no queue job. Project
            // their saved result into the control without creating a new request.
            var job = Jobs.View.Jobs.FirstOrDefault(j => j.Id == run.JobId && j.Target.ProjectId == ProjectId) ?? new AiJobHeader {
                Id = run.Id, Kind = AiJobKind.ScriptAssistant, Backend = run.Backend, Target = new(ProjectId),
                ProjectName = "", TargetName = run.Target.Name, OriginTabId = run.SessionId, RequestFingerprint = run.SourceFingerprint,
                CreatedUtc = run.CreatedUtc, State = run.Status switch { AssistantRunStatus.Completed => AiJobState.Completed, AssistantRunStatus.Cancelled => AiJobState.Cancelled, _ => AiJobState.NeedsAttention }
            };
            var outcome = run is not null && (run.Applied || run.Rejected || Document.AppliedProposalIds.Contains(run.Id)) ? TextRequestOutcome.Resolved
                : run?.Error is not null ? TextRequestOutcome.Invalid : run?.Operation == WritingOperation.Discuss ? TextRequestOutcome.Response : TextRequestOutcome.Proposal;
            var working = run?.Operation switch { WritingOperation.Draft => "Drafting script…", WritingOperation.Outline => "Outlining script…", WritingOperation.Discuss => "Responding…", WritingOperation.Rewrite => "Rewriting…", WritingOperation.Continue => "Continuing…", _ => "Revising…" };
            return new(job, working, outcome, run?.Operation.ToString());
        }
    }
    private Task InspectScript() => InspectionRun is { } run ? Review(run.Id) : Task.CompletedTask;
    private AiJobHeader? _activeJob;
    private AiJobSubmission? _pendingSubmission;
    private Guid? _originJobId, _attemptedAutomaticId, _handledRequestedJob, _restoredJobId;
    private bool _submitting, _refreshing, _refreshAgain, _followsDefault = true, _instructionsEdited;
    private long _queueRevision = -1;
    private ElementReference _originElement, _requestsOrigin;
    private bool TargetReady { get { try { return _target?.Capture(Document) is not null; } catch (WorkspaceStoreException) { return false; } } }
    private bool _modelPreparing;
    private bool CanGenerate => !_modelPreparing && !_applying && !_loading && !_generating && _pendingSubmission is null && !_historyError && _ready && _model is not null && (WholeDocumentOperation || TargetReady);
    private string TargetDescription
    {
        get
        {
            try
            {
                if (_target is null) return "Choose a target in the outline or use the controls above.";
                var target = _target.Capture(Document);
                if (target.Scope != ScriptScope.Passage) return target.Name;
                var excerpt = string.Join(" ", ScriptStructure.TargetBlocks(target).Select(b => b.Text));
                return $"{target.Name}: “{(excerpt.Length > 100 ? excerpt[..100] + "…" : excerpt)}”";
            }
            catch (WorkspaceStoreException e) { return e.Message; }
        }
    }
    private int EstimatedTokens
    {
        get
        {
            ScriptTarget target;
            try { target = !WholeDocumentOperation && _target is not null ? _target.Capture(Document) : ScriptStructure.Capture(Document, ScriptScope.Document, null); }
            catch (WorkspaceStoreException) { target = new(ScriptScope.Document, "Choose a target", []); }
            return ScriptAssistant.EstimateInputTokens(new(new AssistantRun { Instructions = _instructions, Operation = Operation, EditFormat = Operation == WritingOperation.Revise ? 1 : 0, Target = target }, Document, AttachedConversation));
        }
    }
    protected override async Task OnInitializedAsync()
    {
        Jobs.Changed += QueueChanged;
        try { await Jobs.RefreshAsync(); await RefreshJobsAsync(); }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { _historyError = true; _error = e.Message; }
        _loading = false;
    }
    private void ModelChanged(TextModelSelectionState state) { _model = state.Model; _ready = state.Ready; _followsDefault = state.FollowsDefault; }
    private string InstructionsValue { get => _instructions; set { _instructions = value; _instructionsEdited = true; } }
    private async Task StartAsync(WritingOperation operation)
    {
        if (!CanGenerate || _assist is null) return;
        _modelPreparing = true;
        try { if (!await _assist.PrepareSubmitAsync()) return; }
        finally { _modelPreparing = false; }
        _submitting = _generating = true; await CloseReview();
        var model = _model!; var instructions = _instructions; var conversation = AttachedConversation.ToArray(); var followsDefault = _followsDefault;
        var target = operation is WritingOperation.Draft or WritingOperation.Outline or WritingOperation.Discuss ? new ScriptAssistantTarget(ScriptScope.Document) : _target!;
        _error = null;
        _progress = new(GenerationPhase.Saving, "Saving the script…");
        try
        {
            var context = await Capture(target);
            _lifetime.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(instructions) && context.Document.Blocks.All(b => string.IsNullOrWhiteSpace(b.Text)))
                throw new WorkspaceStoreException("Write a starting passage or add instructions first.");
            var run = new AssistantRun { SessionId = Session.Id, Operation = operation, Target = context.Target, SourceRevision = context.Document.Revision,
                SourceFingerprint = ScriptStructure.ContextFingerprint(context.Document), EditFormat = operation == WritingOperation.Revise ? 1 : 0, Instructions = instructions, Backend = model.Backend, Model = model.Model };
            var tab = await Reviews.TabIdAsync();
            _pendingSubmission = await Requests.ScriptAsync(run.Id, tab, new(run, context.Document, conversation, model), followsDefault, _lifetime.Token);
            _pendingSubmission = _assist.Attribute(_pendingSubmission);
            _originJobId = _restoredJobId = run.Id;
            await EnqueuePendingAsync();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e)
        {
            _error = e is AiGenerationException or WorkspaceStoreException or ProjectStoreException ? e.Message : "The request could not be queued. Your script and instructions are unchanged.";
        }
        finally
        {
            _submitting = false;
            if (!_disposed) await RefreshJobsAsync();
        }
    }
    private async Task EnqueuePendingAsync()
    {
        if (_pendingSubmission is null) return;
        try { _error = null; await Jobs.EnqueueAsync(_pendingSubmission, _lifetime.Token); _pendingSubmission = null; if (_assist is not null) await _assist.QueuedAsync(); }
        catch (WorkspaceStoreException e) { _error = e.Message + " Retry saving this exact request; it will not be submitted twice."; }
    }
    private async Task RetryEnqueueAsync()
    { if (_submitting) return; _submitting = _generating = true; try { await EnqueuePendingAsync(); } finally { _submitting = false; await RefreshJobsAsync(); } }
    private void QueueChanged() { if (!_disposed && !_loading) _ = InvokeAsync(RefreshJobsAsync); }
    private async Task RefreshJobsAsync()
    {
        if (_refreshing) { _refreshAgain = true; return; }
        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                var view = Jobs.View;
                _activeJob = view.Jobs.FirstOrDefault(j => j.Kind == AiJobKind.ScriptAssistant && j.Target.ProjectId == ProjectId && j.LocksTarget);
                _generating = _submitting || _activeJob is not null;
                if (_activeJob is { } active)
                {
                    _progress = Jobs.Progress(active.Id)?.Progress;
                    if (_restoredJobId != active.Id && !_instructionsEdited && !_submitting)
                    {
                        var saved = await JobStore.ReadSnapshotAsync(active.Id, _lifetime.Token);
                        var run = AiTextJobHandler.Read(active, saved).Payload<ScriptAssistantRequest>().Run;
                        _instructions = run.Instructions; _chosenOperation = run.Operation; _scope = run.Target.Scope; _restoredJobId = active.Id;
                        _target = run.Target.Scope switch
                        {
                            ScriptScope.Document => new(ScriptScope.Document),
                            ScriptScope.Passage when run.Target.OriginalBlocks.Count > 0 => new(ScriptScope.Passage,
                                Passage: new(run.Target.OriginalBlocks[0].Id, run.Target.StartOffset, run.Target.OriginalBlocks[^1].Id, run.Target.EndOffset),
                                PassageBlockIds: run.Target.OriginalBlocks.Select(b => b.Id).ToList()),
                            _ => new(run.Target.Scope, run.Target.OriginalBlocks.FirstOrDefault()?.Id)
                        };
                    }
                }
                if (_queueRevision != view.Revision)
                {
                    // Saves take the same gate, so a reload that started before an apply or dismissal cannot replace it with the older run.
                    await _historyGate.WaitAsync(_lifetime.Token);
                    try { _runs = (await History.LoadAsync(ProjectId, _lifetime.Token)).Runs; }
                    finally { _historyGate.Release(); }
                    _observedScriptJobs = view.Jobs;
                    foreach (var unsaved in _unsaved.Values) Put(unsaved);
                    _historyError = _unsaved.Count > 0; _queueRevision = view.Revision;
                }
                if (_activeJob is { State: AiJobState.Running } writing && _reviewId == writing.Id &&
                    Jobs.Progress(writing.Id) is { } progress && !ReferenceEquals(progress, _reviewProgress))
                {
                    var partial = await JobStore.ReadArtifactAsync<AiTextJobResult>(writing.Id, AiJobArtifact.Result, _lifetime.Token);
                    if (partial is { Complete: false } && _runs.FirstOrDefault(r => r.JobId == writing.Id) is { Status: AssistantRunStatus.Running } run)
                        Put(run with { Output = partial.Raw });
                    _reviewProgress = progress;
                }
                if (_pendingSubmission is { } pending && view.Jobs.Any(j => j.Id == pending.Id)) _pendingSubmission = null;
                if (!_loading && _originJobId is { } origin && _attemptedAutomaticId != origin && view.Jobs.FirstOrDefault(j => j.Id == origin) is { } completed &&
                    completed.State is not (AiJobState.Waiting or AiJobState.Running))
                {
                    _attemptedAutomaticId = origin;
                    var run = _runs.FirstOrDefault(r => r.JobId == origin); _error = run?.Error;
                    if (_reviewId != origin && run is { Status: AssistantRunStatus.Completed, Error: null } && await Reviews.TryOpenAsync(completed, _originElement, automatic: true)) await ShowReviewAsync(run);
                }
            } while (_refreshAgain && !_disposed);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { _historyError = true; _error = e.Message; }
        finally { _refreshing = false; if (!_disposed) StateHasChanged(); }
    }
    private IReadOnlyList<AiJobHeader> _observedScriptJobs = [];
    private void Put(AssistantRun run) { var i = _runs.FindIndex(r => r.Id == run.Id); if (i < 0) _runs.Add(run); else _runs[i] = run; }
    private async Task<AssistantRun> PersistAsync(AssistantRun run)
    {
        await _historyGate.WaitAsync();
        try { var saved = await History.SaveRunAsync(ProjectId, run); Put(saved); _unsaved.Remove(run.Id); return saved; }
        catch { Put(run); _unsaved[run.Id] = run; _historyError = true; throw; }
        finally { _historyGate.Release(); }
    }
    private async Task RetryHistoryAsync()
    {
        try
        {
            foreach (var run in _unsaved.Values.ToArray()) await PersistAsync(run);
            if (_unsaved.Count == 0) _runs = (await History.LoadAsync(ProjectId)).Runs;
            _historyError = false; _error = null;
        }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { _error = e.Message; }
    }
    private async Task Review(Guid id)
    {
        // The queue can expose View request before its asynchronous history projection
        // finishes. Read that exact request here instead of silently ignoring the click.
        try
        {
            _runs = (await History.LoadAsync(ProjectId, _lifetime.Token)).Runs;
            foreach (var unsaved in _unsaved.Values) Put(unsaved);
        }
        catch (OperationCanceledException) when (_disposed) { return; }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { _error = e.Message; return; }
        if (_disposed) return;
        if (_runs.FirstOrDefault(r => r.Id == id) is not { } run) { _error = "This request is unavailable. Check Activity for its saved status."; return; }
        var job = Jobs.View.Jobs.FirstOrDefault(j => j.Id == run.JobId);
        if (job is not null && !await Reviews.TryOpenAsync(job, _requestsOpen ? _requestsOrigin : _originElement, automatic: false))
        { _error = "Close the other dialog before opening this response."; return; }
        await ShowReviewAsync(run);
    }
    private async Task ShowReviewAsync(AssistantRun run)
    {
        _requestsOpen = false; if (_assist is not null) await _assist.CloseAsync();
        if (_disposed) { await Reviews.CloseAsync(run.Id); return; }
        _reviewId = run.Id; _reviewTarget = run.AppliedTarget; _compare = run.Operation is WritingOperation.Revise or WritingOperation.Rewrite; _reviewProgress = null;
        _editingJson = false; _pendingJsonProposal = null; _jsonError = null;
        _requestDetails = null;
        if (run.JobId is { } jobId && Jobs.View.Jobs.FirstOrDefault(j => j.Id == jobId) is { } header)
        {
            var request = AiTextJobHandler.Read(header, await JobStore.ReadSnapshotAsync(jobId, _lifetime.Token));
            _requestDetails = "Captured model: " + System.Text.Json.JsonSerializer.Serialize(request.Model, AtomicJsonFile.Options) + "\nSelection: " + (request.SelectionSource?.ToString() ?? "Historical captured choice") + "\n\n" + string.Join("\n\n", request.Messages.Select(m => m.Role.ToUpperInvariant() + "\n" + string.Join("\n", m.Parts.Select(p => p.Text))));
        }
        _responseJsonError = run.EditFormat != 1 && run.Error is not null && run.Operation != WritingOperation.Discuss && !string.IsNullOrWhiteSpace(run.Output)
            ? ScreenplayJson.Parse(run.Output).Error : null;

        StateHasChanged();
    }
    private static string EditDescription(AssistantRun run, ScriptEditOperation edit)
    {
        string Label(Guid? id) { var b = run.Target.OriginalBlocks.FirstOrDefault(b => b.Id == id); var text = b?.Text ?? "block"; return text.Length > 70 ? text[..70] + "…" : text; }
        var range = Label(edit.StartId) + (edit.EndId is { } end && end != edit.StartId ? " through " + Label(end) : "");
        return edit.Kind switch { "move" => $"Move {range} {edit.Side} {Label(edit.AnchorId)}", "insert" => $"Insert {edit.Blocks?.Count} blocks {edit.Side} {Label(edit.AnchorId)}", "delete" => $"Delete {range}", _ => $"Replace {range}" };
    }
    private async Task RetargetAsync()
    {
        try
        {
            if (ReviewRun?.EditFormat == 1) { await CloseReview(); await StartAsync(WritingOperation.Revise); return; }
            var target = ReviewRun?.Operation is WritingOperation.Draft or WritingOperation.Outline
                ? new ScriptAssistantTarget(ScriptScope.Document) : _target ?? throw new WorkspaceStoreException("Choose an explicit target before retargeting.");
            _reviewTarget = (await Capture(target)).Target; _compare = true; _error = null;
        }
        catch (WorkspaceStoreException e) { _error = e.Message; }
    }
    private async Task ApplyAsync(AssistantRun run)
    {
        if (_applying) return; _applying = true;
        try
        {
            if (await Apply(run with { Target = _reviewTarget ?? run.Target }))
            {
                await PersistAsync(run with { Applied = true, AppliedTarget = _reviewTarget ?? run.Target }); await Reviews.CloseAsync(run.Id); _reviewId = null;
                if (run.Operation is WritingOperation.Draft or WritingOperation.Outline && _chosenOperation == run.Operation) _chosenOperation = null;
            }
            else _error = "The proposal could not be applied. Close this review to check the editor’s save status, then try again.";
        }
        catch (WorkspaceStoreException e) { _error = e.Message; }
        finally { _applying = false; }
    }
    private async Task RejectAsync(AssistantRun run)
    {
        try { await PersistAsync(run with { Rejected = true }); await Reviews.CloseAsync(run.Id); _reviewId = null; }
        catch (WorkspaceStoreException e) { _error = e.Message; }
    }
    private async Task Reuse(AssistantRun run) { _dismissedLatestId = LatestRun?.Id; _requestsOpen = false; if (_assist is not null) await _assist.OpenAsync(); _instructions = run.Instructions; _instructionsEdited = true; _error = "Instructions restored. Choose an action to make a new request."; }
    private async Task Cancel()
    {
        if (_activeJob is not { } job) return;
        try { await Jobs.CancelAsync(job.Id); await RefreshJobsAsync(); }
        catch (WorkspaceStoreException e) { _error = e.Message; }
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true; Jobs.Changed -= QueueChanged; _lifetime.Cancel();
        if (_reviewId is { } id) await Reviews.CloseAsync(id);
        // The application owns accepted requests; leaving this page only detaches.
    }
}
