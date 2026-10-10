using lumibelle.Models;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private ShotDocument? _deleteSnapshot;
    private Guid? _singleDeleteShotId;
    private Shot? SingleDeleteShot => _deleteSnapshot?.Shots.FirstOrDefault(s => s.Id == _singleDeleteShotId);
    private HashSet<Guid> _deleteShotIds = [];
    private bool _deleteShotsOpen, _deletingShots, _clearProductionSelections, _focusAfterShotDeletion;
    private string? _deleteShotsError;
    private long _deleteDraftVersion;
    private ElementReference _bulkDeleteButton, _addShotButton;
    private static string Counted(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
    private DialogOptions DeleteShotsOptions => new() { MaxWidth = _singleDeleteShotId is null ? MaxWidth.Large : MaxWidth.Small, FullWidth = true, BackdropClick = false, CloseOnEscapeKey = !_deletingShots };
    private int DeleteTakeCount => _deleteSnapshot?.Takes.Count(t => _deleteShotIds.Contains(t.ShotId)) ?? 0;
    private int DeleteProductionCount => _deleteSnapshot?.Shots.Count(s => _deleteShotIds.Contains(s.Id) && s.SelectedTakeId is not null) ?? 0;
    private string? DeleteActivityIssue => AiJobs.View.Jobs.Any(j => j.Kind is (AiJobKind.Video or AiJobKind.PromptComposition) && j.Target.ProjectId == Id && j.Target.ShotId is { } shot && _deleteShotIds.Contains(shot) &&
            (j.State is AiJobState.Waiting or AiJobState.Running || j.RemoteUnconfirmed))
        ? "A selected shot has an active production request. Wait for it to finish, or cancel it in AI activity before deleting." : null;

    private async Task OpenShotDeletion(Guid? single = null)
    {
        if (_deletingShots) return;
        _deletingShots = true; var project = Id;
        await _saveGate.WaitAsync();
        try
        {
            if (!await SaveLocked()) return;
            var latest = await Store.LoadAsync(project, _lifetime.Token);
            if (_disposed || project != Id) return;
            if (_dirty) throw new WorkspaceStoreException("Finish saving your shot edits before opening deletion.");
            if (!SameBaseline(latest)) throw new WorkspaceConflictException();
            _doc = latest; Baseline(latest); _deleteSnapshot = latest.Copy(); _deleteDraftVersion = _version;
            _deleteShotIds = single is { } id && latest.Shots.Any(s => s.Id == id) ? [id] : [];
            _singleDeleteShotId = single;
            _clearProductionSelections = false; _deleteShotsError = null; _deleteShotsOpen = true;
        }
        catch (Exception e) { _error = e.Message; }
        finally { _saveGate.Release(); _deletingShots = false; }
    }
    private void CloseShotDeletion(bool visible = false) { if (!_deletingShots) _deleteShotsOpen = visible; }
    private async Task ReviewLatestDeletion()
    {
        if (_deletingShots) return;
        _deletingShots = true; var project = Id;
        await _saveGate.WaitAsync();
        try
        {
            if (_dirty) throw new WorkspaceStoreException("Close this dialog and save or resolve your shot draft before reviewing deletion again.");
            var latest = await Store.LoadAsync(project, _lifetime.Token);
            if (_disposed || project != Id) return;
            if (_dirty) throw new WorkspaceStoreException("Close this dialog and save your shot edits before reviewing deletion again.");
            _doc = latest; Baseline(latest); _deleteSnapshot = latest.Copy(); _deleteDraftVersion = _version;
            _deleteShotIds.IntersectWith(latest.Shots.Select(s => s.Id));
            _clearProductionSelections = false; _deleteShotsError = null;
        }
        catch (Exception e) { _deleteShotsError = e.Message; }
        finally { _saveGate.Release(); _deletingShots = false; }
    }
    private async Task ConfirmShotDeletion()
    {
        if (_deletingShots || _deleteSnapshot is null || _deleteShotIds.Count == 0 || DeleteActivityIssue is not null || DeleteProductionCount > 0 && !_clearProductionSelections) return;
        var snapshot = _deleteSnapshot; var ids = _deleteShotIds.ToArray(); var clearProduction = _clearProductionSelections;
        _deletingShots = true; _deleteShotsError = null;
        await _saveGate.WaitAsync();
        try
        {
            if (_dirty || _deleteDraftVersion != _version) throw new WorkspaceConflictException();
            var position = _doc.Shots.FindIndex(s => s.Id == _selected);
            var saved = await Store.DeleteShotsAsync(snapshot.ProjectId, ids, snapshot.Revision, clearProduction, _lifetime.Token);
            if (_disposed || snapshot.ProjectId != Id) return;
            Remember(); _coverageDirty = false;
            _doc = saved; Baseline(saved); _dirty = false; _version++; _saveStatus = "Saved";
            if (!_doc.Shots.Any(s => s.Id == _selected)) _selected = _doc.Shots.Count == 0 ? null : _doc.Shots[Math.Clamp(position, 0, _doc.Shots.Count - 1)].Id;
            await SaveCoverageLocked();
            _deleteShotsOpen = false; _focusAfterShotDeletion = true; _error = null;
            Notify($"Deleted {Counted(ids.Length, "shot")}. {Counted(DeleteTakeCount, "saved take")} moved to Trash. Restore shot definitions with Undo or Recovery.");
        }
        catch (WorkspaceConflictException) { _deleteShotsError = "Shots or saved takes changed since you opened this dialog. Nothing was deleted. Review the latest list and confirm again."; }
        catch (Exception e) { _deleteShotsError = e.Message + " Your selection is retained; retry when ready."; }
        finally { _saveGate.Release(); _deletingShots = false; }
    }
}
