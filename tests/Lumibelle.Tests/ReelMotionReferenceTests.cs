using System.Text.Json;
using Lumibelle.Testing;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private static AiReelCapture ReelMotionCapture(QueuedVideoFixture f) => new(f.Assets, f.Assets, f.Settings,
        new MockVideoGenerator(f.Assets, f.Shots, f.ReelMedia),
        new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, f.Preferences, f.Jobs, f.ReelMedia);

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task ReelMotionReferencePromotionCreatesStandardVersionWithVideoOnlyGuidanceAndFreshAudio(bool environment)
    {
        using var f = await QueuedVideoFixture.Create(this);
        var original = await f.CaptureReel(environment: environment, existingRecording: !environment, resolution: VideoResolution.Quick, preset: H3Presets.Beta);
        var first = await f.Claim(original); await f.Worker.ExecuteAsync(first, original.Snapshot, _ct);
        await f.Jobs.UpdateAsync(first.Job.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var source = Assert.Single((await f.Assets.LoadAsync(f.Project.Id, _ct)).Reels);
        var capture = ReelMotionCapture(f);
        var draft = await capture.PrepareMotionReferenceAsync(f.Project.Id, source.Id, _ct);
        Assert.Equal(source.Id, draft.Source.ReelId); Assert.Equal(Guid.Empty, draft.Source.TakeId);
        Assert.NotEqual(source.Media.Id, draft.Source.Media.Id);
        Assert.Equal(source.Generation!.Recipe.Prompt, draft.Source.BasePrompt);
        Assert.Equal("standard", draft.Shot.GenerationPreset); Assert.True(draft.Shot.NativeResolution);
        Assert.False(Assert.Single(draft.Shot.Videos).UseSoundtrack);
        Assert.Equal(environment ? 0 : 1, draft.Shot.Voices.Count);
        draft.Reel!.Recipe.CapturedVisualStyle = new() { Preset = VisualStylePreset.Photographic, Direction = "Keep fine brick detail." };
        f.Settings.Value = f.Settings.Value with { H3 = f.Settings.Value.H3 with { Model = "fresh-ordinary-h3.safetensors" } };
        var id = Guid.NewGuid();
        var updates = new PreparationUpdates();
        var submission = await capture.CaptureMotionReferenceAsync(id, Guid.NewGuid(), f.Project.Id,
            new(draft, draft.Source.BasePrompt, "Follow the camera route.", SaveLosslessFrames: true), _ct, updates);
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(VideoPreparationStage.Setup, updates.Values.First().Stage);
        Assert.Equal(VideoPreparationStage.Request, updates.Values.Last().Stage);
        var saved = updates.Values.Last(p => p.Stage == VideoPreparationStage.Capture);
        Assert.Equal(request.Inputs.Count, saved.Completed);
        Assert.Equal(request.Inputs.Count, saved.Total);
        Assert.Contains(updates.Values, p => p.Stage == VideoPreparationStage.References && p.Completed == request.Inputs.Count);
        Assert.Equal(3, request.Version); Assert.NotNull(request.Recipe); Assert.Null(request.Refinement);
        Assert.Equal("standard", request.Snapshot.Preset!.Key);
        Assert.Equal("fresh-ordinary-h3.safetensors", request.Snapshot.Settings.Model);
        Assert.True(request.Snapshot.OutputPolicy!.SaveLosslessFrames);
        Assert.NotNull(request.Snapshot.Reel); Assert.Equal(source.AssetId, submission.Target.AssetId);
        Assert.Single(submission.Batch!.Candidates); Assert.NotEqual(source.Generation.Seed, submission.Batch.Candidates[0].Seed);
        Assert.Single(request.Inputs, i => i.EffectiveKind == VideoInputKind.Video);
        Assert.DoesNotContain(request.Inputs, i => i.EffectiveKind == VideoInputKind.VideoSoundtrack);
        Assert.Equal(environment ? 0 : 1, request.Inputs.Count(i => i.EffectiveKind == VideoInputKind.Audio));
        Assert.Equal(2, request.Snapshot.Prompt.Split("Visual style direction:").Length);
        var directory = await f.Assets.RunDirectoryAsync(f.Project.Id, id, _ct);
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, directory, _ct);
        var second = await f.Claim(submission);
        f.ReelMedia.Metadata = (request.Snapshot.Width, request.Snapshot.Height, request.Snapshot.FrameCount);
        await f.Worker.ExecuteAsync(second, submission.Snapshot, _ct);
        var library = await f.Assets.LoadAsync(f.Project.Id, _ct);
        Assert.Equal(2, library.Reels.Count);
        var result = Assert.Single(library.Reels, r => r.Id != source.Id);
        Assert.Equal(JsonSerializer.Serialize(source, AtomicJsonFile.Options), JsonSerializer.Serialize(library.Reels.Single(r => r.Id == source.Id), AtomicJsonFile.Options));
        Assert.Empty((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        var graph = f.Graphs.Last().GetProperty("prompt");
        Assert.True(graph.GetProperty("5").GetProperty("inputs").TryGetProperty("ref_videos.ref_video_0", out _));
        Assert.False(graph.GetProperty("5").GetProperty("inputs").TryGetProperty("ref_video_audios.ref_video_audio_0", out _));
        Assert.DoesNotContain(graph.EnumerateObject(), node => node.Value.GetProperty("class_type").GetString() == "LoadLatent");
        var nextDraft = await capture.PrepareMotionReferenceAsync(f.Project.Id, result.Id, _ct);
        Assert.Single(nextDraft.Shot.Videos);
        Assert.Equal(draft.Source.BasePrompt, nextDraft.Source.BasePrompt);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => capture.CaptureMotionReferenceAsync(id, Guid.NewGuid(), f.Project.Id,
            new(draft, draft.Source.BasePrompt, "Duplicate"), _ct));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task RefiningAReelKeepsItsPickedKeyframesLosslesslyAndOnTheNewReel(bool keep)
    {
        using var f = await QueuedVideoFixture.Create(this);
        var original = await f.CaptureReel(environment: true, resolution: VideoResolution.Quick);
        var first = await f.Claim(original); await f.Worker.ExecuteAsync(first, original.Snapshot, _ct);
        await f.Jobs.UpdateAsync(first.Job.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var library = await f.Assets.LoadAsync(f.Project.Id, _ct); var source = Assert.Single(library.Reels);
        ReelFrameIdentity Pick(int i) => new(source.Media.Id, source.Media.Sha256, i, i / 24d);
        var last = source.Media.Frames - 1;
        await f.Assets.SaveKeyframesAsync(f.Project.Id, source, new() { Frames = [
            new() { Frame = Pick(0), Name = "Doorway", Notes = "Opening view", Crop = new() { X = .1, Y = .1, Width = .5, Height = .5 } },
            new() { Frame = Pick(last), Notes = "Window" }] }, library.Revision, _ct);
        source = Assert.Single((await f.Assets.LoadAsync(f.Project.Id, _ct)).Reels);
        var capture = ReelMotionCapture(f);
        var draft = await capture.PrepareMotionReferenceAsync(f.Project.Id, source.Id, _ct);
        Assert.Equal(2, draft.Source.SourceKeyframes!.Count);
        var submission = await capture.CaptureMotionReferenceAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id,
            new(draft, draft.Source.BasePrompt, "Keep the route.", SaveLosslessFrames: false, KeepKeyframes: keep), _ct);
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.False(request.Snapshot.CaptureRefinementData);
        if (keep)
        {
            // Without the full archive, exactly the picked moments are kept: first and last stay first and last.
            Assert.Equal([0, request.Snapshot.FrameCount - 1], request.Snapshot.OutputPolicy!.KeptFrames);
            Assert.Equal(2, request.Snapshot.MotionReference!.SourceKeyframes!.Count);
        }
        else { Assert.Null(request.Snapshot.OutputPolicy!.KeptFrames); Assert.Null(request.Snapshot.MotionReference!.SourceKeyframes); }
        var second = await f.Claim(submission);
        f.ReelMedia.Metadata = (request.Snapshot.Width, request.Snapshot.Height, request.Snapshot.FrameCount);
        await f.Worker.ExecuteAsync(second, submission.Snapshot, _ct);
        var result = Assert.Single((await f.Assets.LoadAsync(f.Project.Id, _ct)).Reels, r => r.Id != source.Id);
        if (!keep) { Assert.Null(result.Keyframes); return; }
        var carried = result.Keyframes!.Frames;
        Assert.Equal([0, result.Media.Frames - 1], carried.Select(k => k.Frame.Index));
        Assert.All(carried, k => Assert.Equal(result.Media.Id, k.Frame.MediaId));
        Assert.Equal(("Doorway", "Opening view", .5), (carried[0].Name, carried[0].Notes, carried[0].Crop!.Width));
        Assert.Equal("Window", carried[1].Notes);
        Assert.DoesNotContain(carried, k => source.Keyframes!.Frames.Any(s => s.Id == k.Id));
    }

    private sealed class PreparationUpdates : IProgress<VideoPreparationProgress>
    {
        public List<VideoPreparationProgress> Values { get; } = [];
        public void Report(VideoPreparationProgress value) => Values.Add(value);
    }

    [Fact]
    public async Task ReelMotionReferenceCaptureAndRetryRemainIndependentOfDeletedSource()
    {
        using var f = await QueuedVideoFixture.Create(this);
        var original = await f.CaptureReel(environment: true);
        var first = await f.Claim(original); await f.Worker.ExecuteAsync(first, original.Snapshot, _ct);
        var library = await f.Assets.LoadAsync(f.Project.Id, _ct); var source = Assert.Single(library.Reels);
        var capture = ReelMotionCapture(f);
        var draft = await capture.PrepareMotionReferenceAsync(f.Project.Id, source.Id, _ct);
        await f.Assets.TrashReelAsync(f.Project.Id, source.Id, library.Revision, _ct);
        var submission = await capture.CaptureMotionReferenceAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id,
            new(draft, draft.Source.BasePrompt, "Keep the route."), _ct);
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Directory.Delete(await f.Assets.RunDirectoryAsync(f.Project.Id, original.Id, _ct), true);
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, await f.Assets.RunDirectoryAsync(f.Project.Id, request.BatchId, _ct), _ct);
        var changed = request with { Snapshot = request.Snapshot with { MotionReference = request.Snapshot.MotionReference! with { ReelId = null, TakeId = source.Id } } };
        Assert.Throws<WorkspaceStoreException>(() => AiVideoJobPolicy.Validate(changed));
    }
}
