using lumibelle.Models;

namespace lumibelle.Services.Assets;

public static class QwenImage21Inputs
{
    public const long MaximumDecodedPixels = NativeImageInputs.MaximumDecodedPixels;
    public static Task<byte[]> PrepareAsync(Stream source, ImageCropRegion? crop, CancellationToken ct = default) =>
        NativeImageInputs.PrepareAsync(source, crop, ct, "Qwen");
}
