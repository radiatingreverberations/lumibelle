using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace lumibelle.Services.Shots;

public sealed partial class FileShotStore
{
    public async Task<ShotDocument> TrimTakeAsync(Guid projectId, TakeTrimRequest request, long? expectedRevision = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (request.TakeId == Guid.Empty || request.ResultId == Guid.Empty || request.TakeId == request.ResultId)
            throw new WorkspaceStoreException("Choose a source and a new trim identity.");
        var dir = await files.DirectoryAsync(projectId, ct);
        using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await Read(dir, projectId, ct);
        if (d.TrimPublications.FirstOrDefault(r => r.ResultId == request.ResultId) is { } receipt) {
            if (receipt != request) throw new WorkspaceStoreException("This trim identity was already saved with different inputs.");
            return d;
        }
        if (expectedRevision is { } revision) Revision(d, revision);
        if (d.TakePublications.Any(r => r.TakeId == request.ResultId) || AllTakes(d).Any(t => t.Id == request.ResultId))
            throw new WorkspaceStoreException("This take identity is already in use.");
        var source = d.Takes.SingleOrDefault(t => t.Id == request.TakeId) ?? throw new WorkspaceStoreException("Restore the source take before trimming it.");
        TakeTrimming.Range(request.StartFrame, request.EndFrameExclusive, source.FrameCount);
        if (request.StartFrame == 0 && request.EndFrameExclusive == source.FrameCount) throw new WorkspaceStoreException("Choose a shorter range before saving a trimmed version.");
        var folder = ArchiveDirectory(dir, source);
        var stage = Path.Combine(dir, "shots", "runs", request.ResultId.ToString("D"), "trim");
        // A retry starts with clean staged media; durable publication receipts are checked above.
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        Directory.CreateDirectory(stage);
        try {
            if (source.Composition is not null) return await TrimCombinedAsync(projectId, request, source, folder, stage, dir, d, ct);
            var result = ShotCopy.Of(source);
            result.Id = request.ResultId; result.Directory = result.Id.ToString("D"); result.CreatedUtc = clock.GetUtcNow();
            result.Timings = null; result.FrameArchiveRemoval = null;
            var offset = source.Trim?.SourceStartFrame ?? 0;
            result.Trim = new(source.Id, source.FrameCount, request.StartFrame, request.EndFrameExclusive,
                offset + request.StartFrame, offset + request.EndFrameExclusive, source.HasLosslessFrames);
            result.Frames = [];
            var count = request.EndFrameExclusive - request.StartFrame;
            var pngFolder = Path.Combine(stage, "frames");
            if (source.HasLosslessFrames) {
                progress?.Report("Preserving selected lossless frames…");
                Directory.CreateDirectory(pngFolder);
                var reader = frameReader ?? TakeFrameReader.Shared;
                for (var first = 0; first < count; first += LosslessFrameArchive.SegmentFrames) {
                    using var animation = new Image<Rgba32>(source.Width, source.Height);
                    var n = Math.Min(LosslessFrameArchive.SegmentFrames, count - first);
                    for (var i = 0; i < n; i++) {
                        await using var pixels = await reader.OpenAsync(folder, source, request.StartFrame + first + i, ct);
                        using var frame = await Image.LoadAsync<Rgba32>(pixels, ct);
                        await frame.SaveAsPngAsync(Path.Combine(pngFolder, $"frame-{first + i:D4}.png"), ct);
                        animation.Frames.AddFrame(frame.Frames.RootFrame);
                        animation.Frames[^1].Metadata.GetWebpMetadata().FrameDelay = (uint)LosslessFrameArchive.FrameMilliseconds;
                    }
                    animation.Frames.RemoveFrame(0);
                    var name = LosslessFrameArchive.FileName(first / LosslessFrameArchive.SegmentFrames);
                    var path = Path.Combine(stage, name);
                    await animation.SaveAsync(path, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless, Method = WebpEncodingMethod.Fastest, SkipMetadata = true }, ct);
                    var indices = await LosslessFrameArchive.ValidateAsync(path, source.Width, source.Height, n, ct);
                    for (var i = 0; i < n; i++) result.Frames.Add(new(first + i, name, new FileInfo(path).Length) { ArchiveFrameIndex = indices[i] });
                }
            }
            if (source.RefinementPackage is { } package) {
                progress?.Report("Retaining full source latents and captured inputs…");
                var verified = await RefinementPackages.InspectAsync(Path.Combine(folder, H3RefinementPackage.FileName), source.Snapshot, source.Refinement, ct);
                if (verified != package) throw new WorkspaceStoreException("The source latent package changed.");
                await TakeTrimming.CopyVerifiedAsync(Path.Combine(folder, H3RefinementPackage.FileName), Path.Combine(stage, H3RefinementPackage.FileName), package.Bytes, package.Sha256, ct);
            }
            if (source.RefinementPackage is not null || source.RetainedSource is not null)
                await RetainInputsAsync(result, folder, stage, ct);
            progress?.Report("Encoding the trimmed video and audio…");
            await (mediaTools ?? new ProductionMediaTools()).TrimTakeAsync(Path.Combine(folder, "video.mp4"), source.HasLosslessFrames ? Path.Combine(pngFolder, "frame-%04d.png") : null,
                Path.Combine(stage, "video.mp4"), request.StartFrame, request.EndFrameExclusive, source.Fps, source.Snapshot.Settings, ct);
            if (Directory.Exists(pngFolder)) Directory.Delete(pngFolder, true);
            result.Bytes = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
            ct.ThrowIfCancellationRequested();
            // Any orphan from a failed manifest write belongs to this exact stable trim operation.
            var orphan = Path.Combine(dir, "shots", "takes", result.Id.ToString("D"));
            if (Directory.Exists(orphan)) Directory.Delete(orphan, true);
            d.TrimPublications.Add(request);
            progress?.Report("Saving trimmed version…");
            return await PublishTakeCore(projectId, result, stage, dir, d, ct);
        }
        catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException) {
            throw new WorkspaceStoreException("The source lossless frames are unreadable. Restore the archive before trimming.", e);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
}
