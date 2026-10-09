using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace lumibelle.Services.Assets;

public sealed partial class FileAssetStore
{
    public async Task<SavedAssetImage> SaveDerivedImageAsync(Guid projectId, DerivedImageRequest request,
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        if (request is null || request.ImageId == Guid.Empty || request.Destination is null ||
            request.Destination.AssetId == Guid.Empty || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 240 ||
            request.Notes is null || request.Notes.Length > 12000 ||
            (request.TakeId is not null ? 1 : 0) + (request.Parent is not null ? 1 : 0) + (request.ReelFrame is not null ? 1 : 0) != 1 ||
            request.ReelFrame is { } reel && (reel.ReelId == Guid.Empty || reel.Frame is null || request.FrameIndex is not null || request.Crop is not null) ||
            request.TakeId is not null && (request.TakeId == Guid.Empty || request.FrameIndex is null or < 0 || request.Crop is not null) ||
            request.Parent is not null && (request.Parent.AssetId == Guid.Empty || request.Parent.ImageId == Guid.Empty || request.Crop is null || request.FrameIndex is not null))
            throw new WorkspaceStoreException("Choose an exact source, destination, and image name.");
        if (request.Crop is { } crop && InvalidCrop(crop)) throw new WorkspaceStoreException("Choose a crop inside the source image.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, AtomicJsonFile.Options)));
        var dir = await files.DirectoryAsync(projectId, ct);
        using var gate = await ProjectFiles.LockAsync(dir, ct);
        var current = await ReadAsync(dir, projectId, ct);
        // A lost response may be retried after later metadata edits, Trash, or even purge.
        // The durable receipt prevents recreating an already completed image operation.
        if (current.ImageCopyReceipts.FirstOrDefault(r => r.ImageId == request.ImageId) is { } receipt)
        {
            if (receipt.Fingerprint != fingerprint) throw new WorkspaceStoreException("This save already completed with different inputs. Start a new copy.");
            var owner = AssetImageLocations.RecordedOwner(current, new(receipt.AssetId, receipt.ImageId));
            return new(current, owner?.Id ?? receipt.AssetId, receipt.ImageId, owner is not null);
        }
        EnsureRevision(current, expectedRevision);
        if (current.Assets.Any(a => a.Images.Any(i => i.Id == request.ImageId)) || current.Trash.Any(t => t.Image.Id == request.ImageId) || current.ImagePublications.Any(r => r.ImageId == request.ImageId))
            throw new WorkspaceStoreException("That image identity is already in use.");
        var destination = request.Destination;
        var asset = current.Assets.SingleOrDefault(a => a.Id == destination.AssetId);
        if (destination.NewAssetName is { } name)
        {
            if (asset is not null || string.IsNullOrWhiteSpace(name) || name.Trim().Length > 240 || !Enum.IsDefined(destination.NewAssetCategory) || destination.LookId is not null)
                throw new WorkspaceStoreException("Choose a new asset name and category, without an existing look.");
            asset = new() { Id = destination.AssetId, Name = name.Trim(), Category = destination.NewAssetCategory,
                CreatedUtc = clock.GetUtcNow(), UpdatedUtc = clock.GetUtcNow() };
        }
        if (asset is null) throw new WorkspaceStoreException("The destination asset no longer exists.");
        if (destination.LookId is { } look && (asset.Category != AssetCategory.Character || LookPolicy.Find(asset, look) is not { Archived: false }))
            throw new WorkspaceStoreException("Choose an active look belonging to the destination character.");

        try
        {
            await using var source = await OpenDerivedSource(dir, current, request, ct);
            using var image = await Image.LoadAsync(source.Content, ct);
            image.Mutate(c => c.AutoOrient());
            AssetImageSource provenance;
            if (source.Frame is { } frame) provenance = new(Frame: frame);
            else if (source.ReelFrame is { } reelFrame) provenance = new() { ReelFrame = reelFrame with { Width = image.Width, Height = image.Height } };
            else
            {
                var parent = request.Parent!;
                provenance = new(Crop: new(projectId, parent.AssetId, parent.ImageId, request.Crop!, image.Width, image.Height));
                image.Mutate(c => c.Crop(ImageGeometry.CropPixels(image.Width, image.Height, request.Crop!)));
            }
            var added = new AssetImage { Id = request.ImageId, Name = request.Name.Trim(), PreservationGuidance = request.Notes.Trim(),
                LookId = destination.LookId, FileName = request.ImageId.ToString("N") + ".png", ContentType = "image/png",
                Width = image.Width, Height = image.Height, Origin = request.Parent is not null ? AssetImageOrigin.Cropped : AssetImageOrigin.VideoFrame,
                Source = provenance, CreatedUtc = clock.GetUtcNow() };
            var path = ImagePath(dir, asset.Id, added);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    await image.SaveAsPngAsync(output, new PngEncoder { SkipMetadata = true }, ct);
                    await output.FlushAsync(ct);
                }
                DurableFile.Flush(temporary);
                File.Move(temporary, path, true); // orphan from an interrupted pre-publication save of this exact operation
                var updated = asset with { Images = [.. asset.Images, added], UpdatedUtc = clock.GetUtcNow() };
                var next = current with { Assets = destination.NewAssetName is not null ? [.. current.Assets, updated] :
                    current.Assets.Select(a => a.Id == asset.Id ? updated : a).ToList(),
                    ImageCopyReceipts = [.. current.ImageCopyReceipts, new(request.ImageId, asset.Id, fingerprint)] };
                try { return new(await PublishAsync(dir, next, current.Revision, ct), asset.Id, added.Id, true); }
                catch { TryDelete(path); throw; }
            }
            finally { TryDelete(temporary); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or UnknownImageFormatException or InvalidImageContentException or AiGenerationException)
        { throw new WorkspaceStoreException("Couldn’t save this image copy. Check the source and available disk space, then retry.", e); }
    }

