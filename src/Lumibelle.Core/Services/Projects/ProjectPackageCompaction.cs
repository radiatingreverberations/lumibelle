using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using PictureInfo = lumibelle.Services.Assets.ImageInfo;

namespace lumibelle.Services.Projects;

// Optional size reductions for a package meant for sharing. They change only the detached
// state and files written to the export folder, never the source project.
internal static class ProjectPackageCompaction
{
    // Records the omission the same way storage cleanup does, so takes decode frames from their MP4.
    // Runs before the plan, which then no longer lists the archive segments.
    internal static HashSet<Guid> LeaveOutTakeArchives(ProjectPackageState state, DateTimeOffset now)
    {
        var changed = new HashSet<Guid>();
        foreach (var take in ProjectPackageRefinement.AllTakes(state).Where(t => t.Frames.Count > 0))
        {
            take.FrameArchiveRemoval = new(now, take.Frames.DistinctBy(f => f.FileName).Select(f => new FrameArchiveFile(f.FileName, f.Bytes)).ToArray(), now);
            take.Frames = []; changed.Add(take.Id);
        }
        return changed;
    }
    // Counts only takes the plan kept, so unreferenced trash does not inflate the summary.
    internal static (int Files, long Bytes) LeftOut(ProjectPackageState state, HashSet<Guid> takes)
    {
        var files = ProjectPackageRefinement.AllTakes(state).Where(t => takes.Contains(t.Id)).SelectMany(t => t.FrameArchiveRemoval!.Files).ToArray();
        return (files.Length, files.Sum(f => f.Bytes));
    }

    // PNG asset images become lossy WebP; JPEG and WebP images are re-encoded only when resized.
    // Every field describing the file changes with it: name, extension, content type and size.
    // The package manifest records the new length and hash. Images whose pixel size is also
    // recorded in generation, frame, crop or regional-edit details keep their dimensions.
    internal static async Task<(int Recompressed, int Resized)> CompressImagesAsync(ProjectPackagePlan plan, string root, ProjectExportOptions options,
        string scratch, CancellationToken ct)
    {
        var library = plan.State.Assets;
        var owned = library.Assets.SelectMany(a => a.Images.Select(i => (Owner: a.Id, Image: i)))
            .Concat(library.Trash.Select(t => (Owner: t.Asset.Id, t.Image))).ToArray();
        var sources = owned.SelectMany(o => Sources(o.Image)).ToHashSet();
        var replaced = new Dictionary<Guid, AssetImage>(); var resized = 0;
        foreach (var (owner, image) in owned)
        {
            ct.ThrowIfCancellationRequested();
            var storage = (image.StorageAssetId ?? owner).ToString("D");
            var relative = $"assets/{storage}/images/{image.FileName}";
            var source = plan.Sources[relative];
            var limit = options.MaxImageDimension is { } max && Math.Max(image.Width, image.Height) > max && !FixedSize(image, sources) ? max : (int?)null;
            if (image.ContentType != "image/png" && limit is null) continue;
            var encoded = await EncodeAsync(source.Physical, image, limit, ct);
            if (encoded is not { } result) continue;
            var fileName = Path.GetFileNameWithoutExtension(image.FileName) + result.Info.Extension;
            var target = $"assets/{storage}/images/{fileName}";
            if (!ProjectPackageFormat.Allowed(target) || !string.Equals(target, relative, StringComparison.OrdinalIgnoreCase) && plan.Sources.ContainsKey(target)) continue;
            var output = Path.Combine(scratch, image.Id.ToString("N") + result.Info.Extension);
            Directory.CreateDirectory(scratch); await File.WriteAllBytesAsync(output, result.Bytes, ct);
            plan.Sources.Remove(relative);
            plan.Sources.Add(target, new(target, output, result.Bytes.Length, Convert.ToHexString(SHA256.HashData(result.Bytes))));
            replaced.Add(image.Id, image with { FileName = fileName, ContentType = result.Info.ContentType, Width = result.Info.Width, Height = result.Info.Height });
            if (limit is not null) resized++;
        }
        AssetImage Updated(AssetImage image) => replaced.GetValueOrDefault(image.Id, image);
        plan.State.Assets = library with {
            Assets = library.Assets.Select(a => a with { Images = a.Images.Select(Updated).ToList() }).ToList(),
            Trash = library.Trash.Select(t => t with { Image = Updated(t.Image) }).ToList()
        };
        return (replaced.Count, resized);
    }
    private static IEnumerable<Guid> Sources(AssetImage image)
    {
        if (image.Source?.Crop is { } crop) yield return crop.ImageId;
        foreach (var region in image.Generation?.Edit?.Regions ?? []) yield return region.Source.ImageId;
        if (image.Generation?.Edit?.Regional is { } regional) yield return regional.Selection.Source.ImageId;
    }
    private static bool FixedSize(AssetImage image, HashSet<Guid> sources) =>
        image.Source is not null || image.Origin is not (AssetImageOrigin.Imported or AssetImageOrigin.Generated or AssetImageOrigin.Edited) ||
        image.Generation is { } g && (g.Workflow is ImageWorkflow.CodexImages or ImageWorkflow.QwenImage21 || g.Codex is not null || g.QwenImage21 is not null || g.Edit?.Regional is not null) ||
        sources.Contains(image.Id);
    private static async Task<(byte[] Bytes, PictureInfo Info)?> EncodeAsync(string path, AssetImage image, int? limit, CancellationToken ct)
    {
        using var decoded = await Image.LoadAsync(path, ct);
        // Recorded sizes describe the oriented picture; see ImageInspector.
        decoded.Mutate(x => x.AutoOrient());
        if (decoded.Width != image.Width || decoded.Height != image.Height) return null;
        if (limit is { } max) decoded.Mutate(x => x.Resize(new ResizeOptions { Size = new(max, max), Mode = ResizeMode.Max, Sampler = KnownResamplers.Lanczos3 }));
        decoded.Metadata.ExifProfile = null; decoded.Metadata.XmpProfile = null;
        using var output = new MemoryStream();
        if (image.ContentType == "image/jpeg") await decoded.SaveAsJpegAsync(output, new JpegEncoder { Quality = ProjectExportOptions.ImageQuality }, ct);
        else await decoded.SaveAsWebpAsync(output, new WebpEncoder { FileFormat = WebpFileFormatType.Lossy, Quality = ProjectExportOptions.ImageQuality }, ct);
        // Keep an original that is already smaller, such as a flat graphic.
        if (limit is null && output.Length >= new FileInfo(path).Length) return null;
        var bytes = output.ToArray();
        return (bytes, ImageInspector.Inspect(bytes));
    }
}
