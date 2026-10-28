using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using lumibelle.Services.Shots;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    private bool _focusLookFilter;
    private ElementReference _galleryLookFilter;
    private string? _lookNotice;
    private string _lookFilter = "all", _bulkLook = "";
    private Guid? _targetLookId, _promptAssetId, _promptLookId;
    private readonly Dictionary<(Guid Asset, Guid? Look), string> _lookPromptDrafts = [];
    private readonly HashSet<Guid> _lookImageSelection = [];
    private Guid? FilterLookId => Guid.TryParse(_lookFilter, out var id) ? id : null;
    private CharacterLook? TargetLook => SelectedAsset is { } asset ? LookPolicy.Find(asset, _targetLookId) : null;
    private string? LookIssue => _targetLookId is not null && TargetLook is null ? "The target look is missing. Choose another look." : TargetLook?.Archived == true ? "Unarchive the target look before generating." : null;
    private IEnumerable<AssetImage> LookImages(ReferenceAsset asset) => asset.Images.Where(i => _lookFilter == "all" || _lookFilter == "general" && i.LookId is null || FilterLookId is { } id && i.LookId == id).OrderByDescending(i => i.CreatedUtc).ThenBy(i => i.Id);
    private void RememberLookPrompt() { if ((!IsEditMode || _imageRecipeCreation) && _promptAssetId is { } asset) _lookPromptDrafts[(asset, _promptLookId)] = _imagePrompt; }
    private void RestoreLookPrompt()
    {
        if (SelectedAsset is not { } asset) return;
        _promptAssetId = asset.Id; _promptLookId = _targetLookId;
        _imagePrompt = _lookPromptDrafts.TryGetValue((asset.Id, _targetLookId), out var draft) ? draft : LookPolicy.SeedPrompt(asset, _targetLookId);
    }
    private void FilterLook(string value) => _lookFilter = value;
    private void UpdateLooks(ReferenceAsset asset) => EditSelected(current => current with { Looks = asset.Looks });
    private void UseGeneralImageDestination()
    {
        RememberLookPrompt();
        _targetLookId = null; _promptLookId = null;
        RememberMediaDraft();
    }
    private bool CanPrepareLookImages => !_composerLocked && !_selectionChanging && !_clearingCreation && _imageSubmittingAsset is null && !_saving;
    private void ManageLook(Guid lookId)
    {
        _lookManagementFilter = lookId.ToString();
        OpenAssetDetails();
    }
    private AssetImage? ApprovedLookIdentity(ReferenceAsset asset)
    {
        var approved = asset.Images.Where(i => i.IsReference && i.PendingReview != true &&
            LookPolicy.Find(asset, i.LookId)?.Archived != true && !ReferenceSetups.IsContinuity(asset.Id, i.Id, _library!)).ToArray();
        return asset.PreferredIdentityReferences.Select(p => approved.FirstOrDefault(i => i.Id == p.ImageId)).OfType<AssetImage>().FirstOrDefault()
            ?? approved.OrderBy(i => i.LookId is not null).ThenByDescending(i => i.IsCover).ThenByDescending(i => i.CreatedUtc).ThenBy(i => i.Id).FirstOrDefault();
    }
    private async Task CreateLookImages(Guid lookId)
    {
        if (!CanPrepareLookImages || SelectedAsset is not { Category: AssetCategory.Character } selected) return;
        var assetId = selected.Id;
        await TransitionTools(async () =>
        {
            if (SelectedAsset is not { } asset || asset.Id != assetId || LookPolicy.Find(asset, lookId) is not { Archived: false } look) return;
            var source = ApprovedLookIdentity(asset);
            if (_presentation.Selection is null && _presentation.Creation == AssetCreationKind.Image && _targetLookId == lookId &&
                (IsEditMode || source is null)) {
                if (_workspace is not null) await _workspace.ShowToolsAsync();
                return;
            }
            RememberLookPrompt(); RememberMediaDraft();
            var prompt = _lookPromptDrafts.GetValueOrDefault((asset.Id, look.Id)) ?? string.Join("\n\n", new[] {
                $"Create a reference image of {asset.Name} for the {look.Name} look.",
                source is null ? "" : "Use Image 1 to preserve the character's identity, facial features and proportions. Apply the requested look; the source outfit is not a requirement.",
                LookPolicy.SeedPrompt(asset, look.Id), asset.PreservationGuidance, look.PreservationGuidance
            }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var draft = CaptureImageComposer();
            ApplyImageComposer(draft with { Prompt = prompt, Look = look.Id, Source = source?.Id, SourceAsset = source is null ? null : asset.Id,
                CreateFromRecipe = true, References = [], Crops = [], Regions = [], Seed = "", Count = 1, Tags = "", Loras = null,
                Aspect = source is null && draft.Aspect == ImageAspectPolicy.FromImage1 ? "1:1" : draft.Aspect });
            _presentation.Selection = null; _presentation.Creation = AssetCreationKind.Image;
            _promptAssetId = asset.Id; _promptLookId = look.Id;
            _lookPromptDrafts[(asset.Id, look.Id)] = _imagePrompt;
            RememberMediaDraft(); _generationError = null; _creationClearError = null;
            await CheckImageWorkflowAsync();
            await RememberAssetPosition();
            if (_workspace is not null) await _workspace.ShowToolsAsync();
        });
        if (_targetLookId == lookId && _assetsModule is not null)
            await _assetsModule.InvokeVoidAsync("focusAssetCreationFields", "Image");
    }
    private void SelectLookImage(Guid id, ChangeEventArgs e) { if (e.Value is true) _lookImageSelection.Add(id); else _lookImageSelection.Remove(id); }
    private void AssignImageLook(Guid id, ChangeEventArgs e)
    {
        var look = Guid.TryParse(e.Value?.ToString(), out var value) ? (Guid?)value : null;
        if (SelectedAsset is { } asset && LookPolicy.Find(asset, look)?.Archived == true) return;
        EditImage(id, image => image with { LookId = look });
        _lookNotice = "Image look assignment updated.";
        _focusLookFilter = _lookFilter != "all" && look != FilterLookId;
    }
    private void AssignSelectedLooks()
    {
        var look = Guid.TryParse(_bulkLook, out var value) ? (Guid?)value : null;
        if (SelectedAsset is not { } asset || LookPolicy.Find(asset, look)?.Archived == true) return;
        EditSelected(a => a with { Images = a.Images.Select(i => _lookImageSelection.Contains(i.Id) ? i with { LookId = look } : i).ToList() });
        _lookImageSelection.Clear(); _focusLookFilter = true; _lookNotice = "Selected images assigned.";
    }
    private AssetLookContext? CaptureLook() => SelectedAsset is { } asset ? LookPolicy.Capture(asset, _targetLookId) : null;
    private AssetLookContext? ReferenceLook(AssetImageReference reference)
    {
        var asset = _library?.Assets.FirstOrDefault(a => a.Id == reference.AssetId);
        var image = asset?.Images.FirstOrDefault(i => i.Id == reference.ImageId);
        return asset is not null && image is not null ? LookPolicy.Capture(asset, image.LookId) : null;
    }
    private IReadOnlyList<AssetReferenceLook> CaptureReferenceLooks() => IsEditMode && BaseReference is { } source
        ? new[] { source }.Concat(_additionalReferences).Select(r => new AssetReferenceLook(r, ReferenceLook(r) ?? throw new WorkspaceStoreException("A reference is unavailable."))).ToArray() : [];
}
