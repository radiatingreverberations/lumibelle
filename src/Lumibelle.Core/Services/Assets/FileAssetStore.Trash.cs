using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed partial class FileAssetStore
{
    private TrashedImage TrashEntry(AssetLibrary library, ReferenceAsset asset, AssetImage image)
    {
        var now = clock.GetUtcNow();
        return new() { Image = image, Asset = asset with { Images = [], DefaultVoiceId = null }, DeletedUtc = now, ExpiresUtc = now.AddDays(30),
            AssetOrder = library.Assets.Select(a => a.Id).ToArray(), ImageOrder = asset.Images.Select(i => i.Id).ToArray() };
    }

    private static HashSet<Guid> DistinctIds(IReadOnlyCollection<Guid> values)
    {
        var ids = values.ToHashSet();
        if (ids.Count == 0 || ids.Count != values.Count || ids.Contains(Guid.Empty))
            throw new WorkspaceStoreException("Choose distinct images.");
        return ids;
    }

    public async Task<ImageTrashLibrary> ListTrashAsync(CancellationToken cancellationToken = default)
    {
        var projects = await files.ListProjectsAsync(cancellationToken);
        List<ImageTrashRow> rows = [];
        List<ImageTrashIssue> issues = projects.Issues.Select(i => new ImageTrashIssue(i.ProjectId, i.Message)).ToList();
        foreach (var project in projects.Projects)
        {
            try
            {
                var library = await LoadAsync(project.Id, cancellationToken);
                rows.AddRange(library.Trash.Select(t => new ImageTrashRow(project.Id, project.Name, library.Revision, t)));
            }
            catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException)
            { issues.Add(new(project.Id, $"{project.Name}: {e.Message}")); }
        }
        return new(rows.OrderByDescending(r => r.Entry.DeletedUtc).ThenBy(r => r.Entry.Id).ToArray(), issues);
    }

    public async Task<AssetMedia?> OpenTrashImageAsync(Guid projectId, Guid trashId, CancellationToken cancellationToken = default)
    {
        var directory = await files.DirectoryAsync(projectId, cancellationToken);
        var current = await ReadAsync(directory, projectId, cancellationToken);
        var item = current.Trash.SingleOrDefault(t => t.Id == trashId && t.State == ImageTrashState.Recoverable);
        if (item is null) return null;
        try
        {
            return new(new FileStream(ImagePath(directory, item.Asset.Id, item.Image), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan),
                item.Image.ContentType, item.Image.CreatedUtc);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new WorkspaceStoreException("Couldn’t read this image in Trash.", e); }
    }

    public async Task<AssetLibrary> RestoreImagesAsync(Guid projectId, IReadOnlyCollection<Guid> trashIds, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var ids = DistinctIds(trashIds);
        var directory = await files.DirectoryAsync(projectId, cancellationToken);
        using var gate = await ProjectFiles.LockAsync(directory, cancellationToken);
        var current = await ReadAsync(directory, projectId, cancellationToken);
        EnsureRevision(current, expectedRevision);
        var entries = current.Trash.Where(t => ids.Contains(t.Id)).OrderByDescending(t => t.DeletedUtc).ThenBy(t => t.Id).ToArray();
        if (entries.Length != ids.Count) throw new WorkspaceStoreException("An image is no longer in Trash. Refresh before retrying.");
        if (entries.Any(t => !t.CanRestore(clock.GetUtcNow())))
            throw new WorkspaceStoreException("An image has expired or is being permanently deleted and cannot be restored.");
        // Check every file before changing anything. Missing files remain listed for explicit cleanup.
        foreach (var item in entries)
        {
            try { using var stream = File.Open(ImagePath(directory, item.Asset.Id, item.Image), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { throw new WorkspaceStoreException("An image file is missing or unreadable. Nothing was restored; the Trash selection is intact.", e); }
        }
        var assets = current.Assets.ToList();
        var legacyAssetOrder = current.Trash.Select(t => t.Asset.Id).Distinct().ToArray();
        // Trash is stored in deletion/input order; its newest-first display order
        // must not determine the asset library's manual order.
        foreach (var group in current.Trash.Where(t => ids.Contains(t.Id)).GroupBy(t => t.Asset.Id)) {
            if (assets.Any(a => a.Id == group.Key)) continue;
            var latest = entries.First(t => t.Asset.Id == group.Key);
            TrashRestoreOrder.Insert(assets, latest.Asset with { Images = [], DefaultVoiceId = null }, latest.AssetOrder ?? legacyAssetOrder, a => a.Id);
        }
        foreach (var item in entries)
        {
            var index = assets.FindIndex(a => a.Id == item.Asset.Id);
            var asset = assets[index];
            if (asset.Images.Any(i => i.Id == item.Image.Id)) throw new WorkspaceStoreException("An image with this identity already exists.");
            var restored = item.Image with { IsCover = item.Image.IsCover && !asset.Images.Any(i => i.IsCover) };
            var images = asset.Images.ToList();
            TrashRestoreOrder.Insert(images, restored, item.ImageOrder ?? current.Trash.Where(t => t.Asset.Id == item.Asset.Id).Select(t => t.Image.Id).Distinct().ToArray(), i => i.Id);
            assets[index] = asset with { Looks = LookPolicy.RestoreLooks(asset, item.Asset, restored.LookId), Images = images, UpdatedUtc = clock.GetUtcNow() };
        }
        return await PublishAsync(directory, current with { Assets = assets, Trash = current.Trash.Where(t => !ids.Contains(t.Id)).ToList() },
            current.Revision, cancellationToken);
    }

    public async Task<ImagePurgeResult> PurgeImagesAsync(Guid projectId, IReadOnlyCollection<Guid> trashIds, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var ids = DistinctIds(trashIds);
        var directory = await files.DirectoryAsync(projectId, cancellationToken);
        using var gate = await ProjectFiles.LockAsync(directory, cancellationToken);
        var current = await ReadAsync(directory, projectId, cancellationToken);
        EnsureRevision(current, expectedRevision);
        if (current.Trash.Count(t => ids.Contains(t.Id)) != ids.Count)
            throw new WorkspaceStoreException("An image is no longer in Trash. Refresh before retrying.");
        return await PurgeLockedAsync(directory, current, ids, cancellationToken);
    }

    private async Task<ImagePurgeResult> PurgeLockedAsync(string directory, AssetLibrary current, HashSet<Guid> ids, CancellationToken ct)
    {
        // Publish deletion intent before touching files. A crash cannot leave a recoverable entry pointing at a purged file.
        if (current.Trash.Any(t => ids.Contains(t.Id) && t.State != ImageTrashState.Purging))
            current = await PublishAsync(directory, current with
            { Trash = current.Trash.Select(t => ids.Contains(t.Id) ? t with { State = ImageTrashState.Purging, CleanupError = null } : t).ToList() }, current.Revision, ct);
        List<Guid> removed = [];
        Dictionary<Guid, string> failures = [];
        foreach (var item in current.Trash.Where(t => ids.Contains(t.Id)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                File.Delete(ImagePath(directory, item.Asset.Id, item.Image));
                removed.Add(item.Id);
            }
            catch (DirectoryNotFoundException) { removed.Add(item.Id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { failures[item.Id] = $"{item.Asset.Name}: couldn’t remove the image file. Check permissions or close apps using it, then retry."; }
        }
        var next = current with { Trash = current.Trash.Where(t => !removed.Contains(t.Id)).Select(t =>
            failures.TryGetValue(t.Id, out var error) ? t with { CleanupError = error } : t).ToList() };
        if (removed.Count > 0 || failures.Count > 0) next = await PublishAsync(directory, next, current.Revision, ct);
        return new(next, removed, failures.Values.ToArray());
    }

    public async Task<IReadOnlyList<ImageTrashIssue>> CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        var projects = await files.ListProjectsAsync(cancellationToken);
        List<ImageTrashIssue> issues = projects.Issues.Select(i => new ImageTrashIssue(i.ProjectId, i.Message)).ToList();
        foreach (var project in projects.Projects)
        {
            try
            {
                var directory = await files.DirectoryAsync(project.Id, cancellationToken);
                using var gate = await ProjectFiles.LockAsync(directory, cancellationToken);
                var current = await ReadAsync(directory, project.Id, cancellationToken);
                var ids = current.Trash.Where(t => t.State == ImageTrashState.Purging || t.ExpiresUtc <= clock.GetUtcNow()).Select(t => t.Id).ToHashSet();
                if (ids.Count == 0) continue;
                var result = await PurgeLockedAsync(directory, current, ids, cancellationToken);
                issues.AddRange(result.Errors.Select(e => new ImageTrashIssue(project.Id, e)));
            }
            catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException)
            { issues.Add(new(project.Id, $"{project.Name}: {e.Message}")); }
        }
        return issues;
    }
}
