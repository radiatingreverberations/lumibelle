using System.Buffers.Binary;
using System.Text;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace lumibelle.Services.Shots;

public static class LosslessFrameArchive
{
    // Native RebatchImages bounds both transfer retries and exact-frame decoding memory.
    public const int SegmentFrames = 24;
    public const int FrameMilliseconds = 1000 / 24; // ComfyUI/Pillow use integer milliseconds.
    private static readonly SemaphoreSlim DecodeGate = new(1, 1);
    private static readonly DecoderOptions DecodeOptions = new() { MaxFrames = SegmentFrames + 1 };
    public static string FileName(int segment) => $"archive-{segment:D4}.webp";

    public static async Task<IReadOnlyList<int>> ValidateAsync(string path, int width, int height, int expectedFrames, CancellationToken ct)
    {
        await DecodeGate.WaitAsync(ct);
        try
        {
            await using (var stream = File.OpenRead(path)) InspectContainer(stream);
            var info = await Image.IdentifyAsync(path, ct);
            if (info.Width != width || info.Height != height || expectedFrames is < 1 or > SegmentFrames)
                throw Invalid();
            using var image = await Image.LoadAsync<Rgb24>(DecodeOptions, path, ct);
            if (image.Frames.Count > expectedFrames) throw Invalid();
            List<int> indices = [];
            for (var i = 0; i < image.Frames.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var delay = image.Frames[i].Metadata.GetWebpMetadata().FrameDelay;
                // libwebp can collapse an entirely static segment to one still image.
                if (image.Frames.Count == 1 && delay == 0) return Enumerable.Repeat(0, expectedFrames).ToArray();
                if (delay == 0 || delay % FrameMilliseconds != 0 || delay / FrameMilliseconds > expectedFrames) throw Invalid();
                indices.AddRange(Enumerable.Repeat(i, (int)delay / FrameMilliseconds));
            }
            if (indices.Count != expectedFrames) throw Invalid();
            return indices;
        }
        catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException or EndOfStreamException)
        { throw Invalid(e); }
        finally { DecodeGate.Release(); }
    }

    // Copy compressed image/animation chunks unchanged. Strip ancillary metadata without
    // decoding/re-encoding the archive, so its lossless pixels and frame timing stay intact.
    public static async Task<IReadOnlyList<int>> CleanAsync(string source, string target, int width, int height, int count, CancellationToken ct)
    {
        var temp = target + ".tmp";
        try
        {
            await using (var input = File.OpenRead(source))
            {
                InspectContainer(input); input.Position = 12;
                await using var output = File.Create(temp);
                await output.WriteAsync("RIFF\0\0\0\0WEBP"u8.ToArray(), ct);
                var header = new byte[8];
                while (input.Position < input.Length)
                {
                    ct.ThrowIfCancellationRequested(); input.ReadExactly(header);
                    var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                    var kind = Encoding.ASCII.GetString(header, 0, 4);
                    if (kind is "EXIF" or "XMP " or "ICCP") { input.Position += length + (length & 1); continue; }
                    await output.WriteAsync(header, ct);
                    if (kind == "VP8X")
                    {
                        var data = new byte[10]; input.ReadExactly(data); data[0] &= 0xD3; // clear ICC/EXIF/XMP flags
                        await output.WriteAsync(data, ct);
                    }
                    else
                    {
                        var remaining = (long)length + (length & 1); var buffer = new byte[81920];
                        while (remaining > 0)
                        {
                            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                            if (read == 0) throw Invalid();
                            await output.WriteAsync(buffer.AsMemory(0, read), ct); remaining -= read;
                        }
                    }
                }
                var size = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(size, checked((uint)(output.Length - 8)));
                output.Position = 4; await output.WriteAsync(size, ct);
            }
            var indices = await ValidateAsync(temp, width, height, count, ct);
            DurableFile.Flush(temp);
            File.Move(temp, target, true);
            return indices;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    // Frames extracted one at a time or several per decode must be byte-identical pictures.
    private static readonly PngEncoder Png = new() { SkipMetadata = true, ColorType = PngColorType.Rgb, BitDepth = PngBitDepth.Bit8 };
    public static void SavePng(Image<Rgba32> frame, string path) => frame.Save(path, Png);
    public static async Task<MemoryStream> OpenFrameAsync(string path, int index, int width, int height, CancellationToken ct)
    {
        await DecodeGate.WaitAsync(ct);
        try
        {
            if (index is < 0 or >= SegmentFrames) throw Invalid();
            var info = await Image.IdentifyAsync(path, ct);
            if (info.Width != width || info.Height != height) throw Invalid();
            // Optimized WebP frames can contain transparent delta pixels even when the
            // composed output is opaque RGB. Keep alpha until animation compositing is done.
            using var image = await Image.LoadAsync<Rgba32>(new DecoderOptions { MaxFrames = (uint)index + 1 }, path, ct);
            if (image.Frames.Count <= index) throw Invalid();
            using var selected = image.Frames.CloneFrame(index);
            var output = new MemoryStream();
            try
            {
                await selected.SaveAsync(output, Png, ct);
                output.Position = 0; return output;
            }
            catch { output.Dispose(); throw; }
        }
        finally { DecodeGate.Release(); }
    }

    public static async Task VisitFramesAsync(string path, IReadOnlyList<int> indices, int width, int height,
        Action<int, Image<Rgba32>> visit, CancellationToken ct)
    {
        if (indices.Count == 0) return;
        if (indices.Any(i => i is < 0 or >= SegmentFrames)) throw Invalid();
        await DecodeGate.WaitAsync(ct);
        try
        {
            var info = await Image.IdentifyAsync(path, ct);
            if (info.Width != width || info.Height != height) throw Invalid();
            using var image = await Image.LoadAsync<Rgba32>(new DecoderOptions { MaxFrames = (uint)indices.Max() + 1 }, path, ct);
            foreach (var index in indices.Distinct()) {
                ct.ThrowIfCancellationRequested(); if (index >= image.Frames.Count) throw Invalid();
                using var frame = image.Frames.CloneFrame(index); visit(index, frame);
            }
        }
        finally { DecodeGate.Release(); }
    }

    private static void InspectContainer(Stream input)
    {
        using var reader = new BinaryReader(input, Encoding.ASCII, leaveOpen: true);
        if (input.Length < 20 || new string(reader.ReadChars(4)) != "RIFF" || reader.ReadUInt32() != input.Length - 8 || new string(reader.ReadChars(4)) != "WEBP") throw Invalid();
        var frames = 0; var stills = 0;
        while (input.Position < input.Length)
        {
            if (input.Length - input.Position < 8) throw Invalid();
            var kind = new string(reader.ReadChars(4)); var size = reader.ReadUInt32();
            var end = input.Position + size; if (end + (size & 1) > input.Length) throw Invalid();
            if (kind == "VP8X" && size != 10 || kind == "ANIM" && size != 6 || kind == "VP8 " || kind == "ALPH") throw Invalid();
            if (kind == "VP8L") { if (size < 5 || reader.ReadByte() != 0x2F) throw Invalid(); stills++; }
            if (kind == "ANMF")
            {
                if (size < 29) throw Invalid(); input.Position += 16;
                // Native lossless output stores a VP8L bitstream inside each animation frame.
                var dataKind = new string(reader.ReadChars(4)); var dataSize = reader.ReadUInt32();
                if (dataKind != "VP8L" || dataSize < 5 || input.Position + dataSize + (dataSize & 1) != end || reader.ReadByte() != 0x2F) throw Invalid();
                frames++;
            }
            input.Position = end + (size & 1);
        }
        if (!(frames is >= 1 and <= SegmentFrames && stills == 0 || frames == 0 && stills == 1)) throw Invalid();
    }

    private static WorkspaceStoreException Invalid(Exception? inner = null) => new("The lossless WebP archive does not match the captured frames. Retry its transfer without regenerating.", inner);
}
