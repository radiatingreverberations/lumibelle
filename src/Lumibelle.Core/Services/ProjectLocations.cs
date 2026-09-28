using lumibelle.Services.Story;

namespace lumibelle.Services;

public sealed record ProjectLocation(Guid ProjectId, string Path, DateTimeOffset AddedUtc);

// Projects kept in folders outside the library. A project in the library's own
// Projects/<id> folder has no record; one listed here is read and saved in its folder.
public sealed class ProjectLocations(ApplicationPaths paths, bool lease = false) : IDisposable
{
    private sealed record Index(int Version, List<ProjectLocation> Projects);
    private sealed record Cached(DateTime Written, long Length, IReadOnlyList<ProjectLocation> Locations);
    public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly Dictionary<string, FileStream> _leases = new(PathComparer);
    private Cached? _cache;
    internal string IndexPath => Path.Combine(paths.Data, "project-locations.json");
    public string LibraryDirectory(Guid id) => Path.Combine(paths.Projects, id.ToString("D"));

    public async Task<IReadOnlyList<ProjectLocation>> ListAsync(CancellationToken ct = default)
    {
        var file = new FileInfo(IndexPath);
        if (!file.Exists) return [];
        if (_cache is { } cached && cached.Written == file.LastWriteTimeUtc && cached.Length == file.Length) return cached.Locations;
        var locations = await ReadAsync(ct);
        _cache = new(file.LastWriteTimeUtc, file.Length, locations);
        return locations;
    }

    public async Task<ProjectLocation?> FindAsync(Guid id, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(l => l.ProjectId == id);

    public async Task<string> DirectoryAsync(Guid id, CancellationToken ct = default) =>
        (await FindAsync(id, ct))?.Path ?? LibraryDirectory(id);

    internal async Task AddAsync(ProjectLocation location, CancellationToken ct) => await ChangeAsync(list =>
    {
        if (list.Any(l => l.ProjectId == location.ProjectId)) throw new WorkspaceStoreException("This project is already in the library.");
        list.Add(location);
    }, ct);

    internal async Task RemoveAsync(Guid id, CancellationToken ct) => await ChangeAsync(list => list.RemoveAll(l => l.ProjectId == id), ct);

    private async Task ChangeAsync(Action<List<ProjectLocation>> change, CancellationToken ct)
    {
        using var gate = await ProjectFiles.LockAsync(IndexPath, ct);
        var list = (await ReadAsync(ct)).ToList();
        change(list);
        await AtomicJsonFile.WriteAsync(IndexPath, new Index(1, list), ct);
        _cache = null;
    }

    private async Task<IReadOnlyList<ProjectLocation>> ReadAsync(CancellationToken ct)
    {
        var index = await AtomicJsonFile.ReadAsync<Index>(IndexPath, ct);
        if (index is null) return [];
        if (index.Version != 1 || index.Projects is null || index.Projects.Any(l => l is null || l.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(l.Path) || !Path.IsPathFullyQualified(l.Path))
            || index.Projects.Select(l => l.ProjectId).Distinct().Count() != index.Projects.Count)
            throw new WorkspaceStoreException("The list of project folders outside the library could not be read. It has not been replaced.");
        return index.Projects.ToArray();
    }

    // Like the library's own lock, a lock file in the folder keeps a second Lumibelle
    // process from editing the same project. Held until removal or shutdown.
    internal void Lease(string directory)
    {
        if (!lease) return;
        lock (_leases)
        {
            if (_leases.ContainsKey(directory)) return;
            FileStream? acquired = null;
            try
            {
                acquired = new FileStream(Path.Combine(directory, ".lumibelle.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                // An internal project is owned through its library root. Check ancestors
                // after taking the project lease; startup performs the inverse check
                // while holding the root lease, so neither startup order can race past it.
                for (var parent = Path.GetDirectoryName(Path.GetFullPath(directory)); parent is not null; parent = Path.GetDirectoryName(parent))
                    CheckAvailable(parent);
                _leases.Add(directory, acquired);
            }
            catch (IOException e) when (e is not (DirectoryNotFoundException or FileNotFoundException))
            {
                acquired?.Dispose();
                throw new WorkspaceStoreException($"The project folder {directory} is open in another Lumibelle library. Close it there first.", e);
            }
            catch { acquired?.Dispose(); throw; }
        }
    }

    internal static void CheckAvailable(string directory)
    {
        var path = Path.Combine(directory, ".lumibelle.lock");
        if (!File.Exists(path)) return;
        try { using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    internal void Release(string directory)
    {
        lock (_leases) if (_leases.Remove(directory, out var stream)) stream.Dispose();
    }

    public void Dispose()
    {
        lock (_leases) { foreach (var stream in _leases.Values) stream.Dispose(); _leases.Clear(); }
    }
}
