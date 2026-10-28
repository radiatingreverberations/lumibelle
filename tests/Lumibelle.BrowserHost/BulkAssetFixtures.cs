using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;

public static class BulkAssetFixtures
{
    public static void MapBulkAssetFixtures(this WebApplication app)
    {
        app.MapPost("/fixtures/{id:guid}/bulk-types", async (Guid id, IAssetStore assets) => {
            var library = await assets.LoadAsync(id);
            return await assets.SaveAsync(library with { Assets = [
                new() { Id = Guid.NewGuid(), Name = "Juniper", Category = AssetCategory.Character, Description = "A small green robot", Looks = [new() { Name = "Raincoat", Description = "Yellow waterproof coat" }] },
                new() { Id = Guid.NewGuid(), Name = "Workshop", Category = AssetCategory.Environment, Description = "A bright workshop with a red door" },
                new() { Id = Guid.NewGuid(), Name = "Desk lamp", Category = AssetCategory.Prop, Description = "A blue desk lamp" }
            ] }, library.Revision);
        });
        app.MapPost("/fixtures/{id:guid}/review-assets", async (Guid id, IAssetStore assets) => {
            var library = await assets.LoadAsync(id);
            return await assets.SaveAsync(library with { Assets = [new() { Id = Guid.NewGuid(), Name = "Juniper", Category = AssetCategory.Character, Description = "A small green robot" },
                new() { Id = Guid.NewGuid(), Name = "Desk lamp", Category = AssetCategory.Prop, Description = "A blue desk lamp" }] }, library.Revision);
        });
        app.MapPost("/fixtures/{id:guid}/bulk-reels", async (Guid id, IAssetStore assets, lumibelle.Services.Shots.IVideoGenerator generator) =>
        {
            // Give the real frame selector a nonblank synthetic clip to inspect.
            ((Lumibelle.Testing.MockVideoGenerator)generator).PatternedFrames = true;
            var library = await assets.LoadAsync(id);
            library = await assets.SaveAsync(library with { Assets = [new() { Id = Guid.NewGuid(), Name = "Juniper", Category = AssetCategory.Character,
                Description = "A small green robot", Looks = [new() { Name = "Raincoat", Description = "Yellow waterproof coat" }] },
                new() { Id = Guid.NewGuid(), Name = "Workshop", Category = AssetCategory.Environment, Description = "A workshop with a central table and a red door" }] }, library.Revision);
            foreach (var owner in library.Assets.ToArray()) {
                using var bytes = new MemoryStream(MockImages.Png());
                library = await assets.AddImageAsync(id, owner.Id, bytes, new("reference.png", [], AssetImageOrigin.Imported), library.Revision);
            }
            return await assets.SaveAsync(library with { Assets = library.Assets.Select(a => a with { Images = a.Images.Select(i => i with { IsReference = true }).ToList() }).ToList() }, library.Revision);
        });
        app.MapGet("/fixtures/{id:guid}/asset-batches", async (Guid id, AssetBatchWorkflow workflow) =>
            (await workflow.LoadAsync(id)).Runs.Select(r => new { r.Id, Kind = r.Kind.ToString(), State = r.State.ToString(),
                r.AspectRatio, PromptModel = r.PromptModel?.Name, pending = r.Pending?.Id, items = r.Items.Select(i => new { i.Id, AssetName = i.Context.AssetName, i.Category, i.Prompt, i.AdditionalInstructions, i.EnhancedPrompt, i.PromptAttempts, i.PromptJobs, State = i.State.ToString(), i.Attempts, i.ReviewAttempts, i.GenerationJobs, i.ReelCandidates, i.ReviewedReels }) }));
        app.MapGet("/fixtures/{id:guid}/visual-style", async (Guid id, lumibelle.Services.AI.IProjectAiPreferencesStore preferences, AssetBatchWorkflow batches, lumibelle.Services.AI.IAiJobStore jobs) => {
            var project = (await preferences.LoadAsync(id)).VisualStyle;
            var runs = (await batches.LoadAsync(id)).Runs;
            var generation = new List<object>(); var reviews = new List<object>();
            foreach (var item in runs.SelectMany(r => r.Items)) {
                foreach (var job in item.GenerationJobs) {
                    var captured = (await jobs.ReadSnapshotAsync(job)).Deserialize<AiImageJobRequest>(lumibelle.Services.Story.AtomicJsonFile.Options);
                    if (captured?.Create is null && captured?.Edit is null) continue;
                    var style = captured.Create?.VisualStyle ?? captured.Edit?.VisualStyle;
                    generation.Add(new { Preset = style?.Preset.ToString(), style?.Direction, captured.OriginalPrompt, captured.Prompt, captured.AspectRatio });
                }
                if (item.ReviewJobId is { } review) {
                    var captured = (await jobs.ReadSnapshotAsync(review)).Deserialize<AiTextJobRequest>(lumibelle.Services.Story.AtomicJsonFile.Options);
                    if (captured?.Kind == AiJobKind.AssetImageEvaluation) reviews.Add(new { captured.Payload<AssetImageEvaluationRequest>().Prompt });
                }
            }
            return new { Project = new { Preset = project.Preset.ToString(), project.Direction }, Generation = generation, Reviews = reviews };
        });
    }
}
