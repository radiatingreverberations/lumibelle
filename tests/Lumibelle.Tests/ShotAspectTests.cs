using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task ShotAspectStaysWithItsShotAcrossSetupsAndCanBeSharedWithItsScene()
    {
        var library = new FileGenerationSetupStore(new ApplicationPaths(_root));
        var f = Fixture(); var a = Ready(); var b = Ready(); var other = Ready(); b.SceneId = a.SceneId;
        await f.Shots.SaveAsync(f.Project.Id, [a, b, other], 0, ct: _ct);
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets,
            new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, _clock, new FileAiJobStore(Path.Combine(_root, "jobs"), _clock), generationSetups: library);
        var d = await store.InitializeAsync(f.Project.Id, _ct);
        Assert.Single(d.Compositions.Select(c => c.GenerationSetupId).Distinct());
        var first = d.Compositions.Single(c => c.ShotId == a.Id);
        first.Shot.AspectOverride = "9:16";
        d = await store.SaveAsync(f.Project.Id, first, first.Version, _ct);
        Assert.Equal("9:16", d.Compositions.Single(c => c.ShotId == a.Id).Shot.AspectOverride);
        // Shots sharing the global setup keep following the project.
        Assert.All(d.Compositions.Where(c => c.ShotId != a.Id), c => Assert.Null(c.Shot.AspectOverride));
        Assert.Equal("16:9", ShotVideoDefaults.Aspect(d.Compositions.Single(c => c.ShotId == b.Id).Shot, f.Project));
        Assert.DoesNotContain("aspectOverride", await File.ReadAllTextAsync(Path.Combine(_root, "generation-setups.json"), _ct));

        // Another setup generates the same shot at the same aspect.
        var alternative = new GenerationSetup { Name = "Alternative", Settings = new() { TakeCount = 2 } };
        await library.SaveAsync(alternative, 0, _ct);
        var adapter = new ProductionComposition { ShotId = a.Id, Shot = ProductionPolicy.CoverageCopy(a) };
        alternative.Version = 1; alternative.Apply(adapter);
        d = await store.SaveAsync(f.Project.Id, adapter, 0, _ct);
        Assert.Equal("9:16", d.Compositions.Single(c => c.Id == adapter.Id).Shot.AspectOverride);

        var stale = d.Compositions.Single(c => c.ShotId == b.Id);
        d = await store.SetAspectAsync(f.Project.Id, [b.Id], "9:16", _ct);
        Assert.Equal("9:16", d.Compositions.Single(c => c.ShotId == b.Id).Shot.AspectOverride);
        Assert.Null(d.Compositions.Single(c => c.ShotId == other.Id).Shot.AspectOverride);
        stale.TakeCount = 3;
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.SaveAsync(f.Project.Id, stale, stale.Version, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.SetAspectAsync(f.Project.Id, [b.Id], "4:3", _ct));
        var revision = d.Revision;
        Assert.Equal(revision, (await store.SetAspectAsync(f.Project.Id, [b.Id], "9:16", _ct)).Revision);
        d = await store.SetAspectAsync(f.Project.Id, [b.Id], null, _ct);
        Assert.Null(d.Compositions.Single(c => c.ShotId == b.Id).Shot.AspectOverride);
    }

    [Fact]
    public async Task AspectOverridesSavedOnGlobalSetupsAreDropped()
    {
        var library = new FileGenerationSetupStore(new ApplicationPaths(_root));
        var f = Fixture(); var shot = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets,
            new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, _clock, new FileAiJobStore(Path.Combine(_root, "jobs"), _clock), generationSetups: library);
        await store.InitializeAsync(f.Project.Id, _ct);
        // Before overrides moved to shots, both the global setup and each adapter stored one.
        var setupsPath = Path.Combine(_root, "generation-setups.json");
        var setups = JsonNode.Parse(await File.ReadAllTextAsync(setupsPath, _ct))!;
        setups["setups"]![0]!["settings"]!["aspectOverride"] = "9:16";
        await File.WriteAllTextAsync(setupsPath, setups.ToJsonString(), _ct);
        var directory = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        var production = JsonNode.Parse((await FileProductionStore.Documents.LoadJsonAsync(directory, f.Project.Id, complete: true, _ct))!)!;
        production["compositions"]![0]!["inputs"]!["aspectOverride"] = "9:16";
        await FileProductionStore.Documents.SaveAsync(directory, f.Project.Id, JsonSerializer.SerializeToElement(production), _ct);
        var c = (await store.LoadAsync(f.Project.Id, _ct)).Compositions.Single();
        Assert.Null(c.Shot.AspectOverride);
        c.TakeCount = 2;
        await store.SaveAsync(f.Project.Id, c, c.Version, _ct);
        Assert.DoesNotContain("aspectOverride", await File.ReadAllTextAsync(setupsPath, _ct));
        Assert.DoesNotContain("aspectOverride", System.Text.Encoding.UTF8.GetString((await FileProductionStore.Documents.LoadJsonAsync(directory, f.Project.Id, complete: true, _ct))!));
    }
}
