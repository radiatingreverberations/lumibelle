using System.Buffers.Binary;
using System.IO.Compression;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Projects;

public sealed partial class ProjectPackageService
{
    private sealed record ImportReceipt(Guid Token, DateTimeOffset CreatedUtc, ProjectInfo Project,
        string ArchiveHash, IReadOnlyList<ProjectPackageFile> Files, IReadOnlyList<string> Notices);
    private sealed record ImportStamp(Guid Token, string ArchiveHash);
    private async Task<bool> ExistsAsync(Guid id, CancellationToken ct) => Directory.Exists(Path.Combine(paths.Projects, id.ToString("D"))) || File.Exists(Path.Combine(paths.Projects, id.ToString("D")))
        || await files.Locations.FindAsync(id, ct) is not null;

    public async Task<ProjectPackageImport> StageImportAsync(Stream content, IProgress<ProjectPackageProgress>? progress = null, CancellationToken ct = default)
    {
        await transfer.WaitAsync(ct); var token = Guid.NewGuid(); var folder = ImportFolder(token);
        try
        {
            CleanupExpired();
            Directory.CreateDirectory(folder);
            ProjectPackageFormat.NoLinks(paths.Projects, folder);
            progress?.Report(new("Receiving project package…"));
            var zipPath = Path.Combine(folder, "source.zip"); ProjectPackageFile uploaded;
            await using (var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                uploaded = await ProjectPackageFormat.CopyAsync(content, output, "source.zip", ProjectPackageFormat.MaxArchiveBytes, ct, Reporter(progress, "Receiving project package…"));
            ProjectPackageManifest manifest;
            string root;
            await using (var input = File.OpenRead(zipPath))
            {
                ValidateCentralDirectory(input);
                using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
                if (zip.Entries.Count is < 2 or > ProjectPackageFormat.MaxFiles + 1) throw new WorkspaceStoreException("Invalid package entry count.");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries) {
                    ProjectPackageFormat.ZipEntry(entry);
                    if (!names.Add(entry.FullName)) throw new WorkspaceStoreException("Duplicate or case-colliding ZIP entries are not supported.");
                    if (entry.FullName != "manifest.json" && (!entry.FullName.StartsWith("project/", StringComparison.Ordinal) || !ProjectPackageFormat.Allowed(entry.FullName[8..])))
                        throw new WorkspaceStoreException("The archive includes an unsupported project file.");
                }
                var manifestEntry = zip.GetEntry("manifest.json") ?? throw new WorkspaceStoreException("This is not a Lumibelle project package.");
                if (manifestEntry.Length > ProjectPackageFormat.MaxJsonBytes) throw new WorkspaceStoreException("Package manifest is too large.");
                using (var manifestBytes = new MemoryStream()) {
                    using var source = manifestEntry.Open();
                    await ProjectPackageFormat.CopyAsync(source, manifestBytes, "manifest.json", ProjectPackageFormat.MaxJsonBytes, ct);
                    manifest = ProjectPackageFormat.Parse<ProjectPackageManifest>(manifestBytes.ToArray());
                }
                ValidateManifest(manifest);
                if (zip.Entries.Count != manifest.Files.Count + 1 || !names.SetEquals(manifest.Files.Select(f => "project/" + f.Path).Append("manifest.json")))
                    throw new WorkspaceStoreException("ZIP contents do not match the package manifest.");
                root = Path.Combine(folder, manifest.ProjectId.ToString("D")); Directory.CreateDirectory(root);
                long total = 0; var count = 0;
                foreach (var file in manifest.Files)
                {
                    ct.ThrowIfCancellationRequested(); var entry = zip.GetEntry("project/" + file.Path)!;
                    if (entry.Length != file.Bytes) throw new WorkspaceStoreException("A file length differs from the package manifest.");
                    var path = ProjectPackageFormat.Under(root, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var source = entry.Open();
                    await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                    var actual = await ProjectPackageFormat.CopyAsync(source, output, file.Path, file.Bytes, ct, Reporter(progress, "Validating project package…", total, count));
                    if (actual != file) throw new WorkspaceStoreException("A project file failed its checksum. Nothing was imported.");
                    total = checked(total + actual.Bytes); count++;
                    if (total > ProjectPackageFormat.MaxExpandedBytes) throw new WorkspaceStoreException("The expanded project is too large.");
                    progress?.Report(new("Validating project package…", total, count));
                }
            }
            File.Delete(zipPath);
            var state = await ProjectPackageState.ReadAsync(root, manifest.ProjectId, ct);
            if (state.Production.Compositions.Any(c => c.GenerationSetupId is not null))
                throw new WorkspaceStoreException("Portable projects must contain captured settings, not live global setup links.");
            var plan = await ProjectPackagePlan.CreateAsync(root, state, new(manifest.IncludedReferencedTrashImages, manifest.LeftOutLosslessArchives), ct);
            var expected = state.Documents().Keys.Concat(plan.Metadata.Keys).Concat(plan.Sources.Keys).ToHashSet(StringComparer.Ordinal);
            if (!expected.SetEquals(manifest.Files.Select(f => f.Path)))
                throw new WorkspaceStoreException("The package contains unreferenced media, unsupported metadata, or missing required files.");
            // Check semantic source hashes as well as the ZIP's own checksums: the latter
            // alone cannot establish agreement with captured reel/RefMod identities.
            foreach (var source in plan.Sources.Values) await VerifySource(source, ct);
            var originals = ProjectPackageRefinement.AllTakes(state).ToDictionary(t => t.Id, t => ShotCopy.Of(t));
            state.Clean(new ProjectPackagePrivacy()); state.FlattenSetups(new()); state.RenewTrash(clock.GetUtcNow());
            await ProjectPackageRefinement.RewriteAsync(plan, originals, Path.Combine(folder, "refinement"), ct);
            foreach (var source in plan.Sources.Values) {
                var target = ProjectPackageFormat.Under(root, source.Relative);
                if (source.Physical != target) File.Move(source.Physical, target, true);
            }
            await state.WriteAsync(root, ct);
            foreach (var (relative, bytes) in plan.Metadata) await File.WriteAllBytesAsync(ProjectPackageFormat.Under(root, relative), bytes, ct);
            await ProjectPackageState.ValidateStoresAsync(root, state.Project, ct);
            var staged = new List<ProjectPackageFile>();
            foreach (var relative in expected.Order(StringComparer.Ordinal)) {
                var path = ProjectPackageFormat.Under(root, relative); ProjectPackageFormat.NoLinks(root, path);
                await using var source = File.OpenRead(path);
                staged.Add(await ProjectPackageFormat.CopyAsync(source, Stream.Null, relative, ProjectPackageFormat.MaxExpandedBytes, ct));
            }
            var notices = manifest.Notices.Concat(plan.Notices).Distinct(StringComparer.Ordinal).ToArray();
            var receipt = new ImportReceipt(token, clock.GetUtcNow(), state.Project, uploaded.Sha256, staged, notices);
            await AtomicJsonFile.WriteAsync(Path.Combine(folder, "receipt.json"), receipt, ct);
            DeleteOwned(Path.Combine(folder, "refinement"));
            progress?.Report(new("Package checked. Confirm Import to add it to your library.", staged.Sum(f => f.Bytes), staged.Count));
            return new(token, state.Project, staged.Sum(f => f.Bytes), staged.Count, notices, await ExistsAsync(state.Project.Id, ct));
        }
        catch (Exception e) when (TransferError(e)) { DeleteOwned(folder); throw Failure(e); }
        catch { DeleteOwned(folder); throw; }
        finally { transfer.Release(); }
    }
    private static async Task VerifySource(ProjectPackageSource file, CancellationToken ct)
    {
        await using var source = File.OpenRead(file.Physical);
        if (file.ExpectedBytes is { } bytes && source.Length != bytes) throw new WorkspaceStoreException("A media record has the wrong file size.");
        if (file.ExpectedHash is { } hash) {
            var actual = await ProjectPackageFormat.CopyAsync(source, Stream.Null, file.Relative, ProjectPackageFormat.MaxExpandedBytes, ct);
            if (actual.Sha256 != hash) throw new WorkspaceStoreException("A media record differs from its captured source hash.");
        }
    }
    private static void ValidateManifest(ProjectPackageManifest m)
    {
        if (m.Format != "lumibelle-project" || m.Version != 1 || m.PrivacyProfile != "project-loras-only-v1" || m.ProjectId == Guid.Empty ||
            m.ExportedUtc == default || m.Files is null || m.Files.Count is < 1 or > ProjectPackageFormat.MaxFiles ||
            m.ReferencedTrashImages < 0 || m.IncludedTrashImages < 0 || m.IncludedTrashImages > m.ReferencedTrashImages ||
            !m.IncludedReferencedTrashImages && m.IncludedTrashImages != 0 || m.RemovedLoraSelections < 0 ||
            m.LeftOutLosslessFiles < 0 || m.LeftOutLosslessBytes < 0 || !m.LeftOutLosslessArchives && (m.LeftOutLosslessFiles != 0 || m.LeftOutLosslessBytes != 0) ||
            m.RecompressedImages < 0 || m.ResizedImages < 0 || m.ResizedImages > m.RecompressedImages ||
            (m.CompressedImages ? m.ImageQuality is not (>= 1 and <= 100) || m.MaxImageDimension is < 256 or > 32768 || m.MaxImageDimension is null && m.ResizedImages != 0
                : m.ImageQuality is not null || m.MaxImageDimension is not null || m.RecompressedImages != 0) ||
            m.Notices is null || m.Notices.Count > 1024 || m.Notices.Any(n => n is null || n.Length > 4096))
            throw new WorkspaceStoreException("Invalid or unsupported project package manifest.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0, metadata = 0;
        foreach (var f in m.Files) {
            if (f is null || !ProjectPackageFormat.Allowed(f.Path) || !names.Add(f.Path) || f.Bytes is <= 0 or > ProjectPackageFormat.MaxExpandedBytes || !ProjectPackageFormat.Hash(f.Sha256))
                throw new WorkspaceStoreException("Invalid package file manifest.");
            total = checked(total + f.Bytes);
            if (f.Path.EndsWith(".json", StringComparison.Ordinal)) {
                metadata = checked(metadata + f.Bytes);
                if (f.Bytes > ProjectPackageFormat.MaxJsonBytes) throw new WorkspaceStoreException("Package metadata exceeds its per-file limit.");
            }
        }
        if (!names.Contains("project.json") || total > ProjectPackageFormat.MaxExpandedBytes || metadata > ProjectPackageFormat.MaxJsonTotalBytes)
            throw new WorkspaceStoreException("The package exceeds its size limits or has no project manifest.");
    }
    public async Task<ProjectInfo> CommitImportAsync(Guid token, CancellationToken ct = default)
    {
        if (token == Guid.Empty) throw new WorkspaceStoreException("Choose a validated package first.");
        await transfer.WaitAsync(ct);
        try
        {
            var folder = ImportFolder(token); ProjectPackageFormat.NoLinks(paths.Projects, folder);
            var receipt = await ProjectPackageFormat.ReadAsync<ImportReceipt>(folder, "receipt.json", ct)
                ?? throw new WorkspaceStoreException("The staged import is unavailable. Choose the package again.");
            if (receipt.Token != token || receipt.Project is null || receipt.Project.Id == Guid.Empty || receipt.Files is null)
                throw new WorkspaceStoreException("Invalid import receipt.");
            var target = Path.Combine(paths.Projects, receipt.Project.Id.ToString("D"));
            using var projectLease = await ProjectFiles.LockAsync(target, ct);
            if (Directory.Exists(target)) {
                ProjectPackageFormat.NoLinks(paths.Projects, target);
                var stamp = await ProjectPackageFormat.ReadAsync<ImportStamp>(target, ".package-import.json", ct);
                if (stamp == new ImportStamp(token, receipt.ArchiveHash))
                    return await projects.GetAsync(receipt.Project.Id, ct) ?? receipt.Project; // Idempotent acknowledgement recovery.
                throw new WorkspaceStoreException("This project ID already exists. Import never overwrites or merges an existing project.");
            }
            if (File.Exists(target)) throw new WorkspaceStoreException("The destination is occupied. Nothing was imported.");
            if (await files.Locations.FindAsync(receipt.Project.Id, ct) is { } linked)
                throw new WorkspaceStoreException($"This project ID is already in the library from the folder {linked.Path}. Import never overwrites or merges an existing project.");
            if (jobs is not null && (await jobs.ReadAsync(ct)).Jobs.Any(j => j.Target.ProjectId == receipt.Project.Id))
                throw new WorkspaceStoreException("This library still has AI request history for that project ID. Import into a different library to avoid linking to unrelated execution records.");
            using (await ProjectFiles.LockAsync(Path.Combine(paths.Data, "generation-setups.json"), ct))
                if ((await setups.LoadAsync(ct)).Imports.Any(i => i.ProjectId == receipt.Project.Id))
                    throw new WorkspaceStoreException("This library has previous generation-setup mappings for that project ID. Import into another library rather than reconnecting to possibly changed settings.");
            var root = Path.Combine(folder, receipt.Project.Id.ToString("D")); ProjectPackageFormat.NoLinks(paths.Projects, root);
            foreach (var file in receipt.Files) await ProjectPackageFormat.VerifyAsync(root, file, ct);
            // Do not publish files inserted after inspection, even if they have an innocuous extension.
            var found = EnumerateFilesWithoutLinks(root).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
            if (found.Contains(".package-import.json")) {
                var prior = await ProjectPackageFormat.ReadAsync<ImportStamp>(root, ".package-import.json", ct);
                if (prior != new ImportStamp(token, receipt.ArchiveHash)) throw new WorkspaceStoreException("The staged import identity changed.");
                found.Remove(".package-import.json");
            }
            if (!found.SetEquals(receipt.Files.Select(f => f.Path))) throw new WorkspaceStoreException("The staged project changed after inspection.");
            await ProjectPackageState.ValidateStoresAsync(root, receipt.Project, ct);
            await AtomicJsonFile.WriteAsync(Path.Combine(root, ".package-import.json"), new ImportStamp(token, receipt.ArchiveHash), ct);
            ct.ThrowIfCancellationRequested();
            DurableFile.FlushDirectory(root);
            Directory.Move(root, target); // Same parent filesystem; one publication point. Never overwrite.
            return receipt.Project; // Do not observe cancellation after successful publication.
        }
        catch (Exception e) when (TransferError(e)) { throw Failure(e); }
        finally { transfer.Release(); }
    }
    private static IEnumerable<string> EnumerateFilesWithoutLinks(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var dir)) {
            ProjectPackageFormat.NoLinks(root, dir);
            foreach (var path in Directory.EnumerateFileSystemEntries(dir)) {
                ProjectPackageFormat.NoLinks(root, path);
                if (Directory.Exists(path)) pending.Push(path); else yield return path;
            }
        }
    }
    public async Task DiscardImportAsync(Guid token, CancellationToken ct = default)
    {
        if (token == Guid.Empty) return;
        await transfer.WaitAsync(ct);
        try { DeleteOwned(ImportFolder(token)); }
        finally { transfer.Release(); }
    }
    private static void ValidateCentralDirectory(Stream input)
    {
        // Bound central-directory allocation before ZipArchive materializes its Entries list.
        // This accepts ZIP64 video projects but rejects multi-disk archives and huge indexes.
        if (input.Length < 22) throw new WorkspaceStoreException("Not a ZIP archive.");
        var tail = new byte[(int)Math.Min(65557, input.Length)]; input.Position = input.Length - tail.Length; input.ReadExactly(tail);
        var offset = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i, 4)) == 0x06054b50 && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20, 2)) == tail.Length) { offset = i; break; }
        if (offset < 0) throw new WorkspaceStoreException("ZIP end record not found.");
        var end = tail.AsSpan(offset); var endPosition = input.Length - tail.Length + offset;
        if (BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) != 0)
            throw new WorkspaceStoreException("Multi-disk project packages are unsupported.");
        ulong count = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        ulong size = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]), start = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        if (count == ushort.MaxValue || size == uint.MaxValue || start == uint.MaxValue)
        {
            if (endPosition < 20) throw new WorkspaceStoreException("Invalid ZIP64 locator.");
            var locator = new byte[20]; input.Position = endPosition - 20; input.ReadExactly(locator);
            var at = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064b50 || BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4)) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) != 1 || at > (ulong)(endPosition - 76)) throw new WorkspaceStoreException("Invalid ZIP64 locator.");
            var record = new byte[56]; input.Position = (long)at; input.ReadExactly(record);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x06064b50 || BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(4)) != 44 ||
                BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(16)) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(20)) != 0)
                throw new WorkspaceStoreException("Unsupported ZIP64 directory.");
            count = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32)); size = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40)); start = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(48));
            if (BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(24)) != count) throw new WorkspaceStoreException("Invalid ZIP64 entry count.");
            endPosition = (long)at;
        } else if (BinaryPrimitives.ReadUInt16LittleEndian(end[8..]) != count) throw new WorkspaceStoreException("Invalid ZIP entry count.");
        if (count is < 2 or > ProjectPackageFormat.MaxFiles + 1 || size > 128UL * 1024 * 1024 || start > (ulong)endPosition || size > (ulong)endPosition - start)
            throw new WorkspaceStoreException("The ZIP directory exceeds package limits.");
        input.Position = 0;
    }
}
