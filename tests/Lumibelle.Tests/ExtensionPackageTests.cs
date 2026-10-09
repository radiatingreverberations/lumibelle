using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using lumibelle.Services.AI;
using Lumibelle.Testing;

namespace Lumibelle.Tests;

public sealed partial class ProjectPackageTests
{
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task CombinedTakeRoundTripsAllOwnedBundlesAndCanReplayWithoutParentsOrQueue(bool refined, bool leading)
    {
        var ct = TestContext.Current.CancellationToken;
        using var source = new Fixture(); using var target = new Fixture(); var project = await source.Create();
        var snapshot = Snapshot(project.Id, "standard") with { CaptureRefinementData = true };
        var originalId = Guid.NewGuid(); var originalDir = $"shots/takes/{originalId:D}";
        await source.Bytes(project, originalDir + "/video.mp4", [1, 2, 3]);
        var originalPackage = await MockRefinementPackage.WriteAsync(Path.Combine(source.Root(project), originalDir), snapshot, null, ct);
        var original = new ShotTake { Id = originalId, ShotId = snapshot.Shot.Id, Directory = originalId.ToString("D"), RunId = Guid.NewGuid(), Snapshot = snapshot,
            Width = snapshot.Width, Height = snapshot.Height, Candidate = 1, RetainedSource = new([]), RefinementPackage = originalPackage };
        var motionFile = new CapturedMotionFile("motion-frame-00.png", Pixel.Length, Convert.ToHexString(SHA256.HashData(Pixel)));
        var motion = new TakeMotionContext(originalId, MotionContextRoute.SingleFrame, 38, 39, 1, snapshot.FrameCount, [motionFile]) { Direction = leading ? TakeExtensionDirection.Before : TakeExtensionDirection.After };
        if (leading) motion = motion with { StartFrame = 0, EndFrameExclusive = 1 };
        var outputSnapshot = snapshot with { Motion = motion, Profile = H3Motion.Profile };
        var fullId = Guid.NewGuid(); var fullDir = $"shots/takes/{fullId:D}";
        await source.Bytes(project, fullDir + "/video.mp4", [1, 2, 3]);
        var outputPackage = await MockRefinementPackage.WriteAsync(Path.Combine(source.Root(project), fullDir), outputSnapshot, null, ct);
        var full = original with { Id = fullId, Directory = fullId.ToString("D"), RunId = Guid.NewGuid(), Snapshot = outputSnapshot, RefinementPackage = outputPackage };
        if (refined) {
            full.Refinement = new(originalId, originalPackage, TakeRefinementMode.Refine, full.Width, full.Height, "h3.safetensors", H3UpscalerImplementation.Lbh);
            full.RetainedSource = new([]) { RefinementInput = originalPackage };
            full.Frames = await MockFrameArchive.WriteAsync(Path.Combine(source.Root(project), fullDir), snapshot.FrameCount, full.Width, full.Height, ct);
            await source.Bytes(project, fullDir + "/refinement-inputs/" + TakeTrimming.RefinementInputFile,
                await File.ReadAllBytesAsync(Path.Combine(source.Root(project), originalDir, H3RefinementPackage.FileName), ct));
        }
        await source.Bytes(project, fullDir + "/refinement-inputs/" + motionFile.FileName, Pixel);
        var takeId = Guid.NewGuid(); var folder = Path.Combine(source.Root(project), "shots", "takes", takeId.ToString("D"));
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var capturedFiles = await TakeBundles.CopyAsync(original, Path.Combine(source.Root(project), originalDir), Path.Combine(folder, H3Motion.SourceFolder), ct);
        await TakeBundles.CopyAsync(original, Path.Combine(source.Root(project), originalDir), Path.Combine(folder, "segments", a.ToString("D")), ct);
        await TakeBundles.CopyAsync(full, Path.Combine(source.Root(project), fullDir), Path.Combine(folder, "segments", b.ToString("D")), ct);
        await TakeBundles.CopyAsync(full, Path.Combine(source.Root(project), fullDir), folder, ct);
        var combined = full with { Id = takeId, Directory = takeId.ToString("D"), Frames = [], Composition = new(leading ? [new(b, full, 0, 38), new(a, original, 0, 39)] : [new(a, original, 0, 39), new(b, full, 1, 39)], 38) { GeneratedSegmentKey = b }, Extension = new(original, 39, true, capturedFiles) { Direction = motion.Direction } };
        await source.Save(project, "shots.json", new ShotDocument { ProjectId = project.Id, Shots = [snapshot.Shot], Takes = [combined] });
        var export = await source.Service.ExportAsync(project.Id, new(), ct: ct);
        await using var stream = (await source.Service.OpenExportAsync(project.Id, export.Id, ct))!;
        var pending = await target.Service.StageImportAsync(stream.Content, ct: ct); await target.Service.CommitImportAsync(pending.Token, ct);
        var store = new FileShotStore(target.Files, TimeProvider.System);
        var imported = Assert.Single((await store.LoadAsync(project.Id, ct)).Takes);
        Assert.Equal(77, imported.FrameCount); Assert.Equal(2, imported.Composition!.Segments.Count);
        var importedFolder = Path.Combine(target.Root(project), "shots", "takes", takeId.ToString("D"));
        Assert.Equal(Directory.EnumerateFiles(importedFolder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length), imported.Bytes);
        var replay = await store.CaptureExtensionVersionAsync(project.Id, imported.Id, Guid.NewGuid(), ct);
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(replay, await store.RunDirectoryAsync(project.Id, replay.BatchId, ct), ct);
        Assert.Equal(leading ? new TakeTrimRange(0, 38) : new(1, 39), replay.OutputTrim);
        Assert.Equal(b, imported.Composition.GeneratedSegmentKey); Assert.Equal(motion.Direction, replay.Snapshot.Motion!.Direction);
        Assert.Equal(Pixel, await File.ReadAllBytesAsync(Path.Combine(importedFolder, TakeTrimming.InputsFolder, motionFile.FileName), ct));
        Assert.Equal(outputPackage.Sha256, imported.RefinementPackage!.Sha256);
        if (refined) Assert.Equal(originalPackage, replay.Refinement!.SourcePackage);
    }
}
