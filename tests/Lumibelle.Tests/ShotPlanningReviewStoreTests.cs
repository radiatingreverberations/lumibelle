using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task ReviewedBreakdownReturnsEarlierScenesToScriptOrderAndUndoRestoresTheList()
    {
        var (project, files, store, _) = Fixture();
        var plaza = ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. MAIN PLAZA — DAY");
        var bedroom = ScriptBlock.Create(ScriptBlockKind.Scene, "INT. RILEY'S BEDROOM — NIGHT");
        await new FileScriptStore(files, _clock).SaveAsync(new() { ProjectId = project.Id, Blocks = [plaza, bedroom] }, 0, cancellationToken: _ct);
        var later = Ready() with { SceneId = bedroom.Id, Title = "Bedroom" };
        var original = await store.SaveAsync(project.Id, [later], 0, ct: _ct);
        var first = Ready() with { SceneId = plaza.Id, Title = "Plaza entrance", Direction = new() };
        var second = Ready() with { SceneId = plaza.Id, Title = "Podium" };
        var job = Guid.NewGuid();
        var saved = await store.ApplyPlanningAsync(project.Id, job, [first, second], original.Revision, _ct);
        Assert.Equal([first.Id, second.Id, later.Id], saved.Shots.Select(s => s.Id));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(later), System.Text.Json.JsonSerializer.Serialize(saved.Shots[2]));
        Assert.Empty(lumibelle.Services.Shots.ShotDirections.Freshness(saved.Shots[0], null,
            lumibelle.Services.Shots.ShotDirections.Neighbours(saved, saved.Shots[0])).Limitations);
        Assert.Equal(saved.Revision, (await store.ApplyPlanningAsync(project.Id, job, [first, second], original.Revision, _ct)).Revision);
        var recovered = await store.RecoverAsync(project.Id, saved.Recovery[0].Id, saved.Revision, _ct);
        Assert.Equal(later.Id, Assert.Single(recovered.Shots).Id);
    }

    [Fact]
    public void BreakdownInsertionOrdersNewScenesAndPreservesExistingManualOrder()
    {
        var scenes = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var sameSceneB = Ready() with { SceneId = scenes[1] };
        var sameSceneA = Ready() with { SceneId = scenes[1] };
        var last = Ready() with { SceneId = scenes[3] };
        var unlinked = Ready() with { SceneId = null };
        var firstNew = Ready() with { SceneId = scenes[0] };
        var sameNew = Ready() with { SceneId = scenes[1] };
        var middleNew = Ready() with { SceneId = scenes[2] };
        var missing = Ready();
        var inserted = lumibelle.Services.Shots.ShotPlanningOrder.Insert([sameSceneB, sameSceneA, last, unlinked],
            [middleNew, sameNew, firstNew, missing], scenes);
        Assert.Equal([firstNew.Id, sameSceneB.Id, sameSceneA.Id, sameNew.Id, middleNew.Id, last.Id, unlinked.Id, missing.Id], inserted.Select(s => s.Id));
        Assert.Equal([sameSceneB.Id, sameSceneA.Id, last.Id, unlinked.Id, missing.Id, firstNew.Id],
            lumibelle.Services.Shots.ShotPlanningOrder.Insert([sameSceneB, sameSceneA, last, unlinked], [missing, firstNew], []).Select(s => s.Id));
    }

    [Fact]
    public async Task PlanningPublicationIsIdempotentAcrossReopeningUndoAndRecovery()
    {
        var (project, _, store, _) = Fixture(); var original = Ready();
        var document = await store.SaveAsync(project.Id, [original], 0, ct: _ct);
        var proposed = Ready(); var job = Guid.NewGuid();
        var saved = await store.ApplyPlanningAsync(project.Id, job, [proposed], document.Revision, _ct);
        Assert.Equal(2, saved.Shots.Count); Assert.Equal(job, Assert.Single(saved.PlanningReviews).JobId);
        Assert.Single(saved.Recovery[0].Shots);
        Assert.Equal(saved.Revision, (await store.ApplyPlanningAsync(project.Id, job, [proposed], document.Revision, _ct)).Revision);
        var recovered = await store.RecoverAsync(project.Id, saved.Recovery[0].Id, saved.Revision, _ct);
        Assert.Equal(original.Id, Assert.Single(recovered.Shots).Id); Assert.Single(recovered.PlanningReviews);
        Assert.Single((await store.ApplyPlanningAsync(project.Id, job, [proposed], document.Revision, _ct)).Shots);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.ApplyPlanningAsync(project.Id, job, [proposed with { Description = "Different review" }], recovered.Revision, _ct));
    }
    [Fact]
    public async Task PlanningPublicationCapturesBeforeLockWaitAndRejectsStaleWrites()
    {
        var (project, files, store, _) = Fixture(); var proposed = Ready(); var job = Guid.NewGuid();
        Task<ShotDocument> write;
        using (await ProjectFiles.LockAsync(await files.DirectoryAsync(project.Id, _ct), _ct))
        {
            write = store.ApplyPlanningAsync(project.Id, job, [proposed], 0, _ct);
            proposed.Description = "Changed after capture";
            Assert.False(write.IsCompleted);
        }
        var saved = await write;
        Assert.NotEqual(proposed.Description, Assert.Single(saved.Shots).Description);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.ApplyPlanningAsync(project.Id, Guid.NewGuid(), [Ready()], 0, _ct));
        Assert.Single((await store.LoadAsync(project.Id, _ct)).PlanningReviews);
    }
    [Fact]
    public async Task FailedPlanningPublicationRetainsTheOriginalAndCanRetryTheSameRequest()
    {
        var (project, files, store, _) = Fixture(); var original = Ready();
        var saved = await store.SaveAsync(project.Id, [original], 0, ct: _ct); var proposed = Ready(); var job = Guid.NewGuid();
        if (OperatingSystem.IsWindows())
        {
            using var locked = new FileStream(Path.Combine(await files.DirectoryAsync(project.Id, _ct), "shots.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.ApplyPlanningAsync(project.Id, job, [proposed], saved.Revision, _ct));
            var unchanged = await store.LoadAsync(project.Id, _ct); Assert.Single(unchanged.Shots); Assert.Empty(unchanged.PlanningReviews);
        }
        Assert.Equal(2, (await store.ApplyPlanningAsync(project.Id, job, [proposed], saved.Revision, _ct)).Shots.Count);
    }
    [Fact]
    public async Task PlanningRechecksLookEligibilityUnderTheProjectLock()
    {
        var (project, _, store, assets) = Fixture(); var character = LookFixtures.Character();
        var library = await assets.SaveAsync(new() { ProjectId = project.Id, Assets = [character] }, 0, _ct);
        var proposed = Ready() with { Characters = [new(Guid.NewGuid(), character.Name) { Appearance = new(character.Id, character.Looks[0].Id) }] };
        library.Assets[0] = character with { Looks = character.Looks.Select(l => l with { Archived = true }).ToArray() };
        await assets.SaveAsync(library, library.Revision, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.ApplyPlanningAsync(project.Id, Guid.NewGuid(), [proposed], 0, _ct));
        Assert.Empty((await store.LoadAsync(project.Id, _ct)).PlanningReviews);
    }
    [Fact]
    public async Task PlanningPreservesTakesTrashAndSubsequentOrdinarySaves()
    {
        var (project, _, store, _) = Fixture(); var original = Ready();
        await store.SaveAsync(project.Id, [original], 0, ct: _ct);
        var withTake = await AddTake(project.Id, store, original);
        var trashed = await store.DiscardAsync(project.Id, withTake.Takes[0].Id, ShotTrashKind.Take, withTake.Revision, _ct);
        var saved = await store.ApplyPlanningAsync(project.Id, Guid.NewGuid(), [Ready()], trashed.Revision, _ct);
        Assert.Equal(trashed.Trash[0].Id, Assert.Single(saved.Trash).Id);
        saved.Shots[0].Description = "Author's updated camera action";
        var updated = await store.SaveAsync(project.Id, saved.Shots, saved.Revision, ct: _ct);
        Assert.Single(updated.PlanningReviews); Assert.Single(updated.Trash); Assert.Equal(saved.Shots[0].Description, updated.Shots[0].Description);
    }
}
