using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Projects;

public enum CompactionPart { Trash, TakeArchives, ReelArchives, ReelCandidates, Backups, PackageManifest }
// What Compact project found. Confirmation acts only on these items; anything added later is left alone.
public sealed record ProjectCompactionPlan(Guid ProjectId, IReadOnlyList<MediaTrashRow> Trash, IReadOnlyList<Guid> Takes, long TakeBytes,
    IReadOnlyList<Guid> Reels, long ReelBytes, IReadOnlyList<string> Backups, long BackupBytes, string? Manifest, long ManifestBytes)
{
    public int Count(CompactionPart part) => part switch {
        CompactionPart.Trash => Trash.Count, CompactionPart.TakeArchives => Takes.Count, CompactionPart.ReelArchives => Reels.Count,
        CompactionPart.ReelCandidates => ReelCandidates.Count,
        CompactionPart.Backups => Backups.Count, _ => Manifest is null ? 0 : 1 };
    public long Bytes(CompactionPart part) => part switch {
        CompactionPart.Trash => Trash.Sum(r => r.Bytes), CompactionPart.TakeArchives => TakeBytes, CompactionPart.ReelArchives => ReelBytes,
        CompactionPart.ReelCandidates => ReelCandidateBytes,
        CompactionPart.Backups => BackupBytes, _ => ManifestBytes };
    /// <summary>What the project folder holds, measured with the plan.</summary>
    public ProjectStorageUsage? Usage { get; init; }
    /// <summary>Generation folders, relative to the project, of reels already saved to Assets.</summary>
    public IReadOnlyList<string> ReelCandidates { get; init; } = [];
    public long ReelCandidateBytes { get; init; }
}
public sealed record ProjectCompactionResult(long ReclaimedBytes, IReadOnlyList<string> Issues);
public interface IProjectCompaction
{
    Task<ProjectCompactionPlan> InspectAsync(Guid project, CancellationToken ct = default);
    Task<ProjectCompactionResult> CompactAsync(ProjectCompactionPlan plan, IReadOnlySet<CompactionPart> parts, IProgress<ProjectPackageProgress>? progress = null, CancellationToken ct = default);
}

