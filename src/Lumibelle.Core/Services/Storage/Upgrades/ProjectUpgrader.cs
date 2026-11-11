using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Services.Story;
using Microsoft.Data.Sqlite;

namespace lumibelle.Services.Storage;

internal interface IProjectFormatStep
{
    int To { get; }
    string Summary { get; }
    Task ApplyAsync(ProjectUpgradeContext context, CancellationToken ct);
}

internal sealed class ProjectUpgradeContext(string directory, Guid project)
{
    public string Directory { get; } = directory;
    public Guid Project { get; } = project;
    private readonly Dictionary<string, PreparedDocument> staged = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, int> Versions() => ProjectDatabase.For(Directory).Read(s =>
    {
        using var command = s.Command("SELECT kind, header FROM documents");
        using var reader = command.ExecuteReader();
        var versions = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read())
        {
            using var header = JsonDocument.Parse(reader.GetString(1));
            if (!header.RootElement.TryGetProperty("projectId", out var identity) || !identity.TryGetGuid(out var storedProject) || storedProject != Project)
                throw new WorkspaceStoreException($"The stored {reader.GetString(0)} document belongs to a different project or has an invalid identity. Upgrade was stopped.");
            if (reader.GetString(0) == "script_snapshots" && !header.RootElement.TryGetProperty("schemaVersion", out _))
            { versions.Add("script_snapshots", 1); continue; }
            if (!header.RootElement.TryGetProperty("schemaVersion", out var v) || !v.TryGetInt32(out var version))
                throw new WorkspaceStoreException($"The stored {reader.GetString(0)} document has no valid schema version. Upgrade was stopped.");
            versions.Add(reader.GetString(0), version);
        }
        return versions;
    }, "project document versions");

    public async Task<JsonObject?> LoadAsync(string kind, bool complete, CancellationToken ct)
    {
        var bytes = await Store(kind).LoadJsonAsync(Directory, Project, complete, ct);
        return bytes is null ? null : JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException("Expected a document object.");
    }

    private static IProjectDocumentStore Store(string kind) => ProjectDocuments.All.Single(s => s.Kind == kind);
    public void Replace(string kind, JsonObject document) => staged[kind] = Store(kind).Prepare(JsonSerializer.SerializeToElement(document));

    internal void Commit(int from, int to, bool reconcileSetups, CancellationToken ct) => ProjectDatabase.For(Directory).Write(s =>
    {
        if (Convert.ToInt32(s.Scalar("PRAGMA user_version")) != from) throw new WorkspaceConflictException();
        foreach (var (kind, prepared) in staged) { ct.ThrowIfCancellationRequested(); Store(kind).Apply(s, prepared, replace: true); }
        ct.ThrowIfCancellationRequested();
        s.Execute($"PRAGMA user_version = {to}");
        // Reconciliation touches the global setup store, so it has its own resumable boundary.
        if (reconcileSetups) s.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES ('format_setup_reconciliation', 'pending')");
        return 0;
    });
}

internal static class ProjectUpgrader
{
    internal static IReadOnlyList<IProjectFormatStep> Steps { get; } = [new DocumentVersionsStep()];

    internal static IReadOnlyList<IProjectFormatStep> Pending(int from, IReadOnlyList<IProjectFormatStep>? steps = null)
    {
        var pending = (steps ?? Steps).Where(s => s.To > from).ToArray();
        var expected = from;
        foreach (var step in pending)
            if (step.To != ++expected) throw new WorkspaceStoreException("The project upgrade sequence is incomplete. Update Lumibelle before upgrading.");
        if (expected != ProjectFormat.Current) throw new WorkspaceStoreException("The project upgrade sequence does not reach this version of Lumibelle.");
        return pending;
    }

    // Caller holds the project lock. Step injection is local to a test, never shared mutable state.
    internal static async Task RunAsync(string directory, Guid project, IProgress<string>? progress, CancellationToken ct,
        IReadOnlyList<IProjectFormatStep>? steps = null, bool reconcileSetups = false)
    {
        var from = ProjectFormat.Inspect(directory);
        if (from == ProjectFormat.Current) return;
        var pending = Pending(from, steps);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(directory, ProjectDatabase.FileName)))
            {
                progress?.Report("Keeping a copy of the project database…");
                Backup(directory, from, ct);
            }
            using var scope = ProjectDatabase.AllowUpgrade(directory);
            progress?.Report("Bringing saved project documents into the database…");
            await ProjectDocuments.MigrateAllAsync(directory, project, ct);
            for (var i = 0; i < pending.Count; i++)
            {
                var step = pending[i];
                progress?.Report($"Step {i + 1} of {pending.Count}: {step.Summary}…");
                var context = new ProjectUpgradeContext(directory, project);
                await step.ApplyAsync(context, ct);
                context.Commit(from, step.To, reconcileSetups, ct);
                DocumentCache.EvictDirectory(directory);
                from = step.To;
            }
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { throw ProjectDatabase.For(directory).Translate(e, "Couldn’t complete the project upgrade. Your backup and completed steps have been kept. Try again to resume."); }
    }

    internal static string BackupPath(string directory, int from) => Path.Combine(directory, LegacyFiles.Folder, $"project-format-{from}.sqlite");

    private static void Backup(string directory, int from, CancellationToken ct)
    {
        var target = BackupPath(directory, from);
        if (File.Exists(target))
        {
            using var existing = ProjectFormat.Connect(target);
            using var check = existing.CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            var valid = check.ExecuteScalar() as string == "ok";
            var version = ProjectFormat.Inspect(existing, target);
            // An interrupted older app can leave an empty, uninitialized database beside legacy JSON.
            if (!valid || version != from && version != 0)
                throw new WorkspaceStoreException($"The upgrade backup at {target} is invalid. Move it aside and try again.");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var source = ProjectFormat.Connect(Path.Combine(directory, ProjectDatabase.FileName)))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = temporary, Pooling = false }.ToString()))
            { destination.Open(); source.BackupDatabase(destination); }
            DurableFile.Flush(temporary);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target);
            DurableFile.FlushDirectory(Path.GetDirectoryName(target)!);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
