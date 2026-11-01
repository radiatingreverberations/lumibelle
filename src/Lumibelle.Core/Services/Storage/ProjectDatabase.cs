using System.Collections.Concurrent;
using lumibelle.Services.Story;
using Microsoft.Data.Sqlite;

namespace lumibelle.Services.Storage;

/// <summary>
/// The authoritative store for one project folder's documents: <c>&lt;project&gt;/project.sqlite</c>.
/// Connections are private and short-lived (no pooling), so no handle stays open between
/// operations and project folders can still be moved, compacted or deleted. The default
/// rollback journal keeps the folder free of <c>-wal</c>/<c>-shm</c> companions, which matters
/// for linked folders, Git-kept projects and package hashing.
/// A damaged database is never deleted or rebuilt: it holds the only copy of the project.
/// </summary>
public sealed class ProjectDatabase
{
    public const string FileName = "project.sqlite";
    private const int SchemaVersion = 1;
    private static readonly ConcurrentDictionary<string, ProjectDatabase> Instances =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private volatile bool schemaReady;

    private ProjectDatabase(string directory)
    {
        Directory = directory;
        Path = System.IO.Path.Combine(directory, FileName);
    }

    public static ProjectDatabase For(string directory) =>
        Instances.GetOrAdd(System.IO.Path.GetFullPath(directory).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar), d => new(d));

    public string Directory { get; }
    public string Path { get; }
    public bool Exists => File.Exists(Path);

    /// <summary>Runs <paramref name="read"/> in one deferred transaction, so all its statements see one snapshot.</summary>
    internal T Read<T>(Func<ProjectDbSession, T> read, string what)
    {
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            var result = read(new(this, connection, transaction));
            transaction.Commit();
            return result;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        { throw Translate(e, $"Couldn’t read {what}. Nothing was changed."); }
    }

    /// <summary>
    /// Runs <paramref name="write"/> in one immediate transaction and commits it. Callers serialize
    /// read-modify-write cycles with <see cref="ProjectFiles.LockAsync"/>; the transaction is only the
    /// publication step and never spans media work.
    /// </summary>
    internal T Write<T>(Func<ProjectDbSession, T> write)
    {
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var result = write(new(this, connection, transaction));
            transaction.Commit();
            return result;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        { throw Translate(e, "Couldn’t save. Check folder permissions and free disk space. Your previous saved version is intact."); }
    }

    private SqliteConnection Open()
    {
        // Never recreate a removed project folder just to open its database.
        if (!System.IO.Directory.Exists(Directory))
            throw new WorkspaceStoreException("Project not found. Return to the project hub.") { Data = { ["path"] = Path } };
        var created = !File.Exists(Path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 30
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // FULL synchronization matches the flush-before-rename durability of the former JSON files.
            command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
            if (created || !schemaReady) { EnsureSchema(connection); schemaReady = true; }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > SchemaVersion)
            throw new WorkspaceStoreException("This project was saved by a newer version of Lumibelle. Update Lumibelle to open it. Nothing was changed.")
                { Data = { ["path"] = Path } };
        if (version == SchemaVersion) return;
        command.CommandText = $"""
            BEGIN IMMEDIATE;
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS migrations(document TEXT PRIMARY KEY, state TEXT NOT NULL, source TEXT NOT NULL,
                started_utc TEXT NOT NULL, completed_utc TEXT NULL, files TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS documents(kind TEXT PRIMARY KEY, stamp INTEGER NOT NULL, updated_utc TEXT NOT NULL,
                header TEXT NOT NULL, lists TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS parts(kind TEXT NOT NULL, list TEXT NOT NULL, id TEXT NOT NULL, position INTEGER NOT NULL,
                payload TEXT NOT NULL, hash TEXT NULL, PRIMARY KEY(kind, list, id));
            CREATE INDEX IF NOT EXISTS parts_order ON parts(kind, list, position);
            INSERT OR IGNORE INTO meta(key, value) VALUES ('created_utc', '{DateTimeOffset.UtcNow:O}');
            INSERT OR IGNORE INTO meta(key, value) VALUES ('instance', '{Guid.NewGuid():N}');
            PRAGMA user_version = {SchemaVersion};
            COMMIT;
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Identifies this database file, so a replaced or restored file never matches cached rows.</summary>
    internal static string Instance(ProjectDbSession session) =>
        session.Scalar("SELECT value FROM meta WHERE key = 'instance'") as string ?? "";

    private WorkspaceStoreException Translate(Exception e, string message)
    {
        if (e is WorkspaceStoreException store) return store;
        var text = e is SqliteException { SqliteErrorCode: 11 or 26 }
            ? $"This project’s database ({Path}) is damaged. Nothing was changed. Restore {FileName} from a backup; the project’s original files may also be in its legacy-json folder."
            : e is SqliteException { SqliteErrorCode: 5 or 6 }
                ? "The project database is in use by another program. Close it and try again. Nothing was changed."
                : message;
        return new WorkspaceStoreException(text, e) { Data = { ["path"] = Path } };
    }
}

/// <summary>One connection and transaction inside <see cref="ProjectDatabase.Read{T}"/> or <see cref="ProjectDatabase.Write{T}"/>.</summary>
internal sealed class ProjectDbSession(ProjectDatabase database, SqliteConnection connection, SqliteTransaction transaction)
{
    public ProjectDatabase Database { get; } = database;

    public SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }
}
