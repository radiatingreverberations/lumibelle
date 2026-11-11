using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Storage;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FormatUpgradePreservesTakesTrashCutAndRecoveryAcrossTwoShotOpens(bool oldProduction)
    {
        var f = Fixture(); var shot = Ready();
        var document = await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        document = await AddTake(f.Project.Id, f.Shots, shot);
        document = await AddTake(f.Project.Id, f.Shots, shot, 2);
        var selected = document.Shots[0].Copy(); selected.SelectedTakeId = document.Takes[0].Id;
        document = await f.Shots.SaveAsync(f.Project.Id, [selected], document.Revision, ct: _ct);
        var cuts = new FileCutStore(f.Files, f.Shots, _clock);
        await cuts.SaveAsync(f.Project.Id, [CutClip.From(selected, document.Takes[0])], 0, _ct);
        document = await f.Shots.DiscardAsync(f.Project.Id, document.Takes[1].Id, ShotTrashKind.Take, document.Revision, _ct);
        var directory = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        var before = await FileShotStore.Documents.LoadJsonAsync(directory, f.Project.Id, true, _ct);
        var cut = await FileCutStore.Documents.LoadJsonAsync(directory, f.Project.Id, true, _ct);
        var setup = new ProductionComposition { ShotId = shot.Id, Version = 2, Prompt = "A retained old prompt." };
        if (oldProduction) await FileProductionStore.Documents.SaveAsync(directory, f.Project.Id,
            new ProductionDocument { ProjectId = f.Project.Id, SchemaVersion = 1, Compositions = [setup] }, _ct);
        TestProjectDatabase.Execute(directory, "PRAGMA user_version = 1");
        await TestProjectDatabase.UpgradeAsync(directory, f.Project.Id, _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, new FileAiJobStore(Path.Combine(_root, "queue"), _clock));
        var first = await store.InitializeAsync(f.Project.Id, _ct);
        var second = await store.InitializeAsync(f.Project.Id, _ct);
        Assert.Equal(first.Revision, second.Revision); Assert.Equal(first.Compositions[0].Id, second.Compositions[0].Id);
        if (oldProduction) { Assert.Equal(setup.Id, first.Compositions[0].Id); Assert.Equal(setup.Prompt, first.Compositions[0].Prompt); }
        Assert.Equal(before, await FileShotStore.Documents.LoadJsonAsync(directory, f.Project.Id, true, _ct));
        Assert.Equal(cut, await FileCutStore.Documents.LoadJsonAsync(directory, f.Project.Id, true, _ct));
        Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Trash);
        Assert.Equal(document.Takes[0].Id, (await f.Shots.LoadAsync(f.Project.Id, _ct)).Shots[0].SelectedTakeId);
    }

    [Fact]
    public async Task AspectUpgradeIncludesTrashOwnersAndDetachedRecoveryShots()
    {
        var f = Fixture(); var shot = Ready();
        var document = await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        shot.Title = "Arrival after an edit";
        document = await f.Shots.SaveAsync(f.Project.Id, [shot], document.Revision, ct: _ct);
        document = await AddTake(f.Project.Id, f.Shots, shot);
        await f.Shots.DiscardAsync(f.Project.Id, document.Takes[0].Id, ShotTrashKind.Take, document.Revision, _ct);
        var directory = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        var json = await TestProjectDatabase.JsonAsync(FileShotStore.Documents, directory, f.Project.Id, _ct);
        var owners = json["shots"]!.AsArray().OfType<JsonObject>()
            .Concat(json["trash"]!.AsArray().Select(t => t!["owner"]).OfType<JsonObject>())
            .Concat(json["recovery"]!.AsArray().SelectMany(r => r!["shots"]!.AsArray()).OfType<JsonObject>());
        foreach (var owner in owners) { owner["aspect"] = "9:16"; owner.Remove("aspectOverride"); }
        json["schemaVersion"] = 1;
        await TestProjectDatabase.SaveJsonAsync(FileShotStore.Documents, directory, f.Project.Id, json, _ct);
        TestProjectDatabase.Execute(directory, "PRAGMA user_version = 1");
        await TestProjectDatabase.UpgradeAsync(directory, f.Project.Id, _ct);
        var restored = await f.Shots.LoadCompleteAsync(f.Project.Id, _ct);
        Assert.Equal("9:16", restored.Shots[0].AspectOverride);
        Assert.Equal("9:16", restored.Trash[0].Owner!.AspectOverride);
        Assert.NotEmpty(restored.Recovery.SelectMany(r => r.Shots));
        Assert.All(restored.Recovery.SelectMany(r => r.Shots), s => Assert.Equal("9:16", s.AspectOverride));
    }

    [Fact]
    public async Task OldScriptRecoveryCanBeRestoredAfterUpgradeAndPublishesCurrentScript()
    {
        var f = Fixture(); var scripts = new FileScriptStore(f.Files, _clock);
        var document = await scripts.SaveAsync(ScriptFixtures.Document(f.Project.Id), 0, cancellationToken: _ct);
        document = await scripts.SaveAsync(document, document.Revision, "Before editing", _ct);
        var directory = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        var history = await TestProjectDatabase.JsonAsync(FileScriptStore.Snapshots, directory, f.Project.Id, _ct);
        foreach (var entry in history["history"]!.AsArray()) entry!["document"]!["schemaVersion"] = 1;
        await TestProjectDatabase.SaveJsonAsync(FileScriptStore.Snapshots, directory, f.Project.Id, history, _ct);
        TestProjectDatabase.Execute(directory, "PRAGMA user_version = 1");
        await TestProjectDatabase.UpgradeAsync(directory, f.Project.Id, _ct);
        var recovery = (await scripts.ListRecoveryAsync(f.Project.Id, _ct))[0];
        Assert.Equal(1, recovery.Document.SchemaVersion);
        var restored = await scripts.RestoreAsync(f.Project.Id, recovery.Id, document.Revision, _ct);
        Assert.Equal(2, restored.SchemaVersion);
        Assert.Equal(2, (await scripts.LoadAsync(f.Project.Id, _ct)).SchemaVersion);
    }
}
