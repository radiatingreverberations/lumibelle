using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData(0, 30)] [InlineData(23, 39)] [InlineData(24, 25)] [InlineData(3, 38)]
    public async Task TrimmingPreservesExactLosslessPixelsAndIndependentLocalFrames(int start, int end)
    {
        var f = Fixture(); var media = new TrimTestMedia(); var store = new FileShotStore(f.Files, _clock, mediaTools: media);
        var shot = Ready(); await store.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var d = await AddTake(f.Project.Id, store, shot); var parent = Assert.Single(d.Takes);
        var request = new TakeTrimRequest(parent.Id, Guid.NewGuid(), start, end);
        d = await store.TrimTakeAsync(f.Project.Id, request, d.Revision, ct: _ct);
        var trimmed = d.Takes.Single(t => t.Id == request.ResultId);
        Assert.Equal(end - start, trimmed.FrameCount); Assert.Equal(parent.Snapshot.FrameCount, trimmed.Snapshot.FrameCount);
        Assert.Equal(start, trimmed.Trim!.SourceStartFrame); Assert.True(trimmed.HasLosslessFrames);
        Assert.Null(d.Shots[0].SelectedTakeId); Assert.Equal(1, d.Shots[0].Duration);
        Assert.Equal(1, media.Trims);
        Assert.Equal(d.Revision, (await store.TrimTakeAsync(f.Project.Id, request, 0, ct: _ct)).Revision);
        Assert.Equal(1, media.Trims);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.TrimTakeAsync(f.Project.Id, request with { StartFrame = start + 1 }, ct: _ct));
        d = await store.DiscardAsync(f.Project.Id, parent.Id, ShotTrashKind.Take, d.Revision, _ct);
        d = await store.PurgeAsync(f.Project.Id, d.Trash.Select(t => t.Id).ToArray(), d.Revision, _ct);
        for (var i = 0; i < end - start; i++) {
            await using var pixels = (await store.OpenAsync(f.Project.Id, trimmed.Id, ShotTrashKind.Take, i, ct: _ct))!;
            using var image = await Image.LoadAsync<Rgb24>(pixels.Content, _ct);
            Assert.Equal((byte)(start + i), image[0, 0].R);
        }
        Assert.Single((await store.LoadAsync(f.Project.Id, _ct)).Takes);
    }

    [Fact]
    public async Task RepeatedTrimComposesRangeAndArchiveRemovalKeepsVideo()
    {
        var f = Fixture(); var store = new FileShotStore(f.Files, _clock, mediaTools: new TrimTestMedia { HasAudio = false });
        var shot = Ready(); await store.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var d = await AddTake(f.Project.Id, store, shot);
        var first = Guid.NewGuid(); d = await store.TrimTakeAsync(f.Project.Id, new(d.Takes[0].Id, first, 5, 35), ct: _ct);
        var second = Guid.NewGuid(); d = await store.TrimTakeAsync(f.Project.Id, new(first, second, 4, 25), ct: _ct);
        var take = d.Takes.Single(t => t.Id == second);
        Assert.Equal(9, take.Trim!.SourceStartFrame); Assert.Equal(30, take.Trim.SourceEndFrameExclusive);
        d = await store.RemoveFrameArchivesAsync(f.Project.Id, [second], d.Revision, _ct);
        Assert.False(d.Takes.Single(t => t.Id == second).HasLosslessFrames);
        await using var video = await store.OpenAsync(f.Project.Id, second, ShotTrashKind.Take, ct: _ct);
        Assert.NotNull(video);
    }

    [Fact]
    public async Task InvalidConflictCancelledAndFailedTrimsDoNotPublish()
    {
        var f = Fixture(); var media = new TrimTestMedia(); var store = new FileShotStore(f.Files, _clock, mediaTools: media);
        var shot = Ready(); await store.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var d = await AddTake(f.Project.Id, store, shot); var parent = d.Takes[0];
        foreach (var range in new[] { (-1, 2), (2, 2), (0, 40), (0, 39) })
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.TrimTakeAsync(f.Project.Id, new(parent.Id, Guid.NewGuid(), range.Item1, range.Item2), ct: _ct));
        var request = new TakeTrimRequest(parent.Id, Guid.NewGuid(), 1, 12);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.TrimTakeAsync(f.Project.Id, request, 0, ct: _ct));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TrimTakeAsync(f.Project.Id, request, ct: cancelled.Token));
        media.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => store.TrimTakeAsync(f.Project.Id, request, ct: _ct));
        Assert.Single((await store.LoadAsync(f.Project.Id, _ct)).Takes);
        media.Fail = false;
        media.OnTrim = ct => Task.Delay(Timeout.Infinite, ct);
        using var duringEncoding = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TrimTakeAsync(f.Project.Id, request, ct: duringEncoding.Token));
        Assert.Single((await store.LoadAsync(f.Project.Id, _ct)).Takes);
        media.OnTrim = null;
        var saved = await store.TrimTakeAsync(f.Project.Id, request, ct: _ct);
        Assert.Equal(2, saved.Takes.Count);
        var invalid = ShotCopy.Of(saved.Takes.Single(t => t.Id == request.ResultId));
        invalid.Snapshot = invalid.Snapshot with { FrameCount = 500 };
        Assert.Throws<WorkspaceStoreException>(() => TakeTrimming.Validate(invalid));
        invalid.Snapshot = parent.Snapshot;
        invalid.Trim = invalid.Trim! with { SourceStartFrame = 0, SourceEndFrameExclusive = 11 };
        Assert.Throws<WorkspaceStoreException>(() => TakeTrimming.Validate(invalid));
    }

    [Theory] [InlineData(TakeRefinementMode.Refine)] [InlineData(TakeRefinementMode.Rework)]
    public async Task TrimRetainsLatentsAndRefinesAfterParentDeletionWithLocalRetry(TakeRefinementMode mode)
    {
        var media = new TrimTestMedia(); using var f = await QueuedVideoFixture.Create(this, trimMedia: media);
        f.Shot.SaveLosslessFrames = true;
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], (await f.Shots.LoadAsync(f.Project.Id, _ct)).Revision, ct: _ct);
        f.Adapter.RealFrames = true;
        var originalRequest = await f.Capture(); var originalContext = await f.Claim(originalRequest);
        await f.Worker.ExecuteAsync(originalContext, originalRequest.Snapshot, _ct);
        await f.Jobs.UpdateAsync(originalRequest.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var d = await f.Shots.LoadAsync(f.Project.Id, _ct); var parent = Assert.Single(d.Takes);
        media.Width = parent.Width; media.Height = parent.Height;
        var id = Guid.NewGuid(); d = await f.Shots.TrimTakeAsync(f.Project.Id, new(parent.Id, id, 5, 30), ct: _ct);
        var trimmed = d.Takes.Single(t => t.Id == id);
        Assert.Equal(parent.RefinementPackage, trimmed.RefinementPackage);
        Assert.NotNull(trimmed.RetainedSource);
        d = await f.Shots.DiscardAsync(f.Project.Id, parent.Id, ShotTrashKind.Take, d.Revision, _ct);
        d = await f.Shots.PurgeAsync(f.Project.Id, d.Trash.Select(t => t.Id).ToArray(), d.Revision, _ct);
        d = await f.Shots.DiscardAsync(f.Project.Id, trimmed.Id, ShotTrashKind.Take, d.Revision, _ct);
        Assert.Equal(trimmed.RetainedSource!.Inputs, Assert.Single(d.Trash).Take!.RetainedSource!.Inputs);
        d = await f.Shots.RestoreAsync(f.Project.Id, d.Trash.Select(t => t.Id).ToArray(), d.Revision, _ct);
        d = await f.Shots.RemoveFrameArchivesAsync(f.Project.Id, [trimmed.Id], d.Revision, _ct);
        var reloaded = await new FileShotStore(f.Files, _clock, mediaTools: media, jobs: f.Jobs).LoadAsync(f.Project.Id, _ct);
        var retained = Assert.Single(reloaded.Takes);
        Assert.False(retained.HasLosslessFrames); Assert.Equal(parent.RefinementPackage, retained.RefinementPackage);
        var request = await f.CaptureService.CaptureRefinementAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, trimmed.Id, mode, parent.Width, parent.Height, _ct);
        var captured = request.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(new TakeTrimRange(5, 30), captured.OutputTrim);
        Assert.Equal(parent.FrameCount, captured.Refinement!.SourcePackage.FrameCount);
        var context = await f.Claim(request); media.Fail = true;
        var failure = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, request.Snapshot, _ct));
        Assert.Equal(AiJobRecovery.RetryOutput, failure.Recovery); Assert.Equal(2, f.Graphs.Count);
        var full = (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes.Single(t => t.RunId == request.Id);
        Assert.Null(full.Trim);
        media.Fail = false; f.NoNetwork = true;
        await f.Worker.RecoverAsync(f.Context(context.Job, true), request.Snapshot, _ct);
        d = await f.Shots.LoadAsync(f.Project.Id, _ct);
        var result = d.Takes.Single(t => t.Id == TakeTrimming.OutputId(full.Id));
        Assert.Equal(25, result.FrameCount); Assert.Equal(full.RefinementPackage, result.RefinementPackage);
        Assert.NotEqual(parent.RefinementPackage!.Id, result.RefinementPackage!.Id);
        Assert.Equal(2, f.Graphs.Count); Assert.Null(d.Shots[0].SelectedTakeId);
        var further = await f.CaptureService.CaptureRefinementAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, result.Id, mode, full.Width, full.Height, _ct);
        Assert.Equal(result.RefinementPackage, further.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Refinement!.SourcePackage);
        var shorterId = Guid.NewGuid();
        await f.Shots.TrimTakeAsync(f.Project.Id, new(result.Id, shorterId, 2, 20), ct: _ct);
        var version = await f.CaptureService.CaptureTrimmedVersionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, shorterId, _ct);
        var versionRequest = version.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(new TakeTrimRange(7, 25), versionRequest.OutputTrim);
        Assert.Equal(captured.Refinement.SourcePackage, versionRequest.Refinement!.SourcePackage);
        Assert.Equal(shorterId, versionRequest.Refinement.ParentTakeId);
        Assert.Equal(new TakeTrimRange(5, 30), (await f.Jobs.ReadSnapshotAsync(request.Id, _ct)).Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.OutputTrim);
    }

    private sealed class TrimTestMedia : IProductionMediaTools
    {
        public bool Fail; public int Trims;
        public bool HasAudio = true;
        public int Width = 32, Height = 32;
        public Func<CancellationToken, Task>? OnTrim;
        private int _frames;
        public async Task TrimTakeAsync(string source, string? frames, string target, int start, int end, double fps, H3Settings settings, CancellationToken ct) {
            Trims++; if (Fail) throw new IOException("Simulated local encoder failure");
            if (OnTrim is not null) await OnTrim(ct);
            _frames = end - start;
            await File.WriteAllBytesAsync(target, [1, 2, 3], ct);
        }
        public Task<double> AudioDurationAsync(string p, H3Settings s, CancellationToken c) => throw new NotSupportedException();
        public Task PrepareVoiceAsync(string p, string t, double a, double b, H3Settings s, CancellationToken c) => throw new NotSupportedException();
        public Task<VideoFileInfo> VideoInfoAsync(string p, H3Settings s, CancellationToken c) => Task.FromResult(new VideoFileInfo(Width, Height, _frames, 24, HasAudio));
        public Task<byte[]> ExtractFrameAsync(string p, int i, int w, int h, H3Settings s, CancellationToken c) => Task.FromResult(AssetStoreTests.Png(w, h));
    }
}
