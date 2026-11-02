using lumibelle.Components.Layout;
using lumibelle.Models;
using lumibelle.Services.Story;
using MudBlazor;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    private ReferenceAsset? _moveSource;
    private IReadOnlyList<AssetImage> _moveImages = [];
    private bool _movingImages, _focusMovedEdit;
    private void OpenImageMove(IEnumerable<Guid> ids)
    {
        if (_composerLocked || SelectedAsset is not { } asset) return;
        var selected = ids.ToHashSet();
        _moveImages = asset.Images.Where(i => selected.Contains(i.Id)).ToArray();
        if (_moveImages.Count > 0) _moveSource = asset;
    }
    private void CloseImageMove() { if (!_movingImages) _moveSource = null; }
    private async Task<SavedAssetImage> MoveImages(ImageDestination destination)
    {
        var project = Id;
        var source = _moveSource ?? throw new WorkspaceStoreException("Choose images to move.");
        var ids = _moveImages.Select(i => i.Id).ToArray();
        _movingImages = true;
        await _saveGate.WaitAsync();
        try
        {
            if (!await SavePendingLockedAsync() || _library is null) throw new WorkspaceStoreException(_saveError ?? "Save your asset changes first.");
            var version = _editVersion;
            var saved = await AssetStore.MoveImagesAsync(project, source.Id, ids, destination, _library.Revision);
            if (_disposed || project != Id) return new(saved, destination.AssetId, ids[0], true);
            ApplyStoredMutation(saved, version, ids.Select(id => new AssetImageReference(source.Id, id)).ToArray());
            // Do not resurrect removed defaults after an in-flight metadata edit.
            _library = _library! with { Assets = _library.Assets.Select(a => a.Id == source.Id ? a with
            {
                PreferredIdentityReferences = a.PreferredIdentityReferences.Where(r => !ids.Contains(r.ImageId)).ToArray(),
                Looks = a.Looks.Select(l => l with { PreferredAppearanceReferences = l.PreferredAppearanceReferences.Where(r => !ids.Contains(r.ImageId)).ToArray() }).ToArray()
            } : a).ToList() };
            _lookImageSelection.Clear();
            return new(saved, destination.AssetId, ids[0], true);
        }
        catch (WorkspaceConflictException e) { _conflict = true; _saveError = e.Message; _saveStatus = SaveStatusText.Conflict; throw; }
        finally { _movingImages = false; _saveGate.Release(); }
    }
    private async Task FinishImageMove(SavedAssetImage result, bool edit)
    {
        if (_disposed || result.Library.ProjectId != Id) return;
        _moveSource = null;
        Snackbar.Add($"{_moveImages.Count} image{(_moveImages.Count == 1 ? "" : "s")} moved.", Severity.Success);
        await SelectAssetAsync(result.AssetId);
        if (edit) { await StartEdit(result.ImageId); _focusMovedEdit = true; }
        else _focusLibrary = true;
    }
}
