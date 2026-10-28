using System.Globalization;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    private bool _imageLorasOpen, _imageLoraDraftValid = true;
    private IReadOnlyList<LoraSelection> _imageLoraDraft = [];
    private readonly HashSet<string> _imageLoraTriggers = [];
    private int ActiveImageLoras => SelectedLoras.Count(l => l.Enabled && l.Strength != 0);
    private void RevealImageLoras()
    {
        if (_composerLocked || IsCodex || _settings is null) return;
        _imageLoraDraft = SelectedLoras.ToArray(); _imageLoraDraftValid = true;
        _imageLoraTriggers.Clear(); _imageLorasOpen = true;
    }
    private void StageImageLoraTrigger(string trigger) => _imageLoraTriggers.Add(trigger);
    private void ApplyImageLoras()
    {
        if (!_imageLoraDraftValid || _composerLocked || _checkingImages || !_visibilityReady) return;
        LorasChanged(_imageLoraDraft);
        foreach (var trigger in _imageLoraTriggers) InsertLoraTrigger(trigger);
        RememberMediaDraft(); _imageLorasOpen = false;
    }

    private int _imageResolution = 1024;
    private int _flux3Resolution = 1024;
    private int ImageResolution => IsQwen ? QwenResolution : IsFlux3 ? _flux3Resolution : _imageResolution;
    private int CapturedImageResolution => IsQwen || IsCodex ? 1024 : ImageResolution;
    private void RestoreImageResolution(int resolution, ImageWorkflow workflow)
    {
        if (workflow == ImageWorkflow.Flux3ImageKlein) _flux3Resolution = resolution;
        else _imageResolution = resolution;
    }
    private void ChangeImageResolution(ChangeEventArgs args)
    {
        if (_composerLocked) return;
        if (IsQwen) { ChangeQwenResolution(args); return; }
        if (int.TryParse(args.Value?.ToString(), out var resolution) && ImageWorkflowLimits.SupportsResolution(_imageWorkflow, resolution))
            RestoreImageResolution(resolution, _imageWorkflow);
        RememberImageComposer();
    }
    private string ImageOutputLabel
    {
        get
        {
            if (IsQwen) return QwenOutputLabel;
            try
            {
                var source = BaseReference is { } reference ? SourceImageSize(reference) : default;
                var size = ImageAspectPolicy.Size(ImageAspect, source.Width, source.Height, ImageResolution);
                return string.Create(CultureInfo.InvariantCulture, $"{size.Width} × {size.Height} · {(long)size.Width * size.Height / 1_000_000d:0.00} MP");
            }
            catch (AiGenerationException) { return "Choose an available source image"; }
        }
    }
}
