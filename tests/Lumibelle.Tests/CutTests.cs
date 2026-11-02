using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task CutStartsEmptyAndKeepsOrderDuplicateSourcesTrimsAndIndependentSelections()
    {
        var f = Fixture(); var first = Ready(); var second = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [first, second], 0, ct: _ct);
        await AddTake(f.Project.Id, f.Shots, first);
        var shots = await AddTake(f.Project.Id, f.Shots, second);
        var cuts = new FileCutStore(f.Files, f.Shots, _clock);
        Assert.Empty((await cuts.LoadAsync(f.Project.Id, _ct)).Clips);
        var a = CutClip.From(first, shots.Takes[0]); a.StartFrame = 5; a.EndFrameExclusive = 6;
        var b = CutClip.From(second, shots.Takes[1]);
        var repeat = CutClip.From(first, shots.Takes[0]);
        var saved = await cuts.SaveAsync(f.Project.Id, [b, a, repeat], 0, _ct);
        a.StartFrame = 20; saved.Clips.Clear();
        var reopened = await cuts.LoadAsync(f.Project.Id, _ct);
        Assert.Equal([b.Id, a.Id, repeat.Id], reopened.Clips.Select(c => c.Id));
        Assert.Equal(5, reopened.Clips[1].StartFrame); Assert.Equal(1d / 24, reopened.Clips[1].Duration);
        Assert.Equal(2, reopened.Clips.Count(c => c.TakeId == shots.Takes[0].Id));
        shots.Shots[0].SelectedTakeId = shots.Takes[0].Id;
        shots.Shots.Reverse(); shots.Shots[0].Title = "Revised shot";
        await f.Shots.SaveAsync(f.Project.Id, shots.Shots, shots.Revision, ct: _ct);
        Assert.Equal([b.Id, a.Id, repeat.Id], (await cuts.LoadAsync(f.Project.Id, _ct)).Clips.Select(c => c.Id));
        await cuts.SaveAsync(f.Project.Id, [], reopened.Revision, _ct);
        var unchanged = await f.Shots.LoadAsync(f.Project.Id, _ct);
        Assert.Equal(2, unchanged.Takes.Count); Assert.Equal(shots.Takes[0].Id, unchanged.Shots.Single(s => s.Id == first.Id).SelectedTakeId);
    }

    [Theory]
    [InlineData(-1, 2)] [InlineData(0, 0)] [InlineData(2, 1)] [InlineData(0, 500)]
    public async Task CutRejectsInvalidFrameRangesWithoutPublication(int start, int end)
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var doc = await AddTake(f.Project.Id, f.Shots, shot); var cut = new FileCutStore(f.Files, f.Shots, _clock);
        var clip = CutClip.From(shot, doc.Takes[0]); clip.StartFrame = start; clip.EndFrameExclusive = end;
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => cut.SaveAsync(f.Project.Id, [clip], 0, _ct));
        Assert.Equal(0, (await cut.LoadAsync(f.Project.Id, _ct)).Revision);
    }

    [Fact]
    public async Task CutRejectsUnknownCrossProjectAndMismatchedSources()
    {
        var f = Fixture(); var other = Fixture(); var shot = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var doc = await AddTake(f.Project.Id, f.Shots, shot); var cuts = new FileCutStore(f.Files, f.Shots, _clock);
        var valid = CutClip.From(shot, doc.Takes[0]);
        foreach (var invalid in new[] { valid with { TakeId = Guid.NewGuid() }, valid with { ShotId = Guid.NewGuid() }, valid with { Fps = 12 }, valid with { FrameCount = 99 } })
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => cuts.SaveAsync(f.Project.Id, [invalid], 0, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => cuts.SaveAsync(other.Project.Id, [valid], 0, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => cuts.SaveAsync(f.Project.Id, [valid, valid], 0, _ct));
    }

    [Fact]
    public async Task CutRetainsUnavailableEntriesAllowsRemovalAndResolvesRestoredOriginal()
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var shots = await AddTake(f.Project.Id, f.Shots, shot); var cuts = new FileCutStore(f.Files, f.Shots, _clock);
        var clip = CutClip.From(shot, shots.Takes[0]); var cut = await cuts.SaveAsync(f.Project.Id, [clip], 0, _ct);
        shots = await f.Shots.DiscardAsync(f.Project.Id, clip.TakeId, ShotTrashKind.Take, shots.Revision, _ct);
        var read = await cuts.LoadAsync(f.Project.Id, _ct); Assert.Equal(clip.TakeId, read.Clips[0].TakeId);
        read.Clips[0].StartFrame = 1;
        cut = await cuts.SaveAsync(f.Project.Id, read.Clips, cut.Revision, _ct);
        var invented = clip with { Id = Guid.NewGuid() };
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => cuts.SaveAsync(f.Project.Id, [invented], cut.Revision, _ct));
        shots = await f.Shots.RestoreAsync(f.Project.Id, [shots.Trash[0].Id], shots.Revision, _ct);
        Assert.Equal(clip.TakeId, shots.Takes[0].Id);
        await f.Shots.DeleteShotsAsync(f.Project.Id, [shot.Id], shots.Revision, ct: _ct);
        var removed = await cuts.SaveAsync(f.Project.Id, [], cut.Revision, _ct);
        Assert.Empty(removed.Clips);
    }

    [Fact]
    public async Task CutConflictsDoNotOverwriteConcurrentEditsOrShotPublication()
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var shots = await AddTake(f.Project.Id, f.Shots, shot); var cuts = new FileCutStore(f.Files, f.Shots, _clock);
        var clip = CutClip.From(shot, shots.Takes[0]);
        var first = cuts.SaveAsync(f.Project.Id, [clip], 0, _ct);
        var publish = AddTake(f.Project.Id, f.Shots, shot, 2);
        var saved = await first; var published = await publish;
        var other = new FileCutStore(f.Files, f.Shots, _clock);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => other.SaveAsync(f.Project.Id, [], 0, _ct));
        Assert.Single((await cuts.LoadAsync(f.Project.Id, _ct)).Clips); Assert.Equal(2, published.Takes.Count);
        Assert.Equal(published.Revision, (await f.Shots.LoadAsync(f.Project.Id, _ct)).Revision);
    }

    [Fact]
    public async Task FailedCutPublicationLeavesPreviousFileAndMediaIntact()
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var shots = await AddTake(f.Project.Id, f.Shots, shot); var cuts = new FileCutStore(f.Files, f.Shots, _clock);
        var cut = await cuts.SaveAsync(f.Project.Id, [CutClip.From(shot, shots.Takes[0])], 0, _ct);
        var dir = await f.Files.DirectoryAsync(f.Project.Id, _ct); var before = TestProjectDatabase.Stamp(dir, "cut");
        using (var locked = TestProjectDatabase.HoldReadOnly(dir))
            await Assert.ThrowsAnyAsync<Exception>(() => cuts.SaveAsync(f.Project.Id, [], cut.Revision, _ct));
        Assert.Equal(before, TestProjectDatabase.Stamp(dir, "cut"));
        await using var media = await f.Shots.OpenAsync(f.Project.Id, shots.Takes[0].Id, ShotTrashKind.Take, ct: _ct); Assert.NotNull(media);
        Assert.Empty((await cuts.SaveAsync(f.Project.Id, [], cut.Revision, _ct)).Clips);
    }
}