// Frees space in place by reusing the existing cleanup operations: Trash, take archive removal
// and reel archive removal. It also deletes one-time migration backups and the stale manifest
// of an unzipped package opened as a folder. Images are never re-encoded in place.
public sealed class ProjectCompaction(ProjectFiles files, IProjectFolders folders, IShotStore shots, IReferenceVideoStore reels,
    IMediaTrashStore trash, IAiSettingsStore settings, IAiJobStore? jobs = null, IAssetStore? assets = null) : IProjectCompaction
{
    private static readonly string[] BackupNames = ["production-before-shared-inputs.json", "production-before-global-setups.json"];

    public async Task<ProjectCompactionPlan> InspectAsync(Guid project, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(project, ct);
        var rows = (await trash.ListAsync(ct)).Items.Where(r => r.ProjectId == project).ToArray();
        var doc = await shots.LoadAsync(project, ct); List<Guid> takes = []; long takeBytes = 0;
        foreach (var take in doc.Takes.Where(t => t.HasLosslessFrames))
        {
            var folder = FileShotStore.ArchiveDirectory(dir, take);
            var bytes = take.Frames.Select(f => f.FileName).Distinct().Select(f => FileShotStore.ArchivePath(folder, f)).Where(File.Exists).Sum(f => new FileInfo(f).Length);
            if (bytes > 0) { takes.Add(take.Id); takeBytes += bytes; }
        }
        List<Guid> media = []; long reelBytes = 0;
        var videos = Path.Combine(dir, "reference-videos");
        if (Directory.Exists(videos))
            foreach (var folder in Directory.EnumerateDirectories(videos).Order(StringComparer.Ordinal))
            {
                if (!Guid.TryParseExact(Path.GetFileName(folder), "D", out var id) || !File.Exists(Path.Combine(folder, "frame-archive.json"))) continue;
                var lossless = Path.Combine(folder, "lossless");
                var bytes = Directory.Exists(lossless) ? Directory.EnumerateFiles(lossless, "archive-*.webp").Sum(f => new FileInfo(f).Length) : 0;
                if (bytes > 0) { media.Add(id); reelBytes += bytes; }
            }
        // The backup is copied from production.json just before a one-time upgrade rewrites it.
        var backups = File.Exists(Path.Combine(dir, "production.json"))
            ? BackupNames.SelectMany(n => new[] { n, n + ".tmp" }).Where(n => File.Exists(Path.Combine(dir, n))).ToArray() : [];
        var manifest = await ManifestAsync(project, ct);
        var (candidates, candidateBytes) = await SavedReelCandidatesAsync(project, dir, ct);
        var usage = await Task.Run(() => ProjectStorageUsage.Measure(dir), ct);
        return new(project, rows, takes, takeBytes, media, reelBytes, backups, backups.Sum(n => new FileInfo(Path.Combine(dir, n)).Length),
            manifest, manifest is null ? 0 : new FileInfo(manifest).Length) { Usage = usage, ReelCandidates = candidates, ReelCandidateBytes = candidateBytes };
    }

    public async Task<ProjectCompactionResult> CompactAsync(ProjectCompactionPlan plan, IReadOnlySet<CompactionPart> parts,
        IProgress<ProjectPackageProgress>? progress = null, CancellationToken ct = default)
    {
        var project = plan.ProjectId;
        if (jobs is not null && (await jobs.ReadAsync(ct)).Jobs.Any(j => j.Target.ProjectId == project && j.LocksTarget))
            throw new WorkspaceStoreException("This project has queued or working AI requests. Wait for them to finish, or cancel them, then compact it.");
        var dir = await files.DirectoryAsync(project, ct); long reclaimed = 0; List<string> issues = [];
        async Task Run(CompactionPart part, string message, Func<Task> action)
        {
            if (!parts.Contains(part) || plan.Count(part) == 0) return;
            ct.ThrowIfCancellationRequested(); progress?.Report(new(message));
            try { await action(); }
            catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or IOException or UnauthorizedAccessException) { issues.Add(e.Message); }
        }
        // Trash first: deleting a removed reel or take also removes archives nothing else needs.
        await Run(CompactionPart.Trash, "Emptying this project's Trash…", async () => {
            var ids = plan.Trash.Select(r => r.Id).ToHashSet();
            var current = (await trash.ListAsync(ct)).Items.Where(r => r.ProjectId == project && ids.Contains(r.Id)).ToArray();
            var result = await trash.ChangeAsync(current, true, ct);
            reclaimed += current.Where(r => result.Succeeded.Contains(r.Id)).Sum(r => r.Bytes);
            issues.AddRange(result.Issues.Select(i => i.Message));
        });
        await Run(CompactionPart.TakeArchives, "Removing lossless take archives…", async () => {
            var doc = await shots.LoadAsync(project, ct);
            var takes = doc.Takes.Where(t => t.HasLosslessFrames && plan.Takes.Contains(t.Id)).Select(t => t.Id).ToArray();
            if (takes.Length == 0) return;
            doc = await shots.RemoveFrameArchivesAsync(project, takes, doc.Revision, ct);
            foreach (var take in doc.Takes.Where(t => takes.Contains(t.Id)))
                if (take.FrameArchiveRemoval is { CompletedUtc: not null } removal) reclaimed += removal.Files.Sum(f => f.Bytes);
                else issues.Add(take.FrameArchiveRemoval?.Error ?? "A take archive could not be removed.");
        });
        await Run(CompactionPart.ReelArchives, "Removing lossless reel archives…", async () => {
            var keep = await ReelKeyframeScan.ProjectAsync(dir, ct); var h3 = (await settings.LoadAsync(ct)).H3;
            foreach (var (media, number) in plan.Reels.Select((m, i) => (m, i + 1)))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new($"Removing lossless reel archives · reel video {number} of {plan.Reels.Count}…", reclaimed));
                if (!File.Exists(Path.Combine(dir, "reference-videos", media.ToString("D"), "media.json"))) continue; // Deleted with its Trash entry.
                try { reclaimed += await reels.RemoveArchiveAsync(project, media, keep, h3, ct); }
                catch (WorkspaceStoreException e) { issues.Add(e.Message); }
            }
        });
        await Run(CompactionPart.ReelCandidates, "Removing copies left by saved reels…", async () => {
            var (saved, _) = await SavedReelCandidatesAsync(project, dir, ct);
            foreach (var folder in plan.ReelCandidates.Intersect(saved))
                foreach (var file in CandidateMedia(Path.Combine(dir, folder)))
                {
                    ct.ThrowIfCancellationRequested();
                    var bytes = file.Length; file.Delete(); reclaimed += bytes;
                }
        });
        await Run(CompactionPart.Backups, "Removing migration backups…", () => {
            foreach (var name in plan.Backups.Where(n => BackupNames.Any(b => n == b || n == b + ".tmp")))
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) { reclaimed += new FileInfo(path).Length; File.Delete(path); }
            }
            return Task.CompletedTask;
        });
        await Run(CompactionPart.PackageManifest, "Removing the package manifest…", async () => {
            if (await ManifestAsync(project, ct) is { } path && path == plan.Manifest) { reclaimed += new FileInfo(path).Length; File.Delete(path); }
        });
        progress?.Report(new("Compaction finished.", reclaimed));
        return new(reclaimed, issues);
    }

    // Saving a generated reel copies its video and lossless frames into reference-videos and records a
    // publication receipt in assets.json, which retries check before using the folder again. Once that
    // receipt exists the copies left in its generation folder are not read; its small JSON files stay.
    private async Task<(IReadOnlyList<string> Folders, long Bytes)> SavedReelCandidatesAsync(Guid project, string dir, CancellationToken ct)
    {
        var runs = Path.Combine(dir, "reel-runs");
        if (assets is null || !Directory.Exists(runs)) return ([], 0);
        var saved = (await assets.LoadAsync(project, ct)).ReelPublications.Select(r => r.ReelId).ToHashSet();
        List<string> folders = []; long bytes = 0;
        foreach (var batch in Directory.EnumerateDirectories(runs).Order(StringComparer.Ordinal))
            foreach (var folder in Directory.EnumerateDirectories(batch, "candidate-*").Order(StringComparer.Ordinal))
            {
                if (!Guid.TryParseExact(Path.GetFileName(folder)["candidate-".Length..], "D", out var reel) || !saved.Contains(reel)) continue;
                var size = CandidateMedia(folder).Sum(f => f.Length);
                if (size > 0) { folders.Add(Path.GetRelativePath(dir, folder)); bytes += size; }
            }
        return (folders, bytes);
    }
    private static IEnumerable<FileInfo> CandidateMedia(string folder) => Directory.Exists(folder)
        ? new DirectoryInfo(folder).EnumerateFiles().Where(f => f.Name == "video.mp4" || f.Name.StartsWith("archive-", StringComparison.Ordinal) && f.Name.EndsWith(".webp", StringComparison.Ordinal)).ToArray()
        : [];

    // An unzipped package opened as a folder keeps manifest.json beside project/. Its file list and
    // hashes describe the package, not the edited folder, and Lumibelle never reads it again.
    private async Task<string?> ManifestAsync(Guid project, CancellationToken ct)
    {
        if (await folders.LocationAsync(project, ct) is not { } location || Path.GetFileName(location.Path.TrimEnd(Path.DirectorySeparatorChar)) != "project") return null;
        var path = Path.Combine(Path.GetDirectoryName(location.Path.TrimEnd(Path.DirectorySeparatorChar))!, "manifest.json");
        if (!File.Exists(path) || new FileInfo(path).Length > ProjectPackageFormat.MaxJsonBytes) return null;
        try
        {
            var manifest = ProjectPackageFormat.Parse<ProjectPackageManifest>(await File.ReadAllBytesAsync(path, ct));
            return manifest.Format == "lumibelle-project" && manifest.ProjectId == project ? path : null;
        }
        catch (Exception e) when (e is WorkspaceStoreException or System.Text.Json.JsonException) { return null; }
    }
}
