using System.IO.Compression;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Services.Projects;

public interface IProjectPackageService
{
    Task<ProjectPackageExport> ExportAsync(Guid project, ProjectExportOptions options, IProgress<ProjectPackageProgress>? progress = null, CancellationToken ct = default);
    Task<AssetMedia?> OpenExportAsync(Guid project, Guid export, CancellationToken ct = default);
    Task DiscardExportAsync(Guid project, Guid export, CancellationToken ct = default);
    Task<ProjectPackageImport> StageImportAsync(Stream content, IProgress<ProjectPackageProgress>? progress = null, CancellationToken ct = default);
    Task<ProjectInfo> CommitImportAsync(Guid token, CancellationToken ct = default);
    Task DiscardImportAsync(Guid token, CancellationToken ct = default);
}

public sealed partial class ProjectPackageService(ApplicationPaths paths, ProjectFiles files, IProjectStore projects,
    IGenerationSetupStore setups, TimeProvider clock, IAiJobStore? jobs = null) : IProjectPackageService
{
    public const long MaximumArchiveBytes = ProjectPackageFormat.MaxArchiveBytes;
    // A transfer does not occupy an AI/GPU queue slot. Large files are streamed to disk.
    private readonly SemaphoreSlim transfer = new(1, 1);
    private string Exports => Path.Combine(paths.Temporary, "project-packages");
    private string ExportFolder(Guid id) => Path.Combine(Exports, id.ToString("D"));
    private string ImportFolder(Guid id) => Path.Combine(paths.Projects, ".importing-" + id.ToString("D"));
    public async Task<ProjectPackageExport> ExportAsync(Guid project, ProjectExportOptions options,
        IProgress<ProjectPackageProgress>? progress = null, CancellationToken ct = default)
    {
        if (options.MaxImageDimension is < 256 or > 32768) throw new WorkspaceStoreException("Choose a maximum image size between 256 and 32768 pixels.");
        await transfer.WaitAsync(ct);
        var id = Guid.NewGuid(); var folder = ExportFolder(id);
        try
        {
            CleanupExpired();
            var root = await files.DirectoryAsync(project, ct);
            ProjectPackageFormat.NoLinks(root, root);
            Directory.CreateDirectory(folder); progress?.Report(new("Capturing saved project state…"));
            using var projectLease = await ProjectFiles.LockAsync(root, ct);
            using var preferencesLease = await ProjectFiles.LockAsync(Path.Combine(root, "ai-preferences.json"), ct);
            // Language variants depend on immutable source jobs; the current portable
            // package schema does not preserve that ownership. Never silently omit them.
            await FileProjectDubbingStore.CheckPortableExportAsync(root, project, ct);
            var state = await ProjectPackageState.ReadAsync(root, project, ct);
            var leftOutTakes = options.LeaveOutLosslessArchives ? ProjectPackageCompaction.LeaveOutTakeArchives(state, clock.GetUtcNow()) : [];
            state.History = await ProjectPackageHistory.CaptureAsync(project, state.History, jobs, ct);
            // Lock order matches production saves: project, then global setups. Release the
            // global lock after copying only the setups this project's compositions use.
            using (await ProjectFiles.LockAsync(Path.Combine(paths.Data, "generation-setups.json"), ct))
                state.FlattenSetups(await setups.LoadAsync(ct));
            var plan = await ProjectPackagePlan.CreateAsync(root, state, options, ct, folder);
            var originals = ProjectPackageRefinement.AllTakes(state).ToDictionary(t => t.Id, t => ShotCopy.Of(t));
            var privacy = new ProjectPackagePrivacy(); state.Clean(privacy);
            var takes = ProjectPackageCompaction.LeftOut(state, leftOutTakes);
            var (leftOutFiles, leftOutBytes) = (plan.LeftOutLosslessFiles + takes.Files, plan.LeftOutLosslessBytes + takes.Bytes);
            var (recompressed, resized) = (0, 0);
            if (options.CompressImages)
            {
                progress?.Report(new("Compressing images for sharing…"));
                (recompressed, resized) = await ProjectPackageCompaction.CompressImagesAsync(plan, root, options, Path.Combine(folder, "images"), ct);
            }
            progress?.Report(new("Filtering metadata and preparing retained refinement data…"));
            await ProjectPackageRefinement.RewriteAsync(plan, originals, Path.Combine(folder, "refinement"), ct);
            state.Validate();
            var metadata = state.Documents(); foreach (var pair in plan.Metadata) metadata.Add(pair.Key, pair.Value);
            if (metadata.Values.Sum(b => (long)b.Length) > ProjectPackageFormat.MaxJsonTotalBytes)
                throw new WorkspaceStoreException("The package exceeds its combined metadata limit.");
            var zipPath = Path.Combine(folder, "project.zip"); var entries = new List<ProjectPackageFile>(); long total = 0;
            ProjectPackageManifest manifest;
            await using (var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (var (name, bytes) in metadata.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        if (!ProjectPackageFormat.Allowed(name)) throw new WorkspaceStoreException("Unsupported metadata path.");
                        using var source = new MemoryStream(bytes, false); using var target = zip.CreateEntry("project/" + name, CompressionLevel.Fastest).Open();
                        var entry = await ProjectPackageFormat.CopyAsync(source, target, name, ProjectPackageFormat.MaxJsonBytes, ct);
                        entries.Add(entry); total = checked(total + entry.Bytes);
                    }
                    foreach (var source in plan.Sources.Values.OrderBy(p => p.Relative, StringComparer.Ordinal))
                    {
                        ct.ThrowIfCancellationRequested();
                        // Original sources cannot be swapped for links while the project is locked.
                        ProjectPackageFormat.NoLinks(source.Physical.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? root : folder, source.Physical);
                        await using var input = File.OpenRead(source.Physical);
                        if (source.ExpectedBytes is { } bytes && input.Length != bytes) throw new WorkspaceStoreException("A source file changed size before export.");
                        using var target = zip.CreateEntry("project/" + source.Relative, CompressionLevel.NoCompression).Open();
                        progress?.Report(new("Packing project media…", total, entries.Count));
                        var report = Reporter(progress, "Packing project media…", total, entries.Count);
                        var entry = await ProjectPackageFormat.CopyAsync(input, target, source.Relative, ProjectPackageFormat.MaxExpandedBytes - total, ct, copied => {
                            if (output.Position > ProjectPackageFormat.MaxArchiveBytes) throw new WorkspaceStoreException("Project packages are limited to 64 GiB compressed.");
                            report(copied);
                        });
                        if (entry.Bytes == 0) throw new WorkspaceStoreException("A project media file is empty. Restore it before exporting.");
                        if (source.ExpectedHash is { } hash && entry.Sha256 != hash) throw new WorkspaceStoreException("A source file no longer matches its captured hash. Export was cancelled.");
                        entries.Add(entry); total = checked(total + entry.Bytes);
                        if (output.Position > ProjectPackageFormat.MaxArchiveBytes) throw new WorkspaceStoreException("Project packages are limited to 64 GiB compressed.");
                    }
                    if (entries.Count > ProjectPackageFormat.MaxFiles) throw new WorkspaceStoreException("The package contains too many files.");
                    manifest = new() { ProjectId = project, ExportedUtc = clock.GetUtcNow(),
                        IncludedReferencedTrashImages = options.IncludeReferencedTrashImages, ReferencedTrashImages = plan.ReferencedTrashImages,
                        IncludedTrashImages = plan.IncludedTrashImages, RemovedLoraSelections = privacy.Removed,
                        LeftOutLosslessArchives = options.LeaveOutLosslessArchives, LeftOutLosslessFiles = leftOutFiles, LeftOutLosslessBytes = leftOutBytes,
                        CompressedImages = options.CompressImages, ImageQuality = options.CompressImages ? ProjectExportOptions.ImageQuality : null,
                        MaxImageDimension = options.CompressImages ? options.MaxImageDimension : null, RecompressedImages = recompressed, ResizedImages = resized,
                        Files = entries, Notices = plan.Notices.Concat(CompactionNotices(options, leftOutFiles, leftOutBytes, recompressed, resized)).Concat(StandardNotices).ToArray() };
                    using var targetManifest = zip.CreateEntry("manifest.json", CompressionLevel.Fastest).Open();
                    var manifestBytes = ProjectPackageFormat.Json(manifest);
                    if (manifestBytes.Length > ProjectPackageFormat.MaxJsonBytes) throw new WorkspaceStoreException("Package manifest exceeds its size limit.");
                    await targetManifest.WriteAsync(manifestBytes, ct);
                }
                await output.FlushAsync(ct);
                if (output.Length > ProjectPackageFormat.MaxArchiveBytes || total > ProjectPackageFormat.MaxExpandedBytes)
                    throw new WorkspaceStoreException("The project exceeds the package transfer limit.");
            }
            var result = new ProjectPackageExport(id, project, "lumibelle-" + project.ToString("D") + ".zip", new FileInfo(zipPath).Length, manifest);
            await AtomicJsonFile.WriteAsync(Path.Combine(folder, "receipt.json"), result, ct);
            foreach (var scratch in new[] { "refinement", "keyframes", "images" }) DeleteOwned(Path.Combine(folder, scratch));
            progress?.Report(new("Project package ready.", result.Bytes, entries.Count));
            return result;
        }
        catch (Exception e) when (TransferError(e)) { DeleteOwned(folder); throw Failure(e); }
        catch { DeleteOwned(folder); throw; }
        finally { transfer.Release(); }
    }
    private static Action<long> Reporter(IProgress<ProjectPackageProgress>? progress, string label, long prior = 0, int files = 0)
    {
        long reported = 0;
        return bytes => {
            if (progress is null || bytes - reported < 8L * 1024 * 1024) return;
            reported = bytes; progress.Report(new(label, checked(prior + bytes), files));
        };
    }
    internal static readonly string[] StandardNotices = [
        "Credentials, model files, the global LoRA catalog, live AI jobs and run folders are not included. Import never starts or resumes generation.",
        "Unused LoRA selections and inactive accelerator paths are removed from structured metadata, including refinement SafeTensors headers. Used LoRA names and paths remain.",
        "Media bytes and authored text are not anonymized. Imported images, audio and videos may contain their own embedded metadata; inspect them before sharing sensitive content.",
        "RefMod source PNGs and recipes are included. Server RefMod cache files are not; they rebuild when needed.",
        "Saved Script discussion and terminal Script responses are included as local history, without remote job controls. Other queue-only responses and captured generation inputs are not; historical One more/request recovery requires the original queue.",
        "Referenced trash recordings, reels and takes are retained as dependencies. Imported trash images, voices and takes get a fresh 30-day recovery window, without restoration."
    ];
    private static IEnumerable<string> CompactionNotices(ProjectExportOptions options, int files, long bytes, int recompressed, int resized)
    {
        if (options.LeaveOutLosslessArchives)
            yield return $"Lossless take and reel frame archives were left out ({files} files, {bytes / (1024d * 1024):0.#} MiB). Takes and reels play from their MP4 files, " +
                "and paused take frames and new reel keyframes are decoded from the MP4. Reel keyframes already chosen from lossless frames are included as PNG pictures.";
        if (options.CompressImages)
            yield return $"{recompressed} image(s) were re-encoded for sharing as lossy WebP (PNG sources) or JPEG, quality {ProjectExportOptions.ImageQuality}" +
                (options.MaxImageDimension is { } max ? $"; {resized} were reduced to at most {max} px. Images whose size is recorded in generation, frame, crop or regional-edit details keep their dimensions." : ".") +
                " Re-encoded images do not keep their EXIF, XMP or PNG text metadata. Reel keyframes and RefMod inputs stay lossless PNG.";
    }
    public async Task<AssetMedia?> OpenExportAsync(Guid project, Guid export, CancellationToken ct = default)
    {
        if (project == Guid.Empty || export == Guid.Empty) return null;
        var dir = ExportFolder(export); var path = Path.Combine(dir, "project.zip");
        if (!File.Exists(path)) return null;
        ProjectPackageFormat.NoLinks(Exports, path);
        var receipt = await AtomicJsonFile.ReadAsync<ProjectPackageExport>(Path.Combine(dir, "receipt.json"), ct);
        if (receipt?.ProjectId != project || receipt.Id != export) return null;
        return new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 131072, true), "application/zip", receipt.Manifest.ExportedUtc);
    }
    public async Task DiscardExportAsync(Guid project, Guid export, CancellationToken ct = default)
    {
        if (project == Guid.Empty || export == Guid.Empty) return;
        await transfer.WaitAsync(ct);
        try {
            var folder = ExportFolder(export);
            if (!Directory.Exists(folder)) return;
            ProjectPackageFormat.NoLinks(Exports, folder);
            var receipt = await ProjectPackageFormat.ReadAsync<ProjectPackageExport>(folder, "receipt.json", ct);
            if (receipt?.ProjectId == project && receipt.Id == export) DeleteOwned(folder);
        } finally { transfer.Release(); }
    }
    private void CleanupExpired()
    {
        // Opportunistic cleanup, not a background worker. The transfer gate protects all
        // live imports/exports in this process; the workspace lease excludes other writers.
        var threshold = clock.GetUtcNow().UtcDateTime.AddHours(-24);
        if (Directory.Exists(Exports)) foreach (var folder in Directory.EnumerateDirectories(Exports))
            if (ProjectPackageFormat.Id(Path.GetFileName(folder)) && Directory.GetLastWriteTimeUtc(folder) < threshold &&
                (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0) DeleteOwned(folder);
        if (Directory.Exists(paths.Projects)) foreach (var folder in Directory.EnumerateDirectories(paths.Projects, ".importing-*")) {
            var name = Path.GetFileName(folder);
            if (name.Length > 11 && ProjectPackageFormat.Id(name[11..]) && Directory.GetLastWriteTimeUtc(folder) < threshold &&
                (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0) DeleteOwned(folder);
        }
    }
    private static bool TransferError(Exception e) => e is IOException or UnauthorizedAccessException or JsonException or FormatException or OverflowException or ArgumentException or InvalidOperationException or NullReferenceException or KeyNotFoundException;
    private static WorkspaceStoreException Failure(Exception e) => new("Project transfer failed: " + e.Message + " No existing project was replaced.", e);
    private static void DeleteOwned(string path)
    {
        // Only call with a private transfer directory we created, never an imported entry path.
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
