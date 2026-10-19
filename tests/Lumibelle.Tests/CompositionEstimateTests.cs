using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task CompositionEstimateDoesNotOpenReferenceOrNeighbourEvidenceImages()
    {
        var f = Fixture();
        var assets = new FakeAssetStore(f.Project.Id);
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Plaza", Category = AssetCategory.Environment };
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "metadata-only.png", ContentType = "image/png", Width = 640, Height = 360 };
        owner = owner with { Images = [image] };
        await assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [owner] }, 0, _ct);
        var binding = new ShotImageBinding { AssetId = owner.Id, MediaId = image.Id, Crop = new() { Width = .5 } };
        var neighbour = new Shot { Title = "Before", Description = "She waits", Duration = 1, Direction = new() { Intent = new() { Purpose = "Hide the reaction" } },
            DraftingEnvironment = new(new(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, owner.Id, owner.Name, "\n", "Preserve scale",
                [new("image/" + image.Id, "Environment image", false, "", binding with { Crop = null }, null, null, new('a', 64))], "snapshot"), new() { Summary = "Podium obscures Riley" }) };
        ShotDirections.Review(neighbour, null);
        var shot = new Shot { Title = "Current", Description = "She turns", Duration = 1, Images = [binding] };
        await f.Shots.SaveAsync(f.Project.Id, [neighbour, shot], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var production = new FileProductionStore(f.Files, f.Shots, assets, projects, _clock, new FileAiJobStore(Path.Combine(_root, "estimate-jobs"), _clock));
        var composition = (await production.InitializeAsync(f.Project.Id, _ct)).Compositions.Single(c => c.ShotId == shot.Id);
        composition.Shot.Images = [binding];
        await production.SaveAsync(f.Project.Id, composition, composition.Version, _ct);
        var capture = new AiTextJobCapture(new FakeAiSettingsStore(), projects, assets, null!, null!, production: production, shots: f.Shots);
        var estimate = await capture.CompositionContextAsync(f.Project.Id, composition.Id, _ct);
        Assert.Equal(new ImageSize(320, 360), Assert.Single(estimate.ReferenceImages));
        Assert.Equal(1, estimate.NearbyShots);
        Assert.True(estimate.NearbyShotsTokens > 0);
        Assert.Equal(0, assets.ImageOpenCalls);
    }
}

public sealed class CompositionEstimateTests
{
    [Fact]
    public void MetadataEstimateIncludesCropsPreparedSizeRefModsAndTakeFrames()
    {
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Plaza" };
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "large.png", ContentType = "image/png", Width = 4000, Height = 2000 };
        owner = owner with { Images = [image] };
        var take = new ShotTake { Id = Guid.NewGuid(), Width = 2000, Height = 2000, Snapshot = null! };
        var shot = new Shot { Images = [new() { AssetId = owner.Id, MediaId = image.Id, Crop = new() { X = .25, Width = .5, Height = .5 } }],
            ContinuityFrame = new(Guid.NewGuid(), take.Id, 0, "Before"), StartFrame = new(take.Id, 0),
            Videos = [new() { Media = new(Guid.NewGuid(), "hash", 1, 640, 360, 24, 24, 1, false), Visuals = ReelVisuals.Keyframes,
                Keyframes = new() { Frames = [new() { Frame = new(Guid.NewGuid(), "hash", 0, 0), Crop = new() { Width = .5 } }] } },
                new() { Media = new(Guid.NewGuid(), "hash", 1, 640, 360, 24, 24, 1, false), Visuals = ReelVisuals.RefMod,
                    RefMod = new(new("protocol", "key", "selection", 512, 512, "vae", ["first", "second"]), "server", "file", Guid.NewGuid()) }] };
        var result = ProductionInputs.EstimateSizes(shot, new() { ProjectId = Guid.NewGuid(), Assets = [owner] }, new() { ProjectId = Guid.NewGuid(), Takes = [take] });
        Assert.Equal(new ImageSize[] { new(2000, 1000), new(1414, 1414), new(320, 360), new(512, 512), new(512, 512), new(2000, 2000) }, result.Images);
        Assert.Equal(2, result.ReelFrames); Assert.True(result.OpeningFrame);
    }
}
