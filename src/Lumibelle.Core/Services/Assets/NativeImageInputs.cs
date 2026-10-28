using lumibelle.Models;
using lumibelle.Services.AI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace lumibelle.Services.Assets;

internal static class NativeImageInputs
{
    public const long MaximumDecodedPixels = 32_000_000;

    // Preserve source detail; the native conditioning node performs the requested resize.
    public static async Task<byte[]> PrepareAsync(Stream source, ImageCropRegion? crop, CancellationToken ct = default, string label = "Image")
    {
        try
        {
            using var encoded = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) != 0)
            {
                if (encoded.Length + read > FileAssetStore.MaximumImageBytes)
                    throw new AiGenerationException("Choose an image no larger than 25 MB.");
                await encoded.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            var bytes = encoded.ToArray();
            var info = Image.Identify(bytes);
            if (info is null || (long)info.Width * info.Height > MaximumDecodedPixels ||
                info.Metadata.DecodedImageFormat?.Name.ToUpperInvariant() is not ("PNG" or "JPEG" or "WEBP"))
                throw new AiGenerationException("Choose a readable PNG, JPEG or WebP with at most 32 million decoded pixels.");
            using var image = Image.Load<Rgba32>(new DecoderOptions { MaxFrames = 1 }, bytes);
            ct.ThrowIfCancellationRequested();
            image.Mutate(context => context.AutoOrient());
            if (crop is not null)
            {
                ComfyReferenceImageEditor.ValidateCrop(crop);
                image.Mutate(context => context.Crop(ImageGeometry.CropPixels(image.Width, image.Height, crop)));
            }
            using var output = new MemoryStream();
            await image.SaveAsPngAsync(output, new PngEncoder { SkipMetadata = true, BitDepth = PngBitDepth.Bit8, ColorType = PngColorType.RgbWithAlpha }, ct);
            if (output.Length is 0 or > FileAssetStore.MaximumImageBytes)
                throw new AiGenerationException($"The prepared {label} reference exceeds 25 MB. Crop or resize the source image before trying again.");
            return output.ToArray();
        }
        catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException or NotSupportedException or ArgumentException)
        { throw new AiGenerationException($"The {label} reference could not be decoded. Choose a valid PNG, JPEG or WebP."); }
    }
}
