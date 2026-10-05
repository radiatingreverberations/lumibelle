using lumibelle.Models;
using lumibelle.Services.Assets;
using Microsoft.JSInterop;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    private bool _imageRecipeCreation;
    private Guid? _imageRecipeSourceAsset;
    private IReadOnlyList<LoraSelection>? _imageRecipeLoras;
    private AssetImage? SelectedRecipeImage => _presentation.Selection is { Kind: AssetMediaKind.Image } selected
        ? SelectedAsset?.Images.FirstOrDefault(i => i.Id == selected.Id) : null;
    private bool CanCreateSimilarImage => CanCreateSimilar(SelectedRecipeImage);
    private bool CanCreateSimilar(AssetImage? image) => image?.Generation is not null && !_composerLocked && !_selectionChanging &&
        !_clearingCreation && _imageSubmittingAsset is null;
    private AssetImage? GalleryImage(Guid imageId) => SelectedAsset?.Images.FirstOrDefault(i => i.Id == imageId);

    private Task CreateSimilarImage() => CreateSimilarImage(SelectedRecipeImage);

    // From a card menu or the details dialog, pending tool edits are saved first, as other card actions do.
    private async Task CreateSimilarFromGallery(Guid imageId)
    {
        if (CanCreateSimilar(GalleryImage(imageId)) && await FlushSelectedTools()) await CreateSimilarImage(GalleryImage(imageId));
    }
    private async Task CreateSimilarFromReview(Guid imageId)
    {
        if (!CanCreateSimilar(GalleryImage(imageId))) return;
        await ClosePreview();
        await CreateSimilarFromGallery(imageId);
    }

    private async Task CreateSimilarImage(AssetImage? image)
    {
        if (!CanCreateSimilar(image) || image is not { Generation: { } recipe }) return;
        await TransitionTools(async () =>
        {
            RememberMediaDraft();
            var edit = recipe.Edit;
            var crops = edit?.ReferenceCrops.ToList() ?? [];
            if (edit?.SourceCrop is { } crop) crops.Insert(0, new(new(edit.SourceAssetId, edit.SourceImageId), crop));
            // Keep the original inputs, including missing ones, so validation can explain what needs restoring.
            var draft = new ImageComposerDraft(recipe.Workflow, recipe.Prompt, string.Join(", ", image.Tags), recipe.AspectRatio,
                recipe.Workflow == ImageWorkflow.CodexImages ? "" : recipe.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                1, edit?.SourceImageId, recipe.Look is { } look && look.AssetId == _selectedAssetId ? look.LookId : null,
                edit?.References.Where(r => r != new AssetImageReference(edit.SourceAssetId, edit.SourceImageId)).ToArray() ?? [], crops,
                edit?.ReferenceBoost ?? 4, edit?.BaseReferenceBoost ?? 1, edit?.GroundingPixels ?? 768, edit?.Regions,
                recipe.QwenImage21?.Options, true, edit?.SourceAssetId,
                recipe.Loras.Select(l => new LoraSelection(l.Reference, l.Strength)).ToArray(), recipe.Resolution);
            ApplyImageComposer(draft);
            _presentation.Selection = null; _presentation.Creation = AssetCreationKind.Image;
            _lookPromptDrafts[(_selectedAssetId!.Value, _targetLookId)] = _imagePrompt;
            RestoreLookPrompt(); RememberMediaDraft(); _generationError = null; _creationClearError = null;
            await CheckImageWorkflowAsync();
            await RememberAssetPosition();
            if (_workspace is not null) await _workspace.ShowToolsAsync();
        });
        if (_presentation.Selection is null && _assetsModule is not null)
            await _assetsModule.InvokeVoidAsync("focusAssetCreationFields", "Image");
    }
}
