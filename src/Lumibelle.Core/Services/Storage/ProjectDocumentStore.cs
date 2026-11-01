using System.Collections.Concurrent;
using System.Text.Json;
using lumibelle.Services.Story;

namespace lumibelle.Services.Storage;

/// <summary>How one kind of project document is stored.</summary>
internal sealed class DocumentSpec<T> where T : class
{
    /// <summary>The document's key in the database, such as <c>shots</c>.</summary>
    public required string Kind { get; init; }
    /// <summary>How people know the document, for messages: "the shot list".</summary>
    public required string Description { get; init; }
    public required IReadOnlyList<DocumentList> Lists { get; init; }
    public required Func<Guid, T> Empty { get; init; }
    /// <summary>Files and folders, relative to the project folder, that held this document before SQLite.</summary>
    public IReadOnlyList<string> Legacy { get; init; } = [];
    /// <summary>
    /// Reads and validates the legacy files, returning the object to store (the typed document, or
    /// raw JSON when the file holds a persisted projection). Defaults to reading <c>Legacy[0]</c> as <typeparamref name="T"/>.
    /// </summary>
    public Func<string, Guid, CancellationToken, Task<object?>>? ReadLegacy { get; init; }
}

/// <summary>A document already split into rows, ready to apply inside a transaction.</summary>
internal sealed class PreparedDocument(string kind, DecomposedDocument rows)
{
    public string Kind { get; } = kind;
    public DecomposedDocument Rows { get; } = rows;
}

/// <summary>
/// Loads and saves one kind of document in each project's <see cref="ProjectDatabase"/>, with a
/// validated cache, row-level writes and the one-time move from legacy JSON. Loads return fresh
/// objects the caller may change. Saves expect the caller to hold the project's
/// <see cref="ProjectFiles.LockAsync"/> lease across its read-modify-write cycle.
/// </summary>
internal interface IProjectDocumentStore
{
    string Kind { get; }
    Task<bool> EnsureAsync(string directory, Guid project, CancellationToken ct);
}

internal sealed class ProjectDocumentStore<T>(DocumentSpec<T> spec) : IProjectDocumentStore where T : class
{
    private static readonly ConcurrentDictionary<string, bool> Migrated = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public DocumentSpec<T> Spec { get; } = spec;
    public string Kind => Spec.Kind;

    /// <summary>The document with detached properties left empty (a summary).</summary>
    public async Task<T> LoadAsync(string directory, Guid project, CancellationToken ct)
    {
        var entry = await EntryAsync(directory, project, ct);
        return entry is null ? Spec.Empty(project) : DocumentCache.Deserialize<T>(entry.Summary(Spec.Lists), Spec.Description);
    }

    /// <summary>The document as a summary, or null when it has never been saved.</summary>
    public async Task<T?> TryLoadAsync(string directory, Guid project, CancellationToken ct) =>
        await EntryAsync(directory, project, ct) is { } entry ? DocumentCache.Deserialize<T>(entry.Summary(Spec.Lists), Spec.Description) : null;

    /// <summary>The whole document, including every detached property.</summary>
    public async Task<T> LoadCompleteAsync(string directory, Guid project, CancellationToken ct) =>
        await LoadJsonAsync(directory, project, complete: true, ct) is { } json ? DocumentCache.Deserialize<T>(json, Spec.Description) : Spec.Empty(project);

    /// <summary>The stored document's JSON (detached properties empty unless <paramref name="complete"/>), or null when it has never been saved.</summary>
    public async Task<byte[]?> LoadJsonAsync(string directory, Guid project, bool complete, CancellationToken ct)
    {
        var entry = await EntryAsync(directory, project, ct);
        if (entry is null) return null;
        if (!complete || !Spec.Lists.Any(l => l.HasDetached)) return entry.Summary(Spec.Lists).ToArray();
        var detached = ProjectDatabase.For(directory).Read(s => ReadDetached(s, null, null), Spec.Description);
        return DocumentJson.Compose(entry.Header, entry.Lists, name =>
        {
            var list = Spec.Lists.First(l => l.Name == name);
            if (!list.HasDetached) return entry.Rows[name].Select(r => r.Payload);
            var extra = detached.GetValueOrDefault(name);
            return entry.Rows[name].Select(r => DocumentJson.Merge(r.Payload, extra?.GetValueOrDefault(r.Id) ?? list.EmptyDetached));
        });
    }

