using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Microsoft.Extensions.Logging.Abstractions;

namespace lumibelle.Services.Projects;

public interface IProjectFolders
{
    Task<IReadOnlyList<ProjectLocation>> ListAsync(CancellationToken ct = default);
    Task<ProjectLocation?> LocationAsync(Guid project, CancellationToken ct = default);
    Task<ProjectInfo> OpenAsync(string folder, CancellationToken ct = default);
    Task<ProjectLocation> MoveOutAsync(Guid project, string parent, IProgress<ProjectPackageProgress>? progress = null, CancellationToken ct = default);
    Task RemoveAsync(Guid project, CancellationToken ct = default);
}

// Adds, moves and removes projects kept in folders outside the library. The stores
// then read and save those projects in place through ProjectLocations.
public sealed class ProjectFolders(ApplicationPaths paths, ProjectLocations locations, IProjectStore projects, TimeProvider clock,
    IAiJobStore? jobs = null, ILogger<ProjectFolders>? logger = null) : IProjectFolders
{
    private readonly ILogger log = logger ?? NullLogger<ProjectFolders>.Instance;
    // Serializes open, move and removal; the index itself has its own lock.
    private string OperationGate => locations.IndexPath + ".operation";
    internal bool CopyAcrossDrives { get; init; }

    public Task<IReadOnlyList<ProjectLocation>> ListAsync(CancellationToken ct = default) => locations.ListAsync(ct);
    public Task<ProjectLocation?> LocationAsync(Guid project, CancellationToken ct = default) => locations.FindAsync(project, ct);

    public async Task<ProjectInfo> OpenAsync(string folder, CancellationToken ct = default)
    {
        var chosen = Folder(folder);
        var root = File.Exists(Path.Combine(chosen, "project.json")) ? chosen
            : File.Exists(Path.Combine(chosen, "manifest.json")) && File.Exists(Path.Combine(chosen, "project", "project.json")) ? Path.Combine(chosen, "project")
            : throw new WorkspaceStoreException("This folder isn’t a Lumibelle project. Choose the folder that contains project.json, or an unzipped project export.");
        ProjectInfo project;
        try { project = await FileProjectStore.ReadManifestAsync(root, ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new WorkspaceStoreException("This folder’s project details (project.json) can’t be read or use an unsupported format. Nothing was changed.", e);
        }
        using var gate = await ProjectFiles.LockAsync(OperationGate, ct);
        if (await locations.FindAsync(project.Id, ct) is { } linked)
            throw new WorkspaceStoreException(ProjectLocations.PathComparer.Equals(linked.Path, root)
                ? "This folder is already in the library."
                : $"“{project.Name}” is already in the library from the folder {linked.Path}. Remove that folder from the library first to use this one. Nothing was changed.");
        var library = locations.LibraryDirectory(project.Id);
        if (Directory.Exists(library) || File.Exists(library))
            throw new WorkspaceStoreException($"This folder holds “{(await projects.GetAsync(project.Id, ct))?.Name ?? project.Name}”, which is already in the library. A project can be in the library only once. Nothing was changed.");
        locations.Lease(root);
        try
        {
            using var projectLock = await ProjectFiles.LockAsync(root, ct);
            await FileProductionStore.ReconcileFolderSetupsAsync(root, project.Id, new FileGenerationSetupStore(paths), ct);
            await locations.AddAsync(new(project.Id, root, clock.GetUtcNow()), ct);
        }
        catch { locations.Release(root); throw; }
        return project;
    }

    public async Task<ProjectLocation> MoveOutAsync(Guid project, string parent, IProgress<ProjectPackageProgress>? progress = null, CancellationToken ct = default)
    {
        var destination = Folder(parent);
        using var gate = await ProjectFiles.LockAsync(OperationGate, ct);
        var info = await projects.GetAsync(project, ct) ?? throw new WorkspaceStoreException("Project not found. Return to the project hub.");
        if (await locations.FindAsync(project, ct) is not null) throw new WorkspaceStoreException("This project is already in a folder outside the library.");
        await RequireIdleAsync(project, "move it", ct);
        var source = locations.LibraryDirectory(project);
        using var projectLock = await ProjectFiles.LockAsync(source, ct);
        CleanupMoved();
        var target = Unique(destination, FolderName(info.Name));
        var renamed = !CopyAcrossDrives && SameRoot(source, target) && TryRename(source, target);
        var hidden = renamed ? null : await CopyAsync(source, target, destination, progress, ct);
        var location = new ProjectLocation(project, target, clock.GetUtcNow());
        try { await locations.AddAsync(location, CancellationToken.None); }
        catch (Exception e)
        {
            // Put the library copy back so the project stays listed exactly once.
            log.LogError(e, "Could not record the moved project {ProjectId}", project);
            if (hidden is null) Directory.Move(target, source);
            else { Directory.Move(hidden, source); Delete(target); }
            throw;
        }
        try { locations.Lease(target); }
        catch (WorkspaceStoreException e) { log.LogWarning(e, "Could not lock the moved project folder {Folder}", target); }
        if (hidden is not null) Delete(hidden);
        return location;
    }

    public async Task RemoveAsync(Guid project, CancellationToken ct = default)
    {
        using var gate = await ProjectFiles.LockAsync(OperationGate, ct);
        var location = await locations.FindAsync(project, ct)
            ?? throw new WorkspaceStoreException("Only projects in folders outside the library can be removed from it this way.");
        await RequireIdleAsync(project, "remove it from the library", ct);
        await locations.RemoveAsync(project, ct);
        locations.Release(location.Path);
    }

    private async Task RequireIdleAsync(Guid project, string action, CancellationToken ct)
    {
        if (jobs is not null && (await jobs.ReadAsync(ct)).Jobs.Any(j => j.Target.ProjectId == project && j.LocksTarget))
            throw new WorkspaceStoreException($"This project has queued or working AI requests. Wait for them to finish, or cancel them, then {action}.");
    }

    private string Folder(string? text)
    {
        var value = text?.Trim().Trim('"') ?? "";
        if (value.Length == 0 || !Path.IsPathFullyQualified(value))
            throw new WorkspaceStoreException("Enter the folder’s full path, starting with its drive or root folder.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (!Directory.Exists(full)) throw new WorkspaceStoreException("That folder doesn’t exist or can’t be reached.");
        if (Within(full, paths.Projects) || Within(full, paths.Data) || Within(paths.Projects, full) || Within(paths.Data, full))
            throw new WorkspaceStoreException("Choose a folder outside this library’s own folders.");
        return full;
    }

    private static bool Within(string path, string folder)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return ProjectLocations.PathComparer.Equals(path, folder) || path.StartsWith(folder + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool SameRoot(string a, string b) => ProjectLocations.PathComparer.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b));

    private static bool TryRename(string source, string target)
    {
        try { Directory.Move(source, target); return true; }
        // Another drive mounted inside this folder tree: copy instead.
        catch (IOException e) when ((e.HResult & 0xffff) is 17 or 18 && Directory.Exists(source) && !Directory.Exists(target)) { return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw Busy(e); }
    }

    private static WorkspaceStoreException Busy(Exception e) =>
        new("Couldn’t move the project folder. Close anything using its files, such as a media player or file browser window, and try again. Nothing was moved.", e);

    // Copies, flushes and checks for changes before publishing the copy by a rename in
    // its destination. The library copy is then renamed aside, so no crash lists both.
    private async Task<string> CopyAsync(string source, string target, string destination, IProgress<ProjectPackageProgress>? progress, CancellationToken ct)
    {
        var before = Snapshot(source);
        var staging = Path.Combine(destination, $".lumibelle-moving-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            long copied = 0; var count = 0;
            foreach (var directory in Directory.EnumerateDirectories(source, "*", Options)) Directory.CreateDirectory(Path.Combine(staging, Path.GetRelativePath(source, directory)));
            foreach (var relative in before.Keys)
            {
                ct.ThrowIfCancellationRequested();
                await using (var input = new FileStream(Path.Combine(source, relative), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 131072, true))
                await using (var output = new FileStream(Path.Combine(staging, relative), FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                {
                    await input.CopyToAsync(output, ct);
                    output.Flush(flushToDisk: true);
                }
                copied += before[relative].Length; count++;
                progress?.Report(new("Copying project files…", copied, count));
            }
            if (!Same(before, Snapshot(source)))
                throw new WorkspaceStoreException("The project changed while it was being copied. Nothing was moved. Close its other windows and try again.");
            Directory.Move(staging, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Delete(staging);
            throw new WorkspaceStoreException("Couldn’t copy the project to that folder. Check its free space and permissions. Nothing was moved.", e);
        }
        catch { Delete(staging); throw; }
        var hidden = Path.Combine(paths.Projects, $".moved-{Guid.NewGuid():N}");
        try { Directory.Move(source, hidden); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Delete(target); throw Busy(e); }
        return hidden;
    }

    private static readonly EnumerationOptions Options = new() { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
    private sealed record FileState(long Length, DateTime Written);

    private static Dictionary<string, FileState> Snapshot(string root)
    {
        var files = new Dictionary<string, FileState>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", Options))
        {
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new WorkspaceStoreException("The project folder contains a linked file or folder, which can’t be moved. Nothing was moved.");
            if ((info.Attributes & FileAttributes.Directory) == 0) files.Add(Path.GetRelativePath(root, path), new(info.Length, info.LastWriteTimeUtc));
        }
        return files;
    }

    private static bool Same(Dictionary<string, FileState> a, Dictionary<string, FileState> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && other == pair.Value);

    private static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })];

    public static string FolderName(string name)
    {
        var chars = name.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? ' ' : c).ToArray();
        var folder = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');
        if (folder.Length > 80) folder = folder[..80].TrimEnd('.', ' ');
        return folder.Length == 0 || folder.StartsWith('.') || Reserved.Contains(folder.Split('.')[0], StringComparer.OrdinalIgnoreCase) ? "Lumibelle project" : folder;
    }

    private static string Unique(string parent, string name)
    {
        for (var number = 1; ; number++)
        {
            var path = Path.Combine(parent, number == 1 ? name : $"{name} {number}");
            if (!Directory.Exists(path) && !File.Exists(path)) return path;
        }
    }

    // Removes library copies left by an earlier move that could not delete them.
    private void CleanupMoved()
    {
        if (!Directory.Exists(paths.Projects)) return;
        foreach (var folder in Directory.EnumerateDirectories(paths.Projects, ".moved-*"))
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0) Delete(folder);
    }

    private void Delete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning(e, "Could not delete {Folder}", folder); }
    }
}
