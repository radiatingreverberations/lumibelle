using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task SceneInspectionCopyRemapsLiveResourcesAndPreservesCapturedEvidence()
    {
        var f = Fixture(); var target = Fixture(); var scripts = new FileScriptStore(f.Files, _clock);
        var scene = ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. PLAZA — DAY");
        var action = ScriptBlock.Create(ScriptBlockKind.Action, "Riley waits behind the tall podium.");
        await scripts.SaveAsync(new() { ProjectId = f.Project.Id, Blocks = [scene, action] }, 0, cancellationToken: _ct);
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Plaza", Category = AssetCategory.Environment };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, _ct);
        using var png = new MemoryStream(AssetStoreTests.Png(40, 32));
        library = await f.Assets.AddImageAsync(f.Project.Id, asset.Id, png, new("plaza.png", [], AssetImageOrigin.Imported), library.Revision, _ct);
        var image = library.Assets[0].Images[0];
        var view = new EnvironmentViewCandidate("image/" + image.Id, "Standing side", true, "Keep the obstruction", new() { AssetId = asset.Id, MediaId = image.Id, Crop = new() { Width = .5 } }, null, null, "captured-hash");
        var setup = new DraftingEnvironmentSetup { AssetId = asset.Id, Constraints = "Riley cannot see over the podium.", Views = [view] };
        var pool = new EnvironmentSourcePool(f.Project.Id, asset.Id, asset.Name, "", "source", [view], [], new Dictionary<string, byte[]>());
        var captured = DraftingEnvironments.Snapshot(scene.Id, setup, pool);
        var shot = Ready(); shot.SceneId = scene.Id; shot.SourceBlockIds = [action.Id]; shot.ApprovedScriptId = null;
        shot.EnvironmentAssetId = asset.Id; shot.DraftingEnvironment = new(captured, new() { Summary = "Preserve the obstruction", Claims = [new("Standing side", ReferenceSupportStatus.Supported, [view.Id])] });
        shot.EnvironmentInspection = ShotCopy.Of(setup);
        await f.Shots.SaveWorkspaceAsync(f.Project.Id, [shot], [new() { SceneId = scene.Id, SceneTitle = scene.Text, Environment = setup }], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = id => Task.FromResult<ProjectInfo?>(id == f.Project.Id ? f.Project : target.Project) };
        var production = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, new FileAiJobStore(Path.Combine(_root, "jobs"), _clock));
        await production.InitializeAsync(f.Project.Id, _ct);
        // Production's one-time migration intentionally retires legacy scene reference setups.
        var migrated = await f.Shots.LoadAsync(f.Project.Id, _ct);
        await f.Shots.SaveWorkspaceAsync(f.Project.Id, migrated.Shots, [new() { SceneId = scene.Id, SceneTitle = scene.Text, Environment = setup }], migrated.Revision, ct: _ct);
        var copier = new ShotProjectCopyStore(f.Files, f.Shots, production, scripts, f.Assets, f.Assets, _clock);
        var preview = await copier.PreviewAsync(f.Project.Id, [shot.Id], _ct);
        await copier.CopyAsync(new(Guid.NewGuid(), f.Project.Id, target.Project.Id, [shot.Id], preview.ShotRevision, preview.ProductionRevision, preview.ScriptRevision), ct: _ct);
        var copied = await f.Shots.LoadAsync(target.Project.Id, _ct); var result = Assert.Single(copied.Shots);
        var copiedAsset = Assert.Single((await f.Assets.LoadAsync(target.Project.Id, _ct)).Assets);
        var live = Assert.Single(copied.SceneSetups).Environment!;
        Assert.Equal(copiedAsset.Id, live.AssetId); Assert.Equal(copiedAsset.Id, result.EnvironmentAssetId);
        Assert.Equal("image/" + copiedAsset.Images[0].Id, live.Views[0].Id);
        Assert.Equal(copiedAsset.Images[0].Id, result.EnvironmentInspection!.Views[0].Image!.MediaId);
        Assert.Equal(JsonSerializer.Serialize(shot.DraftingEnvironment.Snapshot, AtomicJsonFile.Options), JsonSerializer.Serialize(result.DraftingEnvironment!.Snapshot, AtomicJsonFile.Options));
        Assert.Equal(copiedAsset.Id, result.DraftingEnvironment.LiveAssetId);
        Assert.Equal(copiedAsset.Images[0].Id, result.DraftingEnvironment.Resources[image.Id]);
        Assert.Equal(view.Id, result.DraftingEnvironment.Assessment.Claims[0].CandidateIds[0]);
        Assert.Null(ShotDirections.EnvironmentIssue(copied, result));
        var source = await f.Shots.LoadAsync(f.Project.Id, _ct);
        Assert.Equal(asset.Id, source.SceneSetups[0].Environment!.AssetId);
        Assert.NotEmpty(copied.Recovery);
    }
}
