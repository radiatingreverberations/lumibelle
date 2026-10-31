using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

internal static class MotionReferenceFixtures
{
    public static void MapMotionReferenceFixtures(this WebApplication app)
    {
        app.MapPost("/fixtures/{id:guid}/motion-reference-setup", async (Guid id, IProductionStore production, IShotStore shots, int takes = 1) => {
            var document = await shots.LoadAsync(id); document.Shots[0].Duration = 5;
            document = await shots.SaveAsync(id, document.Shots, document.Revision);
            var setups = await production.InitializeAsync(id); var setup = setups.Compositions.First();
            setup.Shot.Duration = 5; setup.Shot.Resolution = VideoResolution.Quick; setup.Shot.NativeResolution = false;
            setup.Shot.GenerationPreset = "standard"; setup.TakeCount = takes; setup.Seed = 52;
            setup.Prompt = H3Policy.Compile(setup.Shot); setup.SourceFingerprint = ProductionPolicy.SourceFingerprint(document.Shots[0]);
            return await production.SaveAsync(id, setup, setup.Version);
        });
    }
}