    /// <summary>One list's elements (detached properties empty), without materializing the rest of the document.</summary>
    public async Task<List<TItem>> LoadListAsync<TItem>(string directory, Guid project, string list, CancellationToken ct)
    {
        var entry = await EntryAsync(directory, project, ct);
        if (entry is null || !entry.Lists.Contains(list)) return [];
        var spec = Spec.Lists.First(l => l.Name == list);
        var rows = entry.Rows[list];
        var builder = new System.Text.StringBuilder(rows.Sum(r => r.Payload.Length + 1) + 2).Append('[');
        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(spec.HasDetached ? DocumentJson.Merge(rows[i].Payload, spec.EmptyDetached) : rows[i].Payload);
        }
        return DocumentCache.Deserialize<List<TItem>>(System.Text.Encoding.UTF8.GetBytes(builder.Append(']').ToString()), Spec.Description);
    }

    /// <summary>One list element's JSON, optionally with its detached properties, or null when absent.</summary>
    public async Task<string?> FindAsync(string directory, Guid project, string list, string id, bool attach, CancellationToken ct)
    {
        var entry = await EntryAsync(directory, project, ct);
        var payload = entry?.Find(list, id);
        var spec = Spec.Lists.First(l => l.Name == list);
        if (payload is null || !spec.HasDetached) return payload;
        if (!attach) return DocumentJson.Merge(payload, spec.EmptyDetached);
        var extra = ProjectDatabase.For(directory).Read(s => ReadDetached(s, list, [id]), Spec.Description).GetValueOrDefault(list)?.GetValueOrDefault(id);
        return DocumentJson.Merge(payload, extra ?? spec.EmptyDetached);
    }

    public async Task<TItem?> FindAsync<TItem>(string directory, Guid project, string list, string id, bool attach, CancellationToken ct) where TItem : class =>
        await FindAsync(directory, project, list, id, attach, ct) is { } json ? DocumentCache.Deserialize<TItem>(System.Text.Encoding.UTF8.GetBytes(json), Spec.Description) : null;

    /// <summary>Detached properties of the given elements, as JSON objects keyed by element id.</summary>
    public async Task<IReadOnlyDictionary<string, string>> DetachedAsync(string directory, Guid project, string list, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        if (await EntryAsync(directory, project, ct) is null || ids.Count == 0) return new Dictionary<string, string>();
        return ProjectDatabase.For(directory).Read(s => ReadDetached(s, list, ids), Spec.Description).GetValueOrDefault(list) ?? new Dictionary<string, string>();
    }

    /// <summary>Stores the document, writing only rows that changed. Hold the project lease.</summary>
    public async Task SaveAsync(string directory, Guid project, object document, CancellationToken ct)
    {
        await EnsureAsync(directory, project, ct);
        var prepared = Prepare(document);
        ct.ThrowIfCancellationRequested();
        var db = ProjectDatabase.For(directory);
        try
        {
            var entry = db.Write(s => Apply(s, prepared));
            DocumentCache.Put(db.Path, Kind, entry);
        }
        catch { DocumentCache.Evict(db.Path, Kind); throw; }
    }

    public PreparedDocument Prepare(object document) => new(Kind, DocumentJson.Decompose(document, Spec.Lists));

    /// <summary>Applies a prepared document inside a caller's transaction. Publish the result with <see cref="Publish"/> after commit.</summary>
    public DocumentEntry Apply(ProjectDbSession s, PreparedDocument prepared, bool replace = false)
    {
        var path = s.Database.Path;
        var instance = ProjectDatabase.Instance(s);
        var stamp = s.Scalar("SELECT stamp FROM documents WHERE kind = $kind", ("$kind", Kind)) is { } value ? Convert.ToInt64(value) : (long?)null;
        var previous = replace || stamp is null ? Empty(instance, 0) : Cached(path, instance, stamp.Value) ?? ReadEntry(s, instance, stamp.Value);
        var rows = prepared.Rows;
        var now = DateTimeOffset.UtcNow.ToString("O");
        var lists = JsonSerializer.Serialize(rows.Lists);
        if (stamp is null)
        {
            s.Execute("INSERT INTO documents(kind, stamp, updated_utc, header, lists) VALUES ($kind, 1, $now, $header, $lists)",
                ("$kind", Kind), ("$now", now), ("$header", rows.Header), ("$lists", lists));
            // A document first created by a save has no legacy files to bring in.
            s.Execute("INSERT OR IGNORE INTO migrations(document, state, source, started_utc, completed_utc, files) VALUES ($kind, 'complete', 'none', $now, $now, '[]')",
                ("$kind", Kind), ("$now", now));
        }
        else if (s.Execute("UPDATE documents SET stamp = stamp + 1, updated_utc = $now, header = $header, lists = $lists WHERE kind = $kind AND stamp = $stamp",
            ("$kind", Kind), ("$now", now), ("$header", rows.Header), ("$lists", lists), ("$stamp", stamp.Value)) != 1)
            throw new WorkspaceConflictException();
        if (replace) s.Execute("DELETE FROM parts WHERE kind = $kind", ("$kind", Kind));

        using var upsert = s.Command("INSERT OR REPLACE INTO parts(kind, list, id, position, payload, hash) VALUES ($kind, $list, $id, $position, $payload, $hash)");
        var pList = upsert.Parameters.Add("$list", Microsoft.Data.Sqlite.SqliteType.Text);
        var pId = upsert.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
        var pPosition = upsert.Parameters.Add("$position", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pPayload = upsert.Parameters.Add("$payload", Microsoft.Data.Sqlite.SqliteType.Text);
        var pHash = upsert.Parameters.Add("$hash", Microsoft.Data.Sqlite.SqliteType.Text);
        upsert.Parameters.AddWithValue("$kind", Kind);
        using var move = s.Command("UPDATE parts SET position = $position WHERE kind = $kind AND list = $list AND id = $id");
        var mList = move.Parameters.Add("$list", Microsoft.Data.Sqlite.SqliteType.Text);
        var mId = move.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
        var mPosition = move.Parameters.Add("$position", Microsoft.Data.Sqlite.SqliteType.Integer);
        move.Parameters.AddWithValue("$kind", Kind);
        using var delete = s.Command("DELETE FROM parts WHERE kind = $kind AND list = $list AND id = $id");
        var dList = delete.Parameters.Add("$list", Microsoft.Data.Sqlite.SqliteType.Text);
        var dId = delete.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
        delete.Parameters.AddWithValue("$kind", Kind);
        void Upsert(string list, string id, int position, string payload, string? hash)
        { pList.Value = list; pId.Value = id; pPosition.Value = position; pPayload.Value = payload; pHash.Value = (object?)hash ?? DBNull.Value; upsert.ExecuteNonQuery(); }

        var hashes = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var list in Spec.Lists)
        {
            var before = previous.Rows.GetValueOrDefault(list.Name) ?? [];
            var old = new Dictionary<string, (int Position, string Payload)>(before.Count, StringComparer.Ordinal);
            for (var i = 0; i < before.Count; i++) old[before[i].Id] = (i, before[i].Payload);
            var after = rows.Rows[list.Name];
            for (var i = 0; i < after.Count; i++)
            {
                var row = after[i];
                if (!old.Remove(row.Id, out var prior) || prior.Payload != row.Payload) Upsert(list.Name, row.Id, i, row.Payload, null);
                else if (prior.Position != i) { mList.Value = list.Name; mId.Value = row.Id; mPosition.Value = i; move.ExecuteNonQuery(); }
            }
            var storedHashes = new Dictionary<string, string>(previous.DetachedHashes.GetValueOrDefault(list.Name) ?? [], StringComparer.Ordinal);
            foreach (var removed in old.Keys)
            {
                dList.Value = list.Name; dId.Value = removed; delete.ExecuteNonQuery();
                if (list.HasDetached && storedHashes.Remove(removed)) { dList.Value = list.DetachedList; delete.ExecuteNonQuery(); }
            }
            if (!list.HasDetached) continue;
            var positions = after.Select((r, i) => (r.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
            foreach (var (id, payload) in rows.Detached[list.Name])
            {
                // Summaries carry empty detached values, which leave the stored ones unchanged.
                var hash = DocumentJson.Hash(payload);
                if (storedHashes.GetValueOrDefault(id) == hash) continue;
                Upsert(list.DetachedList, id, positions[id], payload, hash);
                storedHashes[id] = hash;
            }
            hashes[list.Name] = storedHashes;
        }
        return new()
        {
            Instance = instance, Stamp = (stamp ?? 0) + 1, Header = rows.Header, Lists = rows.Lists,
            Rows = rows.Rows, DetachedHashes = hashes
        };
    }

    public void Publish(ProjectDatabase db, DocumentEntry entry) => DocumentCache.Put(db.Path, Kind, entry);

    /// <summary>The current rows, from the cache when its stamp is current. Null when the document doesn't exist yet.</summary>
    private async Task<DocumentEntry?> EntryAsync(string directory, Guid project, CancellationToken ct)
    {
        if (!await EnsureAsync(directory, project, ct)) return null;
        var db = ProjectDatabase.For(directory);
        var entry = db.Read(s =>
        {
            var instance = ProjectDatabase.Instance(s);
            var stamp = s.Scalar("SELECT stamp FROM documents WHERE kind = $kind", ("$kind", Kind));
            if (stamp is null) return null;
            return Cached(db.Path, instance, Convert.ToInt64(stamp)) ?? ReadEntry(s, instance, Convert.ToInt64(stamp));
        }, Spec.Description);
        if (entry is not null) DocumentCache.Put(db.Path, Kind, entry);
        return entry;
    }

    private DocumentEntry? Cached(string path, string instance, long stamp) =>
        DocumentCache.Get(path, Kind) is { } cached && cached.Instance == instance && cached.Stamp == stamp ? cached : null;

    private DocumentEntry ReadEntry(ProjectDbSession s, string instance, long stamp)
    {
        string header; IReadOnlyList<string> lists;
        using (var command = s.Command("SELECT header, lists FROM documents WHERE kind = $kind", ("$kind", Kind)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new WorkspaceConflictException();
            header = reader.GetString(0);
            lists = JsonSerializer.Deserialize<List<string>>(reader.GetString(1)) ?? [];
        }
        var rows = Spec.Lists.ToDictionary(l => l.Name, _ => new List<DocumentRow>(), StringComparer.Ordinal);
        var hashes = Spec.Lists.Where(l => l.HasDetached).ToDictionary(l => l.Name, _ => new Dictionary<string, string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var detachedOwners = Spec.Lists.Where(l => l.HasDetached).ToDictionary(l => l.DetachedList, l => l.Name, StringComparer.Ordinal);
        using (var command = s.Command("SELECT list, id, CASE WHEN hash IS NULL THEN payload ELSE NULL END, hash FROM parts WHERE kind = $kind ORDER BY list, position", ("$kind", Kind)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var list = reader.GetString(0);
                if (rows.TryGetValue(list, out var target)) target.Add(new(reader.GetString(1), reader.GetString(2)));
                else if (detachedOwners.TryGetValue(list, out var owner)) hashes[owner][reader.GetString(1)] = reader.GetString(3);
            }
        return new() { Instance = instance, Stamp = stamp, Header = header, Lists = lists, Rows = rows, DetachedHashes = hashes };
    }

    private Dictionary<string, Dictionary<string, string>> ReadDetached(ProjectDbSession s, string? onlyList, IReadOnlyCollection<string>? ids)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var list in Spec.Lists.Where(l => l.HasDetached && (onlyList is null || l.Name == onlyList)))
        {
            var found = result[list.Name] = new(StringComparer.Ordinal);
            if (ids is null)
            {
                using var command = s.Command("SELECT id, payload FROM parts WHERE kind = $kind AND list = $list", ("$kind", Kind), ("$list", list.DetachedList));
                using var reader = command.ExecuteReader();
                while (reader.Read()) found[reader.GetString(0)] = reader.GetString(1);
            }
            else
                foreach (var id in ids)
                    if (s.Scalar("SELECT payload FROM parts WHERE kind = $kind AND list = $list AND id = $id", ("$kind", Kind), ("$list", list.DetachedList), ("$id", id)) is string payload)
                        found[id] = payload;
        }
        return result;
    }

    private DocumentEntry Empty(string instance, long stamp) => new()
    {
        Instance = instance, Stamp = stamp, Header = "{}", Lists = [],
        Rows = Spec.Lists.ToDictionary(l => l.Name, _ => new List<DocumentRow>(), StringComparer.Ordinal),
        DetachedHashes = new(StringComparer.Ordinal)
    };

    /// <summary>
    /// Brings this document in from its legacy JSON once. Returns false when neither a database nor
    /// legacy files exist, so reading an empty project creates nothing.
    /// </summary>
    /// <remarks>
    /// Crash safety: the rows and a <c>committed</c> migration row are written in one transaction.
    /// Before that commit the JSON is untouched and authoritative; after it the database is, and
    /// the legacy files are only moved aside, which a later open resumes. JSON is never read again
    /// once the migration row exists.
    /// </remarks>
    public async Task<bool> EnsureAsync(string directory, Guid project, CancellationToken ct)
    {
        var db = ProjectDatabase.For(directory);
        var key = db.Path + "|" + Kind;
        if (Migrated.ContainsKey(key) && db.Exists) return true;
        if (!db.Exists && !LegacyFiles.Any(directory, Spec.Legacy)) return false;
        using var gate = await ProjectFiles.LockAsync(db.Path + "#migrate-" + Kind, ct);
        if (Migrated.ContainsKey(key) && db.Exists) return true;
        var (state, files) = db.Read(s =>
        {
            using var command = s.Command("SELECT state, files FROM migrations WHERE document = $kind", ("$kind", Kind));
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetString(0), JsonSerializer.Deserialize<List<string>>(reader.GetString(1)) ?? []) : ((string?)null, new List<string>());
        }, Spec.Description);
        if (state is null)
        {
            files = LegacyFiles.Existing(directory, Spec.Legacy);
            object? legacy = null;
            if (files.Count > 0)
                legacy = Spec.ReadLegacy is { } read ? await read(directory, project, ct)
                    : await AtomicJsonFile.ReadAsync<T>(Path.Combine(directory, Spec.Legacy[0]), ct);
            var prepared = legacy is null ? null : Prepare(legacy);
            ct.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow.ToString("O");
            var json = JsonSerializer.Serialize(files);
            db.Write(s =>
            {
                var hasDocument = s.Scalar("SELECT 1 FROM documents WHERE kind = $kind", ("$kind", Kind)) is not null;
                s.Execute("INSERT INTO migrations(document, state, source, started_utc, completed_utc, files) VALUES ($kind, $state, $source, $now, NULL, $files)",
                    ("$kind", Kind), ("$state", files.Count > 0 ? "committed" : "complete"), ("$source", files.Count > 0 ? "legacy-json" : "none"), ("$now", now), ("$files", json));
                // A document that already has rows (for example from a package) keeps them.
                if (prepared is not null && !hasDocument) Apply(s, prepared, replace: true);
                return 0;
            });
            DocumentCache.Evict(db.Path, Kind);
            state = files.Count > 0 ? "committed" : "complete";
        }
        if (state == "committed")
        {
            await LegacyFiles.MoveAsync(directory, files, ct);
            db.Write(s => s.Execute("UPDATE migrations SET state = 'complete', completed_utc = $now WHERE document = $kind",
                ("$kind", Kind), ("$now", DateTimeOffset.UtcNow.ToString("O"))));
        }
        Migrated[key] = true;
        return true;
    }

    internal static void ForgetMigrations() => Migrated.Clear();
}
