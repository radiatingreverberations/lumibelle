using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Script;

public partial class ScriptAssistantPanel
{
    [Inject] public ScriptDraftWorkflow DraftWorkflow { get; set; } = null!;
    private ScriptDraftBatch? _batch;
    private bool _batchDrafting, _batchBusy;
    private TextModelSelectionSource _selectionSource;
    private bool BatchActive => _batch?.Active == true;
    private bool ComposerLocked => _generating || _applying || BatchActive;
    private AiJobHeader? BatchJob => _batch?.Pending is { } pending ? Jobs.View.Jobs.FirstOrDefault(j => j.Id == pending.Id) : null;
    private string BatchStatus => _batch?.State switch {
        ScriptDraftBatchState.Running => "Drafting remaining sequences",
        ScriptDraftBatchState.Paused => "Batch paused",
        ScriptDraftBatchState.Completed => "Batch complete",
        _ => "Batch stopped"
    };
    private void BatchChanged(Guid project) { if (project == ProjectId && !_disposed) _ = InvokeAsync(RefreshBatchAsync); }
    private async Task RefreshBatchAsync()
    {
        try {
            var wasActive = BatchActive;
            _batch = (await DraftWorkflow.LoadAsync(ProjectId, _lifetime.Token)).Runs.LastOrDefault();
            if (BatchActive) { _batchDrafting = _useSourceOutline = true; _draftOutlineScene = _redraftOutlineScene = false; _instructions = _batch!.Instructions; }
            else if (wasActive && _batch?.State == ScriptDraftBatchState.Completed) { _batchDrafting = false; _chosenOperation = null; }
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { _error = e.Message; }
        if (!_disposed) StateHasChanged();
    }
    private async Task BatchCommandAsync(string action)
    {
        if (_batch is null || _batchBusy) return;
        _batchBusy = true;
        try {
            _error = null;
            var doc = action switch {
                "pause" => await DraftWorkflow.PauseAsync(ProjectId, _batch.Id, _lifetime.Token),
                "resume" => await DraftWorkflow.ResumeAsync(ProjectId, _batch.Id, _lifetime.Token),
                _ => await DraftWorkflow.StopAsync(ProjectId, _batch.Id, _lifetime.Token)
            };
            _batch = doc.Runs.Last();
        }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or AiGenerationException) { _error = e.Message; }
        finally { _batchBusy = false; }
    }
}
