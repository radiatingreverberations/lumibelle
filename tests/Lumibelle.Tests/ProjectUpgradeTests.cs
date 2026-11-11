using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Projects;
using lumibelle.Services.Shots;
using lumibelle.Services.Storage;
using lumibelle.Services.Story;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed class ProjectUpgradeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lumibelle-format-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationToken ct = TestContext.Current.CancellationToken;

    private async Task<(ProjectInfo Project, ProjectFiles Files, ProjectUpgrades Upgrades, FileAiJobStore Jobs, string Directory)> Fixture()
    {
        var paths = new ApplicationPaths(Path.Combine(root, "library"));
        var locations = new ProjectLocations(paths);
        var projects = new FileProjectStore(paths, TimeProvider.System, NullLogger<FileProjectStore>.Instance, locations);
        var project = await projects.CreateAsync(new("Upgrade fixture"), ct);
        var files = new ProjectFiles(paths, projects, locations);
        var jobs = new FileAiJobStore(paths.Jobs, TimeProvider.System);
        return (project, files, new(paths, files, locations, jobs), jobs, await files.DirectoryAsync(project.Id, ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradeGatesAllDocumentsPreservesBackupsAndIsIdempotent(bool legacy)
    {
        var f = await Fixture();
        if (legacy) await FormatFixtures.LegacyOnly(f.Directory, f.Project.Id, ct);
        else await FormatFixtures.Sqlite(f.Directory, f.Project.Id, ct);
        var before = File.Exists(TestProjectDatabase.PathIn(f.Directory)) ? await File.ReadAllBytesAsync(TestProjectDatabase.PathIn(f.Directory), ct) : null;
        var dump = legacy ? null : TestProjectDatabase.Dump(f.Directory);
        var status = await f.Upgrades.StatusAsync(f.Project.Id, ct);
        Assert.Equal(1, status.From); Assert.Equal(2, status.To); Assert.Single(status.Steps);
        Assert.Equal(!legacy, status.BackupBytes > 0);
        await Assert.ThrowsAsync<ProjectUpgradeRequiredException>(() => new FileAssetStore(f.Files, TimeProvider.System).LoadAsync(f.Project.Id, ct));
        await Assert.ThrowsAsync<ProjectUpgradeRequiredException>(() => FileCutStore.Documents.LoadAsync(f.Directory, f.Project.Id, ct));
        Assert.Empty(await new FileAssetStore(f.Files, TimeProvider.System).CleanupVoiceImportsAsync(ct));
        Assert.Equal(!legacy, File.Exists(TestProjectDatabase.PathIn(f.Directory)));
        await Task.WhenAll(f.Upgrades.UpgradeAsync(f.Project.Id, ct: ct), f.Upgrades.UpgradeAsync(f.Project.Id, ct: ct));
        Assert.Equal(2, ProjectFormat.Inspect(f.Directory));
        Assert.Equal(2, (await new FileScriptStore(f.Files, TimeProvider.System).LoadAsync(f.Project.Id, ct)).SchemaVersion);
        var preferences = await new FileProjectAiPreferencesStore(f.Files).LoadAsync(f.Project.Id, ct);
        Assert.Equal(2, preferences.SchemaVersion); Assert.Equal(0, preferences.TextDefaultRevision);
        var shots = await new FileShotStore(f.Files, TimeProvider.System).LoadAsync(f.Project.Id, ct);
        Assert.Equal("9:16", shots.Shots[0].AspectOverride);
        var production = await FileProductionStore.Documents.LoadAsync(f.Directory, f.Project.Id, ct);
        Assert.Equal(4, production.SchemaVersion); Assert.Equal(Guid.Parse(FormatFixtures.Setup), production.Compositions[0].Id);
        if (before is not null) Assert.Equal(dump, TestProjectDatabase.DumpFile(ProjectUpgrader.BackupPath(f.Directory, 1)));
        else Assert.Equal(FormatFixtures.Json(f.Project.Id)["shots"], await File.ReadAllTextAsync(Path.Combine(f.Directory, "legacy-json", "shots.json"), ct));
        var backup = before is null ? null : await File.ReadAllBytesAsync(ProjectUpgrader.BackupPath(f.Directory, 1), ct);
        var after = await File.ReadAllBytesAsync(TestProjectDatabase.PathIn(f.Directory), ct);
        await f.Upgrades.UpgradeAsync(f.Project.Id, ct: ct);
        Assert.Equal(after, await File.ReadAllBytesAsync(TestProjectDatabase.PathIn(f.Directory), ct));
        if (before is not null)
        {
            File.Copy(ProjectUpgrader.BackupPath(f.Directory, 1), TestProjectDatabase.PathIn(f.Directory), true);
            DocumentCache.EvictDirectory(f.Directory);
            Assert.Equal(1, ProjectFormat.Inspect(f.Directory));
            await f.Upgrades.UpgradeAsync(f.Project.Id, ct: ct);
            Assert.Equal(backup, await File.ReadAllBytesAsync(ProjectUpgrader.BackupPath(f.Directory, 1), ct));
        }
    }

    [Fact]
    public async Task FailedTransactionLeavesDataAndVersionIntact()
    {
        var f = await Fixture(); await FormatFixtures.Sqlite(f.Directory, f.Project.Id, ct);
        TestProjectDatabase.Execute(f.Directory, "CREATE TRIGGER fail_upgrade BEFORE UPDATE ON documents WHEN NEW.kind = 'production' BEGIN SELECT RAISE(ABORT, 'injected failure'); END");
        var before = TestProjectDatabase.Dump(f.Directory);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Upgrades.UpgradeAsync(f.Project.Id, ct: ct));
        Assert.Equal(before, TestProjectDatabase.Dump(f.Directory));
        TestProjectDatabase.Execute(f.Directory, "DROP TRIGGER fail_upgrade");
        await f.Upgrades.UpgradeAsync(f.Project.Id, ct: ct);
        Assert.Equal(2, ProjectFormat.Inspect(f.Directory));
    }

    [Fact]
    public async Task InterruptedLegacyImportResumesAndKeepsDifferentSameLengthOriginals()
    {
        var f = await Fixture(); await FormatFixtures.LegacyOnly(f.Directory, f.Project.Id, ct);
        using (ProjectDatabase.AllowUpgrade(f.Directory))
            await FileScriptStore.Documents.EnsureAsync(f.Directory, f.Project.Id, ct);
        TestProjectDatabase.Execute(f.Directory, "UPDATE migrations SET state = 'committed', completed_utc = NULL WHERE document = 'script'");
        File.Move(Path.Combine(f.Directory, "legacy-json", "script.json"), Path.Combine(f.Directory, "script.json"));
        ProjectDocumentStore<ScriptDocument>.ForgetMigrations();
        // Simulate interruption after committing a document and placing a different archived file.
        var json = FormatFixtures.Json(f.Project.Id)["shots"];
        Directory.CreateDirectory(Path.Combine(f.Directory, "legacy-json"));
        await File.WriteAllTextAsync(Path.Combine(f.Directory, "legacy-json", "shots.json"), json.Replace("Arrival", "Depart!"), ct);
        await f.Upgrades.UpgradeAsync(f.Project.Id, ct: ct);
        Assert.Contains(Directory.GetFiles(Path.Combine(f.Directory, "legacy-json")), p => p.EndsWith(".duplicate"));
        Assert.Equal(2, ProjectFormat.Inspect(f.Directory));
        Assert.False(File.Exists(Path.Combine(f.Directory, "script.json")));
        Assert.Equal(FormatFixtures.Json(f.Project.Id)["script"], await File.ReadAllTextAsync(Path.Combine(f.Directory, "legacy-json", "script.json"), ct));
    }

    [Fact]
    public async Task BusyProjectRefusesUpgradeAndQueueRemainsAccessible()
    {
        var f = await Fixture(); await FormatFixtures.Sqlite(f.Directory, f.Project.Id, ct);
        await f.Jobs.EnqueueAsync(AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.PromptComposition, AiBackend.OpenRouter,
            new(f.Project.Id, ShotId: Guid.NewGuid(), CompositionId: Guid.NewGuid()), "Project", "Setup", Guid.NewGuid(), new { }), ct);
        Assert.NotNull((await f.Upgrades.StatusAsync(f.Project.Id, ct)).Busy);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Upgrades.UpgradeAsync(f.Project.Id, ct: ct));
        Assert.Single((await f.Jobs.ReadAsync(ct)).Jobs); Assert.Equal(1, ProjectFormat.Inspect(f.Directory));
    }

    [Fact]
    public async Task MissingProductionPreservesShotCutAndQueueRecordsAcrossTwoOpens()
    {
        var f = await Fixture(); var shots = new FileShotStore(f.Files, TimeProvider.System);
        var shot = new Shot { Title = "Arrival", Description = "A traveller arrives.", Duration = 2 };
        await shots.SaveAsync(f.Project.Id, [shot], 0, ct: ct);
        var original = await FileShotStore.Documents.LoadJsonAsync(f.Directory, f.Project.Id, true, ct);
        await FileCutStore.Documents.SaveAsync(f.Directory, f.Project.Id, new CutDocument { ProjectId = f.Project.Id }, ct);
        var cut = await FileCutStore.Documents.LoadJsonAsync(f.Directory, f.Project.Id, true, ct);
        await f.Jobs.EnqueueAsync(AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.PromptComposition, AiBackend.OpenRouter,
            new(f.Project.Id, ShotId: shot.Id, CompositionId: Guid.NewGuid()), "Project", "Old setup", Guid.NewGuid(), new { }), ct);
        var job = (await f.Jobs.ReadAsync(ct)).Jobs[0]; await f.Jobs.UpdateAsync(job.Id, j => j with { State = AiJobState.Completed }, ct);
        TestProjectDatabase.Execute(f.Directory, "PRAGMA user_version = 1");
        await f.Upgrades.UpgradeAsync(f.Project.Id, ct: ct);
        var production = new FileProductionStore(f.Files, shots, new FileAssetStore(f.Files, TimeProvider.System),
            new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, TimeProvider.System, f.Jobs);
        var first = await production.InitializeAsync(f.Project.Id, ct); var second = await production.InitializeAsync(f.Project.Id, ct);
        Assert.Equal(first.Compositions[0].Id, second.Compositions[0].Id); Assert.Equal(first.Revision, second.Revision);
        Assert.Equal(original, await FileShotStore.Documents.LoadJsonAsync(f.Directory, f.Project.Id, true, ct));
        Assert.Equal(cut, await FileCutStore.Documents.LoadJsonAsync(f.Directory, f.Project.Id, true, ct));
        Assert.Equal(job.Id, Assert.Single((await f.Jobs.ReadAsync(ct)).Jobs).Id);
    }

    [Fact]
    public async Task LinkedFolderReconciliationCanResumeAfterTheFormatCommit()
    {
        var f = await Fixture(); await FormatFixtures.Sqlite(f.Directory, f.Project.Id, ct);
        using (ProjectDatabase.AllowUpgrade(f.Directory))
        {
            var json = (await new ProjectUpgradeContext(f.Directory, f.Project.Id).LoadAsync("production", true, ct))!;
            json["compositions"]![0]!["generationSetupId"] = Guid.NewGuid();
            await TestProjectDatabase.SaveJsonAsync(FileProductionStore.Documents, f.Directory, f.Project.Id, json, ct);
        }
        var linked = Path.Combine(root, "linked"); Directory.Move(f.Directory, linked);
        var paths = new ApplicationPaths(Path.Combine(root, "library")); var locations = f.Files.Locations;
        var projects = new FileProjectStore(paths, TimeProvider.System, NullLogger<FileProjectStore>.Instance, locations);
        var folders = new ProjectFolders(paths, locations, projects, TimeProvider.System, f.Jobs);
        var before = await File.ReadAllBytesAsync(TestProjectDatabase.PathIn(linked), ct);
        await folders.OpenAsync(linked, ct);
        Assert.Equal(before, await File.ReadAllBytesAsync(TestProjectDatabase.PathIn(linked), ct));
        var setups = new FailingSetups(paths);
        var upgrades = new ProjectUpgrades(paths, f.Files, locations, f.Jobs, setups);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => upgrades.UpgradeAsync(f.Project.Id, ct: ct));
        Assert.Equal(2, ProjectFormat.Inspect(linked));
        Assert.True((await upgrades.StatusAsync(f.Project.Id, ct)).ReconciliationPending);
        await Assert.ThrowsAsync<ProjectUpgradeRequiredException>(() => FileProductionStore.Documents.LoadAsync(linked, f.Project.Id, ct));
        var backup = await File.ReadAllBytesAsync(ProjectUpgrader.BackupPath(linked, 1), ct);
        setups.Fail = false;
        await upgrades.UpgradeAsync(f.Project.Id, ct: ct);
        Assert.False((await upgrades.StatusAsync(f.Project.Id, ct)).Needed);
        Assert.Equal(backup, await File.ReadAllBytesAsync(ProjectUpgrader.BackupPath(linked, 1), ct));
        var production = await FileProductionStore.Documents.LoadAsync(linked, f.Project.Id, ct);
        Assert.Contains((await setups.LoadAsync(ct)).Setups, s => s.Id == production.Compositions[0].GenerationSetupId);
    }

    [Fact]
    public async Task ScopeIsDirectoryBoundAndCacheHitsStillRespectTheGate()
    {
        var f = await Fixture(); await FormatFixtures.Sqlite(f.Directory, f.Project.Id, ct);
        using (ProjectDatabase.AllowUpgrade(f.Directory)) await FileScriptStore.Documents.LoadAsync(f.Directory, f.Project.Id, ct);
        await Assert.ThrowsAsync<ProjectUpgradeRequiredException>(() => FileScriptStore.Documents.LoadAsync(f.Directory, f.Project.Id, ct));
        using (ProjectDatabase.AllowUpgrade(root))
            await Assert.ThrowsAsync<ProjectUpgradeRequiredException>(() => FileScriptStore.Documents.LoadAsync(f.Directory, f.Project.Id, ct));
    }

    [Theory]
    [InlineData(0, "recognized")]
    [InlineData(3, "newer")]
    public async Task StatusRejectsUnrecognizedAndNewerDatabasesWithoutWriting(int version, string message)
    {
        var f = await Fixture(); await FormatFixtures.Sqlite(f.Directory, f.Project.Id, ct);
        TestProjectDatabase.Execute(f.Directory, $"PRAGMA user_version = {version}");
        var before = await File.ReadAllBytesAsync(TestProjectDatabase.PathIn(f.Directory), ct);
        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Upgrades.StatusAsync(f.Project.Id, ct));
        Assert.Contains(message, error.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(TestProjectDatabase.PathIn(f.Directory), ct));
    }

    [Fact]
    public async Task RetryingLegacyImportAcceptsItsOriginalEmptyDatabaseBackup()
    {
        var f = await Fixture(); await FormatFixtures.LegacyOnly(f.Directory, f.Project.Id, ct);
        await File.WriteAllBytesAsync(TestProjectDatabase.PathIn(f.Directory), [], ct);
        var script = Path.Combine(f.Directory, "script.json");
        await File.WriteAllTextAsync(script, FormatFixtures.Json(f.Project.Id)["script"].Replace("\"schemaVersion\":1", "\"schemaVersion\":99"), ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Upgrades.UpgradeAsync(f.Project.Id, ct: ct));
        var backup = await File.ReadAllBytesAsync(ProjectUpgrader.BackupPath(f.Directory, 1), ct);
        using (ProjectDatabase.AllowUpgrade(f.Directory))
            await TestProjectDatabase.SaveJsonAsync(FileScriptStore.Documents, f.Directory, f.Project.Id,
                JsonNode.Parse(FormatFixtures.Json(f.Project.Id)["script"])!, ct);
        await f.Upgrades.UpgradeAsync(f.Project.Id, ct: ct);
        Assert.Equal(2, ProjectFormat.Inspect(f.Directory));
        Assert.Equal(backup, await File.ReadAllBytesAsync(ProjectUpgrader.BackupPath(f.Directory, 1), ct));
    }

    [Fact]
    public async Task UnsupportedDocumentVersionStopsBeforeTheFormatChanges()
    {
        var f = await Fixture(); await FormatFixtures.Sqlite(f.Directory, f.Project.Id, ct);
        TestProjectDatabase.Execute(f.Directory, "UPDATE documents SET header = json_set(header, '$.schemaVersion', 99) WHERE kind = 'script'");
        var before = TestProjectDatabase.Dump(f.Directory);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Upgrades.UpgradeAsync(f.Project.Id, ct: ct));
        Assert.Equal(before, TestProjectDatabase.Dump(f.Directory));
    }

    private sealed class FailingSetups(ApplicationPaths paths) : IGenerationSetupStore
    {
        private readonly FileGenerationSetupStore inner = new(paths);
        public bool Fail = true;
        public Task<GenerationSetupLibrary> LoadAsync(CancellationToken ct = default) => Fail
            ? throw new WorkspaceStoreException("Injected setup failure") : inner.LoadAsync(ct);
        public Task<GenerationSetupLibrary> ImportAsync(ProductionDocument document, CancellationToken ct = default) => inner.ImportAsync(document, ct);
        public Task<GenerationSetupLibrary> SaveAsync(GenerationSetup setup, long expectedVersion, CancellationToken ct = default) => inner.SaveAsync(setup, expectedVersion, ct);
        public Task<GenerationSetupLibrary> SelectAsync(Guid id, CancellationToken ct = default) => inner.SelectAsync(id, ct);
    }

    public void Dispose() { DocumentCache.Clear(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
