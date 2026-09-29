using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public sealed partial class FileReferenceVideoStore
{
    internal const string ArchiveRemovalFile = "lossless-removal.json";
    // Mirrors take archive removal: check the MP4 fallback, keep what depends on the archive,
    // record the intent, then delete only the listed segments. The index stays.
    public async Task<long> RemoveArchiveAsync(Guid project, Guid media, IReadOnlyCollection<ReelFrameIdentity> keep, H3Settings settings, CancellationToken ct = default)
    {
        var record = await Record(project, media, ct);
        var directory = await DirectoryAsync(project, media, ct);
        using var gate = await ProjectFiles.LockAsync(Path.Combine(directory, "frame-archive.json"), ct);
        var archive = await ArchiveAsync(directory, record, ct);
        if (archive is null) return 0;
        if (archive.Files.Where((f, i) => f.FileName != LosslessFrameArchive.FileName(i)).Any()) throw new WorkspaceStoreException("The reel archive contains an invalid segment name.");
        var folder = Path.Combine(directory, "lossless");
        foreach (var path in new[] { directory, folder }.Where(Path.Exists))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new WorkspaceStoreException("Archive removal does not follow linked media folders.");
        var recordPath = Path.Combine(directory, ArchiveRemovalFile);
        var removal = await AtomicJsonFile.ReadAsync<ReelArchiveRemoval>(recordPath, ct);
        if (removal?.CompletedUtc is not null) return 0;
        if (removal is null)
        {
            var present = archive.Files.Count(f => File.Exists(Path.Combine(folder, f.FileName)));
            if (present == 0) return 0; // A compact package already left the segments out.
            if (present != archive.Files.Count) throw new WorkspaceStoreException("This reel's lossless archive is incomplete. It was kept.");
            await ProbeVideoAsync(project, record, directory, settings, ct);
            var source = Hash(archive);
            var frames = keep.Where(f => f.MediaId == media && f.Source == source).Distinct().OrderBy(f => f.Index).ToArray();
            foreach (var batch in frames.Chunk(128)) await PrepareFramesAsync(project, batch, settings, ct);
            removal = new(DateTimeOffset.UtcNow, archive.Files, frames.Select(FrameFileName).ToArray());
            // Persist the intent before deleting. A failed save deletes nothing.
            await AtomicJsonFile.WriteAsync(recordPath, removal, ct);
        }
        if (removal.Keyframes.Any(name => !File.Exists(Path.Combine(directory, name))))
            throw new WorkspaceStoreException("A keyframe picture from this reel's lossless frames is missing. The remaining archive was kept.");
        long reclaimed = 0;
        foreach (var file in removal.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(folder, file.FileName);
            if (!File.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new WorkspaceStoreException("Archive removal does not delete linked media files.");
            reclaimed += new FileInfo(path).Length; File.Delete(path);
        }
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        await AtomicJsonFile.WriteAsync(recordPath, removal with { CompletedUtc = DateTimeOffset.UtcNow }, ct);
        return reclaimed;
    }
    // New picks decode the MP4 once the segments are gone, so check that works first.
    private async Task ProbeVideoAsync(Guid project, ReferenceVideoMedia media, string directory, H3Settings settings, CancellationToken ct)
    {
        await VideoCatalog(project, media, settings, ct);
        var temp = Path.Combine(directory, "probe-" + Guid.NewGuid().ToString("N"));
        await FrameWorkers.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(temp);
            await mediaTools.ExtractReelFramesAsync(Path.Combine(directory, "video.mp4"), [0], temp, 0, settings, ct);
            if (!File.Exists(Path.Combine(temp, "000000.png"))) throw new WorkspaceStoreException("FFmpeg could not decode this reel's MP4. Its lossless archive was kept.");
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); FrameWorkers.Release(); }
    }
}
