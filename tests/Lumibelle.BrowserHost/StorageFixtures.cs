using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Storage;
using lumibelle.Services.Story;
using lumibelle.Services.AI;
using Microsoft.Data.Sqlite;

public static class StorageFixtures
{
    public static void MapStorageFixtures(this WebApplication app)
    {
        app.MapPost("/fixtures/{id:guid}/format/1", async (Guid id, ProjectFiles files) =>
        {
            var directory = await files.DirectoryAsync(id, default);
            await FileProjectAiPreferencesStore.Documents.SaveAsync(directory, id, new ProjectAiPreferences { ProjectId = id, SchemaVersion = 1 }, default);
            using var connection = ProjectFormat.Connect(Path.Combine(directory, ProjectDatabase.FileName));
            using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version = 1"; command.ExecuteNonQuery();
            DocumentCache.EvictDirectory(directory);
            return Results.Ok();
        });
        app.MapGet("/fixtures/{id:guid}/format", async (Guid id, ProjectFiles files) => ProjectFormat.Inspect(await files.LocateAsync(id, default)));
        app.MapPost("/fixtures/{id:guid}/upgrade-blocker", async (Guid id, IAiJobStore jobs, AiJobCoordinator queue) =>
        {
            await queue.SetPausedAsync(AiBackend.OpenRouter, true);
            var job = await jobs.EnqueueAsync(AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.PromptComposition, AiBackend.OpenRouter,
                new(id, ShotId: Guid.NewGuid(), CompositionId: Guid.NewGuid()), "Upgrade project", "Setup", Guid.NewGuid(), new { }));
            return Results.Ok(job.Id);
        });
        app.MapPost("/fixtures/upgrade-blocker/{job:guid}/settle", async (Guid job, IAiJobStore jobs, AiJobCoordinator queue) =>
        {
            await jobs.UpdateAsync(job, j => j with { State = AiJobState.Cancelled });
            await queue.SetPausedAsync(AiBackend.OpenRouter, false);
            return Results.Ok();
        });
        app.MapPost("/fixtures/{id:guid}/archives", async (Guid id, IShotStore shots, int count = 2) =>
        {
            if (count is < 1 or > 26) return Results.BadRequest();
            var doc = await shots.LoadAsync(id);
            var shot = new Shot { Title = "Storage test", Description = "A blue square moves across a neutral background.", Duration = 1, ApprovedScriptId = Guid.NewGuid(), SceneId = Guid.NewGuid(), SaveLosslessFrames = true };
            doc = await shots.SaveAsync(id, [.. doc.Shots, shot], doc.Revision);
            var snapshot = new VideoSnapshot(id, doc.Revision, shot, H3Policy.Compile(shot), H3Policy.Fingerprint(shot), "http://localhost:8188", new(), 32, 32, H3Policy.Frames(1));
            var run = new VideoRun { Snapshot = snapshot }; var mock = new Lumibelle.Testing.MockVideoGenerator();
            var stage = Path.Combine(await shots.RunDirectoryAsync(id, run.Id), "candidate-1");
            var template = await mock.DownloadAsync(run, new VideoCandidate { Number = 1 }, stage, _ => Task.CompletedTask, CancellationToken.None);
            var contents = Directory.GetFiles(stage).ToDictionary(p => Path.GetFileName(p)!, File.ReadAllBytes);
            for (var i = 1; i <= count; i++)
            {
                var folder = Path.Combine(await shots.RunDirectoryAsync(id, run.Id), "candidate-" + i);
                Directory.CreateDirectory(folder);
                foreach (var file in contents) await File.WriteAllBytesAsync(Path.Combine(folder, file.Key!), file.Value);
                doc = await shots.PublishTakeAsync(id, template with { Id = Guid.NewGuid(), Candidate = i }, folder);
            }
            return Results.Ok(doc);
        });
    }
}
