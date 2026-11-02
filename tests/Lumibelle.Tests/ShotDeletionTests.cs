using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task BulkShotDeletionRequiresProductionAcknowledgementAndPreservesRecoveryAndMedia()
    {
        var f = Fixture(); var first = Ready(); var second = Ready(); var third = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [first, second, third], 0, ct: _ct);
        await AddTake(f.Project.Id, f.Shots, first); var d = await AddTake(f.Project.Id, f.Shots, second);
        d.Shots[0].SelectedTakeId = d.Takes[0].Id;
        d = await f.Shots.SaveAsync(f.Project.Id, d.Shots, d.Revision, ct: _ct);
        var takeIds = d.Takes.Select(t => t.Id).ToArray();
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Shots.DeleteShotsAsync(f.Project.Id, [first.Id, second.Id], d.Revision, ct: _ct));
        Assert.Equal(d.Revision, (await f.Shots.LoadAsync(f.Project.Id, _ct)).Revision);
        // Generic saves must still protect selected production takes.
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Shots.SaveAsync(f.Project.Id, [third], d.Revision, ct: _ct));
        var saved = await f.Shots.DeleteShotsAsync(f.Project.Id, [first.Id, second.Id], d.Revision, true, _ct);
        Assert.Equal(d.Revision + 1, saved.Revision); Assert.Equal(third.Id, Assert.Single(saved.Shots).Id);
        Assert.Empty(saved.Takes); Assert.Equal(takeIds, saved.Trash.Select(t => t.Take!.Id));
        Assert.Equal([0, 1], saved.Trash.Select(t => t.OwnerPosition)); Assert.Equal([0, 1], saved.Trash.Select(t => t.Position));
        Assert.All(saved.Trash, t => { Assert.Null(t.Owner!.SelectedTakeId); Assert.Equal(TimeSpan.FromDays(30), t.ExpiresUtc - t.DeletedUtc); });
        Assert.Equal(3, saved.Recovery[0].Shots.Count); Assert.Equal(takeIds[0], saved.Recovery[0].Shots[0].SelectedTakeId);
        foreach (var entry in saved.Trash) { await using var media = await f.Shots.OpenAsync(f.Project.Id, entry.Id, ShotTrashKind.Take, trash: true, ct: _ct); Assert.NotNull(media); }
        saved = await f.Shots.RecoverAsync(f.Project.Id, saved.Recovery[0].Id, saved.Revision, _ct);
        Assert.Equal([first.Id, second.Id, third.Id], saved.Shots.Select(s => s.Id));
        Assert.All(saved.Shots, s => Assert.Null(s.SelectedTakeId)); Assert.Empty(saved.Takes);
        saved = await f.Shots.RestoreAsync(f.Project.Id, saved.Trash.Select(t => t.Id).ToArray(), saved.Revision, _ct);
        Assert.Equal(takeIds, saved.Takes.Select(t => t.Id)); Assert.Empty(saved.Trash);
    }

    [Fact]
    public async Task BulkShotDeletionRejectsStaleOrInvalidSelectionsWithoutPartialChanges()
    {
        var f = Fixture(); var first = Ready(); var second = Ready();
        var old = await f.Shots.SaveAsync(f.Project.Id, [first], 0, ct: _ct);
        var current = await f.Shots.SaveAsync(f.Project.Id, [first, second], old.Revision, ct: _ct);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => f.Shots.DeleteShotsAsync(f.Project.Id, [first.Id], old.Revision, true, _ct));
        foreach (var ids in new Guid[][] { [], [first.Id, first.Id], [first.Id, Guid.NewGuid()], [Guid.Empty] })
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Shots.DeleteShotsAsync(f.Project.Id, ids, current.Revision, true, _ct));
        var saved = await f.Shots.LoadAsync(f.Project.Id, _ct);
        Assert.Equal(current.Revision, saved.Revision); Assert.Equal(2, saved.Shots.Count); Assert.Empty(saved.Trash);
    }

    [Fact]
    public async Task FailedBulkDeletionKeepsProductionSelectionAndMediaForExplicitRetry()
    {
        var f = Fixture(); var first = Ready(); var second = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [first, second], 0, ct: _ct);
        var d = await AddTake(f.Project.Id, f.Shots, first); d.Shots[0].SelectedTakeId = d.Takes[0].Id;
        d = await f.Shots.SaveAsync(f.Project.Id, d.Shots, d.Revision, ct: _ct);
        var dir = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        var before = TestProjectDatabase.Stamp(dir, "shots");
        using (var locked = TestProjectDatabase.HoldReadOnly(dir))
            await Assert.ThrowsAnyAsync<Exception>(() => f.Shots.DeleteShotsAsync(f.Project.Id, [first.Id, second.Id], d.Revision, true, _ct));
        Assert.Equal(before, TestProjectDatabase.Stamp(dir, "shots"));
        await using var media = await f.Shots.OpenAsync(f.Project.Id, d.Takes[0].Id, ShotTrashKind.Take, ct: _ct); Assert.NotNull(media);
        var saved = await f.Shots.DeleteShotsAsync(f.Project.Id, [first.Id, second.Id], d.Revision, true, _ct);
        Assert.Empty(saved.Shots); Assert.Single(saved.Trash);
    }

    [Fact]
    public async Task ACompletedTakeCannotResurrectABulkDeletedShot()
    {
        var f = Fixture(); var shot = Ready();
        var d = await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        await f.Shots.DeleteShotsAsync(f.Project.Id, [shot.Id], d.Revision, ct: _ct);
        var completed = await AddTake(f.Project.Id, f.Shots, shot);
        Assert.Empty(completed.Shots); Assert.Empty(completed.Takes);
        Assert.Equal(shot.Id, Assert.Single(completed.Trash).Owner!.Id);
    }
}
