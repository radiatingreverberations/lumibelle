using System.Text.Json;
using lumibelle.Components.Assets;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using lumibelle.Services.Production;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    private static string MediaCount(int count, string kind) => $"{count} {kind}{(count == 1 ? "" : "s")}";
    private string AssetCategoryFilterLabel => _categoryFilter is { } category ? $"Filter assets: {CategoryName(category)}" : "Filter assets";
    private MudBlazor.MudMenu? _assetCategoryMenu;
    private bool _assetCategoryMenuOpen;
    private async Task SetAssetCategory(AssetCategory? category) { _categoryFilter = category; await CloseAssetCategoryMenu(); await RememberAssetPosition(); }
    private async Task CloseAssetCategoryMenu()
    {
        if (_assetCategoryMenu is not null) await _assetCategoryMenu.CloseMenuAsync();
        if (_assetsModule is not null) await _assetsModule.InvokeVoidAsync("focusAssetCategoryFilter");
    }
    private Task AssetCategoryMenuKeyDown(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e) =>
        e.Key == "Escape" && _assetCategoryMenuOpen ? CloseAssetCategoryMenu() : Task.CompletedTask;
    private AssetsPresentation _presentation = new();
    private readonly Dictionary<Guid, AssetsPresentation> _presentations = [];
    private readonly Dictionary<(Guid Asset, Guid? Source), ImageComposerDraft> _mediaImageDrafts = [];
    private readonly object _reelDetailsSection = new(), _voiceToolsSection = new();
    private ReferenceReelsPanel? _reels;
    private VoiceReferencesPanel? _voices;
    private string _lookManagementFilter = "all";
    private void RegisterReelPanel(ReferenceReelsPanel panel) => _reels = panel;
    private void RegisterVoicePanel(VoiceReferencesPanel panel) => _voices = panel;
    private bool _assetDetailsOpen, _selectionChanging, _imageInputsOpen;
    private bool _clearingCreation;
    private string? _creationClearError;
    private bool CanClearCreation => SelectedAsset is not null && _presentation.Selection is null && !_selectionChanging && !_clearingCreation &&
        (_presentation.Creation == AssetCreationKind.Image ? !_composerLocked && _imageSubmittingAsset is null : _reels?.CanClearCreation == true);
    private async Task ClearCreationFields()
    {
        if (!CanClearCreation) return;
        _clearingCreation = true; _creationClearError = null;
        try
        {
            if (_presentation.Creation == AssetCreationKind.Reel) await _reels!.ClearCreationAsync();
            else
            {
                ApplyImageComposer(CaptureImageComposer() with { Prompt = "", Tags = "", Seed = "", Source = null, SourceAsset = null, CreateFromRecipe = false, Look = null, References = [], Crops = [], Regions = [] });
                _lookPromptDrafts[(_selectedAssetId!.Value, null)] = "";
                RestoreLookPrompt(); RememberMediaDraft(); _generationError = null;
            }
            if (_assetsModule is not null) await _assetsModule.InvokeVoidAsync("focusAssetCreationFields", _presentation.Creation.ToString());
        }
        catch (WorkspaceStoreException error) { _creationClearError = error.Message; }
        finally { _clearingCreation = false; }
    }
    private readonly SemaphoreSlim _toolTransitions = new(1, 1);
    private int _pendingToolTransitions;
    private string? _imageInputsBaseline;
    private Guid? _moveAssetId;
    private Guid _moveTarget;
    private bool _moveAfter, _ordering;
    private string? _orderError;
    private (Guid[] Before, Guid[] After)? _orderUndo;
    private bool ShowImageTools => _presentation.Selection?.Kind == AssetMediaKind.Image || !_presentation.Edit && _presentation.Creation == AssetCreationKind.Image;
    private bool ShowReelTools => !_presentation.Edit && _presentation.Creation == AssetCreationKind.Reel && SelectedAsset is { } a && ReferenceReels.Supports(a.Category);
    private IEnumerable<AssetGalleryItem> AllGalleryItems => SelectedAsset is { } a && _library is not null ? AssetGalleryItem.For(a, _library) : [];
    private IEnumerable<AssetGalleryItem> GalleryItems => AllGalleryItems.Where(i =>
        (_presentation.Filter == "All" || _presentation.Filter == i.Key.Kind + "s") &&
        (string.IsNullOrWhiteSpace(_presentation.Search) || i.Name.Contains(_presentation.Search, StringComparison.OrdinalIgnoreCase)) &&
        (i.Key.Kind == AssetMediaKind.Voice || _lookFilter == "all" || _lookFilter == "general" && i.LookId is null || i.LookId?.ToString() == _lookFilter));
    private string SelectedMediaName => AllGalleryItems.FirstOrDefault(i => i.Key == _presentation.Selection)?.Name ?? "Reference unavailable";
    private bool GalleryGrouped => SelectedAsset?.Category == AssetCategory.Character && _presentation.GroupByLook;
    private IEnumerable<AssetGalleryGroup> GalleryGroups => SelectedAsset is { } asset ? AssetGalleryGroup.For(asset, GalleryItems, GalleryGrouped) : [];
    private string SelectedMediaLook => AllGalleryItems.FirstOrDefault(i => i.Key == _presentation.Selection) is { } item
        ? item.Key.Kind == AssetMediaKind.Voice ? "Voice reference" : item.LookId is null ? "General" : SelectedAsset?.Looks.FirstOrDefault(l => l.Id == item.LookId)?.Name ?? "Unavailable look"
        : "General";
    private string ToolOperation => _presentation.Selection?.Kind switch
    {
        AssetMediaKind.Image => "Edit image",
        AssetMediaKind.Reel => "Edit reference reel",
        AssetMediaKind.Voice => "Edit voice reference",
        _ => _presentation.Creation == AssetCreationKind.Reel ? "Create reference reel" : "Create image"
    };
    private async Task ToggleGalleryGrouping() { _presentation.GroupByLook = !_presentation.GroupByLook; await RememberAssetPosition(); }
    private async Task ResetGalleryFilters() { _presentation.Filter = "All"; _presentation.Search = ""; _lookFilter = "all"; await RememberAssetPosition(); }
    private IReadOnlyList<AssetImageReference> CurrentImageInputs => BaseReference is { } source ? new[] { source }.Concat(_additionalReferences).ToArray() : [];
    private IReadOnlyList<AssetReferenceCrop> AllImageCrops => CurrentImageInputs.Select(r => new { r, Crop = r == BaseReference ? SourceCrop : ReferenceCrop(r) })
        .Where(c => c.Crop is not null).Select(c => new AssetReferenceCrop(c.r, c.Crop!)).ToArray();
    private string ImageInputsFingerprint() => JsonSerializer.Serialize(new { Asset = _selectedAssetId, Inputs = CurrentImageInputs, Crops = AllImageCrops, Regions = CurrentRegions, Aspect = ImageAspect, Resolution = CapturedImageResolution, Qwen = CaptureQwenOptions(), Workflow = _imageWorkflow });
    private void OpenAssetDetails() => _assetDetailsOpen = SelectedAsset is not null;
    private async Task CloseAssetDetails()
    {
        if (await SaveNowAsync()) _assetDetailsOpen = false;
    }
    private Task AssetDetailsKeyDown(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e) =>
        e.Key == "Escape" ? CloseAssetDetails() : Task.CompletedTask;
    private void RememberMediaDraft()
    {
        RememberImageComposer();
        if (_selectedAssetId is { } id && _imageDrafts.TryGetValue(id, out var draft)) _mediaImageDrafts[(id, _imageRecipeCreation ? null : _editSourceImageId)] = draft;
    }
    private async Task<bool> FlushSelectedTools(bool inTransition = false)
    {
        if (_clearingCreation || _selectionChanging && !inTransition || _imageSubmittingAsset is not null || _imageDetailsDirty || _movingImages) return false;
        if (_flushReelDraft is not null && !await _flushReelDraft()) return false;
        if (_voices is not null && !await _voices.FlushAsync()) return false;
        return await SaveNowAsync();
    }
    private async Task TransitionTools(Func<Task> transition)
    {
        _pendingToolTransitions++;
        _selectionChanging = true;
        StateHasChanged();
        await _toolTransitions.WaitAsync();
        try { if (!_disposed && await FlushSelectedTools(inTransition: true)) await transition(); }
        finally { _selectionChanging = --_pendingToolTransitions > 0; _toolTransitions.Release(); if (!_disposed) StateHasChanged(); }
    }
    private void RevealImageDraftSelection()
    {
        _presentation.Creation = AssetCreationKind.Image;
        _presentation.Selection = !_imageRecipeCreation && _editSourceImageId is { } source ? new(AssetMediaKind.Image, source) : null;
    }
    private void SwitchAsset(Guid? id)
    {
        RememberLookPrompt(); RememberMediaDraft();
        if (_selectedAssetId is { } old) { _presentation.LookFilter = _lookFilter; _presentations[old] = _presentation; }
        _flushReelDraft = null; _reels = null; _voices = null; _selectedAssetId = id;
        _presentation = id is { } next ? _presentations.GetValueOrDefault(next) ?? new() : new();
        if (SelectedAsset is not { } owner || !ReferenceReels.Supports(owner.Category)) _presentation.Creation = AssetCreationKind.Image;
        if (_presentation.Selection is { } selection && !AllGalleryItems.Any(i => i.Key == selection)) { _presentation.Selection = null; }
        _assetDetailsOpen = false; _galleryLookError = null; _creationClearError = null; LoadGenerationDraft(); _lookFilter = _presentation.LookFilter;
        if (_presentation.Selection is { Kind: AssetMediaKind.Image } image) PrepareImageSelection(image.Id);
        else if (!_presentation.Edit && id is { } assetId && _mediaImageDrafts.TryGetValue((assetId, null), out var draft)) ApplyImageComposer(draft);
    }
    private async Task RememberAssetPosition()
    {
        if (_selectedAssetId is { } id) { _presentation.LookFilter = _lookFilter; _presentations[id] = _presentation; }
        await Remember(Id, "assets", "asset", _selectedAssetId);
        await Remember(Id, "assets", "search", _search);
        await Remember(Id, "assets", "category", _categoryFilter);
        await Remember(Id, "assets", "presentations", _presentations.ToDictionary(p => p.Key, p => new AssetsPresentation {
            Selection = p.Value.Selection, Creation = p.Value.Creation,
            Filter = p.Value.Filter, Search = p.Value.Search, LookFilter = p.Value.LookFilter, GroupByLook = p.Value.GroupByLook }));
    }
    private async Task PreviewMedia(AssetGalleryItem item)
    {
        if (item.Image is { } image) await OpenPreview(image.Id);
        else await OpenMediaDetails(item.Key);
    }
    private string? _galleryLookError;
    private AssetGalleryItem? _lookChangeItem;
    private Guid _lookChangeOwner;
    private Guid? _lookChangeValue;
    private bool _changingLook;
    private void OpenGalleryLook(Guid ownerId, AssetGalleryItem item)
    {
        _lookChangeOwner = ownerId; _lookChangeItem = item; _lookChangeValue = item.LookId; _galleryLookError = null;
    }
    private async Task ApplyGalleryLook()
    {
        if (_changingLook || _lookChangeItem is not { } baseline) return;
        _changingLook = true;
        _galleryLookError = null;
        var ownerId = _lookChangeOwner; var look = _lookChangeValue; var applied = false;
        try
        {
            await TransitionTools(async () =>
            {
                if (SelectedAsset is not { Category: AssetCategory.Character } owner || owner.Id != ownerId ||
                    AllGalleryItems.FirstOrDefault(i => i.Key == baseline.Key) is not { } current)
                    throw new WorkspaceStoreException("This reference changed or is no longer available. Choose it again to change its look.");
                if (look is not null && owner.Looks.All(l => l.Id != look || l.Archived))
                    throw new WorkspaceStoreException("This look is no longer available. Choose an active look.");
                if (current.LookId != baseline.LookId && current.LookId != look)
                    throw new WorkspaceStoreException("This reference's look changed. Review the current look and choose again.");
                if (current.LookId == look) { applied = true; return; }
                if (current.Image is { } image)
                {
                    AssignImageLook(image.Id, new ChangeEventArgs { Value = look?.ToString() });
                    if (!await SaveNowAsync()) return;
                }
                else if (current.Reel is { } reel && _reels is not null) await _reels.AssignLookAsync(reel, look);
                _lookNotice = "Reference look updated.";
                _focusLookFilter = _lookFilter != "all" && look != FilterLookId;
                applied = true;
            });
            if (applied) _lookChangeItem = null;
            else _galleryLookError = _saveError ?? "Save the current editor's changes before changing this look.";
        }
        catch (WorkspaceStoreException error) { _galleryLookError = error.Message; }
        finally { _changingLook = false; }
    }
    private async Task SelectMedia(AssetMediaSelection key)
    {
        if (_presentation.Multiple) { if (key.Kind == AssetMediaKind.Image) { if (!_lookImageSelection.Add(key.Id)) _lookImageSelection.Remove(key.Id); } return; }
        if (!AllGalleryItems.Any(i => i.Key == key)) return;
        if (_presentation.Selection == key) { await EnterCreate(); return; }
        await TransitionTools(async () => {
            if (!AllGalleryItems.Any(i => i.Key == key)) return;
            RememberMediaDraft(); _reels?.ClearDetails(); _voices?.ClearEditing();
            if (key.Kind == AssetMediaKind.Image) PrepareImageSelection(key.Id);
            _presentation.Selection = key;
            if (_workspace is not null) await _workspace.ShowToolsAsync();
        });
    }
    private Task RenameMedia(AssetMediaSelection key) => OpenMediaDetails(key, rename: true);
    private async Task OpenMediaDetails(AssetMediaSelection key, bool rename = false)
    {
        if (key.Kind == AssetMediaKind.Image) { await OpenDetails(key.Id); return; }
        if (!await FlushSelectedTools()) return;
        if (AllGalleryItems.FirstOrDefault(i => i.Key == key) is not { } item) return;
        if (item.Reel is { } reel) _reels?.EditReel(reel, rename);
        else if (item.Voice is { } voice) _voices?.Edit(voice, rename);
    }
    private void PrepareImageSelection(Guid imageId)
    {
        if (SelectedAsset is not { } asset || asset.Images.SingleOrDefault(i => i.Id == imageId) is not { } image) return;
        // Restored selections also need a creation draft to return to on deselect.
        if (_editSourceImageId is null || _imageRecipeCreation) _mediaImageDrafts.TryAdd((asset.Id, null), CaptureImageComposer());
        if (_mediaImageDrafts.TryGetValue((asset.Id, imageId), out var draft)) { ApplyImageComposer(draft); return; }
        _additionalReferences.Clear(); _referenceCropSelections.Clear(); _restoredCrops.Clear();
        _regions.Clear();
        _imageRecipeCreation = false; _imageRecipeSourceAsset = null; _imageRecipeLoras = null;
        _editSourceImageId = imageId; _targetLookId = null; _imagePrompt = "";
        _imageTags = string.Join(", ", image.Tags); _aspect = ImageAspectPolicy.FromImage1; ResetSourceCrop(); _generationError = null;
    }
    private Task EnterCreate() => TransitionTools(() => {
        RememberMediaDraft(); _reels?.ClearDetails(); _voices?.ClearEditing();
        _presentation.Selection = null;
        if (_selectedAssetId is { } id && _mediaImageDrafts.TryGetValue((id, null), out var draft)) ApplyImageComposer(draft);
        else { _editSourceImageId = null; _targetLookId = null; _additionalReferences.Clear(); _referenceCropSelections.Clear(); _restoredCrops.Clear(); RestoreLookPrompt(); }
        return Task.CompletedTask;
    });
    private async Task ClearMediaSelection()
    {
        await EnterCreate();
        if (_presentation.Selection is null && _assetsModule is not null)
            await _assetsModule.InvokeVoidAsync("focusAssetCreation");
    }
    private Task ChangeCreation(ChangeEventArgs e) => TransitionTools(() => {
        if (Enum.TryParse<AssetCreationKind>(e.Value?.ToString(), out var kind)) {
            RememberMediaDraft(); _presentation.Creation = kind; _creationClearError = null;
            if (kind == AssetCreationKind.Image) {
                if (_selectedAssetId is { } id && _mediaImageDrafts.TryGetValue((id, null), out var draft)) ApplyImageComposer(draft);
                else UseCreateMode();
            }
        }
        return Task.CompletedTask;
    });
    private void RevealSelectedMedia() { _presentation.Filter = "All"; _presentation.Search = ""; _lookFilter = "all"; _revealMedia = _presentation.Selection?.Id; }
    private Guid? _revealMedia;
    private void ToggleMultiple() { _presentation.Multiple = !_presentation.Multiple; _lookImageSelection.Clear(); }
    private async Task ImportReel() { if (_reels is not null && await FlushSelectedTools()) await _reels.OpenImport(); }
    private async Task ImportVoice() { if (_voices is not null && await FlushSelectedTools()) { await EnterCreate(); _voices.OpenImport(); } }
    private async Task VaryReel(AssetReferenceReel reel) { if (_reels is not null && await FlushSelectedTools()) await _reels.Variation(reel); }
    private async Task TrashMedia(AssetGalleryItem item)
    {
        if (!await FlushSelectedTools()) return;
        if (item.Reel is { } reel && _reels is not null) await _reels.Trash(reel);
        else if (item.Voice is { } voice && _voices is not null) await _voices.Discard(voice);
        if (!AllGalleryItems.Any(i => i.Key == _presentation.Selection)) { _reels?.ClearDetails(); _voices?.ClearEditing(); _presentation.Selection = null; }
    }
    private void OpenImageInputs() { if (_composerLocked) return; _imageInputsBaseline = ImageInputsFingerprint(); _imageInputsOpen = true; }
    private Task ApplyImageInputs(ImageEditReferenceManager.ImageEditInputs input)
    {
        if (_composerLocked || _imageInputsBaseline != ImageInputsFingerprint()) throw new WorkspaceStoreException("The edit source or references changed while this manager was open. Close and reopen it to review the current inputs.");
        if (input.Images.Count == 0 || !_imageRecipeCreation && input.Images[0].AssetId != _selectedAssetId || input.Images.Any(i => FindImage(i) is null)) throw new WorkspaceStoreException("A reference is unavailable. Choose an available source and references.");
        if (input.Regions.Any(r => !input.Images.Contains(r.Source) || r.Source != input.Images[0] && r.Mode == RegionalEditMode.Edit))
            throw new WorkspaceStoreException("Only Image 1 can have an edit area. Review the selected image options.");
        RememberMediaDraft();
        _regions.Clear();
        foreach (var region in input.Regions)
            _regions[region.Source] = region.Copy() with { Context = input.Crops.FirstOrDefault(c => c.Reference == region.Source)?.Crop ?? new() { X = 0, Y = 0, Width = 1, Height = 1 } };
        _editSourceImageId = input.Images[0].ImageId; _imageRecipeSourceAsset = input.Images[0].AssetId; _additionalReferences.Clear(); _additionalReferences.AddRange(input.Images.Skip(1));
        ResetSourceCrop(); _referenceCropSelections.Clear(); _restoredCrops.Clear(); foreach (var c in input.Crops) _restoredCrops[c.Reference] = c.Crop;
        if (!_imageRecipeCreation) _presentation.Selection = new(AssetMediaKind.Image, _editSourceImageId.Value);
        RememberMediaDraft(); _imageInputsOpen = false;
        return Task.CompletedTask;
    }
    private async Task RenameAsset(Guid id) { await SelectAssetAsync(id); if (_selectedAssetId == id) { _focusAssetName = true; } }
    private bool _focusAssetName;
    private void OpenAssetMove(Guid id) { _moveAssetId = id; _moveTarget = _library?.Assets.FirstOrDefault(a => a.Id != id)?.Id ?? Guid.Empty; _moveAfter = false; _orderError = null; }
    private Task ConfirmAssetMove() => _moveAssetId is { } id ? MoveAsset(new(id, _moveTarget, _moveAfter)) : Task.CompletedTask;
    private async Task MoveAsset(AssetLibraryList.AssetMove move)
    {
        if (_ordering || !await FlushSelectedTools()) return;
        await _saveGate.WaitAsync(); _ordering = true;
        try {
            if (!await SavePendingLockedAsync() || _library is null) return;
            var before = _library.Assets.Select(a => a.Id).ToArray(); var order = before.ToList();
            if (move.Id == move.Target || !order.Remove(move.Id) || !order.Contains(move.Target)) return;
            order.Insert(order.IndexOf(move.Target) + (move.After ? 1 : 0), move.Id);
            if (order.SequenceEqual(before)) { _moveAssetId = null; return; }
            var baseline = _library.Copy(); var saved = await AssetStore.ReorderAssetsAsync(Id, order, _library.Revision);
            _library = AssetLibraryRebase.Merge(baseline, _library, saved); _savedLibrary = saved.Copy();
            _orderUndo = (before, order.ToArray()); _moveAssetId = null; _orderError = null;
        } catch (WorkspaceConflictException) {
            _orderError = _saveError = "The library order changed in another editor. Review the latest order before moving this asset.";
            _conflict = true; _saveStatus = "Conflict";
        } catch (WorkspaceStoreException e) { _orderError = _saveError = e.Message; }
        finally { _ordering = false; _saveGate.Release(); }
    }
    private async Task ReloadOrder()
    {
        await ReloadAsync(); _orderError = null;
        if (_library?.Assets.Any(a => a.Id == _moveAssetId) != true) _moveAssetId = null;
    }
    private async Task UndoAssetMove()
    {
        if (_ordering || _orderUndo is not { } undo || !await FlushSelectedTools()) return;
        await _saveGate.WaitAsync(); _ordering = true;
        try {
            if (!await SavePendingLockedAsync() || _library is null) return;
            var current = _library.Assets.Select(a => a.Id).ToArray(); var known = undo.After.ToHashSet();
            if (!current.Where(known.Contains).SequenceEqual(undo.After.Where(current.Contains))) throw new WorkspaceConflictException();
            var order = undo.Before.Where(current.Contains).Concat(current.Where(id => !known.Contains(id))).ToArray();
            var baseline = _library.Copy(); var saved = await AssetStore.ReorderAssetsAsync(Id, order, _library.Revision);
            _library = AssetLibraryRebase.Merge(baseline, _library, saved); _savedLibrary = saved.Copy(); _orderUndo = null;
        } catch (WorkspaceConflictException) { _conflict = true; _saveStatus = "Conflict"; _saveError = "The library order changed. Reload the saved order before undoing the move."; }
        catch (WorkspaceStoreException e) { _saveError = e.Message; }
        finally { _ordering = false; _saveGate.Release(); }
    }
}
