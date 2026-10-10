using System.Text.Json;
using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task ProjectCopyOwnsStartingAndContinuityFramesFromAnUnselectedShot()
    {
        var f = Fixture(); var target = Fixture(); var scripts = new FileScriptStore(f.Files, _clock);
        var previous = Ready(); previous.SceneId = null; previous.ApprovedScriptId = null;
        var next = previous.Copy(); next.Id = Guid.NewGuid(); next.Title = "Continue";
        var source = await f.Shots.SaveAsync(f.Project.Id, [previous, next], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = id => Task.FromResult<ProjectInfo?>(id == f.Project.Id ? f.Project : target.Project) };
        var production = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, new FileAiJobStore(Path.Combine(_root, "jobs"), _clock));
        await production.InitializeAsync(f.Project.Id, _ct);
        source = await AddTake(f.Project.Id, f.Shots, previous);
        var originalTake = Assert.Single(source.Takes);
        next.StartFrame = new(originalTake.Id, 1);
        next.ContinuityFrame = new(Guid.NewGuid(), originalTake.Id, 2, "Previous view");
        await f.Shots.SaveAsync(f.Project.Id, [previous, next], source.Revision, ct: _ct);
        var setup = (await production.LoadAsync(f.Project.Id, _ct)).Compositions.Single(c => c.ShotId == next.Id);
        setup.Shot.StartFrame = next.StartFrame; setup.Shot.ContinuityFrame = next.ContinuityFrame;
        await production.SaveAsync(f.Project.Id, setup, setup.Version, _ct);
        var copier = new ShotProjectCopyStore(f.Files, f.Shots, production, scripts, f.Assets, f.Assets, _clock);
        var preview = await copier.PreviewAsync(f.Project.Id, [next.Id], _ct);
        Assert.Equal((1, 1), (preview.Shots, preview.Takes));
        await copier.CopyAsync(new(Guid.NewGuid(), f.Project.Id, target.Project.Id, [next.Id], preview.ShotRevision, preview.ProductionRevision, preview.ScriptRevision), ct: _ct);
        var copied = await f.Shots.LoadAsync(target.Project.Id, _ct);
        var shot = Assert.Single(copied.Shots); var take = Assert.Single(copied.Takes);
        Assert.Equal(shot.Id, take.ShotId); Assert.Equal(take.Id, shot.StartFrame!.TakeId);
        Assert.Equal(take.Id, shot.ContinuityFrame!.TakeId); Assert.NotEqual(originalTake.Id, take.Id);
        var copiedSetup = (await production.LoadAsync(target.Project.Id, _ct)).Compositions.Single().Shot;
        Assert.Equal(shot.StartFrame, copiedSetup.StartFrame); Assert.Equal(shot.ContinuityFrame, copiedSetup.ContinuityFrame);
        await using var originalFrame = (await f.Shots.OpenAsync(f.Project.Id, originalTake.Id, ShotTrashKind.Take, 1, ct: _ct))!;
        await using var copiedFrame = (await f.Shots.OpenAsync(target.Project.Id, take.Id, ShotTrashKind.Take, 1, ct: _ct))!;
        using var originalBytes = new MemoryStream(); using var copiedBytes = new MemoryStream();
        await originalFrame.Content.CopyToAsync(originalBytes, _ct); await copiedFrame.Content.CopyToAsync(copiedBytes, _ct);
        Assert.Equal(originalBytes.ToArray(), copiedBytes.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectCopyRetainsPreparedInputsAndLatentsForRegenerationAndRefinement(bool withRefMod)
    {
        using var f = await QueuedVideoFixture.Create(this);
        var target = Fixture(); var scripts = new FileScriptStore(f.Files, _clock);
        await scripts.SaveAsync(new() { ProjectId = f.Project.Id, Blocks = [new() { Id = f.Shot.SceneId!.Value, Kind = ScriptBlockKind.Scene, Spans = [new("EXT. PLAZA — DAY")] }] }, 0, cancellationToken: _ct);
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Room", Category = AssetCategory.Environment };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, _ct);
        using var png = new MemoryStream(AssetStoreTests.Png(40, 32));
        library = await f.Assets.AddImageAsync(f.Project.Id, asset.Id, png, new("room.png", [], AssetImageOrigin.Imported), library.Revision, _ct);
        f.Shot.Images = [new() { AssetId = asset.Id, MediaId = library.Assets[0].Images[0].Id, Name = "Room" }];
        var projects = new FakeProjectStore { Get = id => Task.FromResult<ProjectInfo?>(id == f.Project.Id ? f.Project : target.Project) };
        var production = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, f.Jobs, new FileReferenceVideoStore(f.Files, f.Shots, null!));
        await production.InitializeAsync(f.Project.Id, _ct);
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], (await f.Shots.LoadAsync(f.Project.Id, _ct)).Revision, ct: _ct);
        var original = await f.Capture(); var context = await f.Claim(original);
        await f.Worker.ExecuteAsync(context, original.Snapshot, _ct);
        var source = await f.Shots.LoadAsync(f.Project.Id, _ct); Assert.Single(source.Takes);
        ReelRefModReference? accepted = null;
        var pixels = AssetStoreTests.Png(640, 640);
        if (withRefMod) {
            var binding = RefModBinding(2); byte[] video = [1, 2, 3, 4];
            binding.OwnerAssetId = asset.Id;
            binding.Media = binding.Media with { Sha256 = Convert.ToHexString(SHA256.HashData(video)), Bytes = video.Length };
            binding.Keyframes!.Frames = binding.Keyframes.Frames.Select(k => k with { Frame = k.Frame with { Source = binding.Media.Sha256 } }).ToList();
            var recipe = ReelRefMods.Recipe(binding, 640, 640, original.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot.Settings.VideoVae,
                [Convert.ToHexString(SHA256.HashData(pixels)), Convert.ToHexString(SHA256.HashData(pixels))]);
            var build = Guid.NewGuid(); accepted = binding.RefMod = new(recipe, "http://video.test:8188", ReelRefMods.BuildStem(f.Project.Id, build), build);
            var root = Path.Combine(f.ProjectDirectory, "reference-videos", binding.Media.Id.ToString("D")); Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(Path.Combine(root, "video.mp4"), video, _ct);
            await AtomicJsonFile.WriteAsync(Path.Combine(root, "media.json"), binding.Media, _ct);
            library = await f.Assets.LoadAsync(f.Project.Id, _ct);
            await AtomicJsonFile.WriteAsync(Path.Combine(f.ProjectDirectory, "assets.json"), library with { Reels = [new() { AssetId = asset.Id, Media = binding.Media, Name = binding.Name, CreatedUtc = _clock.GetUtcNow() }] }, _ct);
            await new ReelRefModStore(f.Files).AcceptSourcesAsync(f.Project.Id, recipe, [pixels, pixels], _ct);
            var stored = source.Takes[0];
            var captured = original.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
            var capturedShot = captured.Snapshot.Shot.Copy(); capturedShot.Videos = [binding];
            var snapshot = captured.Snapshot with { Shot = capturedShot, Fingerprint = H3Policy.Fingerprint(capturedShot),
                Prompt = captured.Snapshot.Production is null ? H3Policy.Compile(capturedShot, captured.Snapshot.ReferenceGuidance, captured.Snapshot.Appearances) : captured.Snapshot.Prompt };
            stored.CopySource = new(f.Project.Id, stored.Id, stored.Snapshot);
            stored.Snapshot = snapshot; stored.CopyRequest = captured with { Version = 2, Snapshot = snapshot };
            AiVideoJobPolicy.Validate(stored.CopyRequest);
            source.Shots[0].Videos = [binding];
            await AtomicJsonFile.WriteAsync(Path.Combine(f.ProjectDirectory, "shots.json"), source, _ct);
            var setup = (await production.LoadAsync(f.Project.Id, _ct)).Compositions.Single();
            setup.Shot.Videos = [binding]; await production.SaveAsync(f.Project.Id, setup, setup.Version, _ct);
            f.Generator.Catalog = (await f.Generator.CheckAsync(f.Settings.Value, _ct)) with { RefModIssue = null };
        }
        var copier = new ShotProjectCopyStore(f.Files, f.Shots, production, scripts, f.Assets, f.Assets, _clock, f.Jobs);
        var preview = await copier.PreviewAsync(f.Project.Id, [f.Shot.Id], _ct);
        var request = new ShotProjectCopyRequest(Guid.NewGuid(), f.Project.Id, target.Project.Id, [f.Shot.Id], preview.ShotRevision, preview.ProductionRevision, preview.ScriptRevision);
        if (accepted is not null) {
            var path = Path.Combine(f.ProjectDirectory, "refmod-previews", accepted.Recipe.Key, ReelRefModStore.FrameName(0));
            await File.WriteAllBytesAsync(path, [0], _ct);
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => copier.CopyAsync(request, ct: _ct));
            Assert.Empty((await f.Shots.LoadAsync(target.Project.Id, _ct)).Shots);
            await File.WriteAllBytesAsync(path, pixels, _ct);
            copier = new(f.Files, f.Shots, production, scripts, f.Assets, f.Assets, _clock, f.Jobs);
            Assert.Equal(AssetReusePolicy.Hash(request), AssetReusePolicy.Hash(await copier.FindPendingAsync(request with { Id = Guid.NewGuid() }, _ct)));
            Assert.Null(await copier.FindPendingAsync(request with { Id = Guid.NewGuid(), ShotRevision = request.ShotRevision + 1 }, _ct));
        }
        await copier.CopyAsync(request, ct: _ct);
        Assert.Null(await copier.FindPendingAsync(request with { Id = Guid.NewGuid() }, _ct));
        var take = Assert.Single((await f.Shots.LoadAsync(target.Project.Id, _ct)).Takes);
        Assert.NotNull(take.CopyRequest); AiVideoJobPolicy.Validate(take.CopyRequest);
        Assert.NotNull(take.RefinementPackage);
        if (accepted is not null) {
            var binding = Assert.Single(take.Snapshot.Shot.Videos);
            ReelRefMods.ValidateBinding(binding, true);
            Assert.NotEqual(accepted.Recipe.Key, binding.RefMod!.Recipe.Key);
            Assert.Equal(accepted.Recipe.FrameHashes, binding.RefMod.Recipe.FrameHashes);
            Assert.StartsWith($"lumibelle/{target.Project.Id:N}/", binding.RefMod.FileName);
            Assert.Equal(pixels, await new ReelRefModStore(f.Files).PreviewAsync(target.Project.Id, binding.RefMod, 0, _ct));
            await copier.CopyAsync(request, ct: _ct);
            Assert.Single((await f.Assets.LoadAsync(target.Project.Id, _ct)).Assets);
        }
        var before = (await f.Jobs.ReadAsync(_ct)).Jobs.Count;
        var capture = new AiVideoJobCapture(f.Shots, scripts, f.Assets, f.Settings, f.Generator, projects, f.Preferences, production, jobs: f.Jobs);
        var regeneration = await capture.CaptureRegenerationAsync(Guid.NewGuid(), Guid.NewGuid(), target.Project.Id, take.Id, VideoResolution.Quick, take.Seed, _ct);
        var regenerated = regeneration.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(regenerated, await f.Shots.RunDirectoryAsync(target.Project.Id, regenerated.BatchId, _ct), _ct);
        var refinement = await capture.CaptureRefinementAsync(Guid.NewGuid(), Guid.NewGuid(), target.Project.Id, take.Id, TakeRefinementMode.Rework, take.Width, take.Height, _ct);
        var refined = refinement.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(refined, await f.Shots.RunDirectoryAsync(target.Project.Id, refined.BatchId, _ct), _ct);
        Assert.Equal(before, (await f.Jobs.ReadAsync(_ct)).Jobs.Count); // Captures are never queued by copying.
    }

    [Fact]
    public async Task ProjectCopyKeepsScriptReferencesOrderMediaAndOriginalsAndRetriesOnce()
    {
        var f = Fixture(); var target = Fixture();
        var scripts = new FileScriptStore(f.Files, _clock);
        var act = ScriptBlock.Create(ScriptBlockKind.Act, "ACT II");
        var scene = ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. PLAZA — DAY");
        var action = ScriptBlock.Create(ScriptBlockKind.Action, "Mira spins her staff.");
        var unrelated = ScriptBlock.Create(ScriptBlockKind.Scene, "INT. ROOM — NIGHT");
        await scripts.SaveAsync(new() { ProjectId = f.Project.Id, Blocks = [act, scene, action, unrelated, ScriptBlock.Create(ScriptBlockKind.Action, "Elsewhere.")] }, 0, cancellationToken: _ct);
        var destinationScene = ScriptBlock.Create(ScriptBlockKind.Scene, "EXISTING DESTINATION SCENE");
        await scripts.SaveAsync(new() { ProjectId = target.Project.Id, Blocks = [destinationScene] }, 0, cancellationToken: _ct);
        var look = new CharacterLook { Name = "Everyday" };
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Mira", Category = AssetCategory.Character, Looks = [look], PreservationGuidance = "Keep glasses" };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, _ct);
        using var image = new MemoryStream(AssetStoreTests.Png(40, 32));
        library = await f.Assets.AddImageAsync(f.Project.Id, asset.Id, image, new("mira.png", [], AssetImageOrigin.Imported, LookId: look.Id), library.Revision, _ct);
        var first = Ready(); first.Title = "First"; first.SceneId = scene.Id; first.SceneTitle = scene.Text; first.ApprovedScriptId = null; first.SourceBlockIds = [scene.Id, action.Id];
        first.Characters = [new(Guid.NewGuid(), "Mira") { Appearance = new(asset.Id, look.Id) }];
        var second = first.Copy(); second.Id = Guid.NewGuid(); second.Title = "Second";
        await f.Shots.SaveAsync(f.Project.Id, [first, second], 0, ct: _ct);
        var jobs = new FileAiJobStore(Path.Combine(_root, "copy-jobs"), _clock);
        var projects = new FakeProjectStore { Get = id => Task.FromResult<ProjectInfo?>(id == f.Project.Id ? f.Project : target.Project) };
        var production = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, jobs);
        var setups = await production.InitializeAsync(f.Project.Id, _ct);
        foreach (var id in setups.Compositions.Select(c => c.Id).ToArray()) {
            var composition = (await production.LoadAsync(f.Project.Id, _ct)).Compositions.Single(c => c.Id == id);
            composition.Shot.Images = [new() { AssetId = asset.Id, MediaId = library.Assets[0].Images[0].Id, LookId = look.Id, Name = "Mira", Crop = new() { X = .1, Y = .1, Width = .5, Height = .5 } }];
            composition.Prompt = "Keep my authored prompt."; composition.DirectingNotes = "Hold the camera.";
            await production.SaveAsync(f.Project.Id, composition, composition.Version, _ct);
        }
        var takeSource = first.Copy(); takeSource.Characters = [];
        takeSource.ApprovedScriptId = (await scripts.CaptureSourceAsync(f.Project.Id, cancellationToken: _ct))!.Id;
        var sourceDoc = await AddTake(f.Project.Id, f.Shots, takeSource);
        sourceDoc.Shots[0].SelectedTakeId = sourceDoc.Takes[0].Id;
        sourceDoc = await f.Shots.SaveAsync(f.Project.Id, sourceDoc.Shots, sourceDoc.Revision, ct: _ct);
        await production.InitializeAsync(target.Project.Id, _ct);
        var sourcePath = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        var originals = new[] { "shots.json", "assets.json", "script.json", "production.json" }.ToDictionary(p => p, p => File.ReadAllText(Path.Combine(sourcePath, p)));
        var copier = new ShotProjectCopyStore(f.Files, f.Shots, production, scripts, f.Assets, f.Assets, _clock);
        var preview = await copier.PreviewAsync(f.Project.Id, [second.Id, first.Id], _ct);
        Assert.Equal((2, 1, 1, 1), (preview.Shots, preview.Takes, preview.Assets, preview.Scenes));
        var request = new ShotProjectCopyRequest(Guid.NewGuid(), f.Project.Id, target.Project.Id, [second.Id, first.Id], preview.ShotRevision, preview.ProductionRevision, preview.ScriptRevision);
        var result = await copier.CopyAsync(request, ct: _ct);
        var copied = await f.Shots.LoadAsync(target.Project.Id, _ct);
        Assert.Equal(new[] { "First", "Second" }, copied.Shots.Select(s => s.Title));
        Assert.All(copied.Shots, s => { Assert.DoesNotContain(s.Id, new[] { first.Id, second.Id }); Assert.Null(s.ApprovedScriptId); Assert.Equal("16:9", s.AspectOverride); });
        var copiedScript = await scripts.LoadAsync(target.Project.Id, _ct);
        Assert.Equal(new[] { destinationScene.Text, act.Text, scene.Text, action.Text }, copiedScript.Blocks.Select(b => b.Text));
        Assert.Equal(copiedScript.Blocks[2].Id, copied.Shots[0].SceneId);
        Assert.Equal(new[] { copiedScript.Blocks[2].Id, copiedScript.Blocks[3].Id }, copied.Shots[0].SourceBlockIds);
        var copiedAssets = await f.Assets.LoadAsync(target.Project.Id, _ct);
        var copiedAsset = Assert.Single(copiedAssets.Assets);
        Assert.NotEqual(asset.Id, copiedAsset.Id);
        Assert.Equal("Keep glasses", copiedAsset.PreservationGuidance);
        var copiedProduction = await production.LoadAsync(target.Project.Id, _ct);
        Assert.Equal(2, copiedProduction.Compositions.Count);
        foreach (var composition in copiedProduction.Compositions) {
            Assert.Equal("Keep my authored prompt.", composition.Prompt); Assert.Null(composition.Accepted);
            var binding = Assert.Single(composition.Shot.Images);
            Assert.Equal(copiedAsset.Id, binding.AssetId); Assert.Equal(copiedAsset.Images[0].Id, binding.MediaId);
            Assert.Equal(copiedAsset.Looks[0].Id, binding.LookId); Assert.Equal(new ImageCropRegion { X = .1, Y = .1, Width = .5, Height = .5 }, binding.Crop);
        }
        var copiedTake = Assert.Single(copied.Takes);
        Assert.Equal(copied.Shots[0].SelectedTakeId, copiedTake.Id);
        Assert.Equal(f.Project.Id, copiedTake.CopySource!.ProjectId);
        Assert.Equal(sourceDoc.Takes[0].Id, copiedTake.CopySource.TakeId);
        Assert.Equal(AssetReusePolicy.Hash(sourceDoc.Takes[0].Snapshot), AssetReusePolicy.Hash(copiedTake.CopySource.Snapshot));
        Assert.Null(copiedTake.AiJobId);
        var copiedSource = await scripts.LoadSourceAsync(target.Project.Id, copiedTake.Snapshot.Shot.ApprovedScriptId!.Value, _ct);
        Assert.NotNull(copiedSource);
        Assert.Equal(target.Project.Id, copiedSource.ProjectId);
        Assert.Equal(scene.Text, copiedSource.Blocks.Single(b => b.Id == copiedTake.Snapshot.Shot.SceneId).Text);
        var takePath = Path.Combine(await f.Files.DirectoryAsync(target.Project.Id, _ct), "shots", "takes", copiedTake.Directory);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(takePath, "video.mp4"), _ct));
        foreach (var file in TakeBundles.Files(copiedTake)) Assert.True(File.Exists(TakeBundles.Under(takePath, file)));
        var retried = await copier.CopyAsync(request, ct: _ct);
        Assert.Equal(result.ShotIds, retried.ShotIds);
        Assert.Equal(copied.Revision, (await f.Shots.LoadAsync(target.Project.Id, _ct)).Revision);
        Assert.Single((await f.Assets.LoadAsync(target.Project.Id, _ct)).Assets);
        Assert.Equal(copiedScript.Revision, (await scripts.LoadAsync(target.Project.Id, _ct)).Revision);
        foreach (var pair in originals) Assert.Equal(pair.Value, await File.ReadAllTextAsync(Path.Combine(sourcePath, pair.Key), _ct));
        Assert.Empty((await jobs.ReadAsync(_ct)).Jobs);
    }

    [Fact]
    public async Task ProjectCopyFailureResumesSavedPlanAndDoesNotDuplicateAssets()
    {
        var f = Fixture(); var target = Fixture(); var scripts = new FileScriptStore(f.Files, _clock);
        var shot = Ready(); shot.SceneId = null; shot.ApprovedScriptId = null;
        await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = id => Task.FromResult<ProjectInfo?>(id == f.Project.Id ? f.Project : target.Project) };
        var production = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, new FileAiJobStore(Path.Combine(_root, "jobs"), _clock));
        await production.InitializeAsync(f.Project.Id, _ct);
        var source = await AddTake(f.Project.Id, f.Shots, shot);
        var copier = new ShotProjectCopyStore(f.Files, f.Shots, production, scripts, f.Assets, f.Assets, _clock);
        var preview = await copier.PreviewAsync(f.Project.Id, [shot.Id], _ct);
        var request = new ShotProjectCopyRequest(Guid.NewGuid(), f.Project.Id, target.Project.Id, [shot.Id], preview.ShotRevision, preview.ProductionRevision, preview.ScriptRevision);
        var path = Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "shots", "takes", source.Takes[0].Directory, "video.mp4");
        File.Delete(path);
        await Assert.ThrowsAnyAsync<IOException>(() => copier.CopyAsync(request, ct: _ct));
        Assert.Empty((await f.Shots.LoadAsync(target.Project.Id, _ct)).Shots);
        await File.WriteAllBytesAsync(path, [1, 2, 3], _ct);
        // New service instance, like reopening after interruption.
        copier = new(f.Files, f.Shots, production, scripts, f.Assets, f.Assets, _clock);
        await copier.CopyAsync(request, ct: _ct);
        Assert.Single((await f.Shots.LoadAsync(target.Project.Id, _ct)).Shots);
        Assert.Empty((await scripts.LoadAsync(target.Project.Id, _ct)).Blocks);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => copier.CopyAsync(request with { ShotIds = [Guid.NewGuid()] }, ct: _ct));
    }

    [Fact]
    public async Task ProjectCopyRejectsStaleSelectionAndSameProjectBeforePublishing()
    {
        var f = Fixture(); var target = Fixture(); var scripts = new FileScriptStore(f.Files, _clock);
        var shot = Ready(); shot.SceneId = null; shot.ApprovedScriptId = null;
        await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = id => Task.FromResult<ProjectInfo?>(id == f.Project.Id ? f.Project : target.Project) };
        var production = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, new FileAiJobStore(Path.Combine(_root, "jobs"), _clock));
        await production.InitializeAsync(f.Project.Id, _ct);
        var copier = new ShotProjectCopyStore(f.Files, f.Shots, production, scripts, f.Assets, f.Assets, _clock);
        var preview = await copier.PreviewAsync(f.Project.Id, [shot.Id], _ct);
        var request = new ShotProjectCopyRequest(Guid.NewGuid(), f.Project.Id, target.Project.Id, [shot.Id], preview.ShotRevision, preview.ProductionRevision, preview.ScriptRevision);
        await f.Shots.SaveAsync(f.Project.Id, [shot], preview.ShotRevision, ct: _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => copier.CopyAsync(request, ct: _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => copier.CopyAsync(request with { DestinationProjectId = f.Project.Id }, ct: _ct));
        Assert.Empty((await f.Shots.LoadAsync(target.Project.Id, _ct)).Shots);
        Assert.Empty((await f.Assets.LoadAsync(target.Project.Id, _ct)).Assets);
    }
}
