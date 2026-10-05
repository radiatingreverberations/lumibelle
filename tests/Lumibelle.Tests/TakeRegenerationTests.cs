using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private async Task AddRegenerationReferences(QueuedVideoFixture f)
    {
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Room", Category = AssetCategory.Environment };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, _ct);
        using var png = new MemoryStream(AssetStoreTests.Png(32, 16));
        library = await f.Assets.AddImageAsync(f.Project.Id, asset.Id, png, new("room.png", [], AssetImageOrigin.Imported), library.Revision, _ct);
        f.Shot.Images = [new() { AssetId = asset.Id, MediaId = library.Assets[0].Images[0].Id, Name = "Room", Crop = new() { Width = .5 } }];
        f.Shot.Dialogue = [new() { Speaker = "Riley", Text = "Hello." }];
        f.Shot.Voices = [new() { AssetId = Guid.NewGuid(), VoiceId = Guid.NewGuid(), Speaker = "Riley", Duration = 3 }];
        var doc = await f.Shots.LoadAsync(f.Project.Id, _ct);
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], doc.Revision, ct: _ct);
    }

    [Theory]
    [InlineData(false, null, false)] [InlineData(false, true, true)] [InlineData(true, false, false)] [InlineData(true, null, true)]
    public async Task TakeRegenerationCanChangeWhetherLosslessFramesAreSaved(bool source, bool? choice, bool expected)
    {
        using var f = await QueuedVideoFixture.Create(this);
        f.Shot.SaveLosslessFrames = source;
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], (await f.Shots.LoadAsync(f.Project.Id, _ct)).Revision, ct: _ct);
        var original = await f.Capture(); var context = await f.Claim(original);
        await f.Worker.ExecuteAsync(context, original.Snapshot, _ct);
        await f.Jobs.UpdateAsync(context.Job.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var take = Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        Assert.Equal(source, take.HasLosslessFrames);
        var repeat = await f.CaptureService.CaptureRegenerationAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, take.Id, VideoResolution.Quick, take.Seed, choice, _ct);
        var request = repeat.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(expected, request.Snapshot.OutputPolicy!.SaveLosslessFrames);
        Assert.Equal(expected, request.Snapshot.Shot.SaveLosslessFrames);
        H3Presets.Validate(request.Snapshot);
        var next = await f.Claim(repeat); await f.Worker.ExecuteAsync(next, repeat.Snapshot, _ct);
        var regenerated = (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes.Single(t => t.Id != take.Id);
        Assert.Equal(expected, regenerated.HasLosslessFrames);
    }

    [Theory]
    [InlineData(VideoResolution.Quick, 52L)] [InlineData(VideoResolution.Preview, 52L)]
    [InlineData(VideoResolution.Detail, 52L)] [InlineData(VideoResolution.Native, 52L)]
    [InlineData(VideoResolution.Preview, 4711L)] [InlineData(VideoResolution.Quick, null)]
    public async Task TakeRegenerationOwnsExactInputsAndChangesOnlyResolutionAndSeed(VideoResolution resolution, long? seed)
    {
        using var f = await QueuedVideoFixture.Create(this);
        await AddRegenerationReferences(f);
        var original = await f.Capture(); var context = await f.Claim(original);
        await f.Worker.ExecuteAsync(context, original.Snapshot, _ct);
        await f.Jobs.UpdateAsync(context.Job.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var document = await f.Shots.LoadAsync(f.Project.Id, _ct); var source = Assert.Single(document.Takes);
        var captured = original.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        var sourceDirectory = await f.Shots.RunDirectoryAsync(f.Project.Id, source.RunId, _ct);
        var inputBytes = await Task.WhenAll(captured.Inputs.Select(i => File.ReadAllBytesAsync(CapturedInputStore.Resolve(sourceDirectory, i.FileName, i.Sha256), _ct)));
        f.Shot.Description = "New author direction";
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], document.Revision, ct: _ct);
        // References can disappear after capture; regeneration must not consult them again.
        var library = await f.Assets.LoadAsync(f.Project.Id, _ct);
        await f.Assets.DeleteImageAsync(f.Project.Id, f.Shot.Images[0].AssetId, f.Shot.Images[0].MediaId, library.Revision, _ct);
        f.Settings.Value = f.Settings.Value with { H3 = f.Settings.Value.H3 with { Model = "other.safetensors" } };
        var repeat = await f.CaptureService.CaptureRegenerationAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, source.Id, resolution, seed, _ct);
        var request = repeat.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(2, request.Version); Assert.Equal(captured.Inputs, request.Inputs); Assert.Equal(2, request.Inputs.Count);
        Assert.Equal(captured.Snapshot.Prompt, request.Snapshot.Prompt);
        Assert.Equal(captured.Snapshot.Settings, request.Snapshot.Settings); Assert.Equal(captured.Snapshot.ComfyUrl, request.Snapshot.ComfyUrl);
        Assert.Equal(captured.Snapshot.Performance, request.Snapshot.Performance); Assert.Equal(captured.Snapshot.Sampling, request.Snapshot.Sampling);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(captured.Snapshot.Preset), JsonSerializer.SerializeToElement(request.Snapshot.Preset)));
        Assert.Equal(captured.Snapshot.OutputPolicy, request.Snapshot.OutputPolicy);
        Assert.Equal(captured.Snapshot.FrameCount, request.Snapshot.FrameCount);
        Assert.Equal(VideoResolutions.Size(source.Snapshot.Shot.Aspect, resolution), (request.Snapshot.Width, request.Snapshot.Height));
        Assert.Equal(source.Id, request.Snapshot.RegenerationSource!.TakeId);
        if (seed is { } expected) Assert.Equal(expected, repeat.Batch!.Candidates[0].Seed);
        else Assert.NotEqual(source.Seed, repeat.Batch!.Candidates[0].Seed);
        var directory = await f.Shots.RunDirectoryAsync(f.Project.Id, request.BatchId, _ct);
        for (var i = 0; i < request.Inputs.Count; i++) Assert.Equal(inputBytes[i], await File.ReadAllBytesAsync(CapturedInputStore.Resolve(directory, request.Inputs[i].FileName, request.Inputs[i].Sha256), _ct));
        var second = await f.Claim(repeat);
        await f.Jobs.EnqueueAsync(repeat, _ct);
        f.Adapter.FailTransfer = true;
        var error = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(second, repeat.Snapshot, _ct));
        Assert.Equal(AiJobRecovery.RetryOutput, error.Recovery); Assert.Equal(2, f.Graphs.Count);
        f.Adapter.FailTransfer = false;
        await f.Worker.RecoverAsync(f.Context(second.Job, true), repeat.Snapshot, _ct);
        Assert.Equal(2, f.Graphs.Count);
        var graph = f.Graphs[1].GetProperty("prompt");
        Assert.Equal(repeat.Batch!.Candidates[0].Seed, graph.GetProperty("7").GetProperty("inputs").GetProperty("noise_seed").GetInt64());
        var conditioning = graph.EnumerateObject().Single(p => p.Value.GetProperty("class_type").GetString() == "MiniMaxH3ReferenceToVideo").Value.GetProperty("inputs");
        Assert.Equal(request.Snapshot.Width, conditioning.GetProperty("width").GetInt32());
        Assert.Equal(request.Snapshot.Height, conditioning.GetProperty("height").GetInt32());
        document = await f.Shots.LoadAsync(f.Project.Id, _ct);
        Assert.Equal(2, document.Takes.Count); Assert.Equal("New author direction", document.Shots[0].Description);
        Assert.Equal(source.Seed, document.Takes.Single(t => t.Id == source.Id).Seed);
    }

    [Fact]
    public async Task TakeRegenerationRejectsChangedCapturedFiles()
    {
        using var f = await QueuedVideoFixture.Create(this); await AddRegenerationReferences(f);
        var original = await f.Capture(); var context = await f.Claim(original);
        await f.Worker.ExecuteAsync(context, original.Snapshot, _ct);
        var take = Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        var request = original.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        var directory = await f.Shots.RunDirectoryAsync(f.Project.Id, take.RunId, _ct);
        await File.WriteAllBytesAsync(Path.Combine(directory, "inputs", request.Inputs[0].FileName), [0, 1], _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.CaptureService.CaptureRegenerationAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, take.Id, VideoResolution.Native, take.Seed, _ct));
        Assert.Single(f.Graphs);
    }

    [Theory]
    [InlineData("16:9")] [InlineData("9:16")] [InlineData("1:1")]
    public async Task FreshTakeResolutionSurvivesSetupSerializationAndCapture(string aspect)
    {
        using var f = await QueuedVideoFixture.Create(this);
        foreach (var resolution in VideoResolutions.Choices)
        {
            f.Shot.Aspect = aspect; f.Shot.AspectOverride = aspect; VideoResolutions.Select(f.Shot, resolution);
            var setup = ShotCopy.Of(new ProductionComposition { Shot = f.Shot });
            Assert.Equal(resolution, VideoResolutions.Selected(setup.Shot));
            var doc = await f.Shots.LoadAsync(f.Project.Id, _ct); await f.Shots.SaveAsync(f.Project.Id, [f.Shot], doc.Revision, ct: _ct);
            var capture = (await f.Capture()).Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
            Assert.Equal(VideoResolutions.Size(aspect, resolution), (capture.Snapshot.Width, capture.Snapshot.Height));
            AiVideoJobPolicy.Validate(capture);
        }
    }
}
