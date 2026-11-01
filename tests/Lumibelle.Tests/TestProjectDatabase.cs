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
