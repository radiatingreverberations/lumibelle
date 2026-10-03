using lumibelle.Models;

namespace lumibelle.Services.AI;

/// <summary>Rough prompt tokens of attached images for a text model, for showing what references cost before composing.</summary>
public static class TextImageTokens
{
    // Hosted vision models scale an image to about this size and charge roughly one token per 750 pixels (Claude's published rule).
    private const int HostedLongSide = 1568;
    private const double HostedPixels = 1_150_000;
    private const double PixelsPerHostedToken = 750;

    public static int Estimate(TextModelReference model, AiSettings settings, IReadOnlyList<byte[]> images)
    {
        if (images.Count == 0) return 0;
        var sizes = ComfyTextVision.InspectSizes(images);
        if (model.Backend == AiBackend.ComfyUI)
        {
            // ComfyUI letterboxes several images to one shared canvas, as the requests do.
            if (sizes.Count > 1) sizes = Enumerable.Repeat(ComfyTextVision.BatchCanvas(sizes, ComfyTextSettings.BatchImageSide(model, settings)), sizes.Count).ToArray();
            return sizes.Sum(size => ComfyTextCapacity.ImageTokens(model.Model, size.Width, size.Height));
        }
        return sizes.Sum(size => Hosted(size.Width, size.Height));
    }

    internal static int Hosted(int width, int height)
    {
        var scale = Math.Min(1, Math.Min(HostedLongSide / (double)Math.Max(width, height), Math.Sqrt(HostedPixels / ((double)width * height))));
        return (int)Math.Ceiling(width * scale * (height * scale) / PixelsPerHostedToken);
    }
}
