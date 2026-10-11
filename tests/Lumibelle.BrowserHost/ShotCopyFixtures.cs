using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

internal static class ShotCopyFixtures
{
    public static void MapShotCopyFixtures(this WebApplication app)
    {
        app.MapPost("/fixtures/{id:guid}/shot-copy", async (Guid id, IShotStore shots, IScriptStore scripts, IAssetStore assets, IProductionStore production) => {
            var script = await scripts.LoadAsync(id);
            var scene = ScriptStructure.Sections(script.Blocks).First(s => s.Kind == ScriptBlockKind.Scene);
            var source = await shots.LoadAsync(id);
            var selected = Enumerable.Range(1, 3).Select(i => new Shot { Title = "Motion study " + i, Description = "Juniper turns toward the camera.", Duration = 3,
                SceneId = scene.Id, SceneTitle = scene.Title, SourceBlockIds = script.Blocks.Skip(scene.Start).Take(scene.Count).Select(b => b.Id).ToList() }).ToArray();
            await shots.SaveAsync(id, selected, source.Revision);
            var document = await production.InitializeAsync(id);
            var owner = (await assets.LoadAsync(id)).Assets.First(a => a.Images.Count > 0);
            foreach (var composition in document.Compositions) {
                composition.Prompt = "A slow continuous turn. Keep the setting and character.";
                composition.Shot.Images = [new() { AssetId = owner.Id, MediaId = owner.Images[0].Id, Name = owner.Name, InferUsage = true }];
                await production.SaveAsync(id, composition, composition.Version);
            }
            return Results.Ok();
        });
    }
}
