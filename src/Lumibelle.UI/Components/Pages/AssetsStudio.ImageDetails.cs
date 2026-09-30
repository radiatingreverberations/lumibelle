using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    private bool _imageDetailsDirty, _openImageDetails;
    private string? _pendingCenterTab;
    private bool _projectPickerOpen;
    private void AddProjectReferences(IReadOnlyList<AssetImageReference> references)
    {
        if (!CanAddReference || references.Count + _additionalReferences.Count + 1 > Math.Min(ReferenceLimit, _editConfiguration?.MaximumReferences ?? 1)) return;
        if (references.Distinct().Count() != references.Count || references.Any(r => !AvailableReferences.Contains(r))) { _generationError = "These references changed. Reopen the picker and review the available images."; return; }
        _additionalReferences.AddRange(references); _projectPickerOpen = false;
    }
    [Parameter] public string? RequestedView { get; set; }

    private async Task OpenDetails(Guid imageId) { _openImageDetails = true; await OpenPreview(imageId); }
    private async Task<string?> ApplyImageDetails(ImageMetadataEdit edit)
    {
        if (edit.ProjectId != Id) return "This project is no longer open. Your details were not changed.";
        await _saveGate.WaitAsync();
        try
        {
            if (!await SavePendingLockedAsync() || _library is null) return _saveError ?? "Save the asset changes first, then retry.";
            var baseline = _library.Copy();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                // Start with the store's current image membership, including arrivals, moves and Trash.
                var latest = await AssetStore.LoadAsync(Id);
                var asset = latest.Assets.SingleOrDefault(a => a.Id == edit.Reference.AssetId);
                if (asset is null) return "The original asset is no longer available. Your details are kept for copying.";
                var updated = edit.Apply(asset);
                var candidate = latest with { Assets = latest.Assets.Select(a => a.Id == asset.Id ? a with { Images = a.Images.Select(i => i.Id == updated.Id ? updated : i).ToList() } : a).ToList() };
                try
                {
                    var saved = await AssetStore.SaveAsync(candidate, latest.Revision);
                    _library = AssetLibraryRebase.Merge(baseline, _library, saved);
                    _savedLibrary = saved.Copy();
                    _saveStatus = _dirty ? "Unsaved" : "Saved";
                    return null;
                }
                catch (WorkspaceConflictException) when (attempt < 2) { /* Recheck each intended field against the next snapshot. */ }
            }
            return "The library keeps changing. Your details are kept; retry applying them.";
        }
        catch (Exception e) when (e is WorkspaceStoreException or InvalidOperationException or IOException) { return e.Message; }
        finally { _saveGate.Release(); }
    }

    private bool CanTrashSelection => SelectedAsset is { } asset && _lookImageSelection.Count > 0 &&
        asset.Images.Where(i => _lookImageSelection.Contains(i.Id)).All(i => !i.IsCover && !i.IsReference && i.Origin != AssetImageOrigin.Imported);
    private async Task TrashSelectedImages()
    {
        if (!CanTrashSelection || SelectedAsset is not { } asset || _library is null) return;
        var ids = _lookImageSelection.ToArray();
        await _saveGate.WaitAsync();
        try
        {
            if (!await SavePendingLockedAsync() || _library is null) return;
            var version = _editVersion;
            var result = await AssetStore.DeleteImagesAsync(Id, asset.Id, ids, _library.Revision);
            ApplyStoredMutation(result.Library, version, ids.Select(id => new AssetImageReference(asset.Id, id)).ToArray());
            if (_latestEditBatch?.AssetId == asset.Id) _latestEditBatch.HiddenTakeIds.UnionWith(ids);
            _lookImageSelection.Clear(); _focusLibrary = true;
            ClearTrashNotification();
            _trashSnackbar = Snackbar.Add($"{ids.Length} images moved to Trash.", MudBlazor.Severity.Success, options =>
            {
                options.Action = "Undo";
                options.OnClick = async _ => { foreach (var trashId in result.TrashIds) { var error = await RestoreReviewedAsync(trashId); if (error is not null) Snackbar.Add(error, MudBlazor.Severity.Error); } };
            });
        }
        catch (WorkspaceStoreException e) { _saveError = e.Message; }
        finally { _saveGate.Release(); }
    }
}
