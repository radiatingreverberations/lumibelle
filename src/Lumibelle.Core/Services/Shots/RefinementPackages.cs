using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

// A take's refinement package holds its video and audio latents as two SafeTensors tensors,
// "video" and "audio". ComfyUI's stock SaveLatent writes each as a separate .latent file
// (tensor "latent_tensor"); Lumibelle combines them after download and splits them again
// for LoadLatent. Only sizes are kept as metadata, so the package holds no prompt or settings.
public static class RefinementPackages
{
    public const long MaximumBytes = 8L * 1024 * 1024 * 1024;
    private const int Version = 2;
    private sealed record Tensor(string Dtype, long[] Shape, long Start, long End);

    public static long[] VideoShape(int frames, int width, int height) => [1, 24, (frames - 5) / 17 * 5 + 2, height / 16, width / 16];
    public static long[] AudioShape(int frames) => [1, 32, 2, (long)Math.Round(frames / 24d * 40)];

    // Combines the two downloaded .latent files into one package.
    public static async Task ComposeAsync(string videoLatent, string audioLatent, string output, Guid id, int width, int height, int frames, CancellationToken ct)
    {
        var video = await LatentTensorAsync(videoLatent, ct); var audio = await LatentTensorAsync(audioLatent, ct);
        var videoBytes = video.End - video.Start; var audioBytes = audio.End - audio.Start;
        var header = new JsonObject
        {
            ["__metadata__"] = new JsonObject { ["lumibelle"] = JsonSerializer.Serialize(new { version = Version, id = id.ToString("D"), fps = 24, width, height, frameCount = frames }) },
            ["video"] = Entry(video.Dtype, video.Shape, 0, videoBytes),
            ["audio"] = Entry(audio.Dtype, audio.Shape, videoBytes, videoBytes + audioBytes)
        };
        await using (var destination = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
        {
            await WriteHeaderAsync(destination, header, ct);
            await CopyRangeAsync(videoLatent, video, destination, ct);
            await CopyRangeAsync(audioLatent, audio, destination, ct);
            await destination.FlushAsync(ct); destination.Flush(true);
        }
    }

    // Writes the package's tensors as the .latent files ComfyUI's LoadLatent reads.
    public static async Task SplitAsync(string package, string videoLatent, string audioLatent, CancellationToken ct)
    {
        var (_, tensors, dataStart) = await ReadHeaderAsync(package, ct);
        foreach (var (name, path) in new[] { ("video", videoLatent), ("audio", audioLatent) })
        {
            var tensor = tensors[name]; var bytes = tensor.End - tensor.Start;
            var header = new JsonObject
            {
                ["latent_tensor"] = Entry(tensor.Dtype, tensor.Shape, 0, bytes),
                // LoadLatent treats files without this marker as an older format and rescales them.
                ["latent_format_version_0"] = Entry("F32", [0], bytes, bytes)
            };
            await using var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
            await WriteHeaderAsync(destination, header, ct);
            await CopyRangeAsync(package, tensor with { Start = dataStart + tensor.Start, End = dataStart + tensor.End }, destination, ct);
        }
    }

    public static async Task<H3RefinementPackage> InspectAsync(string path, VideoSnapshot snapshot, TakeRefinement? refinement, CancellationToken ct)
    {
        try
        {
            var (metadata, tensors, _) = await ReadHeaderAsync(path, ct);
            var w = refinement?.Width ?? snapshot.Width; var h = refinement?.Height ?? snapshot.Height;
            using var parsed = JsonDocument.Parse(metadata ?? throw new InvalidDataException("Missing package metadata."));
            var m = parsed.RootElement;
            if (m.GetProperty("version").GetInt32() != Version || m.GetProperty("fps").GetInt32() != 24 ||
                m.GetProperty("width").GetInt32() != w || m.GetProperty("height").GetInt32() != h || m.GetProperty("frameCount").GetInt32() != snapshot.FrameCount ||
                !Guid.TryParse(m.GetProperty("id").GetString(), out var id) || id == Guid.Empty)
                throw new InvalidDataException("Package sizes do not match the captured take.");
            if (tensors.Count != 2 || !tensors.TryGetValue("video", out var video) || !tensors.TryGetValue("audio", out var audio) ||
                !video.Shape.SequenceEqual(VideoShape(snapshot.FrameCount, w, h)) || !audio.Shape.SequenceEqual(AudioShape(snapshot.FrameCount)) ||
                video.Dtype is not ("F16" or "BF16" or "F32") || audio.Dtype is not ("F16" or "BF16" or "F32"))
                throw new InvalidDataException("Latent shape or dtype mismatch.");
            await using var file = File.OpenRead(path);
            return new(id, file.Length, Convert.ToHexString(await SHA256.HashDataAsync(file, ct)), w, h, snapshot.FrameCount);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or InvalidDataException or EndOfStreamException)
        { throw new WorkspaceStoreException("The refinement data is corrupt, incomplete, or incompatible. Retry transfer; no video was regenerated.", e); }
    }

    public static async Task VerifyFileAsync(string path, long bytes, string hash, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length != bytes || Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) != hash)
            throw new WorkspaceStoreException("A captured refinement file is missing or changed. It cannot be silently replaced.");
    }

    private static async Task<Tensor> LatentTensorAsync(string path, CancellationToken ct)
    {
        try
        {
            var (_, tensors, dataStart) = await ReadHeaderAsync(path, ct);
            if (!tensors.TryGetValue("latent_tensor", out var tensor) || tensors.Keys.Any(k => k is not ("latent_tensor" or "latent_format_version_0")))
                throw new InvalidDataException("Not a ComfyUI latent file.");
            return tensor with { Start = dataStart + tensor.Start, End = dataStart + tensor.End };
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or InvalidDataException or EndOfStreamException)
        { throw new WorkspaceStoreException("ComfyUI returned an unreadable latent file. Retry transfer; no video was regenerated.", e); }
    }

    // Reads and checks a SafeTensors header: known dtypes, exact byte ranges, and tensors that tile the data region.
    private static async Task<(string? Metadata, Dictionary<string, Tensor> Tensors, long DataStart)> ReadHeaderAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        if (file.Length is < 10 or > MaximumBytes) throw new InvalidDataException("Invalid file length.");
        var prefix = new byte[8]; await file.ReadExactlyAsync(prefix, ct);
        var length = BinaryPrimitives.ReadUInt64LittleEndian(prefix);
        if (length is < 2 or > 16 * 1024 * 1024 || (long)length + 8 > file.Length) throw new InvalidDataException("Invalid header.");
        var buffer = new byte[(int)length]; await file.ReadExactlyAsync(buffer, ct);
        using var header = JsonDocument.Parse(buffer);
        var dataStart = 8 + (long)length; var dataBytes = file.Length - dataStart;
        string? metadata = null; Dictionary<string, Tensor> tensors = new(StringComparer.Ordinal);
        foreach (var entry in header.RootElement.EnumerateObject())
        {
            if (entry.Name == "__metadata__") { metadata = entry.Value.TryGetProperty("lumibelle", out var m) ? m.GetString() : null; continue; }
            var dtype = entry.Value.GetProperty("dtype").GetString()!;
            var size = dtype switch { "F64" or "I64" => 8, "F32" or "I32" => 4, "F16" or "BF16" or "I16" => 2, "I8" or "U8" or "BOOL" => 1, _ => throw new InvalidDataException("Unsupported tensor dtype.") };
            var shape = entry.Value.GetProperty("shape").EnumerateArray().Select(e => e.GetInt64()).ToArray();
            if (shape.Length > 8 || shape.Any(n => n < 0)) throw new InvalidDataException("Invalid tensor shape.");
            long bytes = size; foreach (var n in shape) bytes = checked(bytes * n);
            var offsets = entry.Value.GetProperty("data_offsets").EnumerateArray().Select(e => e.GetInt64()).ToArray();
            if (offsets.Length != 2 || offsets[0] < 0 || offsets[1] - offsets[0] != bytes || offsets[1] > dataBytes) throw new InvalidDataException("Invalid tensor byte range.");
            if (!tensors.TryAdd(entry.Name, new(dtype, shape, offsets[0], offsets[1]))) throw new InvalidDataException("Duplicate tensor.");
        }
        long end = 0;
        foreach (var tensor in tensors.Values.OrderBy(t => t.Start).ThenBy(t => t.End))
        { if (tensor.Start != end) throw new InvalidDataException("Overlapping or missing tensor bytes."); end = tensor.End; }
        if (end != dataBytes) throw new InvalidDataException("Incomplete tensor data.");
        return (metadata, tensors, dataStart);
    }

    private static JsonObject Entry(string dtype, long[] shape, long start, long end) =>
        new() { ["dtype"] = dtype, ["shape"] = new JsonArray(shape.Select(n => (JsonNode)n).ToArray()), ["data_offsets"] = new JsonArray(start, end) };

    private static async Task WriteHeaderAsync(Stream destination, JsonObject header, CancellationToken ct)
    {
        var json = Encoding.UTF8.GetBytes(header.ToJsonString());
        var padded = (json.Length + 7) / 8 * 8;
        var prefix = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(prefix, (ulong)padded);
        await destination.WriteAsync(prefix, ct); await destination.WriteAsync(json, ct);
        if (padded != json.Length) await destination.WriteAsync(Enumerable.Repeat((byte)' ', padded - json.Length).ToArray(), ct);
    }

    private static async Task CopyRangeAsync(string source, Tensor tensor, Stream destination, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        input.Position = tensor.Start;
        var remaining = tensor.End - tensor.Start; var buffer = new byte[131072];
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (read == 0) throw new EndOfStreamException();
            await destination.WriteAsync(buffer.AsMemory(0, read), ct); remaining -= read;
        }
    }
}
