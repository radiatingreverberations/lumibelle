using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public sealed partial class FileShotStore
{
    internal static void ValidateArchiveRemoval(ShotTake take)
    {
        if (take.FrameArchiveRemoval is not { } removal) return;
        if (removal.RequestedUtc == default || removal.CompletedUtc < removal.RequestedUtc || removal.Files is null ||
            removal.Files.Count == 0 || removal.Files.Count > 16 || removal.Files.Any(f => f is null || f.Bytes < 0 || !ArchiveFileName(f.FileName)) ||
            removal.Files.Select(f => f.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != removal.Files.Count || take.Frames.Count != 0)
            throw new WorkspaceStoreException("Invalid frame archive cleanup record.");
    }
    private static bool ArchiveFileName(string file) => Enumerable.Range(0, 16).Any(i => file == LosslessFrameArchive.FileName(i));
    internal static string ArchiveDirectory(string projectDirectory, ShotTake take)
    {
        if (take.Directory != take.Id.ToString("D")) throw new WorkspaceStoreException("Invalid take directory.");
        var path = Path.GetFullPath(projectDirectory);
        foreach (var segment in new[] { "shots", "takes", take.Directory })
        {
            path = Path.Combine(path, segment);
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new WorkspaceStoreException("Archive cleanup does not follow linked media directories.");
        }
        return path;
    }
    internal static string ArchivePath(string directory, string file)
    {
        if (!ArchiveFileName(file)) throw new WorkspaceStoreException("Invalid frame archive filename.");
        var path = Path.Combine(directory, file);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new WorkspaceStoreException("Archive cleanup does not delete linked media files.");
        return path;
    }
    public async Task<ShotDocument> RemoveFrameArchivesAsync(Guid projectId, IReadOnlyCollection<Guid> takeIds, long expectedRevision, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var document = await Read(dir, projectId, ct); Revision(document, expectedRevision);
        var selected = document.Takes.Where(t => takeIds.Contains(t.Id)).ToArray();
        if (takeIds.Count == 0 || selected.Length != takeIds.Count || selected.Any(t => !t.HasLosslessFrames && t.FrameArchiveRemoval?.CompletedUtc is not null))
            throw new WorkspaceStoreException("The selected archives changed. Refresh storage cleanup and try again.");
        var media = mediaTools ?? new ProductionMediaTools();
        foreach (var take in selected)
        {
            if (!take.HasLosslessFrames && take.FrameArchiveRemoval is null) throw new WorkspaceStoreException("This take has no lossless archive.");
            var folder = ArchiveDirectory(dir, take); var video = Path.Combine(folder, "video.mp4");
            // Ensure frame access remains possible before accepting an irreversible removal.
            var info = await media.VideoInfoAsync(video, take.Snapshot.Settings, ct);
            if (info.Width != take.Width || info.Height != take.Height || info.Frames != take.FrameCount || info.Fps != take.Fps)
                throw new WorkspaceStoreException("The saved MP4 does not match this take. Its lossless archive was retained.");
            await media.ExtractFrameAsync(video, 0, take.Width, take.Height, take.Snapshot.Settings, ct);
            foreach (var file in take.FrameArchiveRemoval?.Files.Select(f => f.FileName) ?? take.Frames.Select(f => f.FileName).Distinct())
                _ = ArchivePath(folder, file);
        }
        foreach (var take in selected.Where(t => t.FrameArchiveRemoval is null))
        {
            take.FrameArchiveRemoval = new(clock.GetUtcNow(), take.Frames.DistinctBy(f => f.FileName).Select(f => new FrameArchiveFile(f.FileName, f.Bytes)).ToArray());
            take.Frames = [];
        }
        // Persist the user's intent and MP4 fallback first. A disk-full failure deletes nothing.
        document = await Publish(dir, document, ct);
        return await FinishArchiveCleanup(dir, document, takeIds, ct);
    }
    public async Task<ShotDocument> ResumeFrameArchiveCleanupAsync(Guid projectId, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var document = await Read(dir, projectId, ct);
        var ids = AllTakes(document).Where(t => t.FrameArchiveRemoval is { CompletedUtc: null }).Select(t => t.Id).ToArray();
        return ids.Length == 0 ? document : await FinishArchiveCleanup(dir, document, ids, ct);
    }
    private static IEnumerable<ShotTake> AllTakes(ShotDocument doc) => doc.Takes.Concat(doc.Trash.Where(t => t.Take is not null).Select(t => t.Take!));
    private async Task<ShotDocument> FinishArchiveCleanup(string dir, ShotDocument document, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        foreach (var take in AllTakes(document).Where(t => ids.Contains(t.Id) && t.FrameArchiveRemoval is { CompletedUtc: null }))
        {
            ct.ThrowIfCancellationRequested();
            var removal = take.FrameArchiveRemoval!;
            try
            {
                var folder = ArchiveDirectory(dir, take);
                // A missing MP4 after interruption needs human attention, not further deletion.
                if (!File.Exists(Path.Combine(folder, "video.mp4"))) throw new WorkspaceStoreException("The MP4 is missing; archive cleanup is paused.");
                foreach (var file in removal.Files) { ct.ThrowIfCancellationRequested(); File.Delete(ArchivePath(folder, file.FileName)); }
                take.Bytes = Math.Max(0, take.Bytes - removal.Files.Sum(f => f.Bytes));
                take.FrameArchiveRemoval = removal with { CompletedUtc = clock.GetUtcNow(), Error = null };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or WorkspaceStoreException)
            { take.FrameArchiveRemoval = removal with { Error = "Archive cleanup needs a retry: " + e.Message }; }
        }
        return await Publish(dir, document, ct);
    }
}
