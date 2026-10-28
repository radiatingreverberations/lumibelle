using System.Globalization;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    private bool IsQwen => _imageWorkflow == ImageWorkflow.QwenImage21;
    private QwenImage21Options _qwenOptions = new();
    private int QwenResolution => !IsEditMode && _qwenOptions.Resolution == 0 ? 1024 : _qwenOptions.Resolution;
    private bool IsQwenTurbo => IsQwen && QwenImage21Policy.Settings(_settings ?? new()).Variant == QwenImage21Variant.Turbo;
    private QwenImage21Options? CaptureQwenOptions() => IsQwen ? QwenImage21Policy.CaptureOptions(_settings ?? new(), _qwenOptions with { Resolution = QwenResolution }) : null;

    private void ChangeQwenResolution(ChangeEventArgs e)
    {
        if (_composerLocked) return;
        if (int.TryParse(e.Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            _qwenOptions = _qwenOptions with { Resolution = value };
        RememberImageComposer();
    }
    private void ChangeQwenSteps(ChangeEventArgs e)
    {
        if (_composerLocked) return;
        _qwenOptions = _qwenOptions with { Steps = int.TryParse(e.Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0 };
        RememberImageComposer();
    }
    private (int Width, int Height) QwenReferenceSize(AssetImageReference reference)
    {
        var size = SourceImageSize(reference);
        if (_regions.TryGetValue(reference, out var region))
        {
            var canvas = RegionalImageEdits.Canvas(region, ImageAspect, qwen: CaptureQwenOptions());
            return (canvas.CanvasWidth, canvas.CanvasHeight);
        }
        return QwenImage21Policy.Size(size.Width, size.Height, QwenResolution);
    }
    private (int Width, int Height) SourceImageSize(AssetImageReference reference)
    {
        var image = FindImage(reference) ?? throw new AiGenerationException("Choose an available reference image.");
        var crop = reference == BaseReference ? SourceCrop : ReferenceCrop(reference);
        var size = crop is null ? (image.Width, image.Height) :
            (ImageGeometry.CropPixels(image.Width, image.Height, crop).Width, ImageGeometry.CropPixels(image.Width, image.Height, crop).Height);
        return size;
    }
    private string? QwenOptionsIssue
    {
        get
        {
            if (!IsQwen) return null;
            try
            {
                QwenImage21Policy.ValidateOptions(CaptureQwenOptions()!, IsEditMode);
                if (IsEditMode)
                {
                    foreach (var reference in CurrentImageInputs) QwenReferenceSize(reference);
                    var source = SourceImageSize(BaseReference!);
                    QwenImage21Policy.EditSize(ImageAspect, source.Width, source.Height, CaptureQwenOptions()!);
                }
                else QwenImage21Policy.CreateSize(ImageAspect, QwenResolution);
                return null;
            }
            catch (AiGenerationException e) { return e.Message; }
        }
    }
    private string QwenOutputLabel
    {
        get
        {
            try
            {
                var source = BaseReference is { } reference ? SourceImageSize(reference) : default;
                var size = IsEditMode ? QwenImage21Policy.EditSize(ImageAspect, source.Width, source.Height, CaptureQwenOptions()!) : QwenImage21Policy.CreateSize(ImageAspect, QwenResolution);
                return FormattableString.Invariant($"{size.Width} × {size.Height} · {(long)size.Width * size.Height / 1_000_000d:0.00} MP");
            }
            catch (AiGenerationException) { return "Review the output size and references"; }
        }
    }
}
