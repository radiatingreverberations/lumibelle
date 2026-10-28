using lumibelle.Models;
using lumibelle.Services.Assets;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    private VisualStyle _projectVisualStyle = new();
    private VisualStyle? _imageVisualStyle;
    private VisualStyle EffectiveImageStyle => VisualStyles.Resolve(_projectVisualStyle, SelectedAsset?.VisualStyle, _imageVisualStyle);
    private void ImageStyleChanged(VisualStyle? style) { _imageVisualStyle = style; RememberImageComposer(); }
}
