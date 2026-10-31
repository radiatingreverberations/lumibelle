using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using System.Text.Json;

public static class CompositionRecoveryFixtures
{
    public static void MapCompositionRecoveryFixtures(this WebApplication app)
    {
        app.MapPost("/fixtures/{id:guid}/legacy-reel-error/{jobId:guid}", async (Guid id, Guid jobId, IAiJobStore store) => {
            var job = (await store.ReadAsync()).Jobs.Single(j => j.Id == jobId && j.Target.ProjectId == id && j.Kind == AiJobKind.ReelComposition);
            var result = (await store.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result))!;
            const string error = "The prompt omits Picture 1.";
            await store.WriteArtifactAsync(job.Id, AiJobArtifact.Result, result with { Error = error });
            await store.UpdateAsync(job.Id, j => j with { State = AiJobState.NeedsAttention, Recovery = AiJobRecovery.GenerateAgain, Error = error });
            return Results.Ok(new { result.Raw });
        });
        app.MapGet("/fixtures/{id:guid}/reel-response/{jobId:guid}", async (Guid id, Guid jobId, IAiJobStore store) => {
            var job = (await store.ReadAsync()).Jobs.Single(j => j.Id == jobId && j.Target.ProjectId == id && j.Kind == AiJobKind.ReelComposition);
            return Results.Ok(await store.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result));
        });
        app.MapPost("/fixtures/{id:guid}/composition-review", async (Guid id, bool conflict, IProductionStore production,
            AiTextJobCapture capture, IAiJobStore store, AiJobCoordinator queue) =>
        {
            var c = (await production.InitializeAsync(id)).Compositions[0];
            var request = await capture.ComposeAsync(Guid.NewGuid(), Guid.NewGuid(), id, c.Id, c.Version,
                new(AiBackend.OpenRouter, "mock/script", "Mock model"), false);
            var saved = request.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
            var proposed = new PromptCompositionResult(H3Policy.Compile(saved.Payload<PromptCompositionRequest>().Shot), "Saved visual explanation.");
            var response = AiTextResults.Parse(saved, JsonSerializer.Serialize(proposed, AtomicJsonFile.Options), "stop");
            await queue.SetPausedAsync(AiBackend.OpenRouter, true);
            try
            {
                await store.EnqueueAsync(request);
                await store.WriteArtifactAsync(request.Id, AiJobArtifact.Result, response);
                await store.UpdateAsync(request.Id, j => j with { State = AiJobState.Completed, FinishedUtc = DateTimeOffset.UtcNow });
                c.ReviewJobId = request.Id;
                c = (await production.SaveAsync(id, c, c.Version)).Compositions[0];
                // A second save is legitimate: the seed does not affect prompt composition.
                c.Seed = 42;
                if (conflict) c.DirectingNotes = "A different camera direction";
                await production.SaveAsync(id, c, c.Version);
            }
            finally { await queue.SetPausedAsync(AiBackend.OpenRouter, false); }
            return Results.Ok(new { request.Id, proposed.Prompt });
        });
        // A composition that is still receiving text: the provider stays paused so the job keeps waiting.
        app.MapPost("/fixtures/{id:guid}/streaming-composition", async (Guid id, IProductionStore production, AiTextJobCapture capture, IAiJobStore store, AiJobCoordinator queue) =>
        {
            var c = (await production.InitializeAsync(id)).Compositions[0];
            var request = await capture.ComposeAsync(Guid.NewGuid(), Guid.NewGuid(), id, c.Id, c.Version, new(AiBackend.OpenRouter, "mock/script", "Mock model"), false);
            await queue.SetPausedAsync(AiBackend.OpenRouter, true);
            await store.EnqueueAsync(request);
            await store.WriteArtifactAsync(request.Id, AiJobArtifact.Result, new AiTextJobResult("{\"sections\":{\"subject_definitions\":\"<Subject 1> Mira, a courier in a yellow rain"));
            c.ReviewJobId = request.Id;
            await production.SaveAsync(id, c, c.Version);
            await queue.RefreshAsync();
            return Results.Ok(new { request.Id });
        });
        app.MapPost("/fixtures/streaming-composition-advance", async (Guid jobId, IAiJobStore store, AiJobCoordinator queue) =>
        {
            await store.WriteArtifactAsync(jobId, AiJobArtifact.Result, new AiTextJobResult("{\"sections\":{\"subject_definitions\":\"<Subject 1> Mira, a courier in a yellow raincoat.\",\"summary\":\"Mira checks the address and knocks"));
            // Live progress notifies the page while text streams; a harmless header change stands in for it here.
            await store.UpdateAsync(jobId, job => job with { Priority = !job.Priority });
            await queue.RefreshAsync();
        });
        app.MapPost("/fixtures/composition-review-refresh", async (Guid jobId, IAiJobStore store, AiJobCoordinator queue) =>
        {
            await store.UpdateAsync(jobId, job => job with { FinishedUtc = DateTimeOffset.UtcNow });
            await queue.RefreshAsync();
        });
        app.MapPost("/fixtures/{id:guid}/failed-composition", async (Guid id, AiJobRecovery recovery, bool? missingSound, bool? numberWords, bool? freeform, IProductionStore production,
            AiTextJobCapture capture, IAiJobStore store, AiJobCoordinator queue, IShotStore shots) =>
        {
            if (recovery is not (AiJobRecovery.GenerateAgain or AiJobRecovery.RetryOutput or AiJobRecovery.CheckStatus)) return Results.BadRequest();
            var c = (await production.InitializeAsync(id)).Compositions[0];
            if (missingSound == true || numberWords == true || freeform == true)
            {
                var doc = await shots.LoadAsync(id); doc.Shots[0].Atmosphere = "Typing and a regrettable sip."; doc.Shots[0].Music = "None.";
                if (numberWords == true) doc.Shots[0].Duration = 8;
                await shots.SaveAsync(id, doc.Shots, doc.Revision);
                c = (await production.LoadAsync(id)).Compositions[0];
            }
            c.DirectingNotes = "Keep my camera direction.";
            c = (await production.SaveAsync(id, c, c.Version)).Compositions[0];
            var request = await capture.ComposeAsync(Guid.NewGuid(), Guid.NewGuid(), id, c.Id, c.Version,
                new(AiBackend.OpenRouter, "mock/script", "Mock model"), false);
            AiTextJobResult? response = null;
            if (missingSound == true || numberWords == true || freeform == true)
            {
                var saved = request.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
                var full = H3Policy.Compile(saved.Payload<PromptCompositionRequest>().Shot);
                var text = freeform == true ? "A quiet morning. Riley enters, smiles, and says hello.\nKeep the camera still."
                    : numberWords == true ? full.Replace("8 seconds", "eight seconds") : full[..full.IndexOf("overall_soundscape:", StringComparison.Ordinal)].TrimEnd();
                var raw = JsonSerializer.Serialize(new PromptCompositionResult(text, "Saved visual explanation."), AtomicJsonFile.Options);
                response = new(raw, true, "stop", Error: numberWords == true
                    ? "State one continuous take lasting 8 seconds, preserving the shot duration." : "Missing H3 sections.");
            }
            await queue.SetPausedAsync(AiBackend.OpenRouter, true);
            try
            {
                await store.EnqueueAsync(request);
                if (response is not null) await store.WriteArtifactAsync(request.Id, AiJobArtifact.Result, response);
                await store.UpdateAsync(request.Id, j => j with { State = AiJobState.NeedsAttention, Recovery = recovery,
                    Error = response?.Error ?? "Reconnecting... 2/5", FinishedUtc = DateTimeOffset.UtcNow });
                c.ReviewJobId = request.Id;
                await production.SaveAsync(id, c, c.Version);
            }
            finally { await queue.SetPausedAsync(AiBackend.OpenRouter, false); }
            return Results.Ok(new { request.Id });
        });
    }
}
