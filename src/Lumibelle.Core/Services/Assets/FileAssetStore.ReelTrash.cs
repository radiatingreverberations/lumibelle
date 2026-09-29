using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed partial class FileAssetStore
{
    // Deleting a removed reel always removes its Trash entry. Its media folder goes only when
    // nothing else names that media: another reel, a shot or take binding, a production setup,
    // a derived recording or saved frame provenance. Those keep working from the same files.
    public async Task<AssetLibrary> PurgeReelsAsync(Guid project, IReadOnlyCollection<Guid> reels, long expectedRevision, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(project, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await ReadAsync(dir, project, ct); EnsureRevision(d, expectedRevision);
        if (reels.Count == 0 || reels.Any(id => d.ReelTrash.All(t => t.Reel.Id != id))) throw new WorkspaceConflictException();
        // Record the intent first. An interrupted deletion resumes at the next Trash cleanup.
        d = await PublishAsync(dir, d with { ReelTrash = d.ReelTrash.Select(t => reels.Contains(t.Reel.Id) ? t with { Purging = true, Error = null } : t).ToList() }, d.Revision, ct);
        var selected = d.ReelTrash.Where(t => reels.Contains(t.Reel.Id)).ToArray();
        var remaining = d with { ReelTrash = d.ReelTrash.Where(t => !reels.Contains(t.Reel.Id)).ToList() };
        var mentions = await MentionsAsync(dir, remaining, ct);
        var failed = new HashSet<Guid>();
        foreach (var media in selected.Select(t => t.Reel.Media.Id).Distinct())
        {
            if (mentions.Contains(media.ToString("D"), StringComparison.OrdinalIgnoreCase)) continue;
            try { DeleteReelMedia(dir, media); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or WorkspaceStoreException) { failed.Add(media); }
        }
        var kept = selected.Where(t => failed.Contains(t.Reel.Media.Id)).Select(t => t with { Error = "Could not remove the reel's files. Close apps using them and retry." });
        return await PublishAsync(dir, remaining with { ReelTrash = [.. remaining.ReelTrash, .. kept] }, d.Revision, ct);
    }
    // Space each removed reel would free: its media folder, unless something else still uses it.
    public async Task<IReadOnlyDictionary<Guid, long>> ReelTrashBytesAsync(Guid project, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(project, ct); var d = await ReadAsync(dir, project, ct);
        var mentions = await MentionsAsync(dir, d, ct); var result = new Dictionary<Guid, long>();
        foreach (var item in d.ReelTrash)
        {
            var media = item.Reel.Media.Id.ToString("D");
            var own = Count(JsonSerializer.Serialize(item, AtomicJsonFile.Options), media);
            result[item.Reel.Id] = Count(mentions, media) > own ? 0 : FolderBytes(Path.Combine(dir, "reference-videos", media));
        }
        return result;
    }
    private static async Task<string> MentionsAsync(string dir, AssetLibrary library, CancellationToken ct)
    {
        var text = new StringBuilder(JsonSerializer.Serialize(library, AtomicJsonFile.Options));
        foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            // One-time migration backups are never read by Lumibelle.
            var name = Path.GetFileName(file);
            if (name == "assets.json" || name.StartsWith("production-before-", StringComparison.Ordinal)) continue;
            text.Append(await File.ReadAllTextAsync(file, ct));
        }
        return text.ToString();
    }
    private static int Count(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.OrdinalIgnoreCase)) count++;
        return count;
    }
    private static long FolderBytes(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }
    private static void DeleteReelMedia(string dir, Guid media)
    {
        var root = Path.Combine(dir, "reference-videos"); var folder = Path.Combine(root, media.ToString("D"));
        if (!Directory.Exists(folder)) return;
        foreach (var path in new[] { root, folder }.Concat(Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new WorkspaceStoreException("Reel deletion does not follow linked folders.");
        Directory.Delete(folder, true);
    }
}
