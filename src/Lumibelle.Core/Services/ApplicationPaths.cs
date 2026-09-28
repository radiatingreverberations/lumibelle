using Microsoft.Extensions.Options;
namespace lumibelle.Services;

public sealed record ApplicationPaths
{
    public string Data { get; }
    public string Projects { get; }
    public string Settings => Path.Combine(Data, "ai-settings.json");
    public string Jobs => Path.Combine(Data, "ai-jobs");
    public string Logs => Path.Combine(Data, "logs");
    public string Temporary => Path.Combine(Data, "temp");
    public string Keys => Path.Combine(Data, "keys");
    public ApplicationPaths(string data, string? projects = null)
    { Data = Path.GetFullPath(data); Projects = Path.GetFullPath(projects ?? Path.Combine(Data, "Projects")); }
    public static ApplicationPaths Legacy(IHostEnvironment environment, ProjectStorageOptions? options = null) =>
        new(Path.Combine(environment.ContentRootPath, "App_Data"), options is null ? null : Path.GetFullPath(options.RootDirectory, environment.ContentRootPath));
    public static ApplicationPaths UserDefault() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumibelle"));
}

// Acquired before workers start. A file handle, not a stale PID file, owns the lease.
public sealed class WorkspaceOwnership(ApplicationPaths paths) : IHostedService, IDisposable
{
    private readonly List<FileStream> leases = [];
    public Task StartAsync(CancellationToken ct)
    {
        try
        {
            foreach (var directory in new[] { paths.Data, paths.Projects }.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Order())
            {
                ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(directory);
                leases.Add(new FileStream(Path.Combine(directory, ".lumibelle.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            // A project may have been linked into another library while this one was
            // closed. Hold the root locks before probing projects: new links check
            // these roots after acquiring their own lease.
            foreach (var directory in Directory.EnumerateDirectories(paths.Projects))
            {
                ct.ThrowIfCancellationRequested();
                if (Guid.TryParseExact(Path.GetFileName(directory), "D", out _)) ProjectLocations.CheckAvailable(directory);
            }
            return Task.CompletedTask;
        }
        catch (Exception e) { Dispose(); throw new Story.WorkspaceStoreException("This library is already open in another Lumibelle process, or its folder is not writable. Close the other app or choose another library.", e); }
    }
    public Task StopAsync(CancellationToken ct) { Dispose(); return Task.CompletedTask; }
    public void Dispose() { foreach (var lease in leases) lease.Dispose(); leases.Clear(); }
}
