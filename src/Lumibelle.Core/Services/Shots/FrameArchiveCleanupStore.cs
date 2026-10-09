using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public sealed record FrameArchiveRow(Guid ProjectId, string ProjectName, long Revision, Guid TakeId, string Title,
    long Bytes, int FrameCount, bool SelectedTake, bool Pending, string? Issue);
public sealed record FrameArchiveLibrary(IReadOnlyList<FrameArchiveRow> Items, IReadOnlyList<ImageTrashIssue> Issues);
public sealed record FrameArchiveChange(int Completed, long ReclaimedBytes, IReadOnlyList<ImageTrashIssue> Issues);
public interface IFrameArchiveCleanupStore
{
    Task<FrameArchiveLibrary> ListAsync(CancellationToken ct = default);
    Task<FrameArchiveChange> RemoveAsync(IReadOnlyList<FrameArchiveRow> captured, CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
}
public sealed class FrameArchiveCleanupStore(ProjectFiles projects, IShotStore shots) : IFrameArchiveCleanupStore
{
    public async Task<FrameArchiveLibrary> ListAsync(CancellationToken ct = default)
    {
        List<FrameArchiveRow> rows = []; List<ImageTrashIssue> issues = [];
        var library = await projects.ListProjectsAsync(ct);
        foreach (var project in library.Projects)
        {
            try
            {
                var doc = await shots.LoadAsync(project.Id, ct); var dir = await projects.DirectoryAsync(project.Id, ct);
                foreach (var take in doc.Takes.Where(t => t.HasAnyLosslessFrames || t.FrameArchiveRemoval is { CompletedUtc: null }))
                {
                    long bytes = 0; string? issue = take.FrameArchiveRemoval?.Error;
                    try
                    {
                        var folder = FileShotStore.ArchiveDirectory(dir, take);
                        foreach (var file in take.FrameArchiveRemoval?.Files.Select(f => f.FileName) ?? TakeBundles.Contexts(take).SelectMany(c => c.Take.Frames.Select(f => c.Prefix + f.FileName)).Distinct())
                        {
                            var path = FileShotStore.ArchivePath(folder, file);
                            if (File.Exists(path)) bytes += new FileInfo(path).Length;
                        }
                        if (!File.Exists(Path.Combine(folder, "video.mp4"))) issue = "MP4 missing. Restore it before removing frames.";
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or WorkspaceStoreException) { issue = e.Message; }
                    rows.Add(new(project.Id, project.Name, doc.Revision, take.Id, take.Snapshot.Shot.Title + " · Take " + take.Candidate,
                        bytes, take.FrameCount, doc.Shots.Any(s => s.SelectedTakeId == take.Id), take.FrameArchiveRemoval is not null, issue));
                }
            }
            catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or IOException or UnauthorizedAccessException)
            { issues.Add(new(project.Id, e.Message)); }
        }
        return new(rows.OrderByDescending(r => r.Bytes).ThenBy(r => r.Title).ToArray(), issues);
    }
    public async Task<FrameArchiveChange> RemoveAsync(IReadOnlyList<FrameArchiveRow> captured, CancellationToken ct = default)
    {
        int completed = 0; long reclaimed = 0; List<ImageTrashIssue> issues = [];
        foreach (var group in captured.GroupBy(r => r.ProjectId))
        {
            try
            {
                if (group.Select(r => r.Revision).Distinct().Count() != 1) throw new WorkspaceConflictException();
                var doc = await shots.RemoveFrameArchivesAsync(group.Key, group.Select(r => r.TakeId).ToArray(), group.First().Revision, ct);
                foreach (var row in group)
                {
                    var removal = doc.Takes.Single(t => t.Id == row.TakeId).FrameArchiveRemoval!;
                    if (removal.CompletedUtc is not null) { completed++; reclaimed += row.Bytes; }
                    else issues.Add(new(row.ProjectId, row.Title + ": " + removal.Error));
                }
            }
            catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or IOException or UnauthorizedAccessException)
            { issues.Add(new(group.Key, group.First().ProjectName + ": " + e.Message)); }
        }
        return new(completed, reclaimed, issues);
    }
    public async Task ResumeAsync(CancellationToken ct = default)
    {
        List<Exception> errors = [];
        foreach (var project in (await projects.ListProjectsAsync(ct)).Projects)
        {
            try { await shots.ResumeFrameArchiveCleanupAsync(project.Id, ct); }
            catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or IOException or UnauthorizedAccessException) { errors.Add(e); }
        }
        if (errors.Count > 0) throw new AggregateException("Some archive removals could not resume.", errors);
    }
}
public sealed class FrameArchiveCleanupRecovery(IFrameArchiveCleanupStore store, ILogger<FrameArchiveCleanupRecovery> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await store.ResumeAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogWarning(e, "Previously requested frame archive cleanup will retry later."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
public static class StorageSize
{
    public static string Format(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.##} GiB"
        : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.##} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.##} KiB" : $"{bytes} B";
}
