using lumibelle.Components.Layout;
using lumibelle.Models;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    [Parameter] public Guid? RequestedImageId { get; set; }
    [Parameter] public bool EditRequestedImage { get; set; }
    private (Guid ProjectId, Guid ImageId, bool Edit)? _appliedImageRequest;
    private (Guid AssetId, AssetImage Image)? _copySource;
    private void OpenImageCopy(AssetImage image)
    {
        if (_generating || _enhancing || SelectedAsset is not { } asset) return;
        _copySource = (asset.Id, image);
    }
    private async Task<SavedAssetImage> SaveImageCopy(DerivedImageRequest request)
    {
        await _saveGate.WaitAsync();
        try
        {
            if (!await SavePendingLockedAsync() || _library is null) throw new WorkspaceStoreException(_saveError ?? "Save the current asset changes first.");
            var version = _editVersion;
            var result = await AssetStore.SaveDerivedImageAsync(Id, request, _library.Revision);
            _savedLibrary = result.Library.Copy();
            if (version == _editVersion) _library = result.Library;
            else
            {
                var saved = result.Library;
                _library = _library with { Revision = saved.Revision, UpdatedUtc = saved.UpdatedUtc, ImageCopyReceipts = saved.ImageCopyReceipts,
                    Trash = saved.Trash, Voices = saved.Voices, VoiceTrash = saved.VoiceTrash,
                    Assets = _library.Assets.Select(a => saved.Assets.FirstOrDefault(s => s.Id == a.Id) is { } stored ? a with
                        { Images = [.. a.Images, .. stored.Images.Where(i => a.Images.All(existing => existing.Id != i.Id))] } : a)
                        .Concat(saved.Assets.Where(a => _library.Assets.All(local => local.Id != a.Id))).ToList() };
            }
            _saveStatus = _dirty ? SaveStatusText.Unsaved : SaveStatusText.Saved;
            return result;
        }
        catch (WorkspaceConflictException e) { _conflict = true; _saveError = e.Message; throw; }
        finally { _saveGate.Release(); }
    }
    private void ImageCopySaved(SavedAssetImage result)
    {
        _copySource = null;
        Snackbar.Add("Cropped copy saved as an unapproved image.", MudBlazor.Severity.Success);
    }
    private async Task ApplyImageNavigationAsync()
    {
        if (RequestedImageId is null) _appliedImageRequest = null;
        if (_library is null || RequestedImageId is not { } imageId || _appliedImageRequest == (Id, imageId, EditRequestedImage)) return;
        var asset = RequestedAssetId is { } owner ? AssetImageLocations.RecordedOwner(_library, new(owner, imageId)) : null;
        if (asset is null) { _saveError = "The requested image is unavailable. Restore it from Trash if possible."; return; }
        if (SelectedAsset?.Id != asset.Id) { await SelectAssetAsync(asset.Id); if (SelectedAsset?.Id != asset.Id) return; }
        _appliedImageRequest = (Id, imageId, EditRequestedImage);
        _search = ""; _categoryFilter = null; RevealSelectedMedia();
        if (EditRequestedImage) await StartEdit(imageId); else await OpenPreview(imageId);
    }
}
