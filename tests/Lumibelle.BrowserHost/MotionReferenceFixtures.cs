using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

internal static class MotionReferenceFixtures
{
    public static void MapMotionReferenceFixtures(this WebApplication app)
    {
        app.MapGet("/fixtures/{id:guid}/refinement-batches", async (Guid id, RefinementBatchWorkflow batches) => await batches.LoadAsync(id));
        app.MapPost("/fixtures/{id:guid}/refinement-history", async (Guid id, IShotStore shots, AiVideoJobCapture capture, RefinementBatchWorkflow batches) => {
            var source = (await shots.LoadAsync(id)).Takes.First();
            var draft = await capture.PrepareMotionReferenceAsync(id, source.Id);
            for (var i = 0; i < 4; i++) {
                var copy = ShotCopy.Of(draft);
                if (i == 2) copy.Shot.Title = "PREPARATION_FAILURE";
                var entry = new RefinementBatchEntry(RefinementSourceKind.Take, source.Id, $"History clip {i}", new(copy, copy.Source.BasePrompt, copy.Source.Instructions));
                var run = await batches.CreateAsync(id, Guid.NewGuid(), new(Guid.NewGuid(), $"History batch {i}", false, [entry]));
                if (i < 2) await batches.CancelWaitingAsync(id, run.Id);
                if (i == 2) await batches.PrepareAsync(id, run.Id, run.Items[0].Id);
            }
            await batches.AdvanceAsync(id);
            return await batches.LoadAsync(id);
        });
        app.MapPost("/fixtures/{id:guid}/motion-reference-setup", async (Guid id, IProductionStore production, IShotStore shots, IAiSettingsStore settings, int takes = 1, bool loras = false) => {
            var document = await shots.LoadAsync(id); document.Shots[0].Duration = 5;
            document = await shots.SaveAsync(id, document.Shots, document.Revision);
            var setups = await production.InitializeAsync(id); var setup = setups.Compositions.First();
            setup.Shot.Duration = 5; setup.Shot.Resolution = VideoResolution.Quick; setup.Shot.NativeResolution = false;
            setup.Shot.GenerationPreset = "standard"; setup.TakeCount = takes; setup.Seed = 52;
            if (loras) {
                var current = await settings.LoadAsync();
                var character = new LoraDefinition(new(current.ComfyUrl, "h3/character.safetensors", LoraWorkflow.MiniMaxH3Ref2VA, "Batch character"));
                var film = new LoraDefinition(new(current.ComfyUrl, "h3/styles/film.safetensors", LoraWorkflow.MiniMaxH3Ref2VA, "Batch film"));
                await settings.SaveAsync(current with { LoraLibrary = [character, film] });
                setup.Shot.Loras = [new(character.Reference, .4f), new(film.Reference, .8f)];
            }
            setup.Prompt = H3Policy.Compile(setup.Shot); setup.SourceFingerprint = ProductionPolicy.SourceFingerprint(document.Shots[0]);
            return await production.SaveAsync(id, setup, setup.Version);
        });
    }
}
