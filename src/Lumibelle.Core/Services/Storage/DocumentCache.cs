using System.Text.Json;

namespace lumibelle.Services.Storage;

/// <summary>A stored document's rows at one stamp, as read from or written to its database.</summary>
internal sealed class DocumentEntry
{
    public required string Instance { get; init; }
    public required long Stamp { get; init; }
    public required string Header { get; init; }
    public required IReadOnlyList<string> Lists { get; init; }
    public required Dictionary<string, List<DocumentRow>> Rows { get; init; }
    /// <summary>Hashes of stored detached rows, per list and element id. Their payloads are read on request.</summary>
    public required Dictionary<string, Dictionary<string, string>> DetachedHashes { get; init; }
    public long LastUsed;
    private byte[]? summary;
    private Dictionary<string, Dictionary<string, int>>? index;

    private long rowBytes = -1;
    public long Bytes => (rowBytes >= 0 ? rowBytes : rowBytes = 2L * (Header.Length + Rows.Values.Sum(r => r.Sum(x => (long)x.Payload.Length + x.Id.Length)))) + (summary?.Length ?? 0);

    /// <summary>The document's JSON with detached properties replaced by their empty values. Composed once per stamp.</summary>
    public byte[] Summary(IReadOnlyList<DocumentList> lists) => summary ??= DocumentJson.Compose(Header, Lists, name =>
    {
        var list = lists.First(l => l.Name == name);
        var empty = list.EmptyDetached;
        return list.HasDetached ? Rows[name].Select(r => DocumentJson.Merge(r.Payload, empty)) : Rows[name].Select(r => r.Payload);
    }, (int)Math.Min(int.MaxValue / 2, Bytes / 2 + 1024));

    public string? Find(string list, string id)
    {
        var lookup = index ??= Rows.ToDictionary(r => r.Key, r => r.Value.Select((row, i) => (row.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal), StringComparer.Ordinal);
        return lookup.TryGetValue(list, out var rows) && rows.TryGetValue(id, out var i) ? Rows[list][i].Payload : null;
    }
}

/// <summary>
/// Process-wide cache of stored documents, keyed by database path and document kind. Each load
/// checks the database's instance and the document's stamp first, so a hit is never stale, even
/// after another store instance or a restore changed the database. Bounded by size, least recently
/// used first.
/// </summary>
internal static class DocumentCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<(string Path, string Kind), DocumentEntry> Entries = new();
    private static long clock;
    internal static long Limit { get; set; } = 384L * 1024 * 1024;

    public static DocumentEntry? Get(string path, string kind)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue((Key(path), kind), out var entry)) return null;
            entry.LastUsed = ++clock;
            return entry;
        }
    }

    public static void Put(string path, string kind, DocumentEntry entry)
    {
        lock (Gate)
        {
            entry.LastUsed = ++clock;
            Entries[(Key(path), kind)] = entry;
            var total = Entries.Values.Sum(e => e.Bytes);
            foreach (var old in Entries.OrderBy(e => e.Value.LastUsed).ToList())
            {
                if (total <= Limit || ReferenceEquals(old.Value, entry)) break;
                Entries.Remove(old.Key);
                total -= old.Value.Bytes;
            }
        }
    }

    public static void Evict(string path, string? kind = null)
    {
        lock (Gate)
            foreach (var key in Entries.Keys.Where(k => k.Path == Key(path) && (kind is null || k.Kind == kind)).ToList())
                Entries.Remove(key);
    }

    /// <summary>Forgets every document under a folder, for example a project folder that was moved or removed.</summary>
    public static void EvictDirectory(string directory)
    {
        var prefix = Key(Path.GetFullPath(directory)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        lock (Gate)
            foreach (var key in Entries.Keys.Where(k => k.Path.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                Entries.Remove(key);
    }

    internal static void Clear() { lock (Gate) Entries.Clear(); }

    private static string Key(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

    internal static T Deserialize<T>(byte[] json, string what)
    {
        try { return JsonSerializer.Deserialize<T>(json, ProjectJson.Compact) ?? throw new JsonException("The document is null."); }
        catch (JsonException e) { throw new Story.WorkspaceStoreException($"Couldn’t read {what}. Nothing was changed.", e); }
    }
}
