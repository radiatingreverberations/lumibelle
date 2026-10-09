using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory] [InlineData(0, TakeRefinementMode.Refine)] [InlineData(3, TakeRefinementMode.Rework)]
    public async Task LeadInOwnsTheRetainedSuffixAndRefinesOnlyItsNewFirstSegment(int start, TakeRefinementMode mode)
    {
        var media = new TrimTestMedia(); using var f = await QueuedVideoFixture.Create(this, trimMedia: media);
        f.Shot.SaveLosslessFrames = true; f.Adapter.RealFrames = true;
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], (await f.Shots.LoadAsync(f.Project.Id, _ct)).Revision, ct: _ct);
        var initial = await f.Capture(); await f.Worker.ExecuteAsync(await f.Claim(initial), initial.Snapshot, _ct);
        await f.Jobs.UpdateAsync(initial.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var source = Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        var options = new TakeExtensionOptions(source.Id, source.FrameCount, 1, "She approaches the courtyard before walking through it.", [], null, SaveLosslessFrames: true)
            { Direction = TakeExtensionDirection.Before, StartFrame = start };
        var submission = await f.CaptureService.CaptureExtensionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, options, _ct);
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(TakeExtensionDirection.Before, request.Snapshot.Motion!.Direction);
        Assert.Equal(start, request.Extension!.PrefixStartFrame); Assert.Equal(source.FrameCount, request.Extension.RetainedFrames);
        Assert.Equal(start == 0 ? MotionContextRoute.SavedLatents : MotionContextRoute.Frames, request.Snapshot.Motion.Route);
        Assert.Empty(request.Snapshot.Shot.Dialogue);
        var document = await f.Shots.LoadAsync(f.Project.Id, _ct);
        document = await f.Shots.DiscardAsync(f.Project.Id, source.Id, ShotTrashKind.Take, document.Revision, _ct);
        await f.Shots.PurgeAsync(f.Project.Id, document.Trash.Select(t => t.Id).ToArray(), document.Revision, _ct);
        media.Fail = true;
        var context = await f.Claim(submission);
        await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, submission.Snapshot, _ct));
        // Recover the completed mock generation locally.
        media.Fail = false;
        var full = Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        var generated = request.Snapshot.FrameCount - request.Snapshot.Motion.Frames;
        document = await f.Shots.PublishExtensionAsync(f.Project.Id, request, full.Id, _ct);
        var combined = document.Takes.Single(t => t.Id == H3Motion.OutputId(full.Id));
        Assert.Equal(generated + source.FrameCount - start, combined.FrameCount);
        Assert.Equal(full.Id, H3Motion.GeneratedSegment(combined).Source.Id);
        Assert.Equal(full.Id, combined.Composition!.Segments[0].Source.Id);
        Assert.Equal(source.Id, combined.Composition.Segments[1].Source.Id);
        Assert.Equal(new TakeTrimRange(0, generated), new(combined.Composition.Segments[0].StartFrame, combined.Composition.Segments[0].EndFrameExclusive));
        Assert.Equal(source.RefinementPackage, combined.Composition.Segments[1].Source.RefinementPackage);
        var captured = Path.Combine(await f.Shots.RunDirectoryAsync(f.Project.Id, submission.Id, _ct), H3Motion.SourceFolder);
        foreach (var index in new[] { start, 23, 24, source.FrameCount - 1 }.Where(i => i >= start).Distinct()) {
            await using var before = await TakeFrameReader.Shared.OpenAsync(captured, request.Extension.Source, index, _ct);
            await using var after = await f.Shots.OpenAsync(f.Project.Id, combined.Id, ShotTrashKind.Take, generated + index - start, ct: _ct);
            Assert.Equal(await SHA256.HashDataAsync(before, _ct), await SHA256.HashDataAsync(after!.Content, _ct));
        }
        Assert.Null(document.Shots[0].SelectedTakeId); Assert.Equal(f.Shot.Duration, document.Shots[0].Duration);
        await f.Jobs.UpdateAsync(submission.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var refine = await f.CaptureService.CaptureRefinementAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, combined.Id, mode, full.Width, full.Height, _ct);
        var refineRequest = refine.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(combined.Composition.GeneratedSegmentKey, refineRequest.Extension!.ReplacementSegmentKey);
        Assert.Equal(full.RefinementPackage, refineRequest.Refinement!.SourcePackage);
        Assert.Equal(new TakeTrimRange(0, generated), refineRequest.OutputTrim);
        await f.Worker.ExecuteAsync(await f.Claim(refine), refine.Snapshot, _ct);
        await f.Jobs.UpdateAsync(refine.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var revised = (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes.Single(t => t.RunId == refine.Id && t.Composition is not null);
        Assert.Equal(combined.FrameCount, revised.FrameCount);
        Assert.Equal(source.RefinementPackage, revised.Composition!.Segments[1].Source.RefinementPackage);
        Assert.NotEqual(full.RefinementPackage, H3Motion.GeneratedSegment(revised).Source.RefinementPackage);
        var trimId = Guid.NewGuid();
        var trimmed = (await f.Shots.TrimTakeAsync(f.Project.Id, new(revised.Id, trimId, 2, revised.FrameCount - 3), ct: _ct)).Takes.Single(t => t.Id == trimId);
        var replay = await f.CaptureService.CaptureExtensionVersionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, trimmed.Id, _ct);
        await f.Worker.ExecuteAsync(await f.Claim(replay), replay.Snapshot, _ct);
        Assert.Equal(trimmed.FrameCount, (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes.Single(t => t.RunId == replay.Id && t.Composition is not null).FrameCount);
    }
    [Theory] [InlineData(1, 1)] [InlineData(5, 5)] [InlineData(22, 22)] [InlineData(38, 22)]
    public async Task PausedLeadInUsesOnlyTheVisibleFollowingWindowAndInsertsBeforeTheSource(int available, int window)
    {
        var media = new TrimTestMedia(); using var f = await QueuedVideoFixture.Create(this, trimMedia: media);
        f.Shot.SaveLosslessFrames = true; f.Adapter.RealFrames = true;
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], (await f.Shots.LoadAsync(f.Project.Id, _ct)).Revision, ct: _ct);
        var initial = await f.Capture(); await f.Worker.ExecuteAsync(await f.Claim(initial), initial.Snapshot, _ct);
        await f.Jobs.UpdateAsync(initial.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var source = Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        var start = source.FrameCount - available;
        var submission = await f.CaptureService.CaptureExtensionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id,
            new(source.Id, source.FrameCount, 1, "She approaches.", [], null, Combine: false) { Direction = TakeExtensionDirection.Before, StartFrame = start }, _ct);
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(window, request.Snapshot.Motion!.Frames); Assert.Equal(start, request.Snapshot.Motion.StartFrame);
        Assert.Equal(start + window, request.Snapshot.Motion.EndFrameExclusive);
        Assert.Equal(window == 1 ? MotionContextRoute.SingleFrame : MotionContextRoute.Frames, request.Snapshot.Motion.Route);
        var document = await f.Shots.LoadAsync(f.Project.Id, _ct);
        Assert.Equal(source.ShotId, document.Shots[1].Id); Assert.Equal(request.OutputShotId, document.Shots[0].Id);
        await f.Worker.ExecuteAsync(await f.Claim(submission), submission.Snapshot, _ct);
        var separate = (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes.Single(t => t.RunId == submission.Id && t.Composition is not null);
        Assert.Equal(request.Snapshot.FrameCount - window, separate.FrameCount);
        Assert.True(separate.Composition!.HasJoinPreview); Assert.Single(separate.Composition.Segments);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Shots.CaptureLeadInAsync(f.Project.Id, source.Id, Guid.NewGuid(), source.FrameCount, 1, true, _ct));
    }
    [Fact]
    public void LeadingSnapUsesRawStartCoordinatesAndDoesNotReachOutsideVisibleFootage()
    {
        var original = new ShotTake { Id = Guid.NewGuid(), Snapshot = Snapshot(Guid.NewGuid(), Ready()) with { FrameCount = 124 }, Width = 32, Height = 32,
            RefinementPackage = new(Guid.NewGuid(), 100, new('A', 64), 32, 32, 124) };
        var trimmed = original with { Trim = new(original.Id, 124, 10, 97, 10, 97, true) };
        Assert.Equal(7, H3Motion.AlignedStart(trimmed, 0)); Assert.True(H3Motion.CanUseLeadingLatents(trimmed, 7));
        Assert.False(H3Motion.CanUseLeadingLatents(trimmed, 0)); Assert.Null(H3Motion.AlignedStart(trimmed, 86));
        var mixed = original with { Composition = new([new(Guid.NewGuid(), original, 0, 22), new(Guid.NewGuid(), original, 0, 124)], 21) };
        Assert.False(H3Motion.CanUseLeadingLatents(mixed, 0)); Assert.Null(H3Motion.AlignedStart(mixed, 0));
        Assert.True(H3Motion.CanUseLeadingLatents(mixed, 22));
    }
    [Fact]
    public async Task MixedDirectionalExtensionsStayFlatAndTrimmingSelectsTheLatestRemainingGeneration()
    {
        var media = new TrimTestMedia(); using var f = await QueuedVideoFixture.Create(this, trimMedia: media);
        f.Shot.SaveLosslessFrames = true; f.Adapter.RealFrames = true;
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], (await f.Shots.LoadAsync(f.Project.Id, _ct)).Revision, ct: _ct);
        var initial = await f.Capture(); await f.Worker.ExecuteAsync(await f.Claim(initial), initial.Snapshot, _ct);
        await f.Jobs.UpdateAsync(initial.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var source = Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        ShotTake? firstLead = null;
        foreach (var direction in new[] { TakeExtensionDirection.Before, TakeExtensionDirection.After, TakeExtensionDirection.Before }) {
            var submission = await f.CaptureService.CaptureExtensionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id,
                new(source.Id, source.FrameCount, 5, "She keeps moving forward through the courtyard.", [], null, SaveLosslessFrames: true) { Direction = direction }, _ct);
            await f.Worker.ExecuteAsync(await f.Claim(submission), submission.Snapshot, _ct);
            await f.Jobs.UpdateAsync(submission.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
            source = (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes.Single(t => t.RunId == submission.Id && t.Composition is not null);
            firstLead ??= source;
        }
        Assert.True(source.FrameCount > 362); Assert.Equal(4, source.Composition!.Segments.Count);
        Assert.All(source.Composition.Segments, s => { Assert.Null(s.Source.Composition); Assert.Null(s.Source.Extension); });
        var latestLength = source.Composition.Segments[0].EndFrameExclusive;
        // Remove both the latest lead-in and the newer ending, leaving the older lead-in and original.
        var id = Guid.NewGuid();
        var trimmed = (await f.Shots.TrimTakeAsync(f.Project.Id, new(source.Id, id, latestLength, latestLength + firstLead!.FrameCount), ct: _ct)).Takes.Single(t => t.Id == id);
        Assert.Equal(firstLead.RefinementPackage, trimmed.RefinementPackage);
        Assert.Equal(TakeExtensionDirection.Before, trimmed.Snapshot.Motion!.Direction);
        Assert.Equal(trimmed.Composition!.Segments[0].Key, trimmed.Composition.GeneratedSegmentKey);
        var version = await f.CaptureService.CaptureExtensionVersionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, id, _ct);
        await f.Worker.ExecuteAsync(await f.Claim(version), version.Snapshot, _ct);
        Assert.Equal(trimmed.FrameCount, (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes.Single(t => t.RunId == version.Id && t.Composition is not null).FrameCount);
    }
}
