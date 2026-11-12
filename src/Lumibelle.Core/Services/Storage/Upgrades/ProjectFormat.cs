using lumibelle.Services.Story;
using Microsoft.Data.Sqlite;

namespace lumibelle.Services.Storage;

internal static class ProjectFormat
{
    public const int Current = 2;

    public static int Initial(string directory) => ProjectDocuments.All.Any(s => s.HasLegacy(directory)) ? 1 : Current;

    internal static SqliteConnection Connect(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5 }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    public static int Inspect(string directory)
    {
        var db = ProjectDatabase.For(directory);
        if (!db.Exists) return Initial(directory);
        try
        {
            using var connection = Connect(db.Path);
            var version = Inspect(connection, db.Path);
            return version == 0 ? Initial(directory) : version;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        { throw db.Translate(e, "Couldn’t inspect the project database. Check folder permissions and try again."); }
    }

    internal static int Inspect(SqliteConnection connection, string path)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > Current)
            throw new WorkspaceStoreException("This project was saved by a newer version of Lumibelle. Update Lumibelle to open it. Nothing was changed.")
            { Data = { ["path"] = path } };
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
        if (version == 0 && Convert.ToInt32(command.ExecuteScalar()) == 0) return 0;
        if (version < 1) throw Unrecognized(path);
        try
        {
            command.CommandText = "SELECT key, value FROM meta LIMIT 0; SELECT document, state, source, files, started_utc, completed_utc FROM migrations LIMIT 0; SELECT kind, stamp, updated_utc, header, lists FROM documents LIMIT 0; SELECT kind, list, id, position, payload, hash FROM parts LIMIT 0";
            command.ExecuteNonQuery();
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 1) { throw Unrecognized(path); }
        return version;
    }

    private static WorkspaceStoreException Unrecognized(string path) =>
        new("This database is not a recognized Lumibelle project database. Nothing was changed.") { Data = { ["path"] = path } };

    public static void RequireCurrent(string directory)
    {
        if (ProjectDatabase.UpgradeAllowed(directory)) return;
        var version = Inspect(directory);
        if (version < Current || ReconciliationPending(directory)) throw new ProjectUpgradeRequiredException(directory, version);
    }

    internal static bool ReconciliationPending(string directory)
    {
        var db = ProjectDatabase.For(directory);
        if (!db.Exists) return false;
        try
        {
            using var connection = Connect(db.Path);
            if (Inspect(connection, db.Path) != Current) return false;
            return ReconciliationPending(connection);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        { throw db.Translate(e, "Couldn’t read project upgrade progress."); }
    }

    internal static bool ReconciliationPending(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'format_setup_reconciliation'";
        return command.ExecuteScalar() as string == "pending";
    }
}

public sealed class ProjectUpgradeRequiredException : WorkspaceStoreException
{
    public int Found { get; }
    public int Current => ProjectFormat.Current;
    public ProjectUpgradeRequiredException(string directory, int found)
        : base("This project needs upgrading. Open it in Lumibelle to upgrade it.")
    { Found = found; Data["path"] = Path.Combine(directory, ProjectDatabase.FileName); }

    internal static bool IsOnlyUpgradeRequired(Exception error)
    {
        if (error is ProjectUpgradeRequiredException) return true;
        if (error is not AggregateException aggregate) return false;
        var errors = aggregate.Flatten().InnerExceptions;
        return errors.Count > 0 && errors.All(e => e is ProjectUpgradeRequiredException);
    }
}
