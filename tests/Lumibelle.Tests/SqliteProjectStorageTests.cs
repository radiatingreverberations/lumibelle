using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Storage;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private static string StoredJson<T>(T value) => JsonSerializer.Serialize(value, ProjectJson.Compact);

    [Fact]
    public async Task LegacyJsonProjectMovesIntoTheDatabaseIntactAndKeepsItsFiles()
    {
        var f = Fixture(); var scripts = new FileScriptStore(f.Files, _clock);
        var dir = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        var shot = Ready();
        var doc = await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var renamed = doc.Shots[0].Copy(); renamed.Title = "Renamed arrival";
        doc = await f.Shots.SaveAsync(f.Project.Id, [renamed], doc.Revision, ct: _ct);
        var script = await scripts.SaveAsync(ScriptFixtures.Document(f.Project.Id), 0, cancellationToken: _ct);
        script = await scripts.SaveAsync(script with { Blocks = [.. script.Blocks, ScriptBlock.Create(ScriptBlockKind.Action, "It bows.")] }, script.Revision, "Before an edit", _ct);
        var source = (await scripts.CaptureSourceAsync(f.Project.Id, cancellationToken: _ct))!;
        var shots = StoredJson(await f.Shots.LoadCompleteAsync(f.Project.Id, _ct));
        var history = (await scripts.ListRecoveryAsync(f.Project.Id, _ct)).Select(r => r.Id).ToArray();

        // Write the same project as JSON files, the layout before SQLite storage, and remove the database.
        await File.WriteAllBytesAsync(Path.Combine(dir, "shots.json"), (await FileShotStore.Documents.LoadJsonAsync(dir, f.Project.Id, complete: true, _ct))!, _ct);
        await File.WriteAllBytesAsync(Path.Combine(dir, "script.json"), (await FileScriptStore.Documents.LoadJsonAsync(dir, f.Project.Id, complete: true, _ct))!, _ct);
        var snapshots = await FileScriptStore.Snapshots.LoadAsync(dir, f.Project.Id, _ct);
        foreach (var s in snapshots.Sources) await AtomicJsonFile.WriteAsync(Path.Combine(dir, "script-sources", $"{s.Id:D}.json"), s, _ct);
        foreach (var r in snapshots.History) await AtomicJsonFile.WriteAsync(Path.Combine(dir, "script-history", $"{r.Id:D}.json"), r, _ct);
        File.Delete(TestProjectDatabase.PathIn(dir));

        var loaded = await f.Shots.LoadAsync(f.Project.Id, _ct);
        Assert.Equal("Renamed arrival", Assert.Single(loaded.Shots).Title);
        Assert.Equal(shots, StoredJson(await f.Shots.LoadCompleteAsync(f.Project.Id, _ct)));
        Assert.Equal(StoredJson(script), StoredJson(await scripts.LoadAsync(f.Project.Id, _ct)));
        Assert.Equal(history, (await scripts.ListRecoveryAsync(f.Project.Id, _ct)).Select(r => r.Id).ToArray());
        Assert.Equal(StoredJson(source), StoredJson(await scripts.LoadSourceAsync(f.Project.Id, source.Id, _ct)));
        foreach (var legacy in new[] { "shots.json", "script.json" })
        {
            Assert.False(File.Exists(Path.Combine(dir, legacy)));
            Assert.True(File.Exists(Path.Combine(dir, LegacyFiles.Folder, legacy)));
        }
        Assert.False(Directory.Exists(Path.Combine(dir, "script-sources")));
        Assert.True(File.Exists(Path.Combine(dir, LegacyFiles.Folder, "script-sources", $"{source.Id:D}.json")));
    }

    [Fact]
    public async Task RecoveryVersionsLoadOnlyWhenRestored()
    {
        var f = Fixture(); var shot = Ready();
        var doc = await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var renamed = doc.Shots[0].Copy(); renamed.Title = "Renamed arrival";
        doc = await f.Shots.SaveAsync(f.Project.Id, [renamed], doc.Revision, ct: _ct);
        // A save returns its new recovery version complete; later loads carry only its summary.
        Assert.Equal("The arrival", doc.Recovery[0].Shots[0].Title);
        DocumentCache.Clear();
        var loaded = await new FileShotStore(f.Files, _clock).LoadAsync(f.Project.Id, _ct);
        Assert.Equal(2, loaded.Recovery.Count);
        Assert.All(loaded.Recovery, r => Assert.Empty(r.Shots));
        var complete = await f.Shots.LoadCompleteAsync(f.Project.Id, _ct);
        Assert.Equal("The arrival", complete.Recovery[0].Shots[0].Title);
        Assert.Equal("The arrival", (await f.Shots.LoadRecoveryAsync(f.Project.Id, loaded.Recovery[0].Id, _ct))!.Shots[0].Title);

        // Saving a document whose recovery versions are summaries keeps their stored shots.
        var again = loaded.Shots[0].Copy(); again.Description = "Edited after reload.";
        var saved = await f.Shots.SaveAsync(f.Project.Id, [again], loaded.Revision, ct: _ct);
        var restored = await f.Shots.RecoverAsync(f.Project.Id, loaded.Recovery[0].Id, saved.Revision, _ct);
        Assert.Equal("The arrival", Assert.Single(restored.Shots).Title);
        Assert.Equal("Renamed arrival", (await f.Shots.LoadRecoveryAsync(f.Project.Id, saved.Recovery[0].Id, _ct))!.Shots[0].Title);
    }

    [Fact]
    public async Task ReadingAProjectNeverChangesItsDatabase()
    {
        var f = Fixture(); var shot = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var dir = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(TestProjectDatabase.PathIn(dir), _ct));
        DocumentCache.Clear();
        await ProjectDocuments.MigrateAllAsync(dir, f.Project.Id, _ct);
        await f.Shots.LoadCompleteAsync(f.Project.Id, _ct);
        await f.Assets.LoadAsync(f.Project.Id, _ct);
        await new FileScriptStore(f.Files, _clock).LoadAsync(f.Project.Id, _ct);
        await new FileScriptStore(f.Files, _clock).ListRecoveryAsync(f.Project.Id, _ct);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(TestProjectDatabase.PathIn(dir), _ct)));
        Assert.False(File.Exists(TestProjectDatabase.PathIn(dir) + "-journal"));
    }
}
