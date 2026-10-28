using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    [Parameter] public Guid? RequestedJobId { get; set; }
    [Inject] public IPromptEnhancer PromptEnhancer { get; set; } = null!;
    private string? _freshComposer;
    // Asset switching remounts the panel, but a review link belongs to this page visit.
    private Guid? _handledEnhancementNavigation;
    private Guid? RequestedEnhancementJobId => RequestedJobId == _handledEnhancementNavigation ? null : RequestedJobId;
    private readonly Dictionary<AssetImageReference, ImageCropRegion> _restoredCrops = [];

    private void HandleEnhancementNavigation(Guid id)
    {
        _handledEnhancementNavigation = id;
        if (RequestedJobId != id) return;
        // The review link is a one-time action. Keeping it in the URL also keeps
        // it in Settings' return link and replays it when this page is recreated.
        var url = Navigation.GetUriWithQueryParameter("jobId", (string?)null);
        if (url != Navigation.Uri) Navigation.NavigateTo(url, replace: true);
    }

    private async Task<bool> RestoreEnhancementSetup(PromptEnhancementContext captured, bool automatic)
    {
        if (_disposed || _generating || captured.ProjectId != Id || captured.AssetId != _selectedAssetId) return false;
        if (captured.IsEdit && (captured.References.Count == 0 || captured.References[0].Image.AssetId != captured.AssetId)) return false;
        var before = EnhancementContext().Fingerprint();
        if (before == captured.Fingerprint()) return true;
        if (automatic && (_freshComposer != before || _lookPromptDrafts.ContainsKey((captured.AssetId, captured.TargetLook?.LookId)))) return false;
        try { await PromptEnhancer.ValidateInputsAsync(captured); }
        catch (Exception e) when (e is AiGenerationException or WorkspaceStoreException) { _generationError = e.Message; return false; }
        if (_disposed || _generating || before != EnhancementContext().Fingerprint()) return false;
        // Only composer controls are restored. Current library metadata and LoRA choices
        // still participate in the stale-result check and are never rewritten by review.
        RememberLookPrompt();
        _resolveImageDefault = false;
        _imageWorkflow = captured.Workflow; _imagePrompt = captured.Prompt; _aspect = captured.AspectRatio; _imageVisualStyle = captured.VisualStyle ?? new();
        RestoreImageResolution(captured.Resolution, captured.Workflow); _qwenOptions = captured.QwenImage21 ?? new();
        _targetLookId = captured.TargetLook?.LookId;
        _regions.Clear(); foreach (var reference in captured.References) if (reference.Region is { } region) _regions[reference.Image] = region;
        _editSourceImageId = captured.IsEdit ? captured.References[0].Image.ImageId : null;
        _additionalReferences.Clear(); _additionalReferences.AddRange(captured.References.Skip(1).Select(r => r.Image));
        _referenceCropSelections.Clear(); _restoredCrops.Clear(); ResetSourceCrop();
        foreach (var reference in captured.References)
            if (reference.Crop is { } crop) _restoredCrops[reference.Image] = crop with { };
        _freshComposer = null; _showGenerator = true; RevealImageDraftSelection();
        await CheckImageWorkflowAsync(); StateHasChanged(); return true;
    }

    private CropSelection RestoreCropControls(AssetImageReference reference, ImageCropRegion crop)
    {
        var image = FindImage(reference)!;
        var targetRatio = image.Width * crop.Width / (image.Height * crop.Height);
        var aspect = CropAspectRatios.MinBy(a => Math.Abs(Math.Log((a == "Original" ? image.Width / (double)image.Height : AspectValue(a)) / targetRatio)))!;
        var maximum = CropRegion(image, aspect, 1, 50, 50) ?? new ImageCropRegion();
        return new(aspect, maximum.Width / crop.Width, crop.Width >= 1 ? 50 : crop.X / (1 - crop.Width) * 100,
            crop.Height >= 1 ? 50 : crop.Y / (1 - crop.Height) * 100);
    }
}
