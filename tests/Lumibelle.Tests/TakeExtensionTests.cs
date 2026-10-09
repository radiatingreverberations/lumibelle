using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task CancelledExtensionAssemblyRetriesAfterReloadWithoutGeneration()
    {
        var media = new TrimTestMedia(); using var f = await QueuedVideoFixture.Create(this, trimMedia: media);
        f.Shot.Voices = [new() { AssetId = Guid.NewGuid(), VoiceId = Guid.NewGuid(), Speaker = "Riley", Duration = 1 }];
        f.Shot.Dialogue = [new() { Speaker = "Riley", Text = "We keep moving." }];
        f.Shot.SaveLosslessFrames = true; f.Adapter.RealFrames = true;
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], (await f.Shots.LoadAsync(f.Project.Id, _ct)).Revision, ct: _ct);
        var initial = await f.Capture(); await f.Worker.ExecuteAsync(await f.Claim(initial), initial.Snapshot, _ct);
        await f.Jobs.UpdateAsync(initial.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var source = Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.CaptureService.CaptureExtensionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, new(source.Id, 0, 1, "Continue.", [], null), _ct));
        var submission = await f.CaptureService.CaptureExtensionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, new(source.Id, source.FrameCount, 1, "Continue.", [], null), _ct);
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Empty(request.Snapshot.Shot.Dialogue); Assert.Single(request.Snapshot.Shot.Voices);
        var recorded = AiVideoJobPolicy.Run(request);
        recorded.Candidates.Add(new() { Number = 1, TakeId = Guid.NewGuid(), ClientId = Guid.NewGuid().ToString("D") });
        await f.Shots.SaveRunAsync(recorded, _ct);
        Assert.Contains(await f.Shots.RunsAsync(f.Project.Id, _ct), r => r.Id == submission.Id);
        media.Fail = true;
        await Assert.ThrowsAsync<AiJobRecoveryException>(() => Run());
        async Task Run() => await f.Worker.ExecuteAsync(await f.Claim(submission), submission.Snapshot, _ct);
        var full = (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes.Single(t => t.RunId == submission.Id);
        media.Fail = false; f.NoNetwork = true;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        media.OnTrim = ct => { cancelled.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Shots.PublishExtensionAsync(f.Project.Id, request, full.Id, cancelled.Token));
        Assert.DoesNotContain((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes, t => t.Composition is not null);
        media.OnTrim = null;
        var reloaded = new FileShotStore(f.Files, _clock, mediaTools: media, jobs: f.Jobs);
        var saved = await reloaded.PublishExtensionAsync(f.Project.Id, request, full.Id, _ct);
        var combined = saved.Takes.Single(t => t.Id == H3Motion.OutputId(full.Id));
        Assert.Equal(2, f.Graphs.Count); Assert.True(combined.HasVisibleLosslessFrames); Assert.False(combined.HasLosslessFrames);
        Assert.True(combined.IsLosslessFrame(source.FrameCount - 1)); Assert.False(combined.IsLosslessFrame(source.FrameCount));
        Assert.Equal(saved.Revision, (await reloaded.PublishExtensionAsync(f.Project.Id, request, full.Id, _ct)).Revision);
        Assert.Empty(Directory.EnumerateDirectories(await reloaded.RunDirectoryAsync(f.Project.Id, submission.Id, _ct), "assembly-*"));
    }
    [Fact]
    public async Task SuccessiveExtensionsFlattenAndTrimmingKeepsTheReplayRange()
    {
        var media = new TrimTestMedia(); using var f = await QueuedVideoFixture.Create(this, trimMedia: media);
        f.Shot.SaveLosslessFrames = true; f.Adapter.RealFrames = true;
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], (await f.Shots.LoadAsync(f.Project.Id, _ct)).Revision, ct: _ct);
        var initial = await f.Capture(); await f.Worker.ExecuteAsync(await f.Claim(initial), initial.Snapshot, _ct);
        await f.Jobs.UpdateAsync(initial.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var source = Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        for (var n = 0; n < 3; n++) {
            var submission = await f.CaptureService.CaptureExtensionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, new(source.Id, source.FrameCount, 5, "The movement continues.", [], null, SaveLosslessFrames: true), _ct);
            await f.Worker.ExecuteAsync(await f.Claim(submission), submission.Snapshot, _ct);
            await f.Jobs.UpdateAsync(submission.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
            source = (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes.Single(t => t.RunId == submission.Id && t.Composition is not null);
            Assert.Equal(n + 2, source.Composition!.Segments.Count);
            Assert.All(source.Composition.Segments, s => { Assert.Null(s.Source.Composition); Assert.Null(s.Source.Extension); });
        }
        Assert.True(source.FrameCount > 362); Assert.True(source.Snapshot.FrameCount <= 362);
        var id = Guid.NewGuid(); var d = await f.Shots.TrimTakeAsync(f.Project.Id, new(source.Id, id, 7, source.FrameCount - 3), ct: _ct);
        var trimmed = d.Takes.Single(t => t.Id == id);
        Assert.Equal(source.FrameCount - 10, trimmed.FrameCount);
        var version = await f.CaptureService.CaptureExtensionVersionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, id, _ct);
        var request = version.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(7, request.Extension!.PrefixStartFrame); Assert.Equal(source.Snapshot.FrameCount - 3, request.OutputTrim!.EndFrameExclusive);
        await f.Worker.ExecuteAsync(await f.Claim(version), version.Snapshot, _ct);
        var replayed = (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes.Single(t => t.RunId == version.Id && t.Composition is not null);
        Assert.Equal(trimmed.FrameCount, replayed.FrameCount);
        await f.Jobs.UpdateAsync(version.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        // Removing the newest generation exposes an earlier extension as the final segment.
        var previousEnd = source.FrameCount - (source.Composition!.Segments[^1].EndFrameExclusive - source.Composition.Segments[^1].StartFrame);
        var cutBackId = Guid.NewGuid();
        var cutBack = (await f.Shots.TrimTakeAsync(f.Project.Id, new(source.Id, cutBackId, 3, previousEnd - 2), ct: _ct)).Takes.Single(t => t.Id == cutBackId);
        Assert.Null(cutBack.Extension); Assert.True(TakeDisplay.HasExtensionReplay(cutBack));
        var olderVersion = await f.CaptureService.CaptureExtensionVersionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, cutBack.Id, _ct);
        var olderRequest = olderVersion.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.True(olderRequest.Extension!.ReplayLastSegment);
        await f.Worker.ExecuteAsync(await f.Claim(olderVersion), olderVersion.Snapshot, _ct);
        var olderResult = (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes.Single(t => t.RunId == olderVersion.Id && t.Composition is not null);
        Assert.Equal(cutBack.FrameCount, olderResult.FrameCount);
    }
    [Theory]
    [InlineData(39, 5, 175)] [InlineData(22, 5, 158)] [InlineData(5, 1, 39)] [InlineData(1, 1, 39)]
    public void MotionDurationsRoundUpIncludingTheContext(int context, double seconds, int expected)
    {
        Assert.Equal(expected, H3Motion.GenerationFrames(context, seconds));
        Assert.Equal(65, H3Motion.AudioBoundary(39));
        Assert.Equal(12, H3Motion.VideoSteps(39));
        Assert.Throws<WorkspaceStoreException>(() => H3Motion.GenerationFrames(context, 16));
        Assert.Throws<WorkspaceStoreException>(() => H3Motion.GenerationFrames(context, double.NaN));
    }

    [Theory] [InlineData(TakeRefinementMode.Refine)] [InlineData(TakeRefinementMode.Rework)]
    public async Task ExtensionOwnsItsSourcesAndRecoversLocalAssemblyThenRefinesTheFinalSegment(TakeRefinementMode mode)
    {
        var media = new TrimTestMedia(); using var f = await QueuedVideoFixture.Create(this, trimMedia: media);
        f.Shot.SaveLosslessFrames = true; f.Adapter.RealFrames = true;
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], (await f.Shots.LoadAsync(f.Project.Id, _ct)).Revision, ct: _ct);
        var initial = await f.Capture(); var firstContext = await f.Claim(initial);
        await f.Worker.ExecuteAsync(firstContext, initial.Snapshot, _ct);
        await f.Jobs.UpdateAsync(initial.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var document = await f.Shots.LoadAsync(f.Project.Id, _ct); var parent = Assert.Single(document.Takes);
        media.Width = parent.Width; media.Height = parent.Height;
        var submission = await f.CaptureService.CaptureExtensionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id,
            new(parent.Id, parent.FrameCount, 1, "She keeps walking forward.", [], null, SaveLosslessFrames: true), _ct);
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(MotionContextRoute.SavedLatents, request.Snapshot.Motion!.Route);
        Assert.Empty(request.Snapshot.Shot.Dialogue); Assert.Equal("standard", request.Snapshot.Shot.GenerationPreset);
        Assert.Equal(parent.RefinementPackage!.Sha256, request.Extension!.Source.RefinementPackage!.Sha256);
        document = await f.Shots.DiscardAsync(f.Project.Id, parent.Id, ShotTrashKind.Take, document.Revision, _ct);
        await f.Shots.PurgeAsync(f.Project.Id, document.Trash.Select(t => t.Id).ToArray(), document.Revision, _ct);
        var context = await f.Claim(submission); media.Fail = true;
        var failure = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, submission.Snapshot, _ct));
        Assert.Equal(AiJobRecovery.RetryOutput, failure.Recovery); Assert.Equal(2, f.Graphs.Count);
        var full = Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        Assert.Null(full.Composition);
        media.Fail = false; f.NoNetwork = true;
        await f.Worker.RecoverAsync(f.Context(context.Job, true), submission.Snapshot, _ct);
        document = await f.Shots.LoadAsync(f.Project.Id, _ct);
        var combined = document.Takes.Single(t => t.Id == H3Motion.OutputId(full.Id));
        Assert.Equal(parent.FrameCount + full.FrameCount - 39, combined.FrameCount);
        Assert.Equal(2, combined.Composition!.Segments.Count); Assert.Equal(parent.RefinementPackage, combined.Composition.Segments[0].Source.RefinementPackage);
        foreach (var index in new[] { 0, 23, 24, parent.FrameCount - 1 }) {
            await using var before = await TakeFrameReader.Shared.OpenAsync(Path.Combine(await f.Shots.RunDirectoryAsync(f.Project.Id, submission.Id, _ct), H3Motion.SourceFolder), request.Extension.Source, index, _ct);
            await using var after = await f.Shots.OpenAsync(f.Project.Id, combined.Id, ShotTrashKind.Take, index, ct: _ct);
            Assert.Equal(await System.Security.Cryptography.SHA256.HashDataAsync(before, _ct), await System.Security.Cryptography.SHA256.HashDataAsync(after!.Content, _ct));
        }
        Assert.Null(document.Shots[0].SelectedTakeId); Assert.Equal(f.Shot.Duration, document.Shots[0].Duration);
        Assert.Equal(full.RefinementPackage, combined.RefinementPackage);
        await f.Jobs.UpdateAsync(submission.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var refinement = await f.CaptureService.CaptureRefinementAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, combined.Id, mode, full.Width, full.Height, _ct);
        var refinedRequest = refinement.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(new TakeTrimRange(39, full.FrameCount), refinedRequest.OutputTrim);
        Assert.Equal(parent.FrameCount, refinedRequest.Extension!.RetainedFrames);
        Assert.Equal(full.FrameCount, refinedRequest.Snapshot.FrameCount); Assert.Equal(full.RefinementPackage, refinedRequest.Refinement!.SourcePackage);
        var idempotent = await f.Shots.PublishExtensionAsync(f.Project.Id, request, full.Id, _ct);
        Assert.Equal(document.Revision, idempotent.Revision);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Shots.PublishExtensionAsync(f.Project.Id, request with { Snapshot = request.Snapshot with { Prompt = request.Snapshot.Prompt + " Changed." } }, full.Id, _ct));
        f.NoNetwork = false;
        await f.Worker.ExecuteAsync(await f.Claim(refinement), refinement.Snapshot, _ct);
        await f.Jobs.UpdateAsync(refinement.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        document = await f.Shots.LoadAsync(f.Project.Id, _ct);
        var refinedCombined = document.Takes.Single(t => t.RunId == refinement.Id && t.Composition is not null);
        Assert.Equal(combined.FrameCount, refinedCombined.FrameCount);
        Assert.Equal(combined.Composition.Segments[0].Source.RefinementPackage, refinedCombined.Composition!.Segments[0].Source.RefinementPackage);
        Assert.NotEqual(combined.RefinementPackage!.Id, refinedCombined.RefinementPackage!.Id);
        var further = await f.CaptureService.CaptureRefinementAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, refinedCombined.Id, mode, full.Width, full.Height, _ct);
        Assert.Equal(refinedCombined.RefinementPackage, further.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Refinement!.SourcePackage);
        var anotherRefined = await f.CaptureService.CaptureExtensionVersionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, refinedCombined.Id, _ct);
        Assert.Equal(combined.RefinementPackage, anotherRefined.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Refinement!.SourcePackage);
        Assert.Equal(combined.RefinementPackage, refinedCombined.RetainedSource!.RefinementInput);
        var another = await f.CaptureService.CaptureExtensionVersionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, combined.Id, _ct);
        Assert.Equal(JsonSerializer.Serialize(request.Snapshot.Motion, AtomicJsonFile.Options), JsonSerializer.Serialize(another.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot.Motion, AtomicJsonFile.Options));
        var repeated = await f.CaptureService.CaptureExtensionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, new(combined.Id, combined.FrameCount, 1, "She turns the corner.", [], null), _ct);
        Assert.Equal(MotionContextRoute.Frames, repeated.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot.Motion!.Route);
        Assert.Equal(39, repeated.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot.Motion!.Frames);
        // Replay a refined final segment after adding, then trimming away, a newer extension.
        var afterRefined = await f.CaptureService.CaptureExtensionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, new(refinedCombined.Id, refinedCombined.FrameCount, 1, "She walks on.", [], null), _ct);
        await f.Worker.ExecuteAsync(await f.Claim(afterRefined), afterRefined.Snapshot, _ct);
        await f.Jobs.UpdateAsync(afterRefined.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var afterCombined = (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes.Single(t => t.RunId == afterRefined.Id && t.Composition is not null);
        var backId = Guid.NewGuid();
        var back = (await f.Shots.TrimTakeAsync(f.Project.Id, new(afterCombined.Id, backId, 2, refinedCombined.FrameCount - 1), ct: _ct)).Takes.Single(t => t.Id == backId);
        Assert.Null(back.Extension); Assert.NotNull(back.RetainedSource!.RefinementInput);
        var replayRefined = await f.CaptureService.CaptureExtensionVersionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, back.Id, _ct);
        Assert.True(replayRefined.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Extension!.ReplayLastSegment);
        await f.Worker.ExecuteAsync(await f.Claim(replayRefined), replayRefined.Snapshot, _ct);
        Assert.Equal(back.FrameCount, (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes.Single(t => t.RunId == replayRefined.Id && t.Composition is not null).FrameCount);
        document = await f.Shots.LoadAsync(f.Project.Id, _ct);
        media.Frames = combined.FrameCount;
        await f.Shots.RemoveFrameArchivesAsync(f.Project.Id, [combined.Id], document.Revision, _ct);
        Assert.False((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes.Single(t => t.Id == combined.Id).HasAnyLosslessFrames);
        var replay = await f.CaptureService.CaptureExtensionVersionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, combined.Id, _ct);
        Assert.NotNull(replay);
    }

    [Theory] [InlineData(1, 1)] [InlineData(5, 5)] [InlineData(22, 22)] [InlineData(38, 22)]
    public async Task ExactOffGridExtensionCapturesOnlyVisibleFrames(int end, int window)
    {
        var media = new TrimTestMedia(); using var f = await QueuedVideoFixture.Create(this, trimMedia: media);
        f.Shot.SaveLosslessFrames = true; f.Adapter.RealFrames = true;
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], (await f.Shots.LoadAsync(f.Project.Id, _ct)).Revision, ct: _ct);
        var initial = await f.Capture(); await f.Worker.ExecuteAsync(await f.Claim(initial), initial.Snapshot, _ct);
        var source = Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        var submission = await f.CaptureService.CaptureExtensionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, new(source.Id, end, 1, "Next action.", [], null, Combine: false), _ct);
        var r = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(window, r.Snapshot.Motion!.Frames); Assert.Equal(end - window, r.Snapshot.Motion.StartFrame);
        Assert.Equal(end, r.Extension!.RetainedFrames); Assert.False(r.Extension.Combine);
        Assert.NotEqual(source.ShotId, r.OutputShotId);
        Assert.Equal(window, r.Snapshot.Motion.Files.Count(f => f.FileName.StartsWith("motion-frame-")));
    }
}
