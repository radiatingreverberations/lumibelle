using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace lumibelle.Services.Story;

public sealed class ProjectFiles(ApplicationPaths paths, IProjectStore projects, ILogger<SqliteMediaIndex>? indexLogger = null, ProjectLocations? locations = null)
{
    // Shared by every store instance, including fresh instances reading the same library.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    public ProjectFiles(IOptions<ProjectStorageOptions> options, IHostEnvironment environment, IProjectStore projects) : this(ApplicationPaths.Legacy(environment, options.Value), projects) { }
    internal ProjectLocations Locations { get; } = locations ?? new(paths);
    internal SqliteMediaIndex MediaIndex { get; } = new(paths, indexLogger);
    public string SharedAssetsDirectory => Path.Combine(paths.Data, "shared-assets");
    public string AiJobsDirectory => Path.Combine(paths.Data, "ai-jobs");
    public Task<Models.ProjectLibrary> ListProjectsAsync(CancellationToken ct) => projects.ListAsync(ct);
    public async Task<string> DirectoryAsync(Guid id, CancellationToken ct)
    {
        if (await projects.GetAsync(id, ct) is null)
            throw new WorkspaceStoreException("Project not found. Return to the project hub.");
        return await Locations.DirectoryAsync(id, ct);
    }

    public static async Task<IDisposable> LockAsync(string path, CancellationToken ct)
    {
        var gate = Gates.GetOrAdd(Path.GetFullPath(path), _ => new(1, 1));
        await gate.WaitAsync(ct);
        return new Lease(gate);
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }
}

// Publishing a file or folder by rename is only crash-safe once its contents are on
// disk: an operating system crash or power loss can keep the rename but lose data that
// was still cached, leaving zero-filled media. Flush just before the rename.
public static class DurableFile
{
    public static async Task MoveDirectoryAsync(string source, string destination, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++) {
            ct.ThrowIfCancellationRequested();
            try { Directory.Move(source, destination); return; }
            // Windows scanners can briefly hold newly written media or manifests open.
            catch (IOException e) when (OperatingSystem.IsWindows() && attempt < 3 &&
                (e.HResult & 0xffff) is 5 or 32 or 33 && Directory.Exists(source) && !Directory.Exists(destination))
            { await Task.Delay(50 * (attempt + 1), ct); }
        }
    }
    public static void Flush(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.Flush(flushToDisk: true);
    }

    public static void FlushDirectory(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) Flush(file);
    }
}

public static class AtomicJsonFile
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    4096, FileOptions.Asynchronous);
                return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct)
                    ?? throw new JsonException("The document is null.");
            }
            catch (FileNotFoundException) { return default; }
            catch (Exception e) when (OperatingSystem.IsWindows() && attempt < 4 && IsBriefWindowsDenial(e))
            {
                // A concurrent atomic publication can briefly deny opening the file on
                // Windows, which is routine for frequently rewritten progress. Retry the
                // read; corrupt JSON and persistent denials still reach the caller.
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (1 << attempt)), ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new WorkspaceStoreException($"Couldn’t read {Describe(path)}. Nothing was changed.", e)
                    { Data = { ["path"] = path } };
            }
        }
    }

    // Name stored documents the way people know them; the path stays on the exception.
    private static string Describe(string path) => Path.GetFileName(path) switch
    {
        "project.json" => "this project’s details",
        "script.json" => "the script",
        "script-assistant.json" => "the script assistant history",
        "assets.json" => "the asset library",
        "shots.json" => "the shot list",
        "production.json" => "shot prompts and generation setups",
        "cut.json" => "the cut",
        "dubbing.json" or "languages.json" => "the language versions",
        "ai-preferences.json" => "this project’s AI settings",
        "ai-settings.json" => "the AI settings",
        "generation-setups.json" => "the generation presets",
        "queue.json" => "the AI activity list",
        _ when path.Replace('\\', '/').Contains("/ai-jobs/", StringComparison.OrdinalIgnoreCase) => "a saved AI request",
        _ => "some saved project data"
    };

    // Sharing or lock violation, or access denied while a replaced file is pending deletion.
    private static bool IsBriefWindowsDenial(Exception e) =>
        e is UnauthorizedAccessException || e is IOException && (e.HResult & 0xffff) is 32 or 33;

    public static async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, ct);
                // Reach the disk before the rename is journaled. Otherwise an operating system
                // crash or power loss can publish the name over contents still only cached,
                // leaving a zero-filled document.
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try { File.Move(temporary, path, overwrite: true); break; }
                catch (Exception e) when (OperatingSystem.IsWindows() && attempt < 4 && File.Exists(path) && IsBriefWindowsDenial(e))
                {
                    // A reader or scanner can briefly deny the atomic rename on
                    // Windows. Retry the same prepared file, never the operation
                    // which produced it. Persistent failures still reach the caller.
                    await Task.Delay(TimeSpan.FromMilliseconds(20 * (1 << attempt)), ct);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new WorkspaceStoreException("Couldn’t save. Check folder permissions and free disk space. Your previous saved version is intact.", e);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* An unpublished temporary file is safe to leave. */ }
        }
    }
}
