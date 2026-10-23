using lumibelle.Models;
using lumibelle.Services.Story;
using lumibelle.Services.AI;

namespace lumibelle.Components.Script;

public partial class ScriptAssistantPanel
{
    private readonly Dictionary<Guid, string> _jsonDrafts = [];
    private bool _editingJson, _savingJson;
    private string? _jsonError, _responseJsonError;
    private AssistantRun? _pendingJsonProposal;
    private string JsonDraft
    {
        get => _reviewId is { } id ? _jsonDrafts.GetValueOrDefault(id, "") : "";
        set
        {
            if (_reviewId is not { } id) return;
            if (_jsonDrafts.GetValueOrDefault(id) != value) _pendingJsonProposal = null;
            _jsonDrafts[id] = value; _jsonError = null;
        }
    }
    private static string ResponseSource(AssistantRun run) =>
        (run.ManuallyReviewed ? "Saved draft · manual review · " : run.CorrectedFromRunId is null ? "" : "Pasted JSON · based on ") + $"{run.Backend} · {run.Model}";

    private void EditJson()
    {
        if (ReviewRun is not { } run || run.Status == AssistantRunStatus.Running || run.Operation == WritingOperation.Discuss) return;
        var text = _scenePlanning ? PlanningOutput : run.Output;
        if (_reviewedSequence && !_scenePlanning) {
            try { text = System.Text.Json.JsonSerializer.Serialize(ScriptSequenceDrafting.Parse(text).Blocks, AtomicJsonFile.Options); }
            catch (WorkspaceStoreException) { /* Keep the invalid captured response editable. */ }
        }
        _jsonDrafts.TryAdd(run.Id, text);
        _editingJson = true; _jsonError = null;
    }
    private string? SavedResponseJsonError(string text)
    {
        if (!_reviewedSequence) return ScreenplayJson.Parse(text).Error;
        try { ScriptSequenceDrafting.Parse(text); return null; }
        catch (WorkspaceStoreException e) { return e.Message; }
    }
    private void CancelJsonEdit() { if (!_savingJson) { _editingJson = false; _jsonError = null; } }
    private static bool CanReprocessSavedResponse(AssistantRun run) => run.EditFormat == 1 &&
        run.Status == AssistantRunStatus.Completed && run.Operation != WritingOperation.Discuss &&
        !string.IsNullOrWhiteSpace(run.Output) && run.Error is
            "An insertion or move is anchored to another edited block." or "Multiple edits use the same insertion point.";
    // Do not promote cancelled, interrupted or output-limited streams merely because
    // the received prefix parses. Other failures retain the explicit JSON editor.
    private Task ReviewJsonAsync() => _scenePlanning ? SavePlanningJsonAsync() : SaveJsonProposalAsync(JsonDraft, false);
    private async Task SavePlanningJsonAsync()
    {
        if (_savingJson || _applying || _reviewId is not { } id) return;
        _savingJson = true; _jsonError = null;
        try {
            await ScriptScenePlanApproval.SaveCorrectionAsync(JobStore, id, JsonDraft, _lifetime.Token);
            if (Jobs.View.Jobs.FirstOrDefault(j => j.Id == id) is { } header) await LoadRequestDetailsAsync(header);
            _editingJson = false;
        }
        catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException) { _jsonError = e.Message; }
        finally { _savingJson = false; }
    }
    private Task ReprocessSavedResponseAsync() => ReviewRun is { } run && CanReprocessSavedResponse(run)
        ? SaveJsonProposalAsync(run.Output, true) : Task.CompletedTask;
    private async Task SaveJsonProposalAsync(string json, bool reprocessing, bool manualReview = false)
    {
        if (_savingJson || _applying || ReviewRun is not { } source) return;
        var project = ProjectId;
        _savingJson = true; _jsonError = _responseJsonError = null;
        try
        {
            // A previous failed save may belong to a manual JSON draft. Reprocessing
            // always reads the ORIGINAL response and never overwrites that local draft.
            if (_pendingJsonProposal is not { } pending || pending.CorrectedFromRunId != source.Id || pending.Output != json || pending.ManuallyReviewed != manualReview)
                _pendingJsonProposal = ScriptProposals.CorrectJson(source, json, Session.Id) with { ManuallyReviewed = manualReview };
            // A correction is a new local proposal. Never replace the job's raw output
            // or validated result, and retain the candidate identity on a save retry.
            var saved = await History.SaveRunAsync(project, _pendingJsonProposal, _lifetime.Token);
            if (_disposed || project != ProjectId || ReviewRun?.Id != source.Id) return;
            Put(saved); _dismissedLatestId = null; _error = null;
            await Reviews.CloseAsync(source.Id);
            _editingJson = false; _pendingJsonProposal = null;
            await ShowReviewAsync(saved);
        }
        catch (WorkspaceStoreException e)
        {
            if (reprocessing) _responseJsonError = e.Message;
            else _jsonError = e.Message;
        }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _savingJson = false; }
    }
}
