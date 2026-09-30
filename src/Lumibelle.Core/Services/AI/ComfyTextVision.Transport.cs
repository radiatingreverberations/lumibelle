using System.Net.Http.Headers;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace lumibelle.Services.AI;

public static partial class ComfyTextVision
{
    public static async Task<IReadOnlyList<ComfyTextImage>> UploadAsync(HttpClient http, string model,
        ComfyVisionInput mode, IReadOnlyList<byte[]> images, CancellationToken ct = default, int maximumSide = BatchMaximumSide)
    {
        ValidateCount(mode, images.Count);
        ValidateByteLimits(images);
        if (images.Count == 0) return Array.Empty<ComfyTextImage>();
        ct.ThrowIfCancellationRequested();
        // Verify the server schema again immediately before uploading. Model settings are
        // captured with the job; they are not a promise about a newly upgraded/downgraded server.
        using (var response = await http.GetAsync("object_info", ct))
        {
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var nodes = document.RootElement;
            var capabilities = Capabilities(nodes);
            if (!capabilities.SingleImage || images.Count > 1 && !capabilities.ImageBatch)
                throw new WorkspaceStoreException("Update ComfyUI: image-to-text needs LoadImage and TextGenerate with an IMAGE input; multiple images also need ImageBatch. No media was uploaded.");
            if (!HasModel(nodes, model))
                throw new WorkspaceStoreException("The selected VL checkpoint is no longer advertised by CLIPLoader. Refresh models before sending images.");
        }

        // Validate every image before any upload. Single-image descriptions keep their original
        // captured PNG. Batches use bounded, aspect-preserving canvases so ImageBatch cannot stretch
        // or crop later images to match the first; order remains the original attachment order.
        var sizes = InspectSizes(images);
        var canvas = BatchCanvas(sizes, maximumSide);
        var group = Guid.NewGuid().ToString("N");
        var uploaded = new List<ComfyTextImage>();
        for (var i = 0; i < images.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var png = images.Count == 1 ? images[i] : Letterbox(images[i], canvas.Width, canvas.Height);
            ct.ThrowIfCancellationRequested();
            var name = $"lumibelle-vision-{group}-{i + 1}.png";
            using var body = new MultipartFormDataContent();
            using var image = new ByteArrayContent(png);
            image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            body.Add(image, "image", name);
            body.Add(new StringContent("input"), "type");
            body.Add(new StringContent("false"), "overwrite");
            using var response = await http.PostAsync("upload/image", body, ct);
            response.EnsureSuccessStatusCode();
            using var receipt = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = receipt.RootElement;
            // We ask for a new, root-level input file. Never trust arbitrary paths, annotations,
            // URLs, output files, or a different filename returned by an unexpected server.
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("name", out var saved) || saved.ValueKind != JsonValueKind.String || saved.GetString() != name ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "input" ||
                !root.TryGetProperty("subfolder", out var folder) || folder.ValueKind != JsonValueKind.String || folder.GetString() != "")
                throw new WorkspaceStoreException("ComfyUI returned an unexpected image upload receipt. The text workflow was not submitted.");
            uploaded.Add(new(name));
        }
        return uploaded;
    }

    private static bool HasModel(JsonElement nodes, string model)
    {
        if (nodes.ValueKind != JsonValueKind.Object || !nodes.TryGetProperty("CLIPLoader", out var loader) ||
            loader.ValueKind != JsonValueKind.Object || !loader.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Object ||
            !required.TryGetProperty("clip_name", out var names) || names.ValueKind != JsonValueKind.Array || names.GetArrayLength() == 0 ||
            names[0].ValueKind != JsonValueKind.Array) return false;
        return names[0].EnumerateArray().Any(n => n.ValueKind == JsonValueKind.String && n.GetString() == model);
    }

    public static IReadOnlyList<(int Width, int Height)> InspectSizes(IReadOnlyList<byte[]> images)
    {
        ValidateByteLimits(images);
        var sizes = new List<(int Width, int Height)>();
        foreach (var png in images)
        {
            try
            {
                if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                    throw new WorkspaceStoreException("ComfyUI text inspection requires captured PNG images.");
                var info = Image.Identify(png);
                if (info is null || info.Width is < 1 or > 32768 || info.Height is < 1 or > 32768 ||
                    (long)info.Width * info.Height > MaximumDecodedPixels)
                    throw new WorkspaceStoreException("An inspection image exceeds the 64-megapixel decoding limit. Resize it before inspection.");
                // Decode at most two frames: enough to reject APNG without allocating an entire animation.
                using var decoded = Image.Load<Rgba32>(new DecoderOptions { MaxFrames = 2 }, png);
                if (decoded.Frames.Count != 1) throw new WorkspaceStoreException("An inspection attachment must be one still PNG, not an animation.");
                sizes.Add((decoded.Width, decoded.Height));
            }
            catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException or NotSupportedException or ArgumentException)
            {
                throw new WorkspaceStoreException("An inspection attachment is not a readable PNG. The text workflow was not submitted.", e);
            }
        }
        return sizes;
    }

    public static (int Width, int Height) BatchCanvas(IReadOnlyList<(int Width, int Height)> sizes, int maximumSide = BatchMaximumSide)
    {
        if (sizes.Count == 0) return (0, 0);
        if (maximumSide is < 1 or > BatchMaximumSide) throw new WorkspaceStoreException("Invalid batch image size.");
        if (sizes.Any(s => s.Width <= 0 || s.Height <= 0)) throw new WorkspaceStoreException("Invalid inspection image dimensions.");
        // Do not upscale small images. Each image first fits inside the bounded envelope;
        // the shared canvas is only as large as the largest fitted width and height.
        var fitted = sizes.Select(s => {
            var scale = Math.Min(1d, maximumSide / (double)Math.Max(s.Width, s.Height));
            return (Width: Math.Max(1, (int)Math.Round(s.Width * scale)), Height: Math.Max(1, (int)Math.Round(s.Height * scale)));
        }).ToArray();
        return (fitted.Max(s => s.Width), fitted.Max(s => s.Height));
    }

    public static byte[] Letterbox(byte[] png, int width, int height)
    {
        if (width is < 1 or > BatchMaximumSide || height is < 1 or > BatchMaximumSide)
            throw new WorkspaceStoreException("Invalid batch image canvas.");
        using var image = Image.Load<Rgba32>(png);
        var scale = Math.Min(1d, Math.Min(width / (double)image.Width, height / (double)image.Height));
        var resizedWidth = Math.Max(1, (int)Math.Round(image.Width * scale));
        var resizedHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
        image.Mutate(c => c.Resize(resizedWidth, resizedHeight));
        using var canvas = new Image<Rgba32>(width, height, new Rgba32(128, 128, 128, 255));
        canvas.Mutate(c => c.DrawImage(image, new Point((width - resizedWidth) / 2, (height - resizedHeight) / 2), 1f));
        using var output = new MemoryStream();
        canvas.SaveAsPng(output);
        return output.ToArray();
    }
}
