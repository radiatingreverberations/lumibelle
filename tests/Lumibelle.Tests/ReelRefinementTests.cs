using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData(TakeRefinementMode.Refine)]
    [InlineData(TakeRefinementMode.Rework)]
    public async Task ReelRefinementRetainsQuickSeedHunterContextAndSavesAnIndependentVersion(TakeRefinementMode mode)
    {
        using var f = await QueuedVideoFixture.Create(this);
        var original = await f.CaptureReel(environment: true, resolution: VideoResolution.Quick, keepLatents: true, preset: H3Presets.Beta);
        var first = await f.Claim(original); await f.Worker.ExecuteAsync(first, original.Snapshot, _ct);
        await f.Jobs.UpdateAsync(first.Job.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var source = Assert.Single((await f.Assets.LoadAsync(f.Project.Id, _ct)).Reels);
        Assert.NotNull(source.RefinementPackage);
        Assert.True(source.Generation!.Snapshot.CaptureRefinementData);
        var size = RefinementPolicy.DefaultSize(source.Media.Width, source.Media.Height);
        f.Settings.Value = f.Settings.Value with { H3 = f.Settings.Value.H3 with { Model = "unrelated-new-model.safetensors" } };
        var submission = await RegenerationCapture(f).CaptureRefinementAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, source.Id, mode, size.Width, size.Height, _ct);
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(4, request.Version);
        Assert.Equal(source.Id, submission.Target.ReelId);
        Assert.Equal(source.AssetId, submission.Target.AssetId);
        Assert.Equal(JsonSerializer.Serialize(source.Generation.Snapshot, AtomicJsonFile.Options), JsonSerializer.Serialize(request.Snapshot, AtomicJsonFile.Options));
        Assert.Equal(original.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Inputs, request.Inputs);
        Assert.Equal(source.RefinementPackage, request.Refinement!.SourcePackage);
        Assert.Equal(mode, request.Refinement.Mode);
        var directory = await f.Assets.RunDirectoryAsync(f.Project.Id, request.BatchId, _ct);
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, directory, _ct);
        // Captured latents/conditioning survive removal of temporary source-run outputs.
        Directory.Delete(await f.Assets.RunDirectoryAsync(f.Project.Id, source.Generation.BatchId, _ct), true);
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, directory, _ct);
        f.ReelMedia.Metadata = (size.Width, size.Height, request.Snapshot.FrameCount);
        var second = await f.Claim(submission); await f.Worker.ExecuteAsync(second, submission.Snapshot, _ct);
        var library = await f.Assets.LoadAsync(f.Project.Id, _ct);
        Assert.Equal(2, library.Reels.Count);
        var result = Assert.Single(library.Reels, r => r.Id != source.Id);
        Assert.Equal(JsonSerializer.Serialize(source, AtomicJsonFile.Options), JsonSerializer.Serialize(library.Reels.Single(r => r.Id == source.Id), AtomicJsonFile.Options));
        Assert.Equal(source.Id, result.Refinement!.ParentTakeId);
        Assert.Equal((size.Width, size.Height), (result.RefinementPackage!.Width, result.RefinementPackage.Height));
        Assert.NotEqual(source.RefinementPackage!.Id, result.RefinementPackage.Id);
        Assert.Empty((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        var graph = f.Graphs.Last().GetProperty("prompt");
        Assert.Contains(graph.EnumerateObject(), node => node.Value.GetProperty("class_type").GetString() == "LoadLatent");
        Assert.Contains(graph.EnumerateObject(), node => node.Value.GetProperty("class_type").GetString() == "BasicScheduler" &&
            node.Value.GetProperty("inputs").GetProperty("denoise").GetDouble() == request.Refinement.Denoise);
        await f.Jobs.UpdateAsync(second.Job.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var next = await RegenerationCapture(f).CaptureRefinementAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, result.Id,
            mode, size.Width, size.Height, _ct);
        Assert.Equal(result.RefinementPackage, next.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Refinement!.SourcePackage);
    }

    [Fact]
    public async Task ReelRegenerationCanRetainLatentsWithoutChangingTheOriginalSeedOrRecipe()
    {
        using var f = await QueuedVideoFixture.Create(this);
        var original = await f.CaptureReel(environment: true, resolution: VideoResolution.Quick);
        var first = await f.Claim(original); await f.Worker.ExecuteAsync(first, original.Snapshot, _ct);
        await f.Jobs.UpdateAsync(first.Job.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var source = Assert.Single((await f.Assets.LoadAsync(f.Project.Id, _ct)).Reels);
        var submission = await RegenerationCapture(f).CaptureRegenerationAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, source.Id,
            VideoResolution.Quick, true, null, 1, false, true, _ct);
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.True(request.Snapshot.CaptureRefinementData);
        Assert.True(request.Snapshot.Reel!.Recipe.SaveLatents);
        Assert.Equal(source.Generation!.Seed, Assert.Single(submission.Batch!.Candidates).Seed);
        Assert.Equal((source.Generation.Snapshot.Width, source.Generation.Snapshot.Height), (request.Snapshot.Width, request.Snapshot.Height));
        f.ReelMedia.Metadata = (request.Snapshot.Width, request.Snapshot.Height, request.Snapshot.FrameCount);
        var second = await f.Claim(submission); await f.Worker.ExecuteAsync(second, submission.Snapshot, _ct);
        var library = await f.Assets.LoadAsync(f.Project.Id, _ct);
        Assert.Null(library.Reels.Single(r => r.Id == source.Id).RefinementPackage);
        Assert.NotNull(library.Reels.Single(r => r.Id != source.Id).RefinementPackage);
    }

    [Fact]
    public async Task ReelRefinementRejectsMissingOrCorruptLatentsBeforeQueueing()
    {
        using var f = await QueuedVideoFixture.Create(this);
        var original = await f.CaptureReel(environment: true, resolution: VideoResolution.Quick, keepLatents: true);
        var first = await f.Claim(original); await f.Worker.ExecuteAsync(first, original.Snapshot, _ct);
        var source = Assert.Single((await f.Assets.LoadAsync(f.Project.Id, _ct)).Reels);
        var package = Path.Combine(f.ProjectDirectory, "reference-videos", source.Media.Id.ToString("D"), H3RefinementPackage.FileName);
        await File.AppendAllTextAsync(package, "corrupt", _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => RegenerationCapture(f).CaptureRefinementAsync(Guid.NewGuid(), Guid.NewGuid(),
            f.Project.Id, source.Id, TakeRefinementMode.Refine, 1344, 768, _ct));
        File.Delete(package);
        await Assert.ThrowsAsync<FileNotFoundException>(() => RegenerationCapture(f).CaptureRefinementAsync(Guid.NewGuid(), Guid.NewGuid(),
            f.Project.Id, source.Id, TakeRefinementMode.Refine, 1344, 768, _ct));
        Assert.Single((await f.Jobs.ReadAsync(_ct)).Jobs);
        Assert.Single(f.Graphs);
    }
}
