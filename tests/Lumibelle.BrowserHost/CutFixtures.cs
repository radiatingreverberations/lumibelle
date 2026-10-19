using lumibelle.Models;
using lumibelle.Services.Shots;
using Lumibelle.Testing;

internal static class CutFixtures
{
    public static void MapCutFixtures(this WebApplication app)
    {
        app.MapPost("/fixtures/{id:guid}/select-saved-take/{takeId:guid}", async (Guid id, Guid takeId, IShotStore store) => {
            var doc = await store.LoadAsync(id); var take = doc.Takes.Single(t => t.Id == takeId);
            doc.Shots.Single(s => s.Id == take.ShotId).SelectedTakeId = take.Id;
            return await store.SaveAsync(id, doc.Shots, doc.Revision);
        });
        app.MapPost("/fixtures/{id:guid}/reference-video-take", async (Guid id, IShotStore store, IVideoGenerator generator) =>
        {
            var doc = await store.LoadAsync(id); var shot = doc.Shots.Last().Copy(); shot.Duration = 5; shot.Description += " KEYFRAME_FIXTURE";
            var snapshot = new VideoSnapshot(id, doc.Revision, shot, H3Policy.Compile(shot), H3Policy.Fingerprint(shot), "http://mock:8188", new(), 160, 96, H3Policy.Frames(5));
            var run = new VideoRun { Snapshot = snapshot }; var candidate = new VideoCandidate { Number = 1, Seed = 1 };
            var path = Path.Combine(await store.RunDirectoryAsync(id, run.Id), "candidate");
            var take = await generator.DownloadAsync(run, candidate, path, _ => Task.CompletedTask, CancellationToken.None);
            doc = await store.PublishTakeAsync(id, take, path);
            return doc.Takes.Last();
        });
        app.MapGet("/fixtures/{id:guid}/cut", async (Guid id, ICutStore store) => await store.LoadAsync(id));
        app.MapPost("/fixtures/{id:guid}/cut", async (Guid id, CutClip[] clips, ICutStore store) =>
            await store.SaveAsync(id, clips, (await store.LoadAsync(id)).Revision));
        app.MapPost("/fixtures/{id:guid}/cut-takes", async (Guid id, IShotStore store, IVideoGenerator generator) =>
        {
            var first = new Shot { Title = "Arrival", Duration = 2, Description = "A quiet arrival." };
            var second = new Shot { Title = "The reply with a deliberately long title to check wrapping in the cut", Duration = 2, Description = "She replies." };
            var third = new Shot { Title = "Not generated", Duration = 2, Description = "A future shot." };
            var doc = await store.SaveAsync(id, [first, second, third], (await store.LoadAsync(id)).Revision);
            foreach (var (shot, candidate, width, height) in new[] { (first, 1, 160, 96), (first, 2, 160, 96), (second, 1, 96, 160) })
            {
                shot.ApprovedScriptId ??= Guid.NewGuid(); shot.SceneId ??= Guid.NewGuid();
                var snapshot = new VideoSnapshot(id, doc.Revision, shot.Copy(), H3Policy.Compile(shot), H3Policy.Fingerprint(shot), "http://mock:8188", new(), width, height, H3Policy.Frames(2));
                var run = new VideoRun { Snapshot = snapshot }; var item = new VideoCandidate { Number = candidate, Seed = candidate };
                var path = Path.Combine(await store.RunDirectoryAsync(id, run.Id), "candidate");
                var take = await ((MockVideoGenerator)generator).DownloadAsync(run, item, path, _ => Task.CompletedTask, CancellationToken.None);
                doc = await store.PublishTakeAsync(id, take, path);
            }
            doc.Shots[0].SelectedTakeId = doc.Takes[0].Id;
            return await store.SaveAsync(id, doc.Shots, doc.Revision);
        });
        app.MapPost("/fixtures/{id:guid}/cut-trash/{take:guid}", async (Guid id, Guid take, IShotStore store) =>
        {
            var doc = await store.LoadAsync(id);
            foreach (var shot in doc.Shots.Where(s => s.SelectedTakeId == take)) shot.SelectedTakeId = null;
            doc = await store.SaveAsync(id, doc.Shots, doc.Revision);
            return await store.DiscardAsync(id, take, ShotTrashKind.Take, doc.Revision);
        });
        app.MapPost("/fixtures/{id:guid}/cut-restore", async (Guid id, IShotStore store) =>
        {
            var doc = await store.LoadAsync(id);
            return await store.RestoreAsync(id, doc.Trash.Select(t => t.Id).ToArray(), doc.Revision);
        });
    }
}
