using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public sealed partial class FileReferenceVideoStore
{
    private static readonly SemaphoreSlim FrameWorkers = new(2);
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, AtomicJsonFile.Options)));
    private static async Task<string> FileHash(string path, CancellationToken ct)
    { await using var input = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(input, ct)); }
    private async Task<ReferenceVideoMedia> Record(Guid project, Guid id, CancellationToken ct)
    {
        var directory = await DirectoryAsync(project, id, ct);
        var record = await AtomicJsonFile.ReadAsync<ReferenceVideoMedia>(Path.Combine(directory, "media.json"), ct)
            ?? throw new WorkspaceStoreException("The reel media is unavailable.");
        ReferenceVideos.ValidateMedia(record);
        if (record.Id != id) throw new WorkspaceStoreException("The reel media identity changed.");
        return record;
    }
    public async Task PublishArchiveAsync(Guid project, ReferenceVideoMedia media, ShotTake take, string sourceDirectory, CancellationToken ct = default)
    {
        if (!take.HasLosslessFrames) return;
        if (take.FrameCount != media.Frames || take.Frames.Count != media.Frames || take.Width != media.Width || take.Height != media.Height || media.Fps != 24)
            throw new WorkspaceStoreException("The lossless reel frames do not match the video.");
        var directory = await DirectoryAsync(project, media.Id, ct);
        using var gate = await ProjectFiles.LockAsync(Path.Combine(directory, "frame-archive.json"), ct);
        var folder = Path.Combine(directory, "lossless"); Directory.CreateDirectory(folder);
        List<ReelArchiveFile> files = [];
        foreach (var group in take.Frames.GroupBy(f => f.FileName))
        {
            var segment = files.Count; var name = LosslessFrameArchive.FileName(segment);
            if (group.Key != name || group.Any(f => f.Index < segment * 24 || f.Index >= (segment + 1) * 24)) throw new WorkspaceStoreException("Invalid reel archive index.");
            var source = Path.Combine(sourceDirectory, name);
            var indices = await LosslessFrameArchive.ValidateAsync(source, media.Width, media.Height, group.Count(), ct);
            if (!indices.SequenceEqual(group.Select(f => f.ArchiveFrameIndex)) || !take.Frames.Select(f => f.Index).SequenceEqual(Enumerable.Range(0, media.Frames))) throw new WorkspaceStoreException("Invalid reel frame sequence.");
            var sha = await FileHash(source, ct); var bytes = new FileInfo(source).Length;
            files.Add(new(name, bytes, sha));
        }
        var manifest = new ReelFrameArchive(media.Sha256, media.Width, media.Height, media.Frames, take.Frames.ToArray(), files);
        var path = Path.Combine(directory, "frame-archive.json");
        var prior = await AtomicJsonFile.ReadAsync<ReelFrameArchive>(path, ct);
        if (prior is not null && Hash(prior) != Hash(manifest)) throw new WorkspaceStoreException("This reel already has a different immutable frame archive.");
        // Compare the entire index before touching an already published archive. Failed transfers
        // can resume individual segments, but a different result must never overwrite this media.
        foreach (var file in files)
        {
            var source = Path.Combine(sourceDirectory, file.FileName); var destination = Path.Combine(folder, file.FileName);
            if (File.Exists(destination) && await FileHash(destination, ct) == file.Sha256) continue;
            var temp = destination + ".tmp";
            try {
                await using (var input = File.OpenRead(source)) await using (var output = File.Create(temp)) await input.CopyToAsync(output, ct);
                if (await FileHash(temp, ct) != file.Sha256) throw new WorkspaceStoreException("The reel archive changed during transfer. Retry saving this result.");
                ct.ThrowIfCancellationRequested(); DurableFile.Flush(temp); File.Move(temp, destination, true);
            }
            finally { File.Delete(temp); }
        }
        if (prior is null) await AtomicJsonFile.WriteAsync(path, manifest, ct);
    }
    public async Task<ReelFrameCatalog> FrameCatalogAsync(Guid project, ReferenceVideoMedia media, H3Settings settings, CancellationToken ct = default)
    {
        if (await Record(project, media.Id, ct) != media) throw new WorkspaceStoreException("The reel media changed.");
        var directory = await DirectoryAsync(project, media.Id, ct);
        var archive = await ArchiveAsync(directory, media, ct);
        // A compact project package keeps the index but leaves out the segments. New picks then
        // come from the MP4, as for reels saved without lossless frames.
        return archive is not null && HasSegments(directory, archive) ? ArchiveCatalog(archive) : await VideoCatalog(project, media, settings, ct);
    }
    private static async Task<ReelFrameArchive?> ArchiveAsync(string directory, ReferenceVideoMedia media, CancellationToken ct)
    {
        var archive = await AtomicJsonFile.ReadAsync<ReelFrameArchive>(Path.Combine(directory, "frame-archive.json"), ct);
        if (archive is not null && (archive.SourceSha256 != media.Sha256 || archive.FrameCount != media.Frames || archive.Frames.Count != media.Frames))
            throw new WorkspaceStoreException("The reel frame archive is invalid.");
        return archive;
    }
    private static ReelFrameCatalog ArchiveCatalog(ReelFrameArchive archive) => new(Hash(archive), true, Enumerable.Range(0, archive.FrameCount).Select(i => i / 24d).ToArray());
    private static bool HasSegments(string directory, ReelFrameArchive archive) => archive.Files.All(f => File.Exists(Path.Combine(directory, "lossless", f.FileName)));
    private async Task<ReelFrameCatalog> VideoCatalog(Guid project, ReferenceVideoMedia media, H3Settings settings, CancellationToken ct)
    {
        var directory = await DirectoryAsync(project, media.Id, ct);
        // v1 discarded the original PTS origin. Re-probe without changing saved relative frame identities.
        var path = Path.Combine(directory, "frame-times-v2.json"); using var gate = await ProjectFiles.LockAsync(path, ct);
        var cached = await AtomicJsonFile.ReadAsync<ReelFrameCatalog>(path, ct);
        if (cached?.Source == media.Sha256 && cached.Timestamps.Count == media.Frames) return cached;
        await FrameWorkers.WaitAsync(ct);
        try
        {
            var times = await mediaTools.ReelFrameTimesAsync(Path.Combine(directory, "video.mp4"), settings, ct);
            if (times.Count != media.Frames) throw new WorkspaceStoreException("The decoded frame count differs from this reel's media identity.");
            var catalog = new ReelFrameCatalog(media.Sha256, false, times.Select(t => t - times[0]).ToArray()) { PlaybackOrigin = times[0] };
            await AtomicJsonFile.WriteAsync(path, catalog, ct); return catalog;
        }
        finally { FrameWorkers.Release(); }
    }
    private async Task<ReelFrameCatalog> CatalogForFrame(Guid project, ReferenceVideoMedia media, ReelFrameIdentity frame, H3Settings settings, CancellationToken ct)
    {
        // Previously attached MP4-derived frames keep their original source even if an archive is later available.
        // Frames picked from an archive keep its identity even when its segments were left out of a package.
        var archive = frame.Source == media.Sha256 ? null : await ArchiveAsync(await DirectoryAsync(project, media.Id, ct), media, ct);
        var catalog = archive is null ? await VideoCatalog(project, media, settings, ct) : ArchiveCatalog(archive);
        if (catalog.Source != frame.Source || frame.Index < 0 || frame.Index >= catalog.Timestamps.Count || Math.Abs(catalog.Timestamps[frame.Index] - frame.Seconds) > .000001)
            throw new WorkspaceStoreException("This keyframe does not match its immutable reel source.");
        return catalog;
    }
    private static async Task<Stream> ArchiveFrame(string directory, ReelFrameIdentity frame, CancellationToken ct)
    {
        var archive = await AtomicJsonFile.ReadAsync<ReelFrameArchive>(Path.Combine(directory, "frame-archive.json"), ct) ?? throw new WorkspaceStoreException("The lossless reel frames are unavailable.");
        if (Hash(archive) != frame.Source || frame.Index < 0 || frame.Index >= archive.Frames.Count) throw new WorkspaceStoreException("The reel archive changed.");
        var entry = archive.Frames[frame.Index]; var segment = frame.Index / 24;
        var name = LosslessFrameArchive.FileName(segment);
        var file = archive.Files.SingleOrDefault(f => f.FileName == name) ?? throw new WorkspaceStoreException("The reel archive is incomplete.");
        var path = Path.Combine(directory, "lossless", name);
        if (!File.Exists(path)) throw new WorkspaceStoreException("This reel's lossless frames are not in this project. Choose the keyframe again from the video.");
        if (entry.FileName != name || new FileInfo(path).Length != file.Bytes || await FileHash(path, ct) != file.Sha256) throw new WorkspaceStoreException("The lossless reel archive changed on disk.");
        return await LosslessFrameArchive.OpenFrameAsync(path, entry.ArchiveFrameIndex, archive.Width, archive.Height, ct);
    }
    internal static string FrameFileName(ReelFrameIdentity frame) => $"frame-{frame.Source}-{frame.Index:D6}.png";
    private static string FramePath(string directory, ReelFrameIdentity frame) => Path.Combine(directory, FrameFileName(frame));
    public async Task PrepareFramesAsync(Guid project, IEnumerable<ReelFrameIdentity> frames, H3Settings settings, CancellationToken ct = default)
    {
        foreach (var group in frames.Distinct().GroupBy(f => (f.MediaId, f.Source)))
        {
            var media = await Record(project, group.Key.MediaId, ct);
            var directory = await DirectoryAsync(project, media.Id, ct);
            using var gate = await ProjectFiles.LockAsync(Path.Combine(directory, "frame-extraction"), ct);
            ReelFrameCatalog? catalog = null;
            foreach (var frame in group) catalog = await CatalogForFrame(project, media, frame, settings, ct);
            var missing = group.Where(f => !File.Exists(FramePath(directory, f))).OrderBy(f => f.Index).ToArray();
            if (missing.Length == 0) continue;
            if (missing.Length > 128) throw new WorkspaceStoreException("Prepare up to 128 reel frames at a time.");
            await FrameWorkers.WaitAsync(ct); var temp = Path.Combine(directory, "extract-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                if (!catalog!.Lossless) await mediaTools.ExtractReelFramesAsync(Path.Combine(directory, "video.mp4"), missing.Select(f => f.Index).ToArray(), temp, 0, settings, ct);
                for (var i = 0; i < missing.Length; i++)
                {
                    var path = Path.Combine(temp, $"{i:D6}.png");
                    if (catalog.Lossless) {
                        await using var source = await ArchiveFrame(directory, missing[i], ct);
                        await using var target = File.Create(path); await source.CopyToAsync(target, ct);
                    }
                    ct.ThrowIfCancellationRequested(); DurableFile.Flush(path); File.Move(path, FramePath(directory, missing[i]));
                }
            }
            finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); FrameWorkers.Release(); }
        }
    }
    public async Task<AssetMedia> OpenFrameAsync(Guid project, ReelFrameIdentity frame, H3Settings settings, CancellationToken ct = default)
    {
        await PrepareFramesAsync(project, [frame], settings, ct);
        var path = FramePath(await DirectoryAsync(project, frame.MediaId, ct), frame);
        return new(File.OpenRead(path), "image/png", File.GetLastWriteTimeUtc(path));
    }
    public async Task<ReelKeyframeSet> SuggestFramesAsync(Guid project, ReferenceVideoMedia media, int count, H3Settings settings, CancellationToken ct = default)
    {
        var catalog = await FrameCatalogAsync(project, media, settings, ct);
        var directory = await DirectoryAsync(project, media.Id, ct);
        var path = Path.Combine(directory, $"analysis-{ReelFrameSelector.Version}-{catalog.Source}.json");
        using var gate = await ProjectFiles.LockAsync(path, ct);
        var candidates = await AtomicJsonFile.ReadAsync<List<ReelFrameCandidate>>(path, ct);
        if (candidates is null)
        {
            candidates = []; var indices = ReelFrameSelector.Sample(catalog.Timestamps);
            await FrameWorkers.WaitAsync(ct); var temp = Path.Combine(directory, "analyze-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (catalog.Lossless)
                {
                    var archive = await AtomicJsonFile.ReadAsync<ReelFrameArchive>(Path.Combine(directory, "frame-archive.json"), ct)
                        ?? throw new WorkspaceStoreException("The lossless reel archive is unavailable.");
                    if (Hash(archive) != catalog.Source) throw new WorkspaceStoreException("The reel archive changed.");
                    foreach (var segment in indices.GroupBy(i => archive.Frames[i].FileName))
                    {
                        var file = archive.Files.Single(f => f.FileName == segment.Key);
                        var archivePath = Path.Combine(directory, "lossless", file.FileName);
                        if (new FileInfo(archivePath).Length != file.Bytes || await FileHash(archivePath, ct) != file.Sha256) throw new WorkspaceStoreException("The lossless reel archive changed on disk.");
                        await LosslessFrameArchive.VisitFramesAsync(archivePath, segment.Select(i => archive.Frames[i].ArchiveFrameIndex).ToArray(), archive.Width, archive.Height,
                            (decoded, image) => { foreach (var index in segment.Where(i => archive.Frames[i].ArchiveFrameIndex == decoded)) candidates.Add(ReelFrameSelector.Describe(index, catalog.Timestamps[index], image)); }, ct);
                    }
                }
                else
                {
                    await mediaTools.ExtractReelFramesAsync(Path.Combine(directory, "video.mp4"), indices, temp, 192, settings, ct);
                    for (var n = 0; n < indices.Count; n++) {
                        ct.ThrowIfCancellationRequested(); var index = indices[n];
                        using var png = File.OpenRead(Path.Combine(temp, $"{n:D6}.png"));
                        candidates.Add(ReelFrameSelector.Describe(index, catalog.Timestamps[index], png));
                    }
                }
                await AtomicJsonFile.WriteAsync(path, candidates, ct);
            }
            finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); FrameWorkers.Release(); }
        }
        return new() { Frames = ReelFrameSelector.Select(candidates, count).Select(c => new ReelKeyframe {
            Id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{media.Id}/{catalog.Source}/{c.Index}"))[..16]),
            Frame = new(media.Id, catalog.Source, c.Index, c.Seconds) }).ToList() };
    }
}
