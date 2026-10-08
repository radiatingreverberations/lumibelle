using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Shots;

namespace Lumibelle.Testing;

public static class MockRefinementPackage
{
    // Writes what ComfyUI's stock SaveLatent produces: one tensor named latent_tensor plus the
    // empty format marker, with the prompt as metadata.
    public static async Task WriteLatentAsync(string path, long[] shape, CancellationToken ct, string dtype = "F32")
    {
        long count = 1; foreach (var n in shape) count *= n;
        var bytes = count * (dtype == "F32" ? 4 : 2);
        var header = new Dictionary<string, object>
        {
            ["__metadata__"] = new Dictionary<string, string> { ["prompt"] = "{\"1\":{\"class_type\":\"UNETLoader\"}}" },
            ["latent_tensor"] = new { dtype, shape, data_offsets = new[] { 0L, bytes } },
            ["latent_format_version_0"] = new { dtype = "F32", shape = new long[] { 0 }, data_offsets = new[] { bytes, bytes } }
        };
        var json = JsonSerializer.Serialize(header);
        var data = Encoding.UTF8.GetBytes(json.PadRight(json.Length + (8 - Encoding.UTF8.GetByteCount(json) % 8) % 8));
        await using var file = File.Create(path);
        var prefix = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(prefix, (ulong)data.Length);
        await file.WriteAsync(prefix, ct); await file.WriteAsync(data, ct); file.SetLength(8 + data.Length + bytes);
    }

    public static async Task<H3RefinementPackage> WriteAsync(string directory, VideoSnapshot snapshot, TakeRefinement? refinement, CancellationToken ct, Guid? id = null)
    {
        Directory.CreateDirectory(directory);
        var width = refinement?.Width ?? snapshot.Width; var height = refinement?.Height ?? snapshot.Height;
        var video = Path.Combine(directory, "latent-video.tmp"); var audio = Path.Combine(directory, "latent-audio.tmp");
        await WriteLatentAsync(video, RefinementPackages.VideoShape(snapshot.FrameCount, width, height), ct);
        await WriteLatentAsync(audio, RefinementPackages.AudioShape(snapshot.FrameCount), ct);
        var path = Path.Combine(directory, H3RefinementPackage.FileName);
        await RefinementPackages.ComposeAsync(video, audio, path, id ?? Guid.NewGuid(), width, height, snapshot.FrameCount, ct);
        File.Delete(video); File.Delete(audio);
        return await RefinementPackages.InspectAsync(path, snapshot, refinement, ct);
    }
}
