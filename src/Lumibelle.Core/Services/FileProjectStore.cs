using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;
using Microsoft.Extensions.Options;

namespace lumibelle.Services;

public sealed class FileProjectStore : IProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _root;
    private readonly ProjectLocations _locations;
    private readonly TimeProvider _clock;
    private readonly ILogger<FileProjectStore> _logger;

    public FileProjectStore(IOptions<ProjectStorageOptions> options, IHostEnvironment environment,
        TimeProvider clock, ILogger<FileProjectStore> logger)
        : this(ApplicationPaths.Legacy(environment, options.Value), clock, logger) { }

    public FileProjectStore(ApplicationPaths paths, TimeProvider clock, ILogger<FileProjectStore> logger, ProjectLocations? locations = null)
    {
        _root = paths.Projects;
        _locations = locations ?? new(paths);
        _clock = clock;
        _logger = logger;
    }

    public async Task<ProjectLibrary> ListAsync(CancellationToken cancellationToken = default)
    {
        var external = await ExternalAsync(cancellationToken);
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(_root);
        }
        catch (DirectoryNotFoundException) when (!File.Exists(_root))
        {
            directories = [];
        }
        catch (Exception exception) when (IsStorageError(exception))
        {
            throw StorageError("Couldn’t read your project library. Check its location and folder permissions, then try again.", exception);
        }

        List<ProjectInfo> projects = [];
        List<ProjectReadIssue> issues = [];
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Staging directories and unrelated folders aren't published projects.
            if (!Guid.TryParseExact(Path.GetFileName(directory), "D", out var id))
                continue;
            // A project moved to an outside folder is read from there. Never list a stale copy.
            if (external.Any(location => location.ProjectId == id))
                continue;

            try
            {
                projects.Add(await ReadManifestAsync(directory, id, cancellationToken));
            }
            catch (Exception exception) when (IsStorageError(exception) || exception is JsonException)
            {
                _logger.LogWarning(exception, "Could not read project {ProjectId}", id);
                issues.Add(new(id, "This project’s manifest is missing, unreadable, or uses an unsupported format."));
            }
        }
        foreach (var location in external)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                projects.Add(await ReadExternalAsync(location, cancellationToken));
            }
            catch (WorkspaceStoreException exception)
            {
                _logger.LogWarning(exception, "Could not read project {ProjectId} in {Folder}", location.ProjectId, location.Path);
                issues.Add(new(location.ProjectId, exception.Message));
            }
        }

        return new(projects.OrderByDescending(project => project.CreatedUtc).ThenBy(project => project.Id).ToArray(), issues);
    }

    public async Task<ProjectInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if ((await ExternalAsync(cancellationToken)).FirstOrDefault(location => location.ProjectId == id) is { } external)
        {
            try { return await ReadExternalAsync(external, cancellationToken); }
            catch (WorkspaceStoreException exception) { throw StorageError(exception.Message, exception); }
        }
        try
        {
            return await ReadManifestAsync(ProjectDirectory(id), id, cancellationToken);
        }
        catch (DirectoryNotFoundException) when (!File.Exists(_root))
        {
            return null;
        }
        catch (Exception exception) when (IsStorageError(exception) || exception is JsonException)
        {
            throw StorageError("Couldn’t open this project. Its manifest may be missing, unreadable, or use an unsupported format.", exception);
        }
    }

    public async Task<ProjectInfo> CreateAsync(CreateProjectRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ValidationException("Give your project a name.");

        cancellationToken.ThrowIfCancellationRequested();
        var project = new ProjectInfo
        {
            SchemaVersion = ProjectInfo.CurrentSchemaVersion,
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            CreatedUtc = _clock.GetUtcNow()
        };

        var staging = Path.Combine(_root, $".creating-{project.Id:D}");
        try
        {
            Directory.CreateDirectory(staging);
            await using (var stream = new FileStream(Path.Combine(staging, "project.json"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, project, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Rename within the same parent: readers see either no project or a complete manifest.
            DurableFile.FlushDirectory(staging);
            await DurableFile.MoveDirectoryAsync(staging, ProjectDirectory(project.Id), cancellationToken);
            return project;
        }
        catch (Exception exception) when (IsStorageError(exception))
        {
            throw StorageError("Couldn’t save your project. Check the library folder’s permissions and available disk space, then try again.", exception);
        }
        finally
        {
            // Only remove our unpublished file and empty staging directory.
            try
            {
                if (Directory.Exists(staging))
                {
                    File.Delete(Path.Combine(staging, "project.json"));
                    Directory.Delete(staging);
                }
            }
            catch (Exception exception) when (IsStorageError(exception))
            {
                _logger.LogWarning(exception, "Could not clean up unpublished project {ProjectId}", project.Id);
            }
        }
    }

    public async Task<ProjectInfo> UpdateAsync(ProjectInfo original, UpdateProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.VideoAspect is not null and not ("16:9" or "9:16" or "1:1"))
            throw new ValidationException("Choose landscape, portrait, or square for the project video aspect.");
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ValidationException("Give your project a name.");

        var directory = await _locations.DirectoryAsync(original.Id, cancellationToken);
        var path = Path.Combine(directory, "project.json");
        using var lease = await ProjectFiles.LockAsync(directory, cancellationToken);
        try
        {
            var current = await ReadManifestAsync(directory, original.Id, cancellationToken);
            if (current != original)
                throw new ProjectStoreException("This project’s details changed since you opened them. Copy your edits, cancel, refresh the project, and try again.");

            var updated = current with
            {
                Name = request.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                VideoAspect = request.VideoAspect ?? current.VideoAspect
            };
            if (updated.Name != current.Name)
                await FileProjectRoutes.SynchronizeAsync(_root, this, cancellationToken);
            if (updated != current)
                await AtomicJsonFile.WriteAsync(path, updated, cancellationToken);
            return updated;
        }
        catch (WorkspaceStoreException exception)
        {
            throw StorageError(exception.Message, exception);
        }
        catch (Exception exception) when (IsStorageError(exception) || exception is JsonException)
        {
            throw StorageError("Couldn’t update this project. Check that it still exists and its manifest is readable. Your changes have not been saved.", exception);
        }
    }

    private static async Task<ProjectInfo> ReadManifestAsync(string directory, Guid id, CancellationToken cancellationToken)
    {
        var project = await ReadManifestAsync(directory, cancellationToken);
        if (project.Id != id)
            throw new JsonException("Invalid or unsupported project manifest.");
        return project;
    }

    internal static async Task<ProjectInfo> ReadManifestAsync(string directory, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(Path.Combine(directory, "project.json"),
            FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        var project = await JsonSerializer.DeserializeAsync<ProjectInfo>(stream, JsonOptions, cancellationToken);
        if (project is null || project.SchemaVersion != ProjectInfo.CurrentSchemaVersion ||
            project.Id == Guid.Empty || string.IsNullOrWhiteSpace(project.Name) ||
            project.CreatedUtc == default || project.CreatedUtc.Offset != TimeSpan.Zero || project.VideoAspect is not ("16:9" or "9:16" or "1:1"))
        {
            throw new JsonException("Invalid or unsupported project manifest.");
        }
        return project;
    }

    private async Task<IReadOnlyList<ProjectLocation>> ExternalAsync(CancellationToken cancellationToken)
    {
        try { return await _locations.ListAsync(cancellationToken); }
        catch (WorkspaceStoreException exception) { throw StorageError(exception.Message, exception); }
    }

    private async Task<ProjectInfo> ReadExternalAsync(ProjectLocation location, CancellationToken cancellationToken)
    {
        try
        {
            _locations.Lease(location.Path);
            return await ReadManifestAsync(location.Path, location.ProjectId, cancellationToken);
        }
        catch (Exception exception) when (IsStorageError(exception) || exception is JsonException)
        {
            throw new WorkspaceStoreException($"The project folder {location.Path} is missing, can’t be read, or no longer holds this project. Reconnect its drive, or remove it from the library.", exception);
        }
    }

    private string ProjectDirectory(Guid id) => Path.Combine(_root, id.ToString("D"));

    private ProjectStoreException StorageError(string message, Exception exception)
    {
        _logger.LogError(exception, "Project storage operation failed in {Root}", _root);
        return new(message, exception);
    }

    private static bool IsStorageError(Exception exception) => exception is IOException or UnauthorizedAccessException;
}
