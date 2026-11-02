using lumibelle.Models;
using lumibelle.Services.Shots;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task StoredTakeAndFrameFollowDiscardRestoreAndValidateFrameBounds()
    {
        var f = Fixture(); var shot = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var doc = await AddTake(f.Project.Id, f.Shots, shot);
        var take = doc.Takes[0];
        await using (var video = await f.Shots.OpenAsync(f.Project.Id, take.Id, ShotTrashKind.Take, ct: _ct))
            Assert.Equal(3, video?.Content.Length);
        await using (var frame = await f.Shots.OpenAsync(f.Project.Id, take.Id, ShotTrashKind.Take, frame: 0, ct: _ct))
            Assert.Equal("image/png", frame?.ContentType);
        Assert.Null(await f.Shots.OpenAsync(f.Project.Id, take.Id, ShotTrashKind.Take, frame: -1, ct: _ct));
        Assert.Null(await f.Shots.OpenAsync(f.Project.Id, take.Id, ShotTrashKind.Take, frame: take.FrameCount, ct: _ct));
        doc = await f.Shots.DiscardAsync(f.Project.Id, take.Id, ShotTrashKind.Take, doc.Revision, _ct);
        Assert.Null(await f.Shots.OpenAsync(f.Project.Id, take.Id, ShotTrashKind.Take, ct: _ct));
        await using (var trashed = await f.Shots.OpenAsync(f.Project.Id, doc.Trash[0].Id, ShotTrashKind.Take, trash: true, ct: _ct))
            Assert.NotNull(trashed);
        doc = await f.Shots.RestoreAsync(f.Project.Id, [doc.Trash[0].Id], doc.Revision, _ct);
        await using var restored = await new FileShotStore(f.Files, _clock).OpenAsync(f.Project.Id, take.Id, ShotTrashKind.Take, ct: _ct);
        Assert.Equal(3, restored?.Content.Length);
    }
}
