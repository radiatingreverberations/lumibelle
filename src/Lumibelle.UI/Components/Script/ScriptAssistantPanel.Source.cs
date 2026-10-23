using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Story;
using lumibelle.Services.AI;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Script;

public partial class ScriptAssistantPanel
{
    [Inject] public FileSourceStore Sources { get; set; } = null!;
    [Parameter] public Func<SourceAnalysisRun, Task<bool>>? AddOutlineActs { get; set; }
    private SourceAnalysisRun? _sourcePlan;
    private bool NeedsOutlineActs => AddOutlineActs is not null && _sourcePlan is not null &&
        ScriptAdaptation.WithActHeadings(Document, _sourcePlan).Blocks.Count > Document.Blocks.Count;
    private async Task AddOutlineActsAsync()
    {
        if (_applying || _sourcePlan is null || AddOutlineActs is null) return;
        _applying = true;
        try
        {
            _error = await AddOutlineActs(_sourcePlan) ? null : "The act headings could not be saved. Check the editor’s save status.";
        }
        catch (WorkspaceStoreException e) { _error = e.Message; }
        finally { _applying = false; }
    }
    private lumibelle.Services.AI.ScriptScenePlan? _scenePlan;
    private bool _scenePlanning;
    private bool _twoStepSequence;
    private bool _reviewedSequence;
    private ScriptSequenceReview? _sequenceReview;
    private bool _completeSequenceDraft;
    private string? _reviewActionError;
    private bool CanRecheckSequenceReview => _reviewedSequence && _completeSequenceDraft && _sequenceReview?.State == ScriptSequenceReviewState.NeedsAttention &&
        ReviewRun is { Rejected: false } run && Jobs.View.Jobs.Any(j => j.Id == run.JobId && j.State == AiJobState.NeedsAttention && !j.CancelRequested && !j.RemoteUnconfirmed);
    private bool CanManuallyReviewSequence => CanRecheckSequenceReview && !BatchActive && UnapprovedSequenceBlocks is not null;
    private async Task RecheckSequenceReviewAsync()
    {
        if (!CanRecheckSequenceReview || _reviewId is not { } id || _applying) return;
        _applying = true; _reviewActionError = null;
        try {
            await ScriptSequenceReviewRecovery.PrepareRecheckAsync(JobStore, id, _lifetime.Token);
            await Jobs.ResumeAsync(id, _lifetime.Token);
        }
        catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException) { _reviewActionError = e.Message; }
        finally { _applying = false; }
    }
    private Task ReviewSequenceManuallyAsync() => CanManuallyReviewSequence && UnapprovedSequenceBlocks is { } blocks
        ? SaveJsonProposalAsync(System.Text.Json.JsonSerializer.Serialize(blocks, AtomicJsonFile.Options), false, manualReview: true) : Task.CompletedTask;
    private IReadOnlyList<ScriptBlock>? UnapprovedSequenceBlocks {
        get {
            if (!_reviewedSequence || _sequenceReview is null || ReviewRun is not { } run) return null;
            try { return ScriptSequenceDrafting.Parse(run.Output).Blocks; }
            catch (WorkspaceStoreException) { return null; }
        }
    }
    private async Task ContinueSequenceReviewAsync()
    {
        if (!_reviewedSequence || _reviewId is not { } id || !Jobs.View.Jobs.Any(j => j.Id == id && j.CanRetryCaptured)) return;
        try { await Jobs.ResumeAsync(id, _lifetime.Token); }
        catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException) { _error = e.Message; }
    }
    private string? _requestModelName;
    private lumibelle.Services.AI.ScriptScenePlan? _planningPreview;
    private string? _planningPreviewRaw;
    private string? _planningFinishReason;
    private ScriptBriefCorrection? _planningCorrection;
    private string? _planningActionError;
    private bool PlanningBatchPaused => _batch is { State: ScriptDraftBatchState.Paused, Pending: { } pending } && pending.Id == _reviewId;
    private string PlanningOutput => _planningCorrection?.Raw ?? ReviewRun?.Output ?? "";
    private AssistantRun PlanningViewRun(AssistantRun run) => _planningCorrection is null ? run : run with { Output = _planningCorrection.Raw };
    private bool CanApprovePlanning => _scenePlanning && _planningFinishReason == "stop" && ReviewRun is { Status: not AssistantRunStatus.Running, Rejected: false } run &&
        ScriptScenePlanning.Inspect(PlanningOutput) is { CanUse: true } inspected &&
        Jobs.View.Jobs.Any(j => j.Id == run.JobId && j.State == AiJobState.NeedsAttention && !j.CancelRequested && !j.RemoteUnconfirmed &&
            (j.Recovery == AiJobRecovery.ApproveBrief || _planningCorrection is not null || inspected.Warnings.Length > 0));
    private async Task ApprovePlanningAsync()
    {
        if (!CanApprovePlanning || _reviewId is not { } id || _applying) return;
        _applying = true;
        try {
            _planningActionError = null;
            await ScriptScenePlanApproval.ApproveAsync(JobStore, id, _lifetime.Token);
            if (PlanningBatchPaused) await DraftWorkflow.ResumeAsync(ProjectId, _batch!.Id, _lifetime.Token);
            else await Jobs.ResumeAsync(id, _lifetime.Token);
        }
        catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException) { _planningActionError = e.Message; }
        finally { _applying = false; }
    }
    private async Task RetryPlanningAsync()
    {
        if (PlanningBatchPaused) { await BatchCommandAsync("resume"); await CloseReview(); return; }
        await NewRequestAsync();
        if (_reviewId is null && _draftOutlineScene && CanGenerate) await StartAsync(Operation);
    }
    private async Task LoadRequestDetailsAsync(AiJobHeader header)
    {
        var request = AiTextJobHandler.Read(header, await JobStore.ReadSnapshotAsync(header.Id, _lifetime.Token));
        var result = await JobStore.ReadArtifactAsync<AiTextJobResult>(header.Id, AiJobArtifact.Result, _lifetime.Token);
        _scenePlan = result?.ScenePlan;
        _reviewedSequence = request.Profile == ScriptSequenceDrafting.Profile;
        _sequenceReview = result?.SequenceReview;
        _completeSequenceDraft = result is { Complete: true, FinishReason: "stop", ScenePlan: not null };
        if (_sequenceReview is { State: ScriptSequenceReviewState.NeedsAttention, ValidationError: null } report &&
            !report.Findings.Any(f => f.Severity == "repair") && result?.Error is not null)
            _sequenceReview = report with { ValidationError = result.Error };
        _twoStepSequence = request.ScenePlanMessages is not null || _scenePlan is not null;
        _scenePlanning = result?.ScenePlanning == true || _twoStepSequence && _scenePlan is null;
        _requestModelName = request.SelectedModel?.Name ?? request.Model.Name;
        _planningPreviewRaw = _scenePlanning ? result?.Raw : null;
        _planningFinishReason = result?.FinishReason;
        _planningCorrection = _scenePlanning ? (await ScriptScenePlanApproval.ReadAsync(JobStore, header.Id, _lifetime.Token))?.Correction : null;
        if (_planningCorrection is not null) _planningFinishReason = "stop";
        _planningPreview = _scenePlanning && _planningFinishReason == "stop" && result is { Complete: true }
            && ScriptScenePlanning.Inspect(PlanningOutput) is { CanUse: true } inspection ? inspection.Plan : null;
        if (_planningPreview is not null && request.Payload<ScriptAssistantRequest>().Adaptation is { } adaptation)
            _planningPreview = lumibelle.Services.AI.ScriptScenePlanning.Ground(_planningPreview, adaptation);
        static string Transcript(IEnumerable<AiTextMessage> messages) => string.Join("\n\n", messages.Select(m => m.Role.ToUpperInvariant() + "\n" + string.Join("\n", m.Parts.Select(p => p.Text))));
        var draft = request.ScenePlanMessages is null ? null : await JobStore.ReadOperationAsync<AiTextJobRequest>(header.Id, "scene-draft", AiOperationArtifact.Request, _lifetime.Token);
        _requestDetails = "Captured model: " + System.Text.Json.JsonSerializer.Serialize(request.Model, AtomicJsonFile.Options) + "\nSelection: " + (request.SelectionSource?.ToString() ?? "Historical captured choice") + "\n\n" +
            (request.ScenePlanMessages is null ? "" : "SEQUENCE PLANNING INPUT\n" + Transcript(request.ScenePlanMessages) + "\n\nDRAFT INPUT\n") +
            Transcript(draft?.Messages ?? request.Messages);
        if (_reviewedSequence) {
            foreach (var operation in new[] { "sequence-review-0-0", "sequence-review-0-1", "sequence-repair-1", "sequence-review-1-0", "sequence-review-1-1" }) {
                var stage = await JobStore.ReadOperationAsync<AiTextJobRequest>(header.Id, operation, AiOperationArtifact.Request, _lifetime.Token);
                if (stage is not null) _requestDetails += "\n\n" + operation + " INPUT\nCaptured model: " +
                    System.Text.Json.JsonSerializer.Serialize(stage.Model, AtomicJsonFile.Options) + "\n" + Transcript(stage.Messages);
            }
        }
    }
    private string? _sourcePlanError;
    private bool _sourcePlanLoaded, _useSourceOutline, _draftOutlineScene, _redraftOutlineScene;
    private string _outlineFocus = "";
    private IReadOnlyList<SourceOutlineAct> OutlineActs => (_sourcePlan?.Reviewed ?? _sourcePlan?.Proposal)?.Acts ?? [];
    private int? SelectedOutlineAct => int.TryParse(_outlineFocus.Split(':')[0], out var act) && act >= 0 && act < OutlineActs.Count ? act : null;
    private string OutlineActValue => SelectedOutlineAct?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
    private void ChooseOutlineAct(string? value)
    {
        if (!int.TryParse(value, out var act) || act < 0 || act >= OutlineActs.Count) { _outlineFocus = ""; return; }
        var scenes = OutlineScenes.Where(s => s.Act == act).ToArray();
        var next = scenes.FirstOrDefault(s => !s.InScript);
        if (next.Title is null) next = scenes.FirstOrDefault();
        ChooseOutlineSequence(next.Title is null ? "" : $"{next.Act}:{next.Scene}");
    }
    private bool SelectedSequenceAdded => OutlineScenes.Any(s => s.InScript && _outlineFocus == $"{s.Act}:{s.Scene}");
    private void ChooseOutlineSequence(string? value)
    {
        _outlineFocus = value ?? "";
        if (_draftOutlineScene) _redraftOutlineScene = SelectedSequenceAdded;
    }
    private string ActionValue => _batchDrafting ? "draft-remaining" : _draftOutlineScene ? _redraftOutlineScene ? "redraft-sequence" : "outline-scene" : Operation.ToString();
    private static string RunAction(AssistantRun run) => run.SourceScene is { } scene ? $"{(ScriptAdaptation.IsRedraft(run) ? "Redraft" : "Draft")} sequence {scene.Act + 1}.{scene.Scene + 1}" : run.Operation.ToString();
    private AssistantRun? OutlineInspectionRun {
        get {
            if (OutlineSelection is not { Act: { } ai, Scene: { } si } choice) return null;
            var reference = new ScriptSourceScene(choice.RunId, choice.ReviewRevision, ai, si);
            var latest = _runs.Where(r => r.SourceScene == reference && ScriptAdaptation.IsRedraft(r) == _redraftOutlineScene).OrderByDescending(r => r.CreatedUtc).FirstOrDefault();
            return latest?.Id == _dismissedLatestId ? null : latest;
        }
    }
    private IEnumerable<(int Act, int Scene, string Title, bool InScript)> OutlineScenes => _sourcePlan is null ? [] :
        (_sourcePlan.Reviewed ?? _sourcePlan.Proposal)!.Acts.SelectMany((act, ai) => act.Scenes.Select((scene, si) =>
            (ai, si, scene.Title, ScriptAdaptation.InScript(Document).Contains(new(_sourcePlan.Id, _sourcePlan.ReviewRevision, ai, si)))));
    private ScriptOutlineSelection? OutlineSelection {
        get {
            if (!_useSourceOutline || _sourcePlan is null) return null;
            if (_batchDrafting && OutlineScenes.FirstOrDefault(s => !s.InScript) is { Title: not null } next)
                return new(_sourcePlan.Id, _sourcePlan.ReviewRevision, next.Act, next.Scene, DraftScene: true);
            var pair = _outlineFocus.Split(':');
            return pair.Length == 2 && int.TryParse(pair[0], out var ai) && int.TryParse(pair[1], out var si)
                ? new(_sourcePlan.Id, _sourcePlan.ReviewRevision, ai, si, _draftOutlineScene, _redraftOutlineScene)
                : new(_sourcePlan.Id, _sourcePlan.ReviewRevision, DraftScene: _draftOutlineScene);
        }
    }
    private string? RedraftError {
        get {
            if (!_redraftOutlineScene || OutlineSelection is not { Act: { } ai, Scene: { } si } choice) return null;
            try { ScriptAdaptation.SequenceTarget(Document, new(choice.RunId, choice.ReviewRevision, ai, si)); return null; }
            catch (WorkspaceStoreException e) { return e.Message; }
        }
    }
    private bool SourcePlanReady => !_draftOutlineScene || OutlineSelection is { Act: { } ai, Scene: { } si } &&
        (_redraftOutlineScene ? RedraftError is null : !ScriptAdaptation.InScript(Document).Contains(new(_sourcePlan!.Id, _sourcePlan.ReviewRevision, ai, si)));
    private ScriptAdaptationContext? OutlineContext => OutlineSelection is { } selection && _sourcePlan is not null
        ? ScriptAdaptation.Context(_sourcePlan, Document, selection) : null;
    private async Task RefreshSourcePlan()
    {
        try {
            var doc = await Sources.LoadAsync(ProjectId, _lifetime.Token);
            var plan = ScriptAdaptation.Accepted(doc);
            var becameAvailable = _sourcePlan is null && plan is not null;
            if (_sourcePlan?.Id != plan?.Id || _sourcePlan?.ReviewRevision != plan?.ReviewRevision) _outlineFocus = "";
            _sourcePlan = plan;
            _sourcePlanError = plan is null && doc.Runs.Count > 0 ? "Accept a current outline in Source to use it here." : null;
            if (!BatchActive && (!_sourcePlanLoaded || becameAvailable) && plan is not null) {
                UseSourceOutline(true);
            }
            if (!BatchActive && _activeJob is { } active && _runs.FirstOrDefault(r => r.JobId == active.Id)?.SourceScene is { } scene &&
                scene.RunId == plan?.Id && scene.ReviewRevision == plan.ReviewRevision) {
                _useSourceOutline = _draftOutlineScene = true;
                _redraftOutlineScene = _runs.First(r => r.JobId == active.Id).Operation == WritingOperation.Rewrite;
                _chosenOperation = null;
                _outlineFocus = $"{scene.Act}:{scene.Scene}";
            }
            if (plan is null) { _useSourceOutline = false; _batchDrafting = _draftOutlineScene = _redraftOutlineScene = false; }
            _sourcePlanLoaded = true;
        } catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { _sourcePlanError = e.Message; }
    }
    private void ChooseNextOutlineScene(ScriptSourceScene? justApplied = null)
    {
        var remaining = OutlineScenes.Where(s => !s.InScript && !(justApplied is { } applied && applied.RunId == _sourcePlan?.Id &&
            applied.ReviewRevision == _sourcePlan.ReviewRevision && applied.Act == s.Act && applied.Scene == s.Scene)).ToArray();
        _outlineFocus = remaining.Length > 0 ? $"{remaining[0].Act}:{remaining[0].Scene}" : "";
        if (remaining.Length == 0) _draftOutlineScene = false;
    }
    private void ChooseAction(string? action)
    {
        if (BatchActive) return;
        _batchDrafting = action == "draft-remaining";
        if (_batchDrafting) { _useSourceOutline = true; _draftOutlineScene = _redraftOutlineScene = false; _chosenOperation = null; return; }
        // Sequence navigation determines whether writing is added or replaced. Never
        // jump to a different sequence to make an incompatible action work.
        if (_draftOutlineScene && (action == "outline-scene" && SelectedSequenceAdded || action == "redraft-sequence" && !SelectedSequenceAdded)) return;
        var wasDraftingSequence = _draftOutlineScene;
        _redraftOutlineScene = action == "redraft-sequence";
        _draftOutlineScene = action is "outline-scene" or "redraft-sequence";
        if (_draftOutlineScene) {
            _chosenOperation = null;
            _useSourceOutline = true;
            if (_redraftOutlineScene) {
                if (!OutlineScenes.Any(s => s.InScript && _outlineFocus == $"{s.Act}:{s.Scene}")) {
                    var existing = OutlineScenes.Where(s => s.InScript && s.Act == SelectedOutlineAct).LastOrDefault();
                    if (existing.Title is null) existing = OutlineScenes.FirstOrDefault(s => s.InScript);
                    _outlineFocus = existing.Title is null ? "" : $"{existing.Act}:{existing.Scene}";
                }
            }
            else if ((_outlineFocus.Length == 0 || !SourcePlanReady) && (!HasScript || OutlineScenes.Any(s => s.InScript))) ChooseNextOutlineScene();
        } else if (Enum.TryParse<WritingOperation>(action, out var operation)) {
            _chosenOperation = operation;
            if (operation is WritingOperation.Draft or WritingOperation.Outline) _outlineFocus = "";
            if (wasDraftingSequence && operation is WritingOperation.Revise or WritingOperation.Rewrite)
                FocusSourceForBlocks(Selection is { } selected ? [selected.AnchorBlockId, selected.HeadBlockId] : [], clearWhenUnknown: true);
        }
    }
    private void FocusSourceForBlocks(IEnumerable<Guid> blockIds, bool clearWhenUnknown = false)
    {
        if (!_useSourceOutline || _sourcePlan is null) return;
        var ids = blockIds.ToHashSet();
        var links = Document.SourceSceneLinks.Where(l => l.Scene.RunId == _sourcePlan.Id &&
            l.Scene.ReviewRevision == _sourcePlan.ReviewRevision && l.BlockIds.Any(ids.Contains)).Select(l => l.Scene).Distinct().ToArray();
        if (links.Length == 1) _outlineFocus = $"{links[0].Act}:{links[0].Scene}";
        else if (links.Length > 1 || clearWhenUnknown) _outlineFocus = "";
    }
    private void FocusSourceForTarget(ScriptAssistantTarget? target)
    {
        if (_draftOutlineScene || Operation is not (WritingOperation.Revise or WritingOperation.Rewrite)) return;
        if (target is { Scope: ScriptScope.Scene, SectionId: { } scene }) FocusSourceForBlocks([scene]);
        else if (target is { Scope: ScriptScope.Passage, Passage: { } passage })
            FocusSourceForBlocks(target.PassageBlockIds ?? [passage.AnchorBlockId, passage.HeadBlockId]);
    }
    private void UseSourceOutline(bool use)
    {
        _useSourceOutline = use;
        if (!use) _batchDrafting = _draftOutlineScene = _redraftOutlineScene = false;
        else if (_chosenOperation is null or WritingOperation.Draft && (!HasScript || OutlineScenes.Any(s => s.InScript))) {
            _chosenOperation = null;
            _draftOutlineScene = true;
            if (_outlineFocus.Length == 0 || !SourcePlanReady) ChooseNextOutlineScene();
        }
        if (use) FocusSourceForTarget(_target);
    }
}
