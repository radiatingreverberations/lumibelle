using lumibelle.Services.Storage;
using Microsoft.Data.Sqlite;

namespace Lumibelle.Tests;

/// <summary>Inspects and disturbs a project's <c>project.sqlite</c> from tests.</summary>
internal static class TestProjectDatabase
{
    public static string PathIn(string projectDirectory) => Path.Combine(projectDirectory, ProjectDatabase.FileName);

    /// <summary>The stored write counter of a document, or null when it has none.</summary>
    public static long? Stamp(string projectDirectory, string kind) =>
        Scalar(projectDirectory, "SELECT stamp FROM documents WHERE kind = $kind", kind) is { } value ? Convert.ToInt64(value) : null;

    /// <summary>
    /// The saved state of a project folder's documents: each stored document's stamp and the text of
    /// every remaining top-level JSON file. Equal snapshots mean nothing was saved in between.
    /// Brings legacy JSON in first, so a later first read cannot look like a change.
    /// </summary>
    public static async Task<string> SnapshotAsync(string projectDirectory, Guid project, CancellationToken ct)
    {
        await ProjectDocuments.MigrateAllAsync(projectDirectory, project, ct);
        var parts = new List<string>();
        if (File.Exists(PathIn(projectDirectory)))
            parts.AddRange(ProjectDocuments.All.Select(d => $"{d.Kind}={Stamp(projectDirectory, d.Kind)}"));
        parts.AddRange(Directory.EnumerateFiles(projectDirectory, "*.json").Order(StringComparer.Ordinal)
            .Select(f => Path.GetFileName(f) + "=" + File.ReadAllText(f)));
        return string.Join("\n", parts);
    }

    /// <summary>Saves an asset library as stored rows, bypassing the store's checks, for setups and older states.</summary>
    public static Task SaveAssetsAsync(string projectDirectory, lumibelle.Models.AssetLibrary library, CancellationToken ct) =>
        lumibelle.Services.Assets.FileAssetStore.Documents.SaveAsync(projectDirectory, library.ProjectId, library, ct);

    /// <summary>A stored document's JSON, for inspecting or editing it as a test.</summary>
    public static async Task<System.Text.Json.Nodes.JsonNode> JsonAsync<T>(ProjectDocumentStore<T> store, string projectDirectory, Guid project, CancellationToken ct) where T : class =>
        System.Text.Json.Nodes.JsonNode.Parse((await store.LoadJsonAsync(projectDirectory, project, complete: true, ct))!)!;

    public static Task SaveJsonAsync<T>(ProjectDocumentStore<T> store, string projectDirectory, Guid project, System.Text.Json.Nodes.JsonNode json, CancellationToken ct) where T : class =>
        store.SaveAsync(projectDirectory, project, System.Text.Json.JsonSerializer.SerializeToElement(json), ct);

    /// <summary>Replaces a stored document's header with text that isn't JSON.</summary>
    public static void Corrupt(string projectDirectory, string kind)
    {
        Execute(projectDirectory, $"UPDATE documents SET header = 'invalid', stamp = stamp + 1 WHERE kind = '{kind}'");
        lumibelle.Services.Storage.DocumentCache.Clear();
    }

    public static string Dump(string projectDirectory)
    {
        using var connection = Open(projectDirectory);
        var lines = new List<string>();
        foreach (var sql in new[] { "SELECT * FROM meta", "SELECT * FROM migrations", "SELECT kind, stamp, updated_utc, length(header), lists FROM documents", "SELECT kind, list, id, position, length(payload), hash FROM parts ORDER BY kind, list, id", "PRAGMA user_version", "PRAGMA page_count", "PRAGMA freelist_count" })
        {
            using var command = connection.CreateCommand(); command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read()) lines.Add(sql[..Math.Min(30, sql.Length)] + ": " + string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i)))));
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static bool HasDocument(string projectDirectory, string kind) =>
        File.Exists(PathIn(projectDirectory)) && Stamp(projectDirectory, kind) is not null;

    /// <summary>Stored row payloads of one list, in order.</summary>
    public static IReadOnlyList<string> Rows(string projectDirectory, string kind, string list)
    {
        using var connection = Open(projectDirectory);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM parts WHERE kind = $kind AND list = $list ORDER BY position";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$list", list);
        using var reader = command.ExecuteReader();
        List<string> rows = [];
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }

    public static string? Header(string projectDirectory, string kind) =>
        Scalar(projectDirectory, "SELECT header FROM documents WHERE kind = $kind", kind) as string;

    public static void Execute(string projectDirectory, string sql)
    {
        using var connection = Open(projectDirectory);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Opens the database file so others may only read it, like a scanner or backup tool holding it.
    /// Loads still work; the next save fails without changing anything.
    /// </summary>
    public static FileStream HoldReadOnly(string projectDirectory) =>
        new(PathIn(projectDirectory), FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>Overwrites the file header so SQLite no longer recognizes the database.</summary>
    public static void Damage(string projectDirectory)
    {
        using var stream = new FileStream(PathIn(projectDirectory), FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Write("not a database file at all"u8);
    }

    private static object? Scalar(string projectDirectory, string sql, string kind)
    {
        using var connection = Open(projectDirectory);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$kind", kind);
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private static SqliteConnection Open(string projectDirectory)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = PathIn(projectDirectory), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
