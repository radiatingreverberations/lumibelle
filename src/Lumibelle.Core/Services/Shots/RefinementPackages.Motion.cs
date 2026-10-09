using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static partial class RefinementPackages
{
    // Derived inputs only: the complete retained package is never edited or relabelled.
    public static async Task PrepareMotionAsync(string source, string destination, int rawEnd, int generationFrames, CancellationToken ct)
        => await PrepareMotionAsync(source, destination, rawEnd - H3Motion.ContextFrames, generationFrames, false, ct);
    public static async Task PrepareMotionAsync(string source, string destination, int rawStart, int generationFrames, bool leading, CancellationToken ct)
    {
        var (_, tensors, dataStart) = await ReadHeaderAsync(source, ct);
        Directory.CreateDirectory(destination);
        var context = H3Motion.ContextFrames;
        var rawEnd = rawStart + context;
        if (rawStart < 0 || rawStart % 17 != 0 || generationFrames % 17 != 5 || generationFrames > 362)
            throw new WorkspaceStoreException("This boundary cannot reuse saved motion latents.");
        foreach (var name in new[] { "video", "audio" }) {
            var tensor = tensors[name];
            var video = name == "video";
            var sourceTime = tensor.Shape[video ? 2 : 3];
            var start = video ? rawStart / 17 * 5 : H3Motion.AudioBoundary(rawStart);
            var count = video ? H3Motion.VideoSteps(context) : H3Motion.AudioBoundary(rawEnd) - start;
            var targetTime = video ? H3Motion.VideoSteps(generationFrames) : H3Motion.AudioBoundary(generationFrames);
            var element = tensor.Dtype == "F32" ? 4 : 2;
            var planes = video ? tensor.Shape[0] * tensor.Shape[1] : tensor.Shape[0] * tensor.Shape[1] * tensor.Shape[2];
            var stride = video ? tensor.Shape[3] * tensor.Shape[4] * element : element;
            if (start < 0 || start + count > sourceTime || count >= targetTime) throw new WorkspaceStoreException("The saved motion window does not fit the source tensors.");
            var shape = tensor.Shape.ToArray(); shape[video ? 2 : 3] = targetTime;
            var bytes = checked(planes * targetTime * stride);
            var header = new JsonObject { ["latent_tensor"] = Entry(tensor.Dtype, shape, 0, bytes), ["latent_format_version_0"] = Entry("F32", [0], bytes, bytes) };
            await using var input = File.OpenRead(source);
            await using var output = new FileStream(Path.Combine(destination, "motion-" + name + ".latent"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await WriteHeaderAsync(output, header, ct);
            var buffer = new byte[131072];
            for (long plane = 0; plane < planes; plane++) {
                if (leading) {
                    Array.Clear(buffer); long padding = (targetTime - count) * stride;
                    while (padding > 0) { var n = (int)Math.Min(buffer.Length, padding); await output.WriteAsync(buffer.AsMemory(0, n), ct); padding -= n; }
                }
                input.Position = dataStart + tensor.Start + (plane * sourceTime + start) * stride;
                long remaining = count * stride;
                while (remaining > 0) { var n = (int)Math.Min(buffer.Length, remaining); await input.ReadExactlyAsync(buffer.AsMemory(0, n), ct); await output.WriteAsync(buffer.AsMemory(0, n), ct); remaining -= n; }
                Array.Clear(buffer); remaining = leading ? 0 : (targetTime - count) * stride;
                while (remaining > 0) { var n = (int)Math.Min(buffer.Length, remaining); await output.WriteAsync(buffer.AsMemory(0, n), ct); remaining -= n; }
            }
            await output.FlushAsync(ct); output.Flush(true);
        }
        var audioLength = H3Motion.AudioBoundary(generationFrames);
        using var mask = new Image<Rgb24>(audioLength, 2);
        var held = H3Motion.AudioBoundary(context);
        for (var x = 0; x < audioLength; x++) if (leading ? x < audioLength - held : x >= held)
            for (var y = 0; y < 2; y++) mask[x, y] = new(255, 255, 255);
        await mask.SaveAsPngAsync(Path.Combine(destination, "motion-audio-mask.png"), ct);
    }
}
