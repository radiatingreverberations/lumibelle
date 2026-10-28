using lumibelle.Models;
using lumibelle.Services.Shots;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Testing;

public static class MockFrameArchive
{
    public static async Task<byte[]> WebpAsync(int width, int height, int count, int first = 0, bool merged = false, bool lossy = false, CancellationToken ct = default, bool patterned = false)
    {
        using var image = new Image<Rgb24>(width, height, new Rgb24(50, 70, 100));
        if (patterned)
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
                image[x, y] = (x / 12 + y / 12) % 2 == 0 ? new Rgb24(220, 160, 90) : new Rgb24(30, 70, 120);
        for (var i = 0; i < (merged ? 1 : count); i++)
        {
            if (i > 0) image.Frames.AddFrame(image.Frames.RootFrame);
            var frame = image.Frames[i];
            frame[0, 0] = new Rgb24((byte)(first + i), (byte)((first + i) / 256), 200);
            frame.Metadata.GetWebpMetadata().FrameDelay = (uint)(LosslessFrameArchive.FrameMilliseconds * (merged ? count : 1));
        }
        using var output = new MemoryStream();
        await image.SaveAsync(output, new WebpEncoder { FileFormat = lossy ? WebpFileFormatType.Lossy : WebpFileFormatType.Lossless, Method = WebpEncodingMethod.Fastest, SkipMetadata = true }, ct);
        return output.ToArray();
    }

    public static async Task<List<ShotFrame>> WriteAsync(string directory, int count, int width, int height, CancellationToken ct, bool patterned = false)
    {
        List<ShotFrame> frames = [];
        for (var first = 0; first < count; first += LosslessFrameArchive.SegmentFrames)
        {
            var n = Math.Min(LosslessFrameArchive.SegmentFrames, count - first);
            var bytes = await WebpAsync(width, height, n, first, ct: ct, patterned: patterned);
            var file = LosslessFrameArchive.FileName(first / LosslessFrameArchive.SegmentFrames);
            await File.WriteAllBytesAsync(Path.Combine(directory, file), bytes, ct);
            for (var i = 0; i < n; i++) frames.Add(new(first + i, file, bytes.Length) { ArchiveFrameIndex = i });
        }
        return frames;
    }
}
