using System.Text.Json;
using System.Collections.Concurrent;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;

internal sealed class ReferenceTestProductionStore(FileProductionStore inner) : IProductionStore
{
    public ConcurrentDictionary<Guid, bool> Failures { get; } = new();
    public Task<ProductionDocument> LoadAsync(Guid p, CancellationToken ct = default) => inner.LoadAsync(p, ct);
    public Task<ProductionDocument> InitializeAsync(Guid p, CancellationToken ct = default) => inner.InitializeAsync(p, ct);
    public Task<ProductionDocument> SaveAsync(Guid p, ProductionComposition c, long v, CancellationToken ct = default) => Failures.GetValueOrDefault(p)
        ? Task.FromException<ProductionDocument>(new WorkspaceStoreException("Fixture storage unavailable")) : inner.SaveAsync(p, c, v, ct);
    public Task<ProductionDocument> ApplyResultAsync(Guid p, Guid j, bool a, CancellationToken ct = default, bool accept = false) => inner.ApplyResultAsync(p, j, a, ct, accept);
    public Task<ProductionDocument> RecoverResponseAsync(Guid p, Guid j, long v, CancellationToken ct = default) => inner.RecoverResponseAsync(p, j, v, ct);
    public Task<ProductionDocument> AcceptAsync(Guid p, Guid c, long v, Guid? j = null, TextModelReference? m = null, CancellationToken ct = default) => inner.AcceptAsync(p, c, v, j, m, ct);
    public Task<ProductionDocument> SetAspectAsync(Guid p, IReadOnlyCollection<Guid> s, string? a, CancellationToken ct = default) => inner.SetAspectAsync(p, s, a, ct);
    public Task<ProductionDocument> ReplaceShotContentAsync(Guid p, long r, IReadOnlyList<ShotProductionContent> c, CancellationToken ct = default) => inner.ReplaceShotContentAsync(p, r, c, ct);
}
// Holds Start fresh open so a test can act while the panel replaces its recipe.
internal sealed class ReferenceTestReelStore(FileAssetStore inner) : IAssetReelStore
{
    public int FreshDelay { get; set; }
    public async Task<ReferenceReelDraft?> StartFreshAfterSuccessAsync(AiJobHeader j, ReferenceReelDraft s, CancellationToken ct = default)
    { var fresh = await inner.StartFreshAfterSuccessAsync(j, s, ct); await Task.Delay(FreshDelay, ct); return fresh; }
    public Task<AssetLibrary> SaveKeyframesAsync(Guid p, AssetReferenceReel b, ReelKeyframeSet f, long r, CancellationToken ct = default) => inner.SaveKeyframesAsync(p, b, f, r, ct);
    public Task<ReferenceReelDraft> SaveDraftAsync(Guid p, ReferenceReelDraft d, long r, CancellationToken ct = default) => inner.SaveDraftAsync(p, d, r, ct);
    public Task<AssetLibrary> SaveReelAsync(Guid p, AssetReferenceReel reel, long r, CancellationToken ct = default) => inner.SaveReelAsync(p, reel, r, ct);
    public Task<AssetLibrary> EditReelDetailsAsync(Guid p, AssetReferenceReel b, string n, string g, long r, CancellationToken ct = default, Guid? l = null, bool u = false) => inner.EditReelDetailsAsync(p, b, n, g, r, ct, l, u);
    public Task<AssetLibrary> TrashReelAsync(Guid p, Guid reel, long r, CancellationToken ct = default) => inner.TrashReelAsync(p, reel, r, ct);
    public Task<AssetLibrary> RestoreReelAsync(Guid p, Guid reel, long r, CancellationToken ct = default) => inner.RestoreReelAsync(p, reel, r, ct);
    public Task<AssetLibrary> PublishReelAsync(Guid p, AssetReferenceReel reel, CancellationToken ct = default) => inner.PublishReelAsync(p, reel, ct);
    public Task<bool> ApplyPairAsync(AiJobHeader j, ReelCompositionRequest r, ReelPromptPair pair, bool i, CancellationToken ct = default, bool a = false) => inner.ApplyPairAsync(j, r, pair, i, ct, a);
    public Task<string> RunDirectoryAsync(Guid p, Guid b, CancellationToken ct = default) => inner.RunDirectoryAsync(p, b, ct);
}
internal static class ReferencePickerFixtures
{
    public static void MapReferencePickerFixtures(this WebApplication app)
    {
        app.MapGet("/fixtures/{id:guid}/environment-setup", async (Guid id, IProductionStore production) => {
            var c = (await production.LoadAsync(id)).Compositions[0]; return new { c.Id, c.ShotId, c.Shot, c.Prompt };
        });
        app.MapPost("/fixtures/{id:guid}/environment-copy-source", async (Guid id, IProductionStore production, ProjectFiles files) => {
            var document = await production.LoadAsync(id); var source = document.Compositions[0];
            var alternate = source.Copy(); alternate.Id = Guid.NewGuid(); alternate.Name = "Environment preset";
            alternate.GenerationSetupId = null; alternate.GenerationSetupVersion = 0;
            source.Shot.EnvironmentAssetId = null;
            document.Compositions.Add(alternate); document.SchemaVersion = 3;
            foreach (var content in document.ShotContent) content.EnvironmentAssetId = null;
            await AtomicJsonFile.WriteAsync(Path.Combine(await files.DirectoryAsync(id, default), "production.json"), document, default);
            return Results.Ok();
        });
        app.MapPost("/fixtures/{id:guid}/picture-ten", async (Guid id, IProductionStore production, IAssetStore assets, ProjectFiles files) => {
            var c = (await production.LoadAsync(id)).Compositions[0];
            var library = await assets.LoadAsync(id); var reel = library.Reels.First();
            c.Shot.Images = Enumerable.Range(0, 8).Select(_ => { var b = ShotCopy.Of(c.Shot.Images[0]); b.Id = Guid.NewGuid(); return b; }).ToList();
            c.Shot.Videos = [new() { Name = reel.Name, Media = reel.Media, OwnerAssetId = reel.AssetId, OwnerCategory = AssetCategory.Environment,
                Visuals = ReelVisuals.Keyframes, Keyframes = new() { Frames = reel.Keyframes!.Frames.Take(2).ToList() } }];
            // Simulate an older over-budget document without weakening the real save validation.
            var path = Path.Combine(await files.DirectoryAsync(id, CancellationToken.None), "production.json");
            var raw = (await AtomicJsonFile.ReadAsync<ProductionDocument>(path, CancellationToken.None))!;
            raw.ShotContent[0] = ShotProductionContent.From(c);
            await AtomicJsonFile.WriteAsync(path, raw, CancellationToken.None); return Results.Ok();
        });
        app.MapPost("/fixtures/{id:guid}/environment-library", async (Guid id, IAssetStore assets, IAssetReelStore reels, IProductionStore production, int extraViews = 0, string notes = "", bool selected = true, bool fullBudget = false) => {
            var library = await assets.LoadAsync(id); var owner = library.Assets.First();
            owner = owner with { Name = "Plaza environment", Category = AssetCategory.Environment };
            library = await assets.SaveAsync(library with { Assets = library.Assets.Select(a => a.Id == owner.Id ? owner : a).ToList() }, library.Revision);
            foreach (var reel in library.Reels.ToArray()) {
                library = await reels.SaveReelAsync(id, reel with { AssetId = owner.Id, Keyframes = new() { Frames = (extraViews > 0 ? Enumerable.Range(0, extraViews) : new[] { 0, 24, 48 }).Select(index => new ReelKeyframe {
                    Name = reel.Name + " view " + index, Notes = "Keep the doorway design.", Crop = new() { Width = .8 },
                    Frame = new(reel.Media.Id, reel.Media.Sha256, index, index / 24d) }).ToList() } }, library.Revision);
            }
            if (library.Reels.Count > 0) library = await reels.SaveReelAsync(id, library.Reels[0] with { Id = Guid.NewGuid(), Name = "Unkeyframed survey", Keyframes = null }, library.Revision);
            var doc = await production.InitializeAsync(id); var c = doc.Compositions[0]; c.DirectingNotes = notes;
            var character = library.Assets.First(a => a.Id != owner.Id && a.Images.Count > 0);
            c.Shot.Images = [ReferenceSetups.Bind(character, character.Images[0], c.Shot)];
            if (fullBudget) c.Shot.Images = Enumerable.Range(0, 9).Select(_ => { var image = ShotCopy.Of(c.Shot.Images[0]); image.Id = Guid.NewGuid(); return image; }).ToList();
            c.Shot.EnvironmentAssetId = selected ? owner.Id : null;
            c.Prompt = "My authored prompt";
            await production.SaveAsync(id, c, c.Version);
            return library;
        });
        // Seed saved source angles and an asset default; providers remain mocked.
        app.MapPost("/fixtures/{id:guid}/reel-reference-default", async (Guid id, ReelVisuals mode, IAssetStore assets,
            IAssetReelStore reels, IAiSettingsStore settings, IVideoGenerator generator) => {
            var library = await assets.LoadAsync(id);
            library.Assets[0] = library.Assets[0] with { DefaultReelVisuals = mode };
            library = await assets.SaveAsync(library, library.Revision);
            var source = library.Reels.First();
            source = source with { Keyframes = new() { Frames = new[] { 0, 24, 48 }.Select(index => new ReelKeyframe {
                Frame = new(source.Media.Id, source.Media.Sha256, index, index / 24d), Notes = "Source angle " + index }).ToList() } };
            await reels.SaveReelAsync(id, source, library.Revision);
            var mock = (MockVideoGenerator)generator;
            mock.Catalog = (await mock.CheckAsync(await settings.LoadAsync())) with { RefModIssue = null };
            return Results.Ok();
        });
        app.MapPost("/fixtures/{id:guid}/timeline-reel", async (Guid id, bool lossless, HttpRequest request, IReferenceVideoStore videos, IAiSettingsStore settings, IAssetStore assets, IAssetReelStore reels, ProjectFiles files) => {
            var library = await assets.LoadAsync(id); var configured = (await settings.LoadAsync()).H3;
            var media = await videos.ImportAsync(id, request.Body, "timeline.mp4", configured);
            if (lossless) {
                var directory = Path.Combine(await files.DirectoryAsync(id, request.HttpContext.RequestAborted), "timeline-fixture"); Directory.CreateDirectory(directory);
                var frames = await MockFrameArchive.WriteAsync(directory, media.Frames, media.Width, media.Height, CancellationToken.None);
                var take = new ShotTake { Width = media.Width, Height = media.Height, Frames = frames,
                    Snapshot = new(id, 0, new(), "", "", "http://mock:8188", configured, media.Width, media.Height, media.Frames) { OutputPolicy = new(true) } };
                await videos.PublishArchiveAsync(id, media, take, directory);
            }
            var reel = new AssetReferenceReel { AssetId = library.Assets[0].Id, Name = "Timeline reel", UseGuidance = "Timeline test", Media = media };
            await reels.SaveReelAsync(id, reel, library.Revision);
            return new { reel, catalog = await videos.FrameCatalogAsync(id, media, configured) };
        });
        app.MapGet("/fixtures/ai-jobs/{id:guid}/video-inputs", async (Guid id, IAiJobStore jobs) => {
            var request = (await jobs.ReadSnapshotAsync(id)).Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
            return new { inputs = request.Inputs.Select(i => new { kind = i.EffectiveKind.ToString(), i.FileName, i.Sha256 }),
                graph = ComfyH3Video.BuildWorkflow(request.Snapshot, 1, "fixture", request.Inputs.Select(i => new PreparedVideoInput(i.FileName, i.Audio) { Kind = i.Kind, VideoIndex = i.VideoIndex }).ToArray()) };
        });
        app.MapPost("/fixtures/{id:guid}/media-move-target", async (Guid id, IAssetStore assets) => {
            var library = await assets.LoadAsync(id);
            var target = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Moved references", Category = library.Assets[0].Category };
            await assets.SaveAsync(library with { Assets = [.. library.Assets, target] }, library.Revision);
            return target;
        });
        app.MapPost("/fixtures/reel-fresh-delay", (int milliseconds, ReferenceTestReelStore store) => { store.FreshDelay = milliseconds; return Results.Ok(); });
        app.MapPost("/fixtures/{id:guid}/reference-save-failure", (Guid id, bool fail, ReferenceTestProductionStore store) => { store.Failures[id] = fail; return Results.Ok(); });
        app.MapPost("/fixtures/{id:guid}/reference-reels", async (Guid id, Guid takeId, IReferenceVideoStore videos, IAiSettingsStore settings, IAssetStore assets, IAssetReelStore reels, bool environment = false) => {
            var library = await assets.LoadAsync(id); var owner = library.Assets.First(a => a.Category == AssetCategory.Character);
            if (environment) {
                owner = owner with { Category = AssetCategory.Environment };
                library = await assets.SaveAsync(library with { Assets = library.Assets.Select(a => a.Id == owner.Id ? owner : a).ToList() }, library.Revision);
            }
            foreach (var name in new[] { "Angles", "Face detail" }) {
                var media = await videos.CopyTakeAsync(id, takeId, (await settings.LoadAsync()).H3);
                library = await reels.SaveReelAsync(id, new() { AssetId = owner.Id, Name = name, UseGuidance = "Intended " + name + "; preserve identity, not reference actions or words.", Media = media, CreatedUtc = DateTimeOffset.UtcNow }, library.Revision);
            }
            return library;
        });
        app.MapPost("/fixtures/{id:guid}/reference-reel-guidance", async (Guid id, Guid reelId, IAssetStore assets, IAssetReelStore reels) => {
            var library = await assets.LoadAsync(id); var reel = library.Reels.Single(r => r.Id == reelId);
            return await reels.EditReelDetailsAsync(id, reel, reel.Name, "Changed library guidance", library.Revision);
        });
        app.MapPost("/fixtures/{id:guid}/legacy-reference", async (Guid id, Guid reelId, IAssetStore assets, IAssetReelStore reels, IProductionStore production) => {
            var library = await assets.LoadAsync(id); var reel = library.Reels.Single(r => r.Id == reelId);
            var doc = await production.LoadAsync(id); var c = doc.Compositions[0];
            c.Shot.Videos = [new() { Media = reel.Media, Name = "Legacy attachment", Description = "Legacy guidance", UseSoundtrack = true }];
            await production.SaveAsync(id, c, c.Version);
            return await reels.TrashReelAsync(id, reel.Id, library.Revision);
        });
        // Attaches one reel to every shot, optionally under a reviewed prompt.
        app.MapPost("/fixtures/{id:guid}/shot-reel", async (Guid id, Guid reelId, IAssetStore assets, IProductionStore production, bool reviewed = false) => {
            var library = await assets.LoadAsync(id); var reel = library.Reels.Single(r => r.Id == reelId);
            var owner = library.Assets.Single(a => a.Id == reel.AssetId);
            var doc = await production.InitializeAsync(id);
            foreach (var shot in doc.Compositions.Select(c => c.ShotId).Distinct().ToArray()) {
                var c = doc.Compositions.First(x => x.ShotId == shot);
                c.Shot.Videos = [new() { Media = reel.Media, Name = reel.Name, Description = "Shot guidance", OwnerAssetId = owner.Id, OwnerCategory = owner.Category, Visuals = ReelVisuals.FullReel }];
                if (reviewed) c.Prompt = "Juniper looks up at <Video 1>.";
                doc = await production.SaveAsync(id, c, c.Version);
                if (reviewed) { c = doc.Compositions.First(x => x.ShotId == shot); doc = await production.AcceptAsync(id, c.Id, c.Version); }
            }
            return doc;
        });
        app.MapPost("/fixtures/{id:guid}/concurrent-reference", async (Guid id, IProductionStore production) => {
            var d = await production.LoadAsync(id); var c = d.Compositions[0]; c.DirectingNotes = "Other tab saved";
            return await production.SaveAsync(id, c, c.Version);
        });
    }
}
