using System.Text.Json;
using lumibelle.Services.Storage;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ProjectDocumentStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Lumibelle.DocumentStoreTests", Guid.NewGuid().ToString("N"));
    private readonly Guid project = Guid.NewGuid();
    private readonly CancellationToken ct = TestContext.Current.CancellationToken;

    public sealed record Sample
    {
        public Guid ProjectId { get; set; }
        public long Revision { get; set; }
        public string Title { get; set; } = "";
        public List<Item> Items { get; set; } = [];
        public List<Box> Boxes { get; set; } = [];
        public List<string>? Notes { get; set; } = [];
    }
    public sealed record Item(Guid Id, string Name);
    public sealed record Box
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Label { get; set; } = "";
        public List<string> Big { get; set; } = [];
    }

    private static readonly DocumentSpec<Sample> Spec = new()
    {
        Kind = "sample", Description = "the sample", Legacy = ["sample.json", "sample-extras"],
        Empty = id => new() { ProjectId = id },
        Lists = [new("items"), new("boxes", Detached: [new("big", "[]")]), new("notes", Key: null)]
    };

    public ProjectDocumentStoreTests() => Directory.CreateDirectory(root);
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static ProjectDocumentStore<Sample> Store() => new(Spec);
    private static Sample Example(Guid project) => new()
    {
        ProjectId = project, Revision = 3, Title = "First",
        Items = [new(Guid.NewGuid(), "a"), new(Guid.NewGuid(), "b")],
        Boxes = [new() { Label = "one", Big = ["x", "y"] }, new() { Label = "two", Big = [] }],
        Notes = ["n1", "n1"]
    };
    private static string Json(Sample s) => JsonSerializer.Serialize(s, ProjectJson.Compact);
    private static void Forget() { DocumentCache.Clear(); ProjectDocumentStore<Sample>.ForgetMigrations(); }

    [Fact]
    public async Task EmptyFolderReadsWithoutCreatingADatabase()
    {
        var loaded = await Store().LoadAsync(root, project, ct);
        Assert.Equal(project, loaded.ProjectId);
        Assert.Empty(loaded.Items);
        Assert.False(File.Exists(TestProjectDatabase.PathIn(root)));
    }

    [Fact]
    public async Task MissingProjectFolderIsNeverRecreated()
    {
        var gone = Path.Combine(root, "gone");
        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store().SaveAsync(gone, project, Example(project), ct));
        Assert.Contains("not found", error.Message);
        Assert.False(Directory.Exists(gone));
    }

    [Fact]
    public async Task SavedDocumentsRoundTripAndLoadsAreIndependentCopies()
    {
        var saved = Example(project);
        await Store().SaveAsync(root, project, saved, ct);
        var first = await Store().LoadCompleteAsync(root, project, ct);
        Assert.Equal(Json(saved), Json(first));
        first.Items.Clear(); first.Title = "changed";
        var second = await Store().LoadCompleteAsync(root, project, ct);
        Assert.Equal(Json(saved), Json(second));
        Forget();
        Assert.Equal(Json(saved), Json(await Store().LoadCompleteAsync(root, project, ct)));
    }

    [Fact]
    public async Task NullAndEmptyListsAreKeptApart()
    {
        await Store().SaveAsync(root, project, Example(project) with { Notes = null, Items = [] }, ct);
        Forget();
        var loaded = await Store().LoadAsync(root, project, ct);
        Assert.Null(loaded.Notes);
        Assert.NotNull(loaded.Items);
        Assert.Empty(loaded.Items);
    }

    [Fact]
    public async Task DetachedPropertiesLoadOnRequestAndSurviveSavingASummary()
    {
        var saved = Example(project);
        await Store().SaveAsync(root, project, saved, ct);
        var summary = await Store().LoadAsync(root, project, ct);
        Assert.All(summary.Boxes, b => Assert.Empty(b.Big));
        Assert.Equal("one", summary.Boxes[0].Label);

        summary.Boxes[0].Label = "renamed";
        summary.Items.RemoveAt(0);
        await Store().SaveAsync(root, project, summary, ct);
        Forget();
        var complete = await Store().LoadCompleteAsync(root, project, ct);
        Assert.Equal("renamed", complete.Boxes[0].Label);
        Assert.Equal(["x", "y"], complete.Boxes[0].Big);
        Assert.Single(complete.Items);

        var one = await Store().FindAsync<Box>(root, project, "boxes", saved.Boxes[0].Id.ToString(), attach: true, ct);
        Assert.Equal(["x", "y"], one!.Big);
        var light = await Store().FindAsync<Box>(root, project, "boxes", saved.Boxes[0].Id.ToString(), attach: false, ct);
        Assert.Empty(light!.Big);

        complete.Boxes.RemoveAt(0);
        await Store().SaveAsync(root, project, complete, ct);
        Assert.Empty(TestProjectDatabase.Rows(root, "sample", "boxes.detached"));
    }

    [Fact]
    public async Task OnlyChangedRowsAreRewritten()
    {
        var saved = Example(project);
        await Store().SaveAsync(root, project, saved, ct);
        var before = RowIds();
        saved.Items[1] = saved.Items[1] with { Name = "changed" };
        saved.Revision++;
        await Store().SaveAsync(root, project, saved, ct);
        var after = RowIds();
        Assert.Equal(before[saved.Items[0].Id.ToString()], after[saved.Items[0].Id.ToString()]);
        Assert.NotEqual(before[saved.Items[1].Id.ToString()], after[saved.Items[1].Id.ToString()]);
        Assert.Equal(2, TestProjectDatabase.Stamp(root, "sample"));
    }

    private Dictionary<string, long> RowIds()
    {
        var path = TestProjectDatabase.PathIn(root);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, rowid FROM parts WHERE kind = 'sample' AND list = 'items'";
        using var reader = command.ExecuteReader();
        Dictionary<string, long> ids = [];
        while (reader.Read()) ids[reader.GetString(0)] = reader.GetInt64(1);
        return ids;
    }

    [Fact]
    public async Task ChangesMadeOutsideTheStoreAreNeverHiddenByTheCache()
    {
        await Store().SaveAsync(root, project, Example(project), ct);
        Assert.Equal("First", (await Store().LoadAsync(root, project, ct)).Title);
        TestProjectDatabase.Execute(root, """UPDATE documents SET stamp = stamp + 1, header = json_set(header, '$.title', 'Outside') WHERE kind = 'sample'""");
        Assert.Equal("Outside", (await Store().LoadAsync(root, project, ct)).Title);
    }

    [Fact]
    public async Task LegacyJsonIsBroughtInOnceAndMovedAside()
    {
        var legacy = Example(project);
        await AtomicJsonFile.WriteAsync(Path.Combine(root, "sample.json"), legacy, ct);
        Directory.CreateDirectory(Path.Combine(root, "sample-extras"));
        File.WriteAllText(Path.Combine(root, "sample-extras", "a.json"), "{}");
        File.WriteAllText(Path.Combine(root, "sample.json.0123.tmp"), "partial");

        Assert.Equal(Json(legacy), Json(await Store().LoadCompleteAsync(root, project, ct)));
        Assert.False(File.Exists(Path.Combine(root, "sample.json")));
        Assert.False(Directory.Exists(Path.Combine(root, "sample-extras")));
        Assert.True(File.Exists(Path.Combine(root, LegacyFiles.Folder, "sample.json")));
        Assert.True(File.Exists(Path.Combine(root, LegacyFiles.Folder, "sample-extras", "a.json")));
        Assert.True(File.Exists(Path.Combine(root, LegacyFiles.Folder, "sample.json.0123.tmp")));

        // Legacy JSON appearing again later is ignored: the database is authoritative.
        await AtomicJsonFile.WriteAsync(Path.Combine(root, "sample.json"), legacy with { Title = "stale" }, ct);
        Forget();
        Assert.Equal("First", (await Store().LoadAsync(root, project, ct)).Title);
    }

    [Fact]
    public async Task InterruptedMigrationFinishesMovingFilesWithoutRereadingThem()
    {
        await AtomicJsonFile.WriteAsync(Path.Combine(root, "sample.json"), Example(project), ct);
        await Store().LoadAsync(root, project, ct);
        // As if the process stopped right after committing: the files are still at the root.
        TestProjectDatabase.Execute(root, "UPDATE migrations SET state = 'committed', completed_utc = NULL WHERE document = 'sample'");
        await AtomicJsonFile.WriteAsync(Path.Combine(root, "sample.json"), Example(project) with { Title = "never read, but longer" }, ct);
        Forget();

        Assert.Equal("First", (await Store().LoadAsync(root, project, ct)).Title);
        Assert.False(File.Exists(Path.Combine(root, "sample.json")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(root, LegacyFiles.Folder), "sample.json.*.duplicate"));
    }

    [Fact]
    public async Task UnreadableLegacyJsonFailsWithoutCreatingADocument()
    {
        File.WriteAllText(Path.Combine(root, "sample.json"), "{ broken");
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store().LoadAsync(root, project, ct));
        Assert.True(File.Exists(Path.Combine(root, "sample.json")));
        Assert.False(TestProjectDatabase.HasDocument(root, "sample"));
    }

    [Fact]
    public async Task DamagedDatabaseIsReportedAndKept()
    {
        await Store().SaveAsync(root, project, Example(project), ct);
        TestProjectDatabase.Damage(root);
        Forget();
        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store().LoadAsync(root, project, ct));
        Assert.Contains("damaged", error.Message);
        Assert.Equal(TestProjectDatabase.PathIn(root), error.Data["path"]);
        Assert.True(File.Exists(TestProjectDatabase.PathIn(root)));
    }

    [Fact]
    public async Task ADatabaseHeldReadOnlyStillLoadsButRefusesSaves()
    {
        await Store().SaveAsync(root, project, Example(project), ct);
        using (TestProjectDatabase.HoldReadOnly(root))
        {
            Forget();
            Assert.Equal("First", (await Store().LoadAsync(root, project, ct)).Title);
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store().SaveAsync(root, project, Example(project) with { Title = "x" }, ct));
        }
        Assert.Equal("First", (await Store().LoadAsync(root, project, ct)).Title);
    }

    [Fact]
    public async Task DuplicateOrMissingKeysFallBackToPositions()
    {
        var id = Guid.NewGuid();
        var saved = Example(project) with { Items = [new(id, "a"), new(id, "b")] };
        await Store().SaveAsync(root, project, saved, ct);
        Forget();
        Assert.Equal(Json(saved), Json(await Store().LoadCompleteAsync(root, project, ct)));
    }
}
