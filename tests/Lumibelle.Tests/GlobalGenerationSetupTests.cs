using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Lumibelle.Testing;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task GlobalSetupImportDeduplicatesSettingsAndPreservesDifferentVersions()
    {
        var store = new FileGenerationSetupStore(new ApplicationPaths(_root));
        var a = new ProductionComposition { ShotId = Guid.NewGuid(), Name = "Seed Hunter", TakeCount = 4, Version = 1 };
        a.Shot.Resolution = VideoResolution.Quick; a.Prompt = "Never in a global preset";
        a.Shot.Images = [new() { Name = "Private reference" }];
        var b = a.Copy(); b.Id = Guid.NewGuid(); b.Name = "Seed hunter"; b.ShotId = Guid.NewGuid();
        var c = a.Copy(); c.Id = Guid.NewGuid(); c.TakeCount = 2;
        var d = c.Copy(); d.Id = Guid.NewGuid();
        var doc = new ProductionDocument { ProjectId = Guid.NewGuid(), Compositions = [a, b, c, d] };
        var imported = await store.ImportAsync(doc, _ct);
        Assert.Equal(2, imported.Setups.Count);
        Assert.Equal(new[] { "Seed Hunter", "Seed Hunter (2)" }, imported.Setups.Select(s => s.Name));
        Assert.Equal(2, imported.Imports.Select(i => i.SetupId).Distinct().Count());
        Assert.Equal(4, imported.Imports.Count);
        var original = JsonSerializer.Serialize(imported);
        Assert.Equal(original, JsonSerializer.Serialize(await store.ImportAsync(doc, _ct)));
        var persisted = await File.ReadAllTextAsync(Path.Combine(_root, "generation-setups.json"), _ct);
        Assert.DoesNotContain("Never in a global preset", persisted);
        Assert.DoesNotContain("Private reference", persisted);
        var reused = await store.ImportAsync(doc with { ProjectId = Guid.NewGuid() }, _ct);
        Assert.Equal(2, reused.Setups.Count);
        await store.SelectAsync(imported.Setups[1].Id, _ct);
        Assert.Equal(imported.Setups[1].Id, (await new FileGenerationSetupStore(new ApplicationPaths(_root)).LoadAsync(_ct)).SelectedId);
    }

    [Fact]
    public async Task GlobalSetupEditsReachOtherProjectsWithoutMovingShotContentOrRewritingTakes()
    {
        var library = new FileGenerationSetupStore(new ApplicationPaths(_root));
        var f = Fixture(); var g = Fixture(); var a = Ready(); var b = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [a], 0, ct: _ct);
        await g.Shots.SaveAsync(g.Project.Id, [b], 0, ct: _ct);
        var jobs = new FileAiJobStore(Path.Combine(_root, "jobs"), _clock);
        var first = new FileProductionStore(f.Files, f.Shots, f.Assets,
            new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, _clock, jobs, generationSetups: library);
        var second = new FileProductionStore(g.Files, g.Shots, g.Assets,
            new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(g.Project) }, _clock, jobs, generationSetups: library);
        var one = (await first.InitializeAsync(f.Project.Id, _ct)).Compositions.Single();
        var two = (await second.InitializeAsync(g.Project.Id, _ct)).Compositions.Single();
        Assert.Equal(one.GenerationSetupId, two.GenerationSetupId);
        one.Prompt = "First shot's prompt"; one.Shot.Images = [new() { Name = "Room one", AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid() }];
        one = (await first.SaveAsync(f.Project.Id, one, one.Version, _ct)).Compositions.Single();
        two.Prompt = "Second shot's prompt";
        two = (await second.SaveAsync(g.Project.Id, two, two.Version, _ct)).Compositions.Single();
        await AddTake(f.Project.Id, f.Shots, a);
        var shotDirectory = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        var takesBefore = TestProjectDatabase.Stamp(shotDirectory, "shots");
        one.Name = "Seed Hunter"; one.TakeCount = 4; one.Seed = 123;
        one.Shot.Resolution = VideoResolution.Quick; one.Shot.AspectOverride = null;
        one = (await first.SaveAsync(f.Project.Id, one, one.Version, _ct)).Compositions.Single();
        var refreshed = (await second.LoadAsync(g.Project.Id, _ct)).Compositions.Single();
        Assert.Equal("Seed Hunter", refreshed.Name); Assert.Equal(4, refreshed.TakeCount); Assert.Equal(123, refreshed.Seed);
        Assert.Equal(VideoResolution.Quick, refreshed.Shot.Resolution); Assert.Null(refreshed.Shot.AspectOverride);
        Assert.Equal("Second shot's prompt", refreshed.Prompt); Assert.Empty(refreshed.Shot.Images);
        Assert.Equal("First shot's prompt", one.Prompt); Assert.Equal("Room one", Assert.Single(one.Shot.Images).Name);
        Assert.Equal(takesBefore, TestProjectDatabase.Stamp(shotDirectory, "shots"));
        two.TakeCount = 2;
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => second.SaveAsync(g.Project.Id, two, two.Version, _ct));
        Assert.Equal(4, (await second.LoadAsync(g.Project.Id, _ct)).Compositions.Single().TakeCount);
        Assert.Single((await library.LoadAsync(_ct)).Setups);
    }

    [Fact]
    public async Task GlobalSetupArchivePreservesDefinitionAndPreventsStaleEdits()
    {
        var store = new FileGenerationSetupStore(new ApplicationPaths(_root));
        var setup = new GenerationSetup { Name = "Seed Hunter", Settings = new() { TakeCount = 4 } };
        setup = (await store.SaveAsync(setup, 0, _ct)).Setups.Single();
        await store.SelectAsync(setup.Id, _ct);
        var saved = await store.SaveAsync(setup with { Archived = true }, setup.Version, _ct);
        Assert.Null(saved.SelectedId); Assert.True(saved.Setups.Single().Archived);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.SelectAsync(setup.Id, _ct));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.SaveAsync(setup with { Name = "Stale rename" }, setup.Version, _ct));
        var restored = saved.Setups.Single() with { Archived = false };
        await store.SaveAsync(restored, restored.Version, _ct);
        Assert.Equal(setup.Id, (await store.SelectAsync(setup.Id, _ct)).SelectedId);
    }

    [Fact]
    public async Task LocalSetupMigrationKeepsAnExactBackupAndIsIdempotent()
    {
        var f = Fixture(); var source = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var jobs = new FileAiJobStore(Path.Combine(_root, "jobs"), _clock);
        var oldStore = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, jobs);
        var old = (await oldStore.InitializeAsync(f.Project.Id, _ct)).Compositions.Single();
        old.Name = "Seed Hunter"; old.TakeCount = 4; old.Shot.Resolution = VideoResolution.Quick;
        old.Prompt = "Preserved shot prompt";
        await oldStore.SaveAsync(f.Project.Id, old, old.Version, _ct);
        var directory = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        var original = await FileProductionStore.Documents.LoadJsonAsync(directory, f.Project.Id, complete: true, _ct);
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, jobs,
            generationSetups: new FileGenerationSetupStore(new ApplicationPaths(_root)));
        var migrated = await store.InitializeAsync(f.Project.Id, _ct);
        var setup = Assert.Single(migrated.Compositions);
        Assert.NotNull(setup.GenerationSetupId); Assert.Equal(old.Id, setup.Id);
        Assert.Equal(old.Prompt, setup.Prompt); Assert.Equal(4, setup.TakeCount); Assert.Equal(VideoResolution.Quick, setup.Shot.Resolution);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(directory, "legacy-json", "production-before-global-setups.json"), _ct));
        Assert.Equal(migrated.Revision, (await store.InitializeAsync(f.Project.Id, _ct)).Revision);
    }
}
