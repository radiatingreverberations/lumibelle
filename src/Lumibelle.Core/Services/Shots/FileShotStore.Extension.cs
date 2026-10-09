using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public sealed partial class FileShotStore
{
    public async Task<AiVideoJobRequest> CaptureExtensionVersionAsync(Guid projectId, Guid takeId, Guid runId, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var document = await Read(dir, projectId, ct);
        var take = document.Takes.SingleOrDefault(t => t.Id == takeId) ?? throw new WorkspaceStoreException("Restore this take before making another version.");
        var retainedInputs = take.RetainedSource ?? throw new WorkspaceStoreException("The final segment's captured inputs are unavailable.");
        var folder = ArchiveDirectory(dir, take); var run = await RunDirectoryAsync(projectId, runId, ct); var inputs = Path.Combine(run, "inputs");
        if (Directory.Exists(inputs)) throw new WorkspaceStoreException("This version already has captured inputs. Retry its saved request.");
        Directory.CreateDirectory(inputs);
        TakeExtensionRequest intent;
        if (take.Extension is { } captured) {
            intent = captured;
            await TakeBundles.CopyCapturedAsync(intent.SourceFiles, Path.Combine(folder, H3Motion.SourceFolder), Path.Combine(run, H3Motion.SourceFolder), ct);
        } else {
            var lastVisible = take.Composition?.Segments[^1] ?? throw new WorkspaceStoreException("This take has no captured extension request.");
            if (lastVisible.Source.Snapshot.Motion is null && lastVisible.Source.Refinement is null) throw new WorkspaceStoreException("The final segment has no extension request to replay.");
            var source = ShotCopy.Of(take); source.Extension = null;
            var manifest = await TakeBundles.CopyAsync(source, folder, Path.Combine(run, H3Motion.SourceFolder), ct);
            var prefix = take.FrameCount - (lastVisible.EndFrameExclusive - lastVisible.StartFrame);
            intent = new(source, Math.Max(1, prefix), prefix > 0, manifest) { ReplayLastSegment = true };
        }
        foreach (var input in retainedInputs.Inputs) await TakeTrimming.CopyVerifiedAsync(Path.Combine(folder, TakeTrimming.InputsFolder, input.FileName), Path.Combine(inputs, input.FileName), input.Bytes, input.Sha256, ct);
        await TakeBundles.CopyCapturedAsync(take.Snapshot.Motion?.Files ?? [], Path.Combine(folder, TakeTrimming.InputsFolder), inputs, ct);
        if (take.Refinement is { } refinement) {
            var original = take.RetainedSource?.RefinementInput is not null
                ? Path.Combine(folder, TakeTrimming.InputsFolder, TakeTrimming.RefinementInputFile)
                : Path.Combine(folder, H3Motion.SourceFolder, H3RefinementPackage.FileName);
            await TakeTrimming.CopyVerifiedAsync(original, Path.Combine(inputs, H3RefinementPackage.FileName), refinement.SourcePackage.Bytes, refinement.SourcePackage.Sha256, ct);
        }
        var last = take.Composition!.Segments[^1];
        return new(2, runId, take.Snapshot, ShotCopy.Of(retainedInputs.Inputs)) { Extension = ShotCopy.Of(intent), Refinement = take.Refinement,
            OutputTrim = new((last.Source.Trim?.SourceStartFrame ?? 0) + last.StartFrame, (last.Source.Trim?.SourceStartFrame ?? 0) + last.EndFrameExclusive),
            DestinationShotId = take.ShotId == take.Snapshot.Shot.Id ? null : take.ShotId };
    }
    private async Task<ShotDocument> TrimCombinedAsync(Guid projectId, TakeTrimRequest request, ShotTake source, string folder, string stage, string dir, ShotDocument document, CancellationToken ct)
    {
        var ranges = TakeBundles.Range(source, request.StartFrame, request.EndFrameExclusive);
        foreach (var range in ranges) {
            var original = source.Composition!.Segments.First(s => s.Source.Id == range.Source.Id && range.StartFrame >= s.StartFrame && range.EndFrameExclusive <= s.EndFrameExclusive);
            await TakeBundles.CopyAsync(range.Source, Path.Combine(folder, "segments", original.Key.ToString("D")), Path.Combine(stage, "segments", range.Key.ToString("D")), ct);
        }
        var last = ranges[^1]; var result = ShotCopy.Of(last.Source);
        result.Id = request.ResultId; result.Directory = result.Id.ToString("D"); result.ShotId = source.ShotId; result.CreatedUtc = clock.GetUtcNow();
        result.Frames = []; result.FrameArchiveRemoval = null; result.Extension = null; result.Timings = null;
        result.Composition = new(ranges, Math.Max(0, Math.Min(source.Composition!.JoinFrame - request.StartFrame, request.EndFrameExclusive - request.StartFrame - 1)));
        result.Trim = new(source.Id, source.FrameCount, request.StartFrame, request.EndFrameExclusive, request.StartFrame, request.EndFrameExclusive, source.HasLosslessFrames);
        var lastFolder = Path.Combine(stage, "segments", last.Key.ToString("D"));
        if (result.RefinementPackage is { } package) await TakeTrimming.CopyVerifiedAsync(Path.Combine(lastFolder, H3RefinementPackage.FileName), Path.Combine(stage, H3RefinementPackage.FileName), package.Bytes, package.Sha256, ct);
        await RetainInputsAsync(result, lastFolder, stage, ct);
        await AssembleSegmentsAsync(result, stage, ct);
        if (source.Extension is { } extension && last.Source.Id == source.Composition.Segments[^1].Source.Id) {
            var prefixLength = extension.Combine ? extension.RetainedFrames - extension.PrefixStartFrame : 0;
            result.Extension = extension with { Combine = extension.Combine && request.StartFrame < prefixLength,
                PrefixStartFrame = extension.PrefixStartFrame + Math.Min(request.StartFrame, Math.Max(0, prefixLength - 1)) };
            await TakeBundles.CopyCapturedAsync(extension.SourceFiles, Path.Combine(folder, H3Motion.SourceFolder), Path.Combine(stage, H3Motion.SourceFolder), ct);
        }
        await CreateJoinPreviewAsync(result, stage, ct);
        result.Bytes = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
        ct.ThrowIfCancellationRequested(); var orphan = Path.Combine(dir, "shots", "takes", result.Id.ToString("D")); if (Directory.Exists(orphan)) Directory.Delete(orphan, true);
        document.TrimPublications.Add(request);
        return await PublishTakeCore(projectId, result, stage, dir, document, ct);
    }
    public async Task<ShotDocument> PublishExtensionAsync(Guid projectId, AiVideoJobRequest request, Guid fullTakeId, CancellationToken ct = default)
    {
        AiVideoJobPolicy.Validate(request);
        var intent = request.Extension ?? throw new WorkspaceStoreException("The extension request is missing.");
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var document = await Read(dir, projectId, ct); var resultId = H3Motion.OutputId(fullTakeId);
        var fingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request, AtomicJsonFile.Options)));
        if (document.ExtensionPublications.SingleOrDefault(p => p.ResultId == resultId) is { } receipt) {
            if (receipt.FullTakeId != fullTakeId || receipt.Fingerprint != fingerprint) throw new WorkspaceStoreException("This extension identity was already saved with different inputs.");
            return document;
        }
        if (document.TakePublications.Any(p => p.TakeId == resultId) || AllTakes(document).Any(t => t.Id == resultId))
            throw new WorkspaceStoreException("This extension output identity is already in use.");
        var full = document.Takes.SingleOrDefault(t => t.Id == fullTakeId) ?? throw new WorkspaceStoreException("Restore the completed full generation before retrying saving.");
        if (full.RunId != request.BatchId || full.ShotId != request.OutputShotId || full.Trim is not null || full.Composition is not null ||
            !System.Text.Json.JsonElement.DeepEquals(System.Text.Json.JsonSerializer.SerializeToElement(full.Snapshot, AtomicJsonFile.Options), System.Text.Json.JsonSerializer.SerializeToElement(request.Snapshot, AtomicJsonFile.Options)))
            throw new WorkspaceStoreException("The completed generation does not belong to this extension.");
        var run = await RunDirectoryAsync(projectId, request.BatchId, ct);
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, run, ct);
        var sourceFolder = Path.Combine(run, H3Motion.SourceFolder); var stage = Path.Combine(run, "assembly-" + resultId.ToString("D"));
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        Directory.CreateDirectory(stage);
        try {
            var result = ShotCopy.Of(full); result.Id = resultId; result.Directory = resultId.ToString("D");
            result.Frames = []; result.FrameArchiveRemoval = null; result.Trim = null; result.Timings = null; result.Extension = ShotCopy.Of(intent);
            await TakeBundles.CopyCapturedAsync(intent.SourceFiles, sourceFolder, Path.Combine(stage, H3Motion.SourceFolder), ct);
            var segments = new List<TakeSegment>();
            if (intent.Combine) {
                var ranges = TakeBundles.Range(intent.Source, intent.PrefixStartFrame, intent.RetainedFrames);
                var originalRanges = intent.Source.Composition?.Segments;
                foreach (var range in ranges) {
                    var old = originalRanges?.First(s => s.Source.Id == range.Source.Id && range.StartFrame >= s.StartFrame && range.EndFrameExclusive <= s.EndFrameExclusive);
                    var from = old is null ? sourceFolder : Path.Combine(sourceFolder, "segments", old.Key.ToString("D"));
                    await TakeBundles.CopyAsync(range.Source, from, Path.Combine(stage, "segments", range.Key.ToString("D")), ct);
                    segments.Add(range);
                }
            }
            var leaf = ShotCopy.Of(full); leaf.Extension = null; leaf.Composition = null;
            var leafKey = Guid.NewGuid(); var leafFolder = Path.Combine(stage, "segments", leafKey.ToString("D"));
            await TakeBundles.CopyAsync(leaf, ArchiveDirectory(dir, full), leafFolder, ct);
            await RetainInputsAsync(leaf, ArchiveDirectory(dir, full), leafFolder, ct);
            var visible = request.OutputTrim ?? new(request.Snapshot.Motion!.Frames, full.FrameCount);
            segments.Add(new(leafKey, leaf, visible.StartFrame, visible.EndFrameExclusive));
            result.Composition = new(segments, intent.Combine ? intent.RetainedFrames - intent.PrefixStartFrame - 1 : 0);
            if (full.RefinementPackage is { } package)
                await TakeTrimming.CopyVerifiedAsync(Path.Combine(leafFolder, H3RefinementPackage.FileName), Path.Combine(stage, H3RefinementPackage.FileName), package.Bytes, package.Sha256, ct);
            await RetainInputsAsync(result, ArchiveDirectory(dir, full), stage, ct);
            await AssembleSegmentsAsync(result, stage, ct);
            await CreateJoinPreviewAsync(result, stage, ct);
            result.Bytes = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
            ct.ThrowIfCancellationRequested();
            var orphan = Path.Combine(dir, "shots", "takes", resultId.ToString("D"));
            if (Directory.Exists(orphan)) Directory.Delete(orphan, true);
            document.ExtensionPublications.Add(new(resultId, fullTakeId, fingerprint));
            return await PublishTakeCore(projectId, result, stage, dir, document, ct);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    public async Task<AssetMedia?> OpenJoinPreviewAsync(Guid projectId, Guid takeId, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct);
        var take = (await Read(dir, projectId, ct)).Takes.SingleOrDefault(t => t.Id == takeId && t.Composition?.HasJoinPreview == true);
        if (take is null) return null;
        try { return new(new FileStream(Path.Combine(ArchiveDirectory(dir, take), "join-preview.mp4"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete), "video/mp4", take.CreatedUtc); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }
    private async Task CreateJoinPreviewAsync(ShotTake take, string stage, CancellationToken ct)
    {
        if (take.Composition is not { Segments.Count: 1 } composition || take.Extension is not { } extension) return;
        var end = extension.RetainedFrames; var start = Math.Max(extension.PrefixStartFrame, end - (int)take.Fps);
        await (mediaTools ?? new ProductionMediaTools()).AssembleTakeAsync([
            new(Path.Combine(stage, H3Motion.SourceFolder, "video.mp4"), null, start, end, take.Fps),
            new(Path.Combine(stage, "video.mp4"), null, 0, Math.Min((int)take.Fps, take.FrameCount), take.Fps)
        ], Path.Combine(stage, "join-preview.mp4"), take.Snapshot.Settings, ct);
        take.Composition = composition with { HasJoinPreview = true };
    }
    private async Task AssembleSegmentsAsync(ShotTake result, string stage, CancellationToken ct)
    {
        List<TakeAssemblyMedia> media = [];
        foreach (var segment in result.Composition!.Segments) {
            var folder = Path.Combine(stage, "segments", segment.Key.ToString("D")); string? pattern = null;
            if (segment.Source.HasLosslessFrames) {
                var pixels = Path.Combine(folder, "encode-frames"); Directory.CreateDirectory(pixels);
                for (var frame = segment.StartFrame; frame < segment.EndFrameExclusive; frame++) {
                    await using var from = await (frameReader ?? TakeFrameReader.Shared).OpenAsync(folder, segment.Source, frame, ct);
                    await using var to = File.Create(Path.Combine(pixels, $"frame-{frame - segment.StartFrame:D6}.png")); await from.CopyToAsync(to, ct);
                }
                pattern = Path.Combine(pixels, "frame-%06d.png");
            }
            media.Add(new(Path.Combine(folder, "video.mp4"), pattern, segment.StartFrame, segment.EndFrameExclusive, result.Fps));
        }
        await (mediaTools ?? new ProductionMediaTools()).AssembleTakeAsync(media, Path.Combine(stage, "video.mp4"), result.Snapshot.Settings, ct);
        foreach (var segment in result.Composition.Segments) {
            var pixels = Path.Combine(stage, "segments", segment.Key.ToString("D"), "encode-frames");
            if (Directory.Exists(pixels)) Directory.Delete(pixels, true);
        }
    }
    private async Task RetainInputsAsync(ShotTake take, string folder, string target, CancellationToken ct)
    {
        IReadOnlyList<AiVideoInput> inputs;
        if (take.RetainedSource is { } retained) inputs = retained.Inputs;
        else {
            var job = jobs is null ? null : (await jobs.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == take.AiJobId);
            if (job is null) throw new WorkspaceStoreException("This take's captured inputs are unavailable. Restore its saved request before extending it.");
            var request = AiVideoJobHandler.Read(job, await jobs!.ReadSnapshotAsync(job.Id, ct));
            if (request.BatchId != take.RunId || !System.Text.Json.JsonElement.DeepEquals(System.Text.Json.JsonSerializer.SerializeToElement(request.Snapshot, AtomicJsonFile.Options), System.Text.Json.JsonSerializer.SerializeToElement(take.Snapshot, AtomicJsonFile.Options)))
                throw new WorkspaceStoreException("The saved request does not match the take.");
            inputs = request.Inputs;
        }
        var inputFolder = Path.Combine(target, TakeTrimming.InputsFolder); Directory.CreateDirectory(inputFolder);
        foreach (var input in inputs) {
            var path = take.RetainedSource is not null ? Path.Combine(folder, TakeTrimming.InputsFolder, input.FileName)
                : CapturedInputStore.Resolve(await RunDirectoryAsync(take.Snapshot.ProjectId, take.RunId, ct), input.FileName, input.Sha256);
            var output = Path.Combine(inputFolder, input.FileName);
            if (!File.Exists(output)) await TakeTrimming.CopyVerifiedAsync(path, output, input.Bytes, input.Sha256, ct);
        }
        foreach (var input in take.Snapshot.Motion?.Files ?? []) {
            var path = take.RetainedSource is not null ? Path.Combine(folder, TakeTrimming.InputsFolder, input.FileName) : Path.Combine(await RunDirectoryAsync(take.Snapshot.ProjectId, take.RunId, ct), "inputs", input.FileName);
            var output = Path.Combine(inputFolder, input.FileName);
            if (!File.Exists(output)) await TakeTrimming.CopyVerifiedAsync(path, output, input.Bytes, input.Sha256, ct);
        }
        var originalPackage = take.Refinement?.SourcePackage;
        if (originalPackage is not null) {
            var original = take.RetainedSource?.RefinementInput is not null ? Path.Combine(folder, TakeTrimming.InputsFolder, TakeTrimming.RefinementInputFile)
                : Path.Combine(await RunDirectoryAsync(take.Snapshot.ProjectId, take.RunId, ct), "inputs", H3RefinementPackage.FileName);
            var output = Path.Combine(inputFolder, TakeTrimming.RefinementInputFile);
            if (!File.Exists(output)) await TakeTrimming.CopyVerifiedAsync(original, output, originalPackage.Bytes, originalPackage.Sha256, ct);
        }
        take.RetainedSource = new(ShotCopy.Of(inputs)) { RefinementInput = originalPackage };
        if (File.Exists(Path.Combine(target, "video.mp4")))
            take.Bytes = TakeBundles.Files(take).Distinct().Sum(f => new FileInfo(TakeBundles.Under(target, f)).Length);
    }
    public async Task<(TakeExtensionRequest Source, TakeMotionContext Motion)> CaptureExtensionAsync(Guid projectId, Guid takeId, Guid runId, int endFrameExclusive, double addedSeconds, bool combine, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var document = await Read(dir, projectId, ct);
        var take = ShotCopy.Of(document.Takes.SingleOrDefault(t => t.Id == takeId) ?? throw new WorkspaceStoreException("Restore the source take before extending it."));
        TakeTrimming.Range(0, endFrameExclusive, take.FrameCount);
        var run = await RunDirectoryAsync(projectId, runId, ct);
        var captured = Path.Combine(run, H3Motion.SourceFolder); var inputs = Path.Combine(run, "inputs");
        if (Directory.Exists(captured) || Directory.Exists(inputs)) throw new WorkspaceStoreException("This extension already has captured inputs. Retry its saved request.");
        var sourceFolder = ArchiveDirectory(dir, take);
        // Remove replay ancestry; flattened leaves and their owned bundles are sufficient for this operation.
        take.Extension = null;
        Directory.CreateDirectory(captured); Directory.CreateDirectory(inputs);
        try {
            await TakeBundles.CopyAsync(take, sourceFolder, captured, ct);
            await RetainInputsAsync(take, sourceFolder, captured, ct);
            foreach (var segment in take.Composition?.Segments ?? []) {
                var relative = Path.Combine("segments", segment.Key.ToString("D"));
                await RetainInputsAsync(segment.Source, Path.Combine(sourceFolder, relative), Path.Combine(captured, relative), ct);
            }
            var (tail, localEnd) = H3Motion.Tail(take, endFrameExclusive);
            var count = H3Motion.Window(endFrameExclusive);
            var route = H3Motion.CanUseLatents(take, endFrameExclusive) ? MotionContextRoute.SavedLatents : count == 1 ? MotionContextRoute.SingleFrame : MotionContextRoute.Frames;
            var generation = H3Motion.GenerationFrames(count, addedSeconds);
            var leafFolder = take.Composition is null ? captured : Path.Combine(captured, "segments", tail.Key.ToString("D"));
            if (route == MotionContextRoute.SavedLatents) {
                var package = tail.Source.RefinementPackage!;
                var path = Path.Combine(leafFolder, H3RefinementPackage.FileName);
                await RefinementPackages.VerifyFileAsync(path, package.Bytes, package.Sha256, ct);
                await RefinementPackages.PrepareMotionAsync(path, inputs, (tail.Source.Trim?.SourceStartFrame ?? 0) + localEnd, generation, ct);
            } else {
                for (var i = 0; i < count; i++) {
                    await using var frame = await (frameReader ?? TakeFrameReader.Shared).OpenAsync(captured, take, endFrameExclusive - count + i, ct);
                    await using var output = File.Create(Path.Combine(inputs, $"motion-frame-{i:D2}.png")); await frame.CopyToAsync(output, ct);
                }
                await (mediaTools ?? new ProductionMediaTools()).CaptureMotionAudioAsync(Path.Combine(captured, "video.mp4"), Path.Combine(inputs, "motion-audio.wav"), endFrameExclusive - count, endFrameExclusive, take.Fps, take.Snapshot.Settings, ct);
            }
            var manifest = new List<CapturedMotionFile>();
            foreach (var path in Directory.EnumerateFiles(inputs)) { await using var file = File.OpenRead(path); manifest.Add(new(Path.GetFileName(path), file.Length, Convert.ToHexString(await SHA256.HashDataAsync(file, ct)))); }
            var sourceManifest = new List<CapturedMotionFile>();
            foreach (var relative in TakeBundles.Files(take).Distinct()) { await using var file = File.OpenRead(TakeBundles.Under(captured, relative)); sourceManifest.Add(new(relative, file.Length, Convert.ToHexString(await SHA256.HashDataAsync(file, ct)))); }
            return (new(take, endFrameExclusive, combine, sourceManifest), new(take.Id, route, endFrameExclusive - count, endFrameExclusive, count, generation, manifest));
        }
        catch { if (Directory.Exists(captured)) Directory.Delete(captured, true); if (Directory.Exists(inputs)) Directory.Delete(inputs, true); throw; }
    }
}
