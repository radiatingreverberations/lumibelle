using lumibelle.Services.AI;

namespace lumibelle.Services.Assets;

public static class ImageAspectPolicy
{
    public const string FromImage1 = "From Image 1";

    public static bool IsSupported(string aspect, bool editing) =>
        ComfyReferenceImageGenerator.Sizes.ContainsKey(aspect) || editing && aspect == FromImage1;

    public static bool IsResolutionSupported(int resolution) => resolution is 1024 or 1440 or 2048 or 4096;

    // Resolution is a pixel budget; source proportions include the submitted crop.
    public static (int Width, int Height) Size(string aspect, int sourceWidth, int sourceHeight, int resolution = 1024)
    {
        if (!IsResolutionSupported(resolution)) throw new AiGenerationException("Choose a supported image resolution.");
        if (resolution == 4096)
        {
            var original = aspect == FromImage1 ? (Width: sourceWidth, Height: sourceHeight)
                : ComfyReferenceImageGenerator.Sizes.TryGetValue(aspect, out var dimensions) ? dimensions
                : throw new AiGenerationException("Choose a supported aspect ratio.");
            if (original.Width <= 0 || original.Height <= 0) throw new AiGenerationException("Choose Image 1 before following its aspect ratio.");
            var scale = Math.Min(resolution / Math.Sqrt((double)original.Width * original.Height), 16384.0 / Math.Max(original.Width, original.Height));
            return (Math.Max(32, (int)(original.Width * scale) / 32 * 32), Math.Max(32, (int)(original.Height * scale) / 32 * 32));
        }
        if (aspect == FromImage1) return QwenImage21Policy.Size(sourceWidth, sourceHeight, resolution);
        if (!ComfyReferenceImageGenerator.Sizes.TryGetValue(aspect, out var size)) throw new AiGenerationException("Choose a supported aspect ratio.");
        return resolution == 1024 ? size : QwenImage21Policy.Size(size.Width, size.Height, resolution);
    }

    public static (int Width, int Height) Size(string aspect, byte[]? source = null, int resolution = 1024)
    {
        if (aspect != FromImage1) return Size(aspect, 0, 0, resolution);
        if (source is null) throw new AiGenerationException("Choose Image 1 before following its aspect ratio.");
        var size = QwenImage21Policy.PngSize(source);
        return Size(aspect, size.Width, size.Height, resolution);
    }
}
