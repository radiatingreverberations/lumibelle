using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private static AiReelCapture RegenerationCapture(QueuedVideoFixture f) => new(f.Assets, f.Assets, f.Settings, f.Generator,
        new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, f.Preferences, f.Jobs);

    [Fact]
    public async Task ReelRegenerationQueuesChosenTakeCountWithDistinctSeedsAndExactInputs()
    {
        using var f = await QueuedVideoFixture.Create(this);
        var submission = await f.CaptureReel(environment: true);
        var first = await f.Claim(submission); await f.Worker.ExecuteAsync(first, submission.Snapshot, _ct);
        await f.Jobs.UpdateAsync(first.Job.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var source = Assert.Single((await f.Assets.LoadAsync(f.Project.Id, _ct)).Reels);
        var repeat = await RegenerationCapture(f).CaptureRegenerationAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id,
            source.Id, VideoResolution.Quick, false, null, 3, _ct);
        Assert.Equal(3, repeat.Batch!.Candidates.Count);
        Assert.Equal(3, repeat.Batch.Candidates.Select(c => c.Seed).Distinct().Count());
        Assert.DoesNotContain(repeat.Batch.Candidates, c => c.Seed == source.Generation!.Seed);
        var request = repeat.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(source.Generation!.Snapshot.Prompt, request.Snapshot.Prompt);
        Assert.Equal(submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Inputs, request.Inputs);
        var next = await f.Claim(repeat);
        await f.Jobs.EnqueueAsync(repeat, _ct);
        await f.Worker.ExecuteAsync(next, repeat.Snapshot, _ct);
        Assert.Equal(4, (await f.Assets.LoadAsync(f.Project.Id, _ct)).Reels.Count);
        Assert.Equal(2, f.Graphs.Count); // The three new takes share one multi-take workflow.
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => RegenerationCapture(f).CaptureRegenerationAsync(
            Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, source.Id, VideoResolution.Quick, true, null, 3, _ct));
    }

    [Theory]
    [InlineData(false, null, false)] [InlineData(false, true, true)] [InlineData(true, false, false)] [InlineData(true, null, true)]
    public async Task ReelRegenerationCanChangeWhetherLosslessFramesAreKept(bool source, bool? choice, bool expected)
    {
        using var f = await QueuedVideoFixture.Create(this);
        var submission = await f.CaptureReel(environment: true, keepFrames: source);
        var context = await f.Claim(submission); await f.Worker.ExecuteAsync(context, submission.Snapshot, _ct);
        var reel = Assert.Single((await f.Assets.LoadAsync(f.Project.Id, _ct)).Reels);
        var repeat = await RegenerationCapture(f).CaptureRegenerationAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, reel.Id,
            VideoResolution.Quick, false, null, 1, choice, _ct);
        var captured = repeat.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        // The recipe, shot inputs and output policy agree, so the request passes the preset validation.
        Assert.Equal(expected, captured.Snapshot.OutputPolicy!.SaveLosslessFrames);
        Assert.Equal(expected, captured.Snapshot.Shot.SaveLosslessFrames);
        Assert.Equal(expected, captured.Snapshot.Reel!.Recipe.SaveLosslessFrames ?? false);
        H3Presets.Validate(captured.Snapshot);
        Assert.Equal(source, reel.Generation!.Snapshot.OutputPolicy!.SaveLosslessFrames);
    }

    [Theory]
    [InlineData(4711L)] [InlineData(null)]
    public async Task ReelRegenerationCanExploreAnotherSeed(long? seed)
    {
        using var f = await QueuedVideoFixture.Create(this);
        var submission = await f.CaptureReel(environment: true);
        var context = await f.Claim(submission); await f.Worker.ExecuteAsync(context, submission.Snapshot, _ct);
        var source = Assert.Single((await f.Assets.LoadAsync(f.Project.Id, _ct)).Reels);
        var repeat = await RegenerationCapture(f).CaptureRegenerationAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, source.Id, VideoResolution.Quick, false, seed, _ct);
        var captured = repeat.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        if (seed is { } expected) Assert.Equal(expected, repeat.Batch!.Candidates[0].Seed);
        else Assert.NotEqual(source.Generation!.Seed, repeat.Batch!.Candidates[0].Seed);
        Assert.Equal(source.Generation!.Seed, captured.Snapshot.Reel!.RegenerationSource!.Seed);
        Assert.Equal(source.Generation.Snapshot.Prompt, captured.Snapshot.Prompt);
    }

    [Theory]
    [InlineData(false, VideoResolution.Quick)] [InlineData(false, VideoResolution.Preview)] [InlineData(false, VideoResolution.Detail)] [InlineData(false, VideoResolution.Native)]
    [InlineData(true, VideoResolution.Quick)] [InlineData(true, VideoResolution.Preview)] [InlineData(true, VideoResolution.Detail)] [InlineData(true, VideoResolution.Native)]
    public async Task ReelRegenerationReusesSeedAndCapturedInputsAtChosenResolution(bool environment, VideoResolution resolution)
    {
        using var f = await QueuedVideoFixture.Create(this);
        var lora = VideoLora(server: f.Settings.Value.ComfyUrl);
        f.Settings.Value = f.Settings.Value with { LoraLibrary = [new(lora.Reference)] };
        var submission = await f.CaptureReel(loras: [lora], environment: environment, keepFrames: false, existingRecording: !environment);
        var original = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        var first = await f.Claim(submission);
        await f.Worker.ExecuteAsync(first, submission.Snapshot, _ct);
        await f.Jobs.UpdateAsync(first.Job.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var library = await f.Assets.LoadAsync(f.Project.Id, _ct);
        var source = Assert.Single(library.Reels);
        var draft = library.ReelDrafts.Single().Copy(); draft.Prompt = "Unrelated new draft"; draft.NativeResolution = resolution != VideoResolution.Native;
        await f.Assets.SaveDraftAsync(f.Project.Id, draft, draft.Revision, _ct);
        f.Settings.Value = f.Settings.Value with { H3 = f.Settings.Value.H3 with { Model = "different.safetensors" } };

        var repeat = await RegenerationCapture(f).CaptureRegenerationAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, source.Id, resolution, _ct);
        var request = repeat.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        var seed = Assert.Single(repeat.Batch!.Candidates).Seed;
        Assert.Equal(source.Generation!.Seed, seed);
        Assert.NotEqual(original.BatchId, request.BatchId);
        Assert.Equal(VideoResolutions.Size(original.Snapshot.Shot.Aspect, resolution), (request.Snapshot.Width, request.Snapshot.Height));
        Assert.Equal(resolution, VideoResolutions.Selected(request.Snapshot.Reel!.Recipe));
        Assert.Equal(original.Snapshot.Prompt, request.Snapshot.Prompt);
        Assert.Equal(original.Snapshot.ComfyUrl, request.Snapshot.ComfyUrl);
        Assert.Equal(original.Snapshot.Settings, request.Snapshot.Settings);
        Assert.Equal(original.Snapshot.Sampling, request.Snapshot.Sampling);
        Assert.Equal(original.Snapshot.FrameCount, request.Snapshot.FrameCount);
        Assert.Equal(original.Snapshot.OutputPolicy, request.Snapshot.OutputPolicy);
        Assert.Equal(original.Snapshot.AppliedLoras, request.Snapshot.AppliedLoras);
        Assert.Equal(environment ? 0 : 1, request.Inputs.Count(i => i.Audio));
        Assert.Equal(original.Inputs, request.Inputs);
        Assert.Equal(source.Id, request.Snapshot.Reel!.RegenerationSource!.ReelId);
        Assert.Equal(source.Generation.Seed, request.Snapshot.Reel.RegenerationSource.Seed);
        Assert.Equal("Unrelated new draft", (await f.Assets.LoadAsync(f.Project.Id, _ct)).ReelDrafts.Single().Prompt);
        var sourceDirectory = await f.Assets.RunDirectoryAsync(f.Project.Id, original.BatchId, _ct);
        var directory = await f.Assets.RunDirectoryAsync(f.Project.Id, request.BatchId, _ct);
        foreach (var input in request.Inputs)
            Assert.Equal(await File.ReadAllBytesAsync(CapturedInputStore.Resolve(sourceDirectory, input.FileName, input.Sha256), _ct),
                await File.ReadAllBytesAsync(CapturedInputStore.Resolve(directory, input.FileName, input.Sha256), _ct));
        // The regenerated reel does not depend on the source run's folder; both read the project's input store.
        Directory.Delete(sourceDirectory, true);
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, directory, _ct);
        var second = await f.Claim(repeat);
        await f.Jobs.EnqueueAsync(repeat, _ct); // A lost enqueue acknowledgement must not create another candidate.
        await f.Worker.ExecuteAsync(second, repeat.Snapshot, _ct);
        Assert.Equal(2, f.Graphs.Count);
        var graph = f.Graphs[1].GetProperty("prompt");
        Assert.Equal(seed, graph.GetProperty("7").GetProperty("inputs").GetProperty("noise_seed").GetInt64());
        var conditioning = graph.EnumerateObject().Single(n => n.Value.GetProperty("class_type").GetString() == "MiniMaxH3ReferenceToVideo").Value.GetProperty("inputs");
        Assert.Equal(request.Snapshot.Width, conditioning.GetProperty("width").GetInt32());
        Assert.Equal(request.Snapshot.Height, conditioning.GetProperty("height").GetInt32());
        Assert.Equal(2, (await f.Assets.LoadAsync(f.Project.Id, _ct)).Reels.Count);
    }

    [Fact]
    public async Task ReelRegenerationRejectsChangedCapturedBytesBeforeEnqueue()
    {
        using var f = await QueuedVideoFixture.Create(this);
        var submission = await f.CaptureReel(environment: true); var context = await f.Claim(submission);
        await f.Worker.ExecuteAsync(context, submission.Snapshot, _ct);
        var source = Assert.Single((await f.Assets.LoadAsync(f.Project.Id, _ct)).Reels);
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        var directory = await f.Assets.RunDirectoryAsync(f.Project.Id, request.BatchId, _ct);
        await File.WriteAllBytesAsync(Path.Combine(directory, "inputs", request.Inputs[0].FileName), [9, 8, 7], _ct);
        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => RegenerationCapture(f).CaptureRegenerationAsync(
            Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, source.Id, VideoResolution.Native, _ct));
        Assert.Contains("changed", error.Message);
        Assert.Single(f.Graphs);
        Assert.Single((await f.Jobs.ReadAsync(_ct)).Jobs);
    }

    [Fact]
    public async Task ReelRegenerationDownloadRetryRetainsSeedAndDoesNotRepeatInference()
    {
        using var f = await QueuedVideoFixture.Create(this);
        var submission = await f.CaptureReel(environment: true); var context = await f.Claim(submission);
        await f.Worker.ExecuteAsync(context, submission.Snapshot, _ct);
        await f.Jobs.UpdateAsync(context.Job.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var source = Assert.Single((await f.Assets.LoadAsync(f.Project.Id, _ct)).Reels);
        var repeat = await RegenerationCapture(f).CaptureRegenerationAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, source.Id, VideoResolution.Detail, _ct);
        var second = await f.Claim(repeat);
        f.Adapter.FailTransfer = true;
        var error = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(second, repeat.Snapshot, _ct));
        Assert.Equal(AiJobRecovery.RetryOutput, error.Recovery);
        Assert.Equal(2, f.Graphs.Count);
        f.Adapter.FailTransfer = false;
        await f.Worker.RecoverAsync(f.Context(second.Job, true), repeat.Snapshot, _ct);
        Assert.Equal(2, f.Graphs.Count);
        var saved = (await f.Assets.LoadAsync(f.Project.Id, _ct)).Reels.Single(r => r.Id != source.Id);
        Assert.Equal(source.Generation!.Seed, saved.Generation!.Seed);
        Assert.Equal((1120, 640), (saved.Generation.Snapshot.Width, saved.Generation.Snapshot.Height));
    }
}
