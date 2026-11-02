using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Story;
using MudBlazor;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    private AssetLibrary? _deleteAssetsSnapshot;
    private bool _deleteAllAssetsOpen, _deletingAllAssets, _deleteAssetsConflict, _focusEmptyLibrary, _focusAssetLibraryOptions;
    private long _deleteAssetsDraftVersion;
    private string? _deleteAssetsError;
    private string _deleteAssetsProgress = "";
    private string? AssetDeletionActivityIssue => _imageSubmittingAsset is not null || _enhancing || _finishingReview ||
        AiJobs.View.Jobs.Any(j => j.Target.ProjectId == Id && (j.Target.AssetId is not null || j.Kind == AiJobKind.AssetExtraction) &&
            (j.LocksTarget || j.RemoteUnconfirmed))
        ? "Finish or cancel active asset requests in AI activity before deleting the library." : null;

    private void CloseAllAssetDeletion(bool visible)
    {
        if (_deletingAllAssets) return;
        _deleteAllAssetsOpen = visible;
        if (!visible) { _deleteAssetsSnapshot = null; _focusAssetLibraryOptions = true; }
    }

    private async Task OpenAllAssetDeletionAsync()
    {
        if (_deletingAllAssets || _library is null) return;
        _deleteAllAssetsOpen = true; _deletingAllAssets = true; _deleteAssetsError = null; _deleteAssetsConflict = false;
        _deleteAssetsProgress = "Preparing the asset list…"; var project = Id;
        await _saveGate.WaitAsync();
        try
        {
            if (!await SavePendingLockedAsync()) throw new WorkspaceStoreException(_saveError ?? "Save your asset edits before deleting.");
            var saved = await AssetStore.LoadAsync(project);
            if (_disposed || project != Id) return;
            if (_dirty) throw new WorkspaceStoreException("Finish editing and save before reviewing deletion.");
            _library = saved; _savedLibrary = saved.Copy(); _deleteAssetsSnapshot = saved.Copy(); _deleteAssetsDraftVersion = _editVersion;
        }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { _deleteAssetsSnapshot = null; _deleteAssetsError = e.Message; }
        finally { _saveGate.Release(); _deletingAllAssets = false; }
    }

    private async Task ConfirmAllAssetDeletionAsync()
    {
        if (_deletingAllAssets || _deleteAssetsSnapshot is not { Assets.Count: > 0 } snapshot || _deleteAssetsConflict || AssetDeletionActivityIssue is not null) return;
        _deletingAllAssets = true; _deleteAssetsError = null; _deleteAssetsProgress = "Deleting assets and moving media to Trash…";
        var ids = snapshot.Assets.Select(a => a.Id).ToHashSet();
        await _saveGate.WaitAsync();
        try
        {
            if (AssetDeletionActivityIssue is { } activityIssue) throw new WorkspaceStoreException(activityIssue);
            if (_dirty || _editVersion != _deleteAssetsDraftVersion || _library?.Revision != snapshot.Revision) throw new WorkspaceConflictException();
            var saved = await AssetStore.DeleteAssetsAsync(snapshot.ProjectId, ids, snapshot.Revision);
            if (_disposed || snapshot.ProjectId != Id) return;
            ApplyStoredMutation(saved, _deleteAssetsDraftVersion, []);
            _library = _library! with { Assets = _library.Assets.Where(a => !ids.Contains(a.Id)).ToList() };
            foreach (var id in ids) { _imageDrafts.Remove(id); _pendingImages.Remove(id); }
            SwitchAsset(_library.Assets.FirstOrDefault()?.Id); _search = ""; _categoryFilter = null;
            _lookImageSelection.Clear(); _latestEditBatch = null;
            LoadGenerationDraft(); _deleteAllAssetsOpen = false; _deleteAssetsSnapshot = null; _focusEmptyLibrary = true;
            Snackbar.Add($"Deleted {ids.Count} assets. Saved images and voices are in Trash.", Severity.Success);
        }
        catch (WorkspaceConflictException)
        { _deleteAssetsConflict = true; _deleteAssetsError = "The library changed since you opened this dialog. Nothing was deleted. Review the latest assets and confirm again."; }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException)
        { _deleteAssetsError = e.Message + " Nothing was deleted; you can retry."; }
        finally { _saveGate.Release(); _deletingAllAssets = false; }
    }
}
