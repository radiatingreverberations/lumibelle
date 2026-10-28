namespace lumibelle.Models;

public enum VisualStylePreset { Unspecified, Photographic, Anime, Illustration, ThreeDimensional, Custom }

public sealed record VisualStyle
{
    public VisualStylePreset Preset { get; init; }
    public string Direction { get; init; } = "";
}