    private sealed record DerivedSource(Stream Content, VideoFrameSource? Frame = null, ReelImageSource? ReelFrame = null) : IAsyncDisposable
    { public ValueTask DisposeAsync() => Content.DisposeAsync(); }

    private async Task<DerivedSource> OpenDerivedSource(string dir, AssetLibrary library, DerivedImageRequest request, CancellationToken ct)
    {
        if (request.ReelFrame is { } export)
        {
            var reel = library.Reels.SingleOrDefault(r => r.Id == export.ReelId)
                ?? throw new WorkspaceStoreException("Restore the source reel before saving a frame.");
            if (export.Frame.MediaId != reel.Media.Id) throw new WorkspaceStoreException("Choose a frame from this reel.");
            var store = referenceVideos ?? throw new WorkspaceStoreException("Reel frame storage is unavailable.");
            var configured = settings is null ? new H3Settings() : (await settings.LoadAsync(ct)).H3;
            // OpenFrame validates immutable source identity, decoded index and presentation time.
            var source = await store.OpenFrameAsync(library.ProjectId, export.Frame, configured, ct);
            return new(source.Content, ReelFrame: new(library.ProjectId, reel.Id, reel.Name, export.Frame,
                export.Frame.Source != reel.Media.Sha256, 0, 0));
        }
        if (request.TakeId is { } takeId)
        {
            var shots = await AtomicJsonFile.ReadAsync<ShotDocument>(Path.Combine(dir, "shots.json"), ct)
                ?? throw new WorkspaceStoreException("The source take is unavailable.");
            FileShotStore.Validate(shots, library.ProjectId);
            var take = shots.Takes.SingleOrDefault(t => t.Id == takeId)
                ?? throw new WorkspaceStoreException("Restore the source take before saving a frame.");
            var index = request.FrameIndex!.Value;
            if (index >= take.FrameCount) throw new WorkspaceStoreException("Choose an available frame.");
            var path = Path.Combine(dir, "shots", "takes", take.Directory);
            return new(await (frameReader ?? TakeFrameReader.Shared).OpenAsync(path, take, index, ct),
                new(library.ProjectId, take.ShotId, take.Id, index, index / take.Fps, take.Fps, take.Width, take.Height) { Lossless = take.IsLosslessFrame(index) });
        }
        var parent = request.Parent!;
        var image = library.Assets.SingleOrDefault(a => a.Id == parent.AssetId)?.Images.SingleOrDefault(i => i.Id == parent.ImageId)
            ?? throw new WorkspaceStoreException("Restore or replace the source image before making a crop.");
        return new(new FileStream(ImagePath(dir, parent.AssetId, image), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous));
    }

    private static bool InvalidImageSource(AssetImage image)
    {
        if (image.Origin == AssetImageOrigin.VideoFrame && image.Source?.Frame is null && image.Source?.ReelFrame is null || image.Origin == AssetImageOrigin.Cropped && image.Source?.Crop is null) return true;
        if (image.Source is not { } source) return false;
        if ((source.Frame is not null ? 1 : 0) + (source.Crop is not null ? 1 : 0) + (source.ReelFrame is not null ? 1 : 0) != 1) return true;
        if (source.ReelFrame is { } r) return image.Origin != AssetImageOrigin.VideoFrame || r.ProjectId == Guid.Empty || r.ReelId == Guid.Empty ||
            string.IsNullOrWhiteSpace(r.ReelName) || r.Frame is null || r.Frame.MediaId == Guid.Empty || r.Frame.Index < 0 ||
            !double.IsFinite(r.Frame.Seconds) || r.Frame.Seconds < 0 || r.Frame.Source is not { Length: 64 } || r.Frame.Source.Any(c => !Uri.IsHexDigit(c)) ||
            r.Width != image.Width || r.Height != image.Height;
        if (source.Frame is { } f) return image.Origin != AssetImageOrigin.VideoFrame || f.ProjectId == Guid.Empty || f.ShotId == Guid.Empty || f.TakeId == Guid.Empty ||
            f.FrameIndex is < 0 or >= 362 || !double.IsFinite(f.Fps) || f.Fps != 24 || !double.IsFinite(f.Timestamp) || Math.Abs(f.Timestamp - f.FrameIndex / f.Fps) > 0.000001 ||
            f.Width != image.Width || f.Height != image.Height;
        var c = source.Crop!;
        if (image.Origin != AssetImageOrigin.Cropped || c.ProjectId == Guid.Empty || c.AssetId == Guid.Empty || c.ImageId == Guid.Empty || c.SourceWidth <= 0 || c.SourceHeight <= 0 || c.Crop is null || InvalidCrop(c.Crop)) return true;
        var rect = ImageGeometry.CropPixels(c.SourceWidth, c.SourceHeight, c.Crop);
        return image.Width != rect.Width || image.Height != rect.Height;
    }
}
